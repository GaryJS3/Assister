using Assister.Contracts;

namespace Assister.Interactions;

// Only server-created conversation routes can resolve to an authenticated owner.
// Persist the binding so recurring reminders survive conversation-history retention.
public sealed class RichNotificationBridge(NotificationStore Notifications, InteractionStore Interactions,
    IConfiguration Configuration, ClientSignals Signals)
{
    public string? Owner(string? Route)
    {
        const string Prefix = "client-conversation-";
        if (Route is null || !Route.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(Route[Prefix.Length..], "N", out var Conversation)) return null;
        if (Notifications.RouteOwner(Route) is { } Existing) return Existing;
        var Owner = Interactions.ConversationOwner(Conversation);
        if (Owner is not null) Notifications.RegisterRoute(Route, Owner);
        return Owner;
    }

    public (string Id, string Name)[] Clients(string Route)
    {
        var OwnerId = Owner(Route);
        if (OwnerId is null) return [];
        var Registered = Signals.Clients(OwnerId);
        return Configuration.GetSection("RichClients:Clients").GetChildren()
            .Where(Client => !string.IsNullOrWhiteSpace(Client["Token"]) && (Client["Owner"] ?? Client.Key) == OwnerId)
            .Select(Client => (Id: Client.Key, Name: Registered.FirstOrDefault(Item => Item.ClientId == Client.Key)?.DeviceName ?? Client.Key))
            .OrderBy(Client => Client.Name, StringComparer.OrdinalIgnoreCase).ThenBy(Client => Client.Id, StringComparer.Ordinal).ToArray();
    }

    public bool Publish(string Route, string Kind, string Text, string Key, string Importance, string? Target = null)
    {
        var OwnerId = Owner(Route);
        if (OwnerId is null) return false;
        Notifications.Create(OwnerId, new(Kind, Text, Key, Importance: Importance, TargetClientId: Target));
        return true; // Durable acceptance, never a claim of display or playback.
    }
}
