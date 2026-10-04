namespace Assister.Contracts;

public sealed record UserRequest(string Message, string SatelliteId = "test", string? Area = null, Guid? ConversationId = null, bool NewConversation = false);

public sealed record RequestResult(string Response, Guid? ConversationId, string HandledBy, Guid RunId,
    string Outcome, IReadOnlyList<string> EntityIds, double? ResolutionConfidence, double DurationMilliseconds, string? SpokenResponse = null)
{
    // Compatibility for existing clients and audit columns. This is the Assister interaction ID, never an Activity trace ID.
    public Guid TraceId => RunId;
}

public interface IRequestCoordinator
{
    Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken);
}

public interface IStreamingRequestCoordinator : IRequestCoordinator
{
    Task<RequestResult> ProcessStreamingAsync(UserRequest Request,
        Func<string, CancellationToken, Task> OnText, CancellationToken CancellationToken);
}
