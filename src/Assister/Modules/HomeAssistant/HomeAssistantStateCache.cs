using System.Text.Json;

namespace Assister.Modules.HomeAssistant;

public sealed record HomeAssistantEntity(string EntityId, string Name, string? AreaId, string? AreaName, JsonElement State)
{
    public string Domain => EntityId.Split('.')[0];
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? DeviceName { get; init; }
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
    private Dictionary<string, JsonElement> States = new();
    private JsonElement EntityRegistry;
    private JsonElement DeviceRegistry;
    private JsonElement AreaRegistry;
    private JsonElement Services;
    private bool IsStale = true;
    private long StateEventCount;
    private DateTimeOffset? LastStateEventAt;

    public void Load(JsonElement NewStates, JsonElement NewServices, JsonElement Entities, JsonElement Devices, JsonElement Areas)
    {
        lock (Gate)
        {
            States = NewStates.EnumerateArray().ToDictionary(Item => Item.GetProperty("entity_id").GetString()!, Item => Item.Clone());
            Services = NewServices.Clone();
            EntityRegistry = Entities.Clone();
            DeviceRegistry = Devices.Clone();
            AreaRegistry = Areas.Clone();
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
            if (State.ValueKind == JsonValueKind.Null) { States.Remove(Id); }
            else { States[Id] = State.Clone(); }
            StateEventCount++;
            LastStateEventAt = DateTimeOffset.UtcNow;
        }
    }

    public HomeAssistantCacheStatus Status()
    {
        lock (Gate)
        {
            return new(IsStale, States.Count, StateEventCount, LastStateEventAt);
        }
    }

    public HomeAssistantSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new(IsStale, HomeAssistantEntityIndex.Build(States.Values, EntityRegistry, DeviceRegistry, AreaRegistry), Services);
        }
    }
}

public static class HomeAssistantEntityIndex
{
    private static string? Text(JsonElement Item, string Key) => Item.ValueKind == JsonValueKind.Object && Item.TryGetProperty(Key, out var Value) && Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
    private static Dictionary<string, JsonElement> Map(JsonElement Items, string Key) => Items.ValueKind == JsonValueKind.Array ? Items.EnumerateArray().ToDictionary(Item => Text(Item, Key)!, Item => Item) : new();

    public static IReadOnlyList<HomeAssistantEntity> Build(IEnumerable<JsonElement> States, JsonElement Entities, JsonElement Devices, JsonElement Areas)
    {
        var EntityMap = Map(Entities, "entity_id");
        var DeviceMap = Map(Devices, "id");
        var AreaMap = Map(Areas, "area_id");
        return States.Select(State =>
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
                Aliases = Aliases,
                DeviceName = Text(Device, "name_by_user") ?? Text(Device, "name")
            };
        }).ToArray();
    }
}
