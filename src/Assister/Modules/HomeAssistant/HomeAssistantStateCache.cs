using System.Text.Json;

namespace Assister.Modules.HomeAssistant;

public sealed record HomeAssistantEntity(string EntityId, string Name, string? AreaId, string? AreaName, JsonElement State)
{
    public string Domain => EntityId.Split('.')[0];
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? DeviceName { get; init; }
    public string? DeviceId { get; init; }
    public string? Platform { get; init; }
    public string? OriginalName { get; init; }
    public bool SupportsFanSpeed => Domain == "fan" && State.GetProperty("attributes").TryGetProperty("supported_features", out var Features)
        && Features.ValueKind == JsonValueKind.Number && Features.TryGetInt32(out var Bits) && (Bits & 1) != 0;
    public double? FanSpeedPercent => Domain == "fan" && State.GetProperty("attributes").TryGetProperty("percentage", out var Value)
        && Value.ValueKind == JsonValueKind.Number && Value.TryGetDouble(out var Percent) && double.IsFinite(Percent) && Percent is >= 0 and <= 100 ? Percent : null;
    public double? FanPercentageStep => Domain == "fan" && State.GetProperty("attributes").TryGetProperty("percentage_step", out var Value)
        && Value.ValueKind == JsonValueKind.Number && Value.TryGetDouble(out var Step) && double.IsFinite(Step) && Step is > 0 and <= 100 ? Step : null;
    public bool IsUnavailable => State.GetProperty("state").GetString() is "unavailable" or "unknown";
    public bool IsTemperature => Domain == "sensor" && State.GetProperty("attributes").TryGetProperty("device_class", out var Class)
        && Class.ValueKind == JsonValueKind.String && Class.GetString() == "temperature";
    public bool SupportsBrightness => Domain == "light" && State.GetProperty("attributes").TryGetProperty("supported_color_modes", out var Modes)
        && Modes.ValueKind == JsonValueKind.Array && Modes.EnumerateArray().Any(Mode => Mode.ValueKind == JsonValueKind.String
            && Mode.GetString() is "brightness" or "color_temp" or "hs" or "xy" or "rgb" or "rgbw" or "rgbww" or "white");
}
public sealed record HomeAssistantSnapshot(bool IsStale, IReadOnlyList<HomeAssistantEntity> Entities, JsonElement Services);
public sealed record HomeAssistantCacheStatus(bool IsStale, int EntityCount, long StateEventCount, DateTimeOffset? LastStateEventAt);

public sealed class HomeAssistantStateCache
{
    private readonly object Gate = new();
    private Dictionary<string, HomeAssistantEntity> Entities = new();
    private HomeAssistantEntityIndex Index = new(default, default, default);
    private IReadOnlyList<HomeAssistantEntity>? SnapshotEntities;
    private JsonElement Services;
    private bool IsStale = true;
    private long StateEventCount;
    private DateTimeOffset? LastStateEventAt;

    public void Load(JsonElement NewStates, JsonElement NewServices, JsonElement Entities, JsonElement Devices, JsonElement Areas)
    {
        lock (Gate)
        {
            var NewIndex = new HomeAssistantEntityIndex(Entities, Devices, Areas);
            var NewEntities = NewStates.EnumerateArray().Select(State => NewIndex.Resolve(State.Clone()))
                .ToDictionary(Entity => Entity.EntityId);
            Index = NewIndex;
            this.Entities = NewEntities;
            SnapshotEntities = null;
            Services = NewServices.Clone();
        }
    }

    public void UpdateRegistries(JsonElement Entities, JsonElement Devices, JsonElement Areas)
    {
        lock (Gate)
        {
            var NewIndex = new HomeAssistantEntityIndex(Entities, Devices, Areas);
            var NewEntities = this.Entities.Values.Select(Entity => NewIndex.Resolve(Entity.State))
                .ToDictionary(Entity => Entity.EntityId);
            Index = NewIndex;
            this.Entities = NewEntities;
            SnapshotEntities = null;
        }
    }

    public void SetStale(bool Value)
    {
        lock (Gate) { IsStale = Value; }
    }

    public void ApplyEvent(JsonElement Data)
    {
        lock (Gate)
        {
            var Id = Data.GetProperty("entity_id").GetString()!;
            var State = Data.GetProperty("new_state");
            if (State.ValueKind == JsonValueKind.Null) { Entities.Remove(Id); }
            else { Entities[Id] = Index.Resolve(State.Clone()); }
            SnapshotEntities = null;
            StateEventCount++;
            LastStateEventAt = DateTimeOffset.UtcNow;
        }
    }

    public HomeAssistantCacheStatus Status()
    {
        lock (Gate)
        {
            return new(IsStale, Entities.Count, StateEventCount, LastStateEventAt);
        }
    }

    public HomeAssistantSnapshot Snapshot()
    {
        lock (Gate)
        {
            // Readers share an immutable view until an event or registry reload changes it.
            // State events resolve only the changed entity against the existing registry maps.
            SnapshotEntities ??= Array.AsReadOnly(Entities.Values.ToArray());
            return new(IsStale, SnapshotEntities, Services);
        }
    }
}

public sealed class HomeAssistantEntityIndex
{
    private readonly Dictionary<string, JsonElement> EntityMap;
    private readonly Dictionary<string, JsonElement> DeviceMap;
    private readonly Dictionary<string, JsonElement> AreaMap;

    public HomeAssistantEntityIndex(JsonElement Entities, JsonElement Devices, JsonElement Areas)
    {
        EntityMap = Map(Entities, "entity_id");
        DeviceMap = Map(Devices, "id");
        AreaMap = Map(Areas, "area_id");
    }

    private static string? Text(JsonElement Item, string Key) => Item.ValueKind == JsonValueKind.Object && Item.TryGetProperty(Key, out var Value) && Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
    private static Dictionary<string, JsonElement> Map(JsonElement Items, string Key) => Items.ValueKind == JsonValueKind.Array ? Items.Clone().EnumerateArray().ToDictionary(Item => Text(Item, Key)!, Item => Item) : new();

    public static IReadOnlyList<HomeAssistantEntity> Build(IEnumerable<JsonElement> States, JsonElement Entities, JsonElement Devices, JsonElement Areas)
    {
        var Index = new HomeAssistantEntityIndex(Entities, Devices, Areas);
        return Array.AsReadOnly(States.Select(Index.Resolve).ToArray());
    }

    public HomeAssistantEntity Resolve(JsonElement State)
    {
        var Id = Text(State, "entity_id")!;
        EntityMap.TryGetValue(Id, out var Entity);
        DeviceMap.TryGetValue(Text(Entity, "device_id") ?? "", out var Device);
        var AreaId = Text(Entity, "area_id") ?? Text(Device, "area_id");
        AreaMap.TryGetValue(AreaId ?? "", out var Area);
        var Name = Text(Entity, "name") ?? Text(State.GetProperty("attributes"), "friendly_name") ?? Text(Entity, "original_name") ?? Id;
        var Aliases = Entity.ValueKind == JsonValueKind.Object && Entity.TryGetProperty("aliases", out var Values)
            && Values.ValueKind == JsonValueKind.Array ? Values.EnumerateArray().Where(Value => Value.ValueKind == JsonValueKind.String).Select(Value => Value.GetString()!).ToArray() : [];
        return new HomeAssistantEntity(Id, Name, AreaId, Text(Area, "name"), State)
        {
            Aliases = Array.AsReadOnly(Aliases),
            DeviceId = Text(Entity, "device_id"),
            Platform = Text(Entity, "platform"),
            OriginalName = Text(Entity, "original_name"),
            DeviceName = Text(Device, "name_by_user") ?? Text(Device, "name")
        };
    }
}
