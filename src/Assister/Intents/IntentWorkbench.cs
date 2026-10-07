using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Modules.HomeAssistant;
using Assister.Voice;

namespace Assister.Intents;

public sealed record IntentInspectRequest(string Text, string? Area = null, IntentDefinition? Draft = null);
public sealed record IntentExecuteRequest(string Text, string? Area, string Fingerprint);
public sealed record IntentPreview(string Text, string Normalized, string MatchStatus, string Outcome, string Reason,
    IntentCandidate[] Candidates, IntentActionDescriptor? Action, string[] EntityIds, object[] Alternatives,
    string? Response, bool StateChanging, bool CanExecute, string Fingerprint);
public sealed record IntentTestResult(IntentExample Example, bool Passed, string[] Failures, IntentPreview Actual);

public sealed class IntentWorkbench(IIntentEngine Engine, IntentStore Store, IntentClassifier Native,
    IEntityResolver Resolver, HomeAssistantStateCache Cache, DirectIntentHandler Handler, IConfiguration Configuration, IntentActionRegistry Registry, IntegrationActionDispatcher Dispatcher)
{
    private async Task<(IntentPreview Preview, IntentCandidate? Match, EntityResolutionResult? Resolution)> PlanAsync(IntentInspectRequest Request, CancellationToken Token)
    {
        if (Request.Area?.Length > 128) { throw new ArgumentException("Area is limited to 128 characters."); }
        IntentDecision Decision;
        if (Request.Draft is not null)
        {
            IntentStore.Validate(Request.Draft, Registry);
            var Definitions = await Store.DefinitionsAsync(Token);
            Decision = IntentMatching.Match(Request.Text, Definitions.Where(Row => Row.Id != Request.Draft.Id).Append(Request.Draft).ToArray(), Native, Registry);
        }
        else { Decision = await Engine.MatchAsync(Request.Text, Token); }
        var Match = Decision.Match;
        var Outcome = Decision.Status == "unmatched" ? "llm-fallback" : Decision.Status;
        var Reason = Decision.Reason;
        EntityResolutionResult? Resolution = null;
        string? Response = null;
        var Action = Match is null ? null : Registry.Get(Match.Definition.ActionId);
        var Changing = Action?.StateChanging == true;
        if (Match is not null)
        {
            if (!Dispatcher.CanExecute(Match.Definition.ActionId))
            {
                Outcome = "unsupported";
                Reason = "This integration has no registered intent executor.";
            }
            else if (Match.Intent is null)
            {
                Outcome = "ready";
                Response = IntentResponses.Render(Match, "", Configuration);
            }
            else
            {
                var Snapshot = Cache.Snapshot();
                Resolution = Resolver.Resolve(Match.Intent, Request.Area, Snapshot);
                Outcome = Snapshot.IsStale ? "unavailable" : Resolution.Entities.Count == 0
                    ? Resolution.Alternatives.Count > 0 ? "ambiguous" : "not-found"
                    : Resolution.Entities.Any(Entity => Entity.IsUnavailable) ? "unavailable"
                    : Match.Intent.Kind == DirectIntentKind.SetBrightness && Resolution.Entities.Any(Entity => !Entity.SupportsBrightness) ? "unsupported" : Match.Intent.Kind == DirectIntentKind.SetFanSpeed && Resolution.Entities.Any(Entity => !Entity.SupportsFanSpeed) ? "unsupported" : "ready";
                Reason = Outcome switch
                {
                    "unavailable" => "Home Assistant or the resolved device is unavailable.", "ambiguous" => "The device target needs clarification.",
                    "not-found" => "No matching device was found in the requested area.", "unsupported" => "The device does not support the requested percentage control.",
                    _ => "The resolved action is ready. No command has been sent."
                };
                if (Outcome == "ready")
                {
                    // Query handlers are read-only. Mutating handlers are never called during inspection.
                    var Expected = Changing ? Match.Intent.Kind == DirectIntentKind.SetBrightness
                        ? $"Set to {Match.Intent.BrightnessPercent} percent." : "Done."
                        : (await Handler.ExecuteAsync(Match.Intent, Resolution, Token)).Response;
                    Response = IntentResponses.Render(Match, Expected, Configuration);
                }
            }
        }
        var Ids = Resolution?.Entities.Select(Entity => Entity.EntityId).ToArray() ?? [];
        var Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Request.Text, Request.Area, Outcome, rule = Match?.Definition, intent = Match?.Intent, targets = Ids
        }))));
        var Preview = new IntentPreview(Request.Text, Decision.Normalized, Decision.Status, Outcome, Reason,
            Decision.Candidates, Action, Ids,
            Resolution?.Alternatives.Select(Entity => (object)new { Entity.EntityId, Entity.Name, Entity.AreaName }).ToArray() ?? [],
            Response, Changing, Outcome == "ready" && Request.Draft is null, Fingerprint);
        return (Preview, Match, Resolution);
    }

    public async Task<IntentPreview> InspectAsync(IntentInspectRequest Request, CancellationToken Token) => (await PlanAsync(Request, Token)).Preview;

    public async Task<object> ExecuteAsync(IntentExecuteRequest Request, RunStore Runs, CancellationToken Token)
    {
        var Plan = await PlanAsync(new(Request.Text, Request.Area), Token);
        if (!Plan.Preview.CanExecute || Plan.Preview.Fingerprint != Request.Fingerprint)
        {
            throw new InvalidOperationException("The plan changed or cannot execute. Inspect the request again.");
        }
        using var Run = RunTracing.BeginRun(Runs, "intent-workbench", "intent-workbench", Request.Area, Text: Request.Text);
        using var Step = RunTracing.Start("Intent execution", "Execute only the inspected deterministic handler; LLM fallback is disabled.");
        Step.Input(new { Plan.Match!.Definition.Id, Plan.Match.Definition.ActionId, Plan.Preview.Action!.IntegrationId, Plan.Preview.EntityIds, Plan.Match.Intent });
        try
        {
            var Result = await Dispatcher.ExecuteAsync(Plan.Match!, Plan.Resolution, Token);
            var Text = Result.Outcome == "succeeded" ? IntentResponses.Render(Plan.Match, Result.Response, Configuration) : Result.Response;
            var Spoken = VoiceFormatter.Format(Text);
            Step.Output(new { response = Text, Result.Outcome });
            Step.Complete(Result.Outcome);
            RunTracing.Response(Text, Spoken, Result.Outcome, "direct-intent", null);
            Run.Complete(Result.Outcome);
            return new { response = Text, Result.Outcome, runId = RunTracing.RunId, Plan.Preview.EntityIds };
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
        catch (Exception Error) when (Error is InvalidOperationException or HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            const string Response = "I could not confirm the command completed. Please check the device before trying again.";
            Step.Metadata(new { failureCategory = Error.GetType().Name });
            Step.Complete("failed");
            RunTracing.Response(Response, Response, "failed", "direct-intent", null);
            Run.Complete("failed");
            return new { response = Response, outcome = "failed", runId = RunTracing.RunId, Plan.Preview.EntityIds };
        }
    }

    public async Task<IntentTestResult[]> TestAsync(CancellationToken Token)
    {
        var Results = new List<IntentTestResult>();
        foreach (var Example in await Store.ExamplesAsync(Token))
        {
            var Actual = await InspectAsync(new(Example.Text, Example.Area), Token);
            var Failures = new List<string>();
            if (Actual.MatchStatus != Example.MatchStatus) { Failures.Add($"Expected {Example.MatchStatus}, got {Actual.MatchStatus}."); }
            var Matched = Actual.MatchStatus == "matched" ? Actual.Candidates.FirstOrDefault() : null;
            if (Example.ExpectedRuleId is not null && Matched?.Definition.Id != Example.ExpectedRuleId) { Failures.Add("The expected rule did not match uniquely."); }
            if (Example.ExpectedTarget is not null && Matched?.Intent?.Target != Example.ExpectedTarget) { Failures.Add("The target slot differs."); }
            if (Example.ExpectedBrightness is not null && Matched?.Intent?.BrightnessPercent != Example.ExpectedBrightness) { Failures.Add("The brightness slot differs."); }
            Results.Add(new(Example, Failures.Count == 0, Failures.ToArray(), Actual));
        }
        return Results.ToArray();
    }
}
