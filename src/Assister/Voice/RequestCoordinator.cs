using Assister.Diagnostics;
using System.Diagnostics;
using Assister.Contracts;
using Assister.Intents;
using Assister.Modules.HomeAssistant;
using Assister.Llm;
using Assister.Modules.Timers;

namespace Assister.Voice;

public sealed class RequestCoordinator(IIntentEngine Classifier, IEntityResolver Resolver, DirectIntentHandler Handler,
    HomeAssistantStateCache Cache, ILogger<RequestCoordinator> Logger, ToolLoop? LanguageModel = null, TimerIntentHandler? Timers = null, RunStore? Diagnostics = null,
    IConfiguration? Configuration = null, IntegrationActionDispatcher? Actions = null, Assister.Satellites.SatelliteManager? Satellites = null) : IRequestCoordinator
{
    public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken) => ProcessWithHistoryAsync(Request, [], CancellationToken);

    public async Task<RequestResult> ProcessWithHistoryAsync(UserRequest Request, IReadOnlyList<LlmMessage> History, CancellationToken CancellationToken,
        Func<string, CancellationToken, Task>? OnText = null)
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
            || Request.SatelliteId.Length > 128 || Request.Area?.Length > 128)
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
        IntentMatch? Intent;
        IntentDecision Decision;
        using (var Step = RunTracing.Start("Intent classification", "Match supported deterministic commands before selecting a device."))
        {
            Decision = await Classifier.MatchAsync(Request.Message, CancellationToken);
            Intent = Decision.Match?.Intent;
            Step.Input(new { originalInput = Request.Message, normalizedInput = IntentClassifier.Normalize(Request.Message) }, false);
            Step.Output(new { matched = Decision.Status == "matched", rule = Decision.Match?.Definition.BuiltIn == true && Intent is not null ? Intent.MatchedRule : Decision.Match?.Definition.Id, intent = Intent?.Kind.ToString(),
                Intent?.Target, Intent?.BrightnessPercent, Intent?.ExplicitArea, reason = Decision.Reason }, false);
            Step.Metadata(new { ruleId = Decision.Match?.Definition.Id, actionId = Decision.Match?.Definition.ActionId, integrationId = Decision.Match?.Definition.ActionId.Split('.')[0],
                pattern = Decision.Match?.Pattern, candidates = Decision.Candidates.Select(Item => Item.Definition.Id), Decision.Reason });
            Step.Complete(Decision.Status);
        }
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
                return Result(await LanguageModel.RespondAsync(Request, History, CancellationToken, TraceId, OnText), "succeeded", "language-model");
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
            catch (ControlNotConfirmedException)
            {
                return Result("I could not confirm the command completed. Please check the device or use its full name.", "failed", "language-model");
            }
            catch (Exception Error) when (Error is HttpRequestException or OperationCanceledException or InvalidOperationException or System.IO.IOException or System.Text.Json.JsonException)
            {
                using (var Failure = RunTracing.Start("Error", "Language model failure", "LLM routing stopped; supported direct commands remain available."))
                {
                    Failure.Metadata(new { failureCategory = Error.GetType().Name });
                    Failure.Complete("failed");
                }
                Logger.LogWarning("Language model request failed ({FailureType}).", Error.GetType().Name);
                return Result("The language model is unavailable. You can still use supported direct device commands.", "unavailable", "language-model");
            }
        }
        if (Intent.Kind == DirectIntentKind.SetBrightness && Intent.BrightnessPercent is not (>= 0 and <= 100))
        {
            return Result("Brightness must be between 0 and 100 percent.", "invalid-request");
        }

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
            return Result(Resolution.Alternatives.Count > 0 ? "Which device do you mean? Please use its full name or a more specific area." : "I could not find that device in the requested area.",
                Resolution.Alternatives.Count > 0 ? "ambiguous" : "not-found");
        }

        try
        {
            IntentResult Response;
            using (var Step = RunTracing.Start("Intent execution", "Execute the resolved intent or answer from cached state."))
            {
                Step.Input(new { action = Intent.Kind.ToString(), entities = Resolution.Entities.Select(Entity => Entity.EntityId), Intent.BrightnessPercent });
                Response = Actions is null ? await Handler.ExecuteAsync(Intent, Resolution, CancellationToken)
                    : await Actions.ExecuteAsync(Decision.Match!, Resolution, CancellationToken);
                Step.Output(new { Response.Response, Response.Outcome });
                Step.Complete(Response.Outcome);
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
