using System.Diagnostics;
using Assister.Contracts;
using Assister.Intents;
using Assister.Modules.HomeAssistant;

namespace Assister.Voice;

public sealed class RequestCoordinator(IntentClassifier Classifier, IEntityResolver Resolver, DirectIntentHandler Handler,
    HomeAssistantStateCache Cache, ILogger<RequestCoordinator> Logger) : IRequestCoordinator
{
    public async Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken)
    {
        var TraceId = Guid.NewGuid();
        var Clock = Stopwatch.StartNew();
        using var Scope = Logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = TraceId, ["SatelliteId"] = Request.SatelliteId });
        EntityResolutionResult? Resolution = null;
        RequestResult Result(string Text, string Outcome, string HandledBy = "direct-intent")
        {
            Logger.LogInformation("Request completed: {Outcome}, {DurationMilliseconds} ms.", Outcome, Clock.Elapsed.TotalMilliseconds);
            return new(Text, Request.ConversationId, HandledBy, TraceId, Outcome,
                Resolution?.Entities.Select(Entity => Entity.EntityId).ToArray() ?? [], Resolution?.Confidence, Clock.Elapsed.TotalMilliseconds);
        }

        if (string.IsNullOrWhiteSpace(Request.Message) || Request.Message.Length > 1000 || string.IsNullOrWhiteSpace(Request.SatelliteId)
            || Request.SatelliteId.Length > 128 || Request.Area?.Length > 128)
        {
            return Result("Please send a message of 1 to 1000 characters and a valid satellite identifier.", "invalid-request", "validation");
        }

        var Intent = Classifier.Classify(Request.Message);
        if (Intent is null) { return Result("I cannot handle that request yet.", "unmatched", "unhandled"); }
        if (Intent.Kind == DirectIntentKind.SetBrightness && Intent.BrightnessPercent is not (>= 0 and <= 100))
        {
            return Result("Brightness must be between 0 and 100 percent.", "invalid-request");
        }

        var Snapshot = Cache.Snapshot();
        if (Snapshot.IsStale) { return Result("Home Assistant is unavailable. Please try again when it reconnects.", "unavailable"); }
        Resolution = Resolver.Resolve(Intent, Request.Area, Snapshot);
        if (Resolution.Entities.Count == 0)
        {
            return Result(Resolution.Alternatives.Count > 0 ? "Which device do you mean? Please use its full name or a more specific area." : "I could not find that device in the requested area.",
                Resolution.Alternatives.Count > 0 ? "ambiguous" : "not-found");
        }

        try
        {
            var Response = await Handler.ExecuteAsync(Intent, Resolution, CancellationToken);
            return Result(Response.Response, Response.Outcome);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception Error) when (Error is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            Logger.LogWarning("Home Assistant request failed ({FailureType}).", Error.GetType().Name);
            return Result("I could not confirm the command completed. Please check the device before trying again.", "failed");
        }
    }
}
