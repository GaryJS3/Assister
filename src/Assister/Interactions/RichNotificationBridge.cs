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

    public string[] Groups(string? Source)
    {
        var OwnerId = Owner(Source);
        var Memberships = Notifications.SatelliteGroups();
        return (OwnerId is not null
            ? Memberships.Where(Item => Item.Owner == OwnerId).SelectMany(Item => Item.Groups)
                .Concat(Signals.Clients(OwnerId).SelectMany(Client => Client.Groups ?? []))
            : Memberships.Where(Item => Item.SatelliteId == Source).SelectMany(Item => Item.Groups))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string? GroupOwner(string? Source, string Group)
    {
        if (!Groups(Source).Contains(Group, StringComparer.OrdinalIgnoreCase)) return null;
        if (Owner(Source) is { } OwnerId) return OwnerId;
        // A satellite may originate notices only for groups it belongs to. Ambiguous owners require a rich client.
        var Owners = Notifications.SatelliteGroups().Where(Item => Item.SatelliteId == Source
            && Item.Groups.Contains(Group, StringComparer.OrdinalIgnoreCase)).Select(Item => Item.Owner).Distinct().ToArray();
        return Owners.Length == 1 ? Owners[0] : null;
    }

    public bool PublishGroup(string Source, string Group, string Kind, string Text, string Key, string Importance)
    {
        if (GroupOwner(Source, Group) is not { } OwnerId) return false;
        Notifications.Create(OwnerId, new(Kind, Text, Key, Importance: Importance, TargetGroup: Group));
        return true;
    }
}
