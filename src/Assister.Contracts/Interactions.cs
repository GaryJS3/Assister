using System.Text.Json;

namespace Assister.Contracts;

public sealed record SubmitInteraction(string Message, string IdempotencyKey, IReadOnlyList<Guid>? AttachmentIds = null,
    Guid? AudioAttachmentId = null, bool Speak = false);
public sealed record ClientRegistration(string ClientType, string DeviceName, IReadOnlyList<string> Capabilities);
public sealed record RegisteredClient(string ClientId, string Owner, string ClientType, string DeviceName,
    IReadOnlyList<string> Capabilities, DateTimeOffset LastSeen, bool Online = true);
public sealed record DeviceRequest(Guid RequestId, Guid InteractionId, string TargetClientId, string Capability,
    JsonElement Parameters, DateTimeOffset Deadline);
public sealed record DeviceResponse(Guid RequestId, bool Success, JsonElement? Result = null, ProtocolError? Error = null);
public sealed record PlaybackReport(string State, Guid PlaybackId);
public sealed record ToneCue(string Name, string Url, DateTimeOffset ExpiresAt, string Placement = "immediate");
public sealed record ClientAttachment(Guid Id, string Name, string MimeType, long Size, string Source,
    DateTimeOffset CreatedAt, string Processing, string? ClientId = null);
public sealed record InputDocument(Guid AttachmentId, string Name, string Text, string? ClientId = null);
public sealed record ContextSelection(string Key, string Type, string Source, string Name, string Content,
    object Provenance, int ModelRound);
public sealed record InteractionContext(string Id, Guid InteractionId, string Type, string Source, string Name,
    string Content, JsonElement Provenance, IReadOnlyList<int> ModelRounds, bool Truncated);
public sealed record InteractionSnapshot(Guid Id, Guid ConversationId, string Status, string Input,
    string Response, DateTimeOffset CreatedAt, long LastSequence, Guid? RunId, bool CancelRequested);
public sealed record InteractionEvent(long Sequence, Guid EventId, Guid InteractionId, Guid ConversationId,
    DateTimeOffset Timestamp, string Type, JsonElement Data);
public sealed record ClientConversation(Guid Id, string Title, DateTimeOffset CreatedAt);
public sealed record ProtocolError(string Code, string Message, bool Recoverable = false);

// Execution instrumentation is transport independent. Native clients consume the resulting persisted events.
public static class InteractionFeedback
{
    private static readonly AsyncLocal<Action<string, object>?> Current = new();
    public static IDisposable Observe(Action<string, object> Observer)
    {
        var Previous = Current.Value;
        Current.Value = Observer;
        return new Subscription(() => Current.Value = Previous);
    }
    public static void Emit(string Type, object Data) => Current.Value?.Invoke(Type, Data);
    private sealed class Subscription(Action Restore) : IDisposable
    {
        public void Dispose() => Restore();
    }
}
