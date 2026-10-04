namespace Assister.Contracts;

public sealed record UserRequest(string Message, string SatelliteId = "test", string? Area = null, Guid? ConversationId = null);

public sealed record RequestResult(string Response, Guid? ConversationId, string HandledBy, Guid TraceId,
    string Outcome, IReadOnlyList<string> EntityIds, double? ResolutionConfidence, double DurationMilliseconds);

public interface IRequestCoordinator
{
    Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken);
}
