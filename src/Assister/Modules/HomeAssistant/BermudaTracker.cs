using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assister.Modules.HomeAssistant;

public sealed record TrackerLocation(string EntityId, string Name, string? CurrentArea, string? LastSeenArea,
    bool CurrentlySeen, DateTimeOffset? LastUpdatedAt, double? LastUpdatedSecondsAgo, string Response);

public static class BermudaTracker
{
    private static string? Text(JsonElement Value, string Key) => Value.ValueKind == JsonValueKind.Object
        && Value.TryGetProperty(Key, out var Item) && Item.ValueKind == JsonValueKind.String ? Item.GetString() : null;
    private static string Normalize(string Value) => Regex.Replace(Value.ToLowerInvariant().Replace("’", "").Replace("'", ""),
        @"[^a-z0-9]+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim();
    private static string Role(HomeAssistantEntity Entity) => Normalize(Entity.OriginalName ?? Entity.Name);
    public static bool IsArea(HomeAssistantEntity Entity) => Entity.Platform == "bermuda" && Entity.Domain == "sensor"
        && (Role(Entity) == "area" || Role(Entity).EndsWith(" area", StringComparison.Ordinal));
    private static bool IsLastArea(HomeAssistantEntity Entity) => Entity.Platform == "bermuda" && Entity.Domain == "sensor"
        && (Role(Entity) == "area last seen" || Role(Entity).EndsWith(" area last seen", StringComparison.Ordinal));
    private static string Label(HomeAssistantEntity Entity) => Entity.DeviceName
        ?? Regex.Replace(Entity.Name, @"\s+Area$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static HomeAssistantEntity[] Find(HomeAssistantSnapshot Snapshot, string Query)
    {
        var Target = Normalize(Assister.Intents.LanguageParser.Noun(Query));
        return Snapshot.Entities.Where(IsArea).Where(Entity => new[] { Entity.EntityId, Entity.Name, Label(Entity) }
            .Concat(Entity.Aliases).Any(Name => Normalize(Name) == Target)).ToArray();
    }
    private static string? Area(HomeAssistantEntity? Entity)
    {
        var Value = Entity is null ? null : Text(Entity.State, "state");
        return string.IsNullOrWhiteSpace(Value) || Value.ToLowerInvariant() is "unknown" or "unavailable" or "not_home" or "none" ? null : Value;
    }
    private static DateTimeOffset? Timestamp(string? Value, DateTimeOffset Now) => Value is not null
        && (Value.EndsWith('Z') || Regex.IsMatch(Value, @"[+-]\d{2}:\d{2}$"))
        && DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var Time) && Time <= Now ? Time : null;
    private static string Ago(TimeSpan Age) => Age.TotalMinutes < 1 ? "less than a minute ago"
        : Age.TotalHours < 1 ? $"about {(int)Age.TotalMinutes} minute{((int)Age.TotalMinutes == 1 ? "" : "s")} ago"
        : Age.TotalDays < 1 ? $"about {(int)Age.TotalHours} hour{((int)Age.TotalHours == 1 ? "" : "s")} ago"
        : $"about {(int)Age.TotalDays} day{((int)Age.TotalDays == 1 ? "" : "s")} ago";
    public static TrackerLocation Read(HomeAssistantSnapshot Snapshot, HomeAssistantEntity Entity, DateTimeOffset? Clock = null)
    {
        if (Snapshot.IsStale) { throw new InvalidOperationException("Home Assistant is unavailable."); }
        var Now = Clock ?? DateTimeOffset.UtcNow;
        // Registry device IDs pair sensors even after users rename them. Never pair by a similar name.
        var Related = Entity.DeviceId is null ? new[] { Entity } : Snapshot.Entities
            .Where(Item => Item.Platform == "bermuda" && Item.DeviceId == Entity.DeviceId).ToArray();
        var LastAreas = Related.Where(IsLastArea).ToArray();
        var Current = Area(Entity);
        var Last = Current is null && LastAreas.Length == 1 ? Area(LastAreas[0]) : null;
        // Report when the sensor supplying the location was updated, not when BLE detected the device.
        var LocationSensor = Last is not null ? LastAreas[0] : Entity;
        var Updated = Timestamp(Text(LocationSensor.State, "last_updated"), Now);
        var Name = Label(Entity);
        var Response = Current is not null ? $"{Name}: currently in {Current}."
            : Text(Entity.State, "state") == "unavailable" ? $"{Name}: the tracker sensor is unavailable; its current location cannot be confirmed."
            : $"{Name}: no longer being seen in an area.";
        if (Last is not null) { Response += $" Last seen area: {Last}."; }
        else if (Current is null) { Response += " The last seen area is unavailable."; }
        Response += Updated is not null ? $" Last updated {Ago(Now - Updated.Value)}."
            : " The last update time is unavailable.";
        return new(Entity.EntityId, Name, Current, Last, Current is not null, Updated,
            Updated is null ? null : (Now - Updated.Value).TotalSeconds, Response);
    }
}
