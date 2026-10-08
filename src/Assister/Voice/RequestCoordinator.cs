using Assister.Diagnostics;
using System.Diagnostics;
using Assister.Contracts;
using Assister.Intents;
using Assister.Modules.HomeAssistant;
using Assister.Llm;
using Assister.Modules.Timers;
using Assister.Tools;

namespace Assister.Voice;

public sealed class RequestCoordinator(IIntentEngine Classifier, IEntityResolver Resolver, DirectIntentHandler Handler,
    HomeAssistantStateCache Cache, ILogger<RequestCoordinator> Logger, ToolLoop? LanguageModel = null, TimerIntentHandler? Timers = null, RunStore? Diagnostics = null,
    IConfiguration? Configuration = null, IntegrationActionDispatcher? Actions = null, Assister.Satellites.SatelliteManager? Satellites = null) : IRequestCoordinator
{
    public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken) => ProcessWithHistoryAsync(Request, [], CancellationToken);

    public async Task<RequestResult> ProcessWithHistoryAsync(UserRequest Request, IReadOnlyList<LlmMessage> History, CancellationToken CancellationToken,
        Func<string, CancellationToken, Task>? OnText = null, DeviceConversationContext? DeviceContext = null)
    {
        using var Run = RunTracing.EnsureRun(Diagnostics, "text", Request.SatelliteId, Request.Area, Request.ConversationId, Request.Message);
        var TraceId = RunTracing.RunId;
        var Clock = Stopwatch.StartNew();
        using var Scope = Logger.BeginScope(new Dictionary<string, object> { ["RunId"] = TraceId, ["SatelliteId"] = Request.SatelliteId });
        EntityResolutionResult? Resolution = null;
        RequestResult Result(string Text, string Outcome, string HandledBy = "direct-intent")
        {
            var Spoken = VoiceFormatter.Format(Text);
            using (var Step = RunTracing.Start("ResponseFormatting", "Response formatting", "Normalize the exact text that will be supplied to speech synthesis."))
            {
                Step.Input(new { rawResponse = Text });
                Step.Output(new { spokenResponse = Spoken });
                Step.Complete();
            }
            RunTracing.Response(Text, Spoken, Outcome, HandledBy, Request.ConversationId);
            Logger.LogInformation("Request completed: {Outcome}, {DurationMilliseconds} ms.", Outcome, Clock.Elapsed.TotalMilliseconds);
            return new(Text, Request.ConversationId, HandledBy, TraceId, Outcome,
                Resolution?.Entities.Select(Entity => Entity.EntityId).ToArray() ?? [], Resolution?.Confidence, Clock.Elapsed.TotalMilliseconds, Spoken);
        }

        if (string.IsNullOrWhiteSpace(Request.Message) || Request.Message.Length > 1000 || string.IsNullOrWhiteSpace(Request.SatelliteId)
            || Request.SatelliteId.Length > 128 || Request.Area?.Length > 128 || !RequestInputLimits.ValidDocuments(Request))
        {
            return Result("Please send a message of 1 to 1000 characters and a valid satellite identifier.", "invalid-request", "validation");
        }

        if (StopCommands.IsStop(Request.Message))
        {
            var Stopped = Satellites is not null && await Satellites.StopAsync(Request.SatelliteId, CancellationToken);
            return Result(Stopped ? "Stopped." : "There is no connected satellite to stop.", Stopped ? "succeeded" : "unavailable", "satellite-stop");
        }
        if (Timers is not null && await Timers.TryHandleAsync(Request, CancellationToken) is { } TimerResponse)
        {
            return Result(TimerResponse, "succeeded", "timer");
        }
        var VolumeCommand = System.Text.RegularExpressions.Regex.Match(LanguageParser.Normalize(Request.Message),
            @"^(?:(?:set|turn|change) (?:the )?)?volume(?: (?:to|at))? (?<level>-?\d{1,3})(?<percent>\s*(?:percent|%))?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (VolumeCommand.Success)
        {
            var Level = int.Parse(VolumeCommand.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture);
            // Bare levels follow the familiar 0–10 voice-device scale; explicit percentages use 0–100.
            var Maximum = VolumeCommand.Groups["percent"].Length > 0 ? 100 : 10;
            if (Level < 0 || Level > Maximum) { return Result($"Volume must be between 0 and {Maximum}.", "invalid-request", "satellite-volume"); }
            if (Satellites is null || !Satellites.TryGet(Request.SatelliteId, out var Connection))
                return Result("There is no connected speaker to adjust.", "unavailable", "satellite-volume");
            if (!Satellites.State(Request.SatelliteId).Capabilities.VolumeControl)
                return Result("Voice volume control is unavailable on this speaker. Use its volume buttons or settings.", "unsupported", "satellite-volume");
            try
            {
                await Connection!.SendEventAsync(new("set-volume", (Level / (double)Maximum).ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken);
            }
            catch (Exception Error) when (Error is IOException or HttpRequestException or InvalidOperationException
                || Error is OperationCanceledException && !CancellationToken.IsCancellationRequested)
            { return Result("I could not confirm the volume command was sent. Please check the speaker.", "failed", "satellite-volume"); }
            return Result("Volume command sent.", "succeeded", "satellite-volume");
        }
        DeviceContext?.Expire();
        var CurrentControl = ControlRequest.Parse(Request.Message, DeviceContext);
        var ClassifierText = Request.Message;
        if (CurrentControl is null && DeviceContext?.LastAttempted is { } LastAttempt
            && System.Text.RegularExpressions.Regex.IsMatch(LanguageParser.Normalize(Request.Message), @"^(?:did you|have you) (?:turn|switch|set|dim|change)\b.*\b(?:them|it|those|these|both)\b"))
        {
            string Names(IEnumerable<string> Ids) => string.Join(" and ", Ids.Select(Id => Cache.Snapshot().Entities.FirstOrDefault(Entity => Entity.EntityId == Id)?.Name ?? Id));
            var Action = LastAttempt.Action switch { "turn_on" => "turn on", "turn_off" => "turn off", "set_fan_speed" => "set the speed of", _ => "set the brightness of" };
            var Amount = LastAttempt.BrightnessPercent is { } Percent ? " to " + Percent + " percent" : "";
            return Result(LastAttempt.Outcome == "completed" ? "The last confirmed action was to " + Action + " " + Names(LastAttempt.EntityIds) + Amount + "."
                : "I could not confirm the last command for " + Names(LastAttempt.EntityIds) + ". Please check those devices before trying again.",
                LastAttempt.Outcome == "completed" ? "succeeded" : "failed", "action-receipt");
        }
        if (DeviceContext is { References.Length: > 0 } && CurrentControl is null && ControlRequest.IsSelection(Request.Message))
        {
            if (LanguageParser.Noun(Request.Message).StartsWith("both", StringComparison.Ordinal) && DeviceContext.References.Length != 2)
                return Result("Which two devices do you mean?", "ambiguous", "clarification");
            if (DeviceContext.References.Length > 1 && LanguageParser.Noun(Request.Message) is "it" or "yes" or "that one")
                return Result("Which device do you mean: " + string.Join(" or ", DeviceContext.References.Select(Item => Item.Name)) + "?", "ambiguous", "clarification");
            DeviceContext.AwaitingAction = true;
            DeviceContext.UpdatedAt = DateTimeOffset.UtcNow;
            return Result("Do you want " + string.Join(" and ", DeviceContext.References.Select(Item => Item.Name)) + " turned on or off?", "ambiguous", "clarification");
        }
        if (DeviceContext is { References.Length: > 1 } && CurrentControl is null && LanguageParser.Normalize(Request.Message) is "on" or "off")
            return Result("Which devices should I change? Please name them or say both.", "ambiguous", "clarification");
        if (CurrentControl is not null && (ControlRequest.IsReference(CurrentControl.Target)
            || ControlRequest.Parse(Request.Message) is null))
        {
            var Targets = CurrentControl.Resolve(Cache.Snapshot(), DeviceContext);
            if (Targets.Length == 0) { return Result("Which device do you mean? Please name the devices and the action you want.", "ambiguous", "clarification"); }
            var Names = string.Join(" and ", Targets.Select(Item => Item.EntityId));
            ClassifierText = CurrentControl.Action switch
            {
                "turn_on" => "turn on " + Names,
                "turn_off" => "turn off " + Names,
                "set_fan_speed" => "set " + Names + " to " + CurrentControl.SpeedPercent + " percent",
                _ => "set " + Names + " to " + CurrentControl.Brightness + " percent"
            };
        }
        // An unrelated question consumes a pending clarification, so an older action
        // cannot be resurrected later. References remain available for explicit commands.
        if (DeviceContext is not null) { DeviceContext.Pending = null; DeviceContext.AwaitingAction = false; }
        IntentMatch? Intent;
        IntentDecision Decision;
        using (var Step = RunTracing.Start("Intent classification", "Match supported deterministic commands before selecting a device."))
        {
            Decision = await Classifier.MatchAsync(ClassifierText, CancellationToken);
            Intent = Decision.Match?.Intent;
            if (CurrentControl is null && Intent is { Kind: DirectIntentKind.TurnOn or DirectIntentKind.TurnOff or DirectIntentKind.SetBrightness or DirectIntentKind.SetFanSpeed })
                CurrentControl = new(Intent.Kind == DirectIntentKind.TurnOn ? "turn_on" : Intent.Kind == DirectIntentKind.TurnOff ? "turn_off" : Intent.Kind == DirectIntentKind.SetFanSpeed ? "set_fan_speed" : "set_brightness",
                    Intent.Target, Intent.BrightnessPercent, Intent.ExplicitArea, Intent.SpeedPercent);
            Step.Input(new { originalInput = Request.Message, normalizedInput = IntentClassifier.Normalize(Request.Message) }, false);
            Step.Output(new { matched = Decision.Status == "matched", rule = Decision.Match?.Definition.BuiltIn == true && Intent is not null ? Intent.MatchedRule : Decision.Match?.Definition.Id, intent = Intent?.Kind.ToString(),
                Intent?.Target, Intent?.BrightnessPercent, Intent?.SpeedPercent, Intent?.ExplicitArea, reason = Decision.Reason }, false);
            Step.Metadata(new { ruleId = Decision.Match?.Definition.Id, actionId = Decision.Match?.Definition.ActionId, integrationId = Decision.Match?.Definition.ActionId.Split('.')[0],
                pattern = Decision.Match?.Pattern, candidates = Decision.Candidates.Select(Item => Item.Definition.Id), Decision.Reason });
            Step.Complete(Decision.Status);
        }
        if (Decision.Match is not null) { await VoiceFeedback.EmitAsync("intent-match", CancellationToken); }
        if (Decision.Status is "ambiguous" or "invalid-request") { return Result(Decision.Reason, Decision.Status); }
        if (Decision.Match is { } Registered && Actions is not null && !Actions.CanExecute(Registered.Definition.ActionId))
        {
            return Result("This integration does not have an available action executor.", "unavailable");
        }
        if (Decision.Match is { Intent: null } Simple)
        {
            var Response = Actions is null ? new IntentResult("", "succeeded") : await Actions.ExecuteAsync(Simple, null, CancellationToken);
            return Result(Response.Outcome == "succeeded" ? IntentResponses.Render(Simple, Response.Response, Configuration ?? new ConfigurationBuilder().Build()) : Response.Response, Response.Outcome);
        }
        if (Intent is null)
        {
            if (LanguageModel is null) { return Result("I cannot handle that request yet.", "unmatched", "unhandled"); }
            try
            {
                var Response = await LanguageModel.RespondAsync(Request, History, CancellationToken, TraceId, OnText, DeviceContext);
                return Result(Response, "succeeded", "language-model");
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
            catch (ControlNotConfirmedException Error)
            {
                return Result(Error.Response ?? "I could not confirm the command completed. Please check the device or use its full name.", "failed", "language-model");
            }
            catch (DeviceSearchFailedException)
            {
                return Result("I couldn't complete the device search. That does not mean the devices are missing. Please try again.", "unavailable", "language-model");
            }
            catch (Exception Error) when (Error is HttpRequestException or OperationCanceledException or InvalidOperationException or System.IO.IOException or System.Text.Json.JsonException)
            {
                using (var Failure = RunTracing.Start("Error", "Language model failure", "LLM routing stopped; supported direct commands remain available."))
                {
                    Failure.Metadata(new { failureCategory = Error.GetType().Name,
                        responseFailure = (Error as Assister.Llm.InvalidLanguageModelResponseException)?.Reason });
                    Failure.Complete("failed");
                }
                Logger.LogWarning("Language model request failed ({FailureType}).", Error.GetType().Name);
                return Result(Error is Assister.Llm.InvalidLanguageModelResponseException
                    ? "The language model returned an invalid or incomplete response. Please try again."
                    : "The language model is unavailable. You can still use supported direct device commands.", "unavailable", "language-model");
            }
        }
        if (Intent.Kind == DirectIntentKind.SetBrightness && Intent.BrightnessPercent is not (>= 0 and <= 100))
        {
            return Result("Brightness must be between 0 and 100 percent.", "invalid-request");
        }

        if (Intent.Kind == DirectIntentKind.SetFanSpeed && Intent.SpeedPercent is not (>= 0 and <= 100))
        { return Result("Fan speed must be between 0 and 100 percent.", "invalid-request"); }

        var Snapshot = Cache.Snapshot();
        if (Snapshot.IsStale) { return Result("Home Assistant is unavailable. Please try again when it reconnects.", "unavailable"); }
        using (var Step = RunTracing.Start("Entity resolution", "Resolve the named device against the current Home Assistant cache and requested area."))
        {
            Resolution = Resolver.Resolve(Intent, Request.Area, Snapshot);
            Step.Input(new { Intent.Target, Intent.ExplicitArea, satelliteArea = Request.Area }, false);
            Step.Metadata(new { Resolution.EffectiveArea, Resolution.RequiredDomain, Resolution.Confidence });
            Step.Output(new { selected = Resolution.Entities.Select(Entity => new { Entity.EntityId, Entity.Name, Entity.AreaName }),
                alternatives = Resolution.Alternatives.Select(Entity => new { Entity.EntityId, Entity.Name, Entity.AreaName }) }, false);
            Step.Complete(Resolution.Entities.Count == 0 ? Resolution.Alternatives.Count > 0 ? "ambiguous" : "not-found" : "resolved");
        }
        if (Resolution.Entities.Count == 0)
        {
            var SpeechArtifact = System.Text.RegularExpressions.Regex.Match(Intent.Target, @"^[a-z],\s+(?<target>.+)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (Resolution.Alternatives.Count == 0 && SpeechArtifact.Success)
            {
                var Suggested = Resolver.Resolve(Intent with { Target = SpeechArtifact.Groups["target"].Value }, Request.Area, Snapshot);
                if (Suggested.Entities.Count > 0)
                    return Result("Did you mean " + string.Join(" and ", Suggested.Entities.Select(Entity => Entity.Name)) + "? Please repeat the command.", "ambiguous", "clarification");
            }
            if (DeviceContext is not null && Resolution.Alternatives.Count > 0)
            {
                DeviceContext.References = Resolution.Alternatives.Where(Entity => Entity.Domain is "light" or "switch")
                    .Take(10).Select(Entity => new DeviceReference(Entity.EntityId, Entity.Name)).ToArray();
                DeviceContext.Pending = CurrentControl;
                DeviceContext.UpdatedAt = DateTimeOffset.UtcNow;
            }
            return Result(Resolution.Alternatives.Count > 0 ? "Which device do you mean: " + string.Join(" or ", Resolution.Alternatives.Select(Entity => Entity.Name)) + "?" : "I could not find that device in the requested area.",
                Resolution.Alternatives.Count > 0 ? "ambiguous" : "not-found");
        }

        try
        {
            IntentResult Response;
            using (var Step = RunTracing.Start("Intent execution", "Execute the resolved intent or answer from cached state."))
            {
                Step.Input(new { action = Intent.Kind.ToString(), entities = Resolution.Entities.Select(Entity => Entity.EntityId), Intent.BrightnessPercent, Intent.SpeedPercent });
                if (DeviceContext is not null && CurrentControl is not null)
                    DeviceContext.LastAttempted = new(CurrentControl.Action, Resolution.Entities.Select(Entity => Entity.EntityId).ToArray(), "unconfirmed", DateTimeOffset.UtcNow, Intent.BrightnessPercent, Intent.SpeedPercent);
                Response = Actions is null ? await Handler.ExecuteAsync(Intent, Resolution, CancellationToken)
                    : await Actions.ExecuteAsync(Decision.Match!, Resolution, CancellationToken);
                Step.Output(new { Response.Response, Response.Outcome });
                Step.Complete(Response.Outcome);
            }
            if (DeviceContext is not null && Resolution.Entities.All(Entity => Entity.Domain is "light" or "switch"))
            {
                DeviceContext.References = Resolution.Entities.Take(10).Select(Entity => new DeviceReference(Entity.EntityId, Entity.Name)).ToArray();
                DeviceContext.UpdatedAt = DateTimeOffset.UtcNow;
                if (Response.Outcome == "succeeded" && CurrentControl is not null)
                    DeviceContext.LastCompleted = new(CurrentControl.Action, Resolution.Entities.Select(Entity => Entity.EntityId).ToArray(), DateTimeOffset.UtcNow, Intent.BrightnessPercent, Intent.SpeedPercent);
                if (CurrentControl is not null && DeviceContext.LastAttempted is { } Attempt)
                    DeviceContext.LastAttempted = Attempt with { Outcome = Response.Outcome == "succeeded" ? "completed" : Response.Outcome };
            }
            return Result(Response.Outcome == "succeeded" && Decision.Match is { } Matched
                ? IntentResponses.Render(Matched, Response.Response, Configuration ?? new ConfigurationBuilder().Build()) : Response.Response, Response.Outcome);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception Error) when (Error is HttpRequestException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException or System.IO.IOException)
        {
            using (var Failure = RunTracing.Start("Error", "Direct action failure", "Device action completion could not be confirmed."))
            {
                Failure.Metadata(new { failureCategory = Error.GetType().Name });
                Failure.Complete("failed");
            }
            Logger.LogWarning("Home Assistant request failed ({FailureType}).", Error.GetType().Name);
            return Result("I could not confirm the command completed. Please check the device before trying again.", "failed");
        }
    }
}
