namespace Assister.Contracts;

// Presentation and acknowledgement are always optional client policy.
public sealed record CreateNotification(string Kind, string Text, string IdempotencyKey,
    DateTimeOffset? DueAt = null, DateTimeOffset? ExpiresAt = null,
    string Importance = "notify", string? TargetClientId = null);
public sealed record ClientNotification(Guid Id, string Kind, string Text, DateTimeOffset CreatedAt,
    DateTimeOffset DueAt, DateTimeOffset ExpiresAt, string Importance, string? TargetClientId, string Status);
public sealed record NotificationAcknowledgement(string State);
public sealed record NotificationReceipt(Guid NotificationId, string ClientId, string State, DateTimeOffset Timestamp);
