namespace Assister.Contracts;

public sealed record UserRequest(string Message, string SatelliteId = "test", string? Area = null, Guid? ConversationId = null, bool NewConversation = false,
    IReadOnlyList<InputDocument>? Documents = null, string? DeviceId = null);

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

public static class RequestInputLimits
{
    public static bool ValidDocuments(UserRequest Request) => Request.Documents is not { } Items
        || Items.Count <= 8 && Items.All(Item => Item is not null && Item.Name is { Length: > 0 and <= 128 }
            && Item.Text is not null && !Item.Text.Contains('\0')) && Items.Sum(Item => Item.Text.Length) <= 12000;
}
