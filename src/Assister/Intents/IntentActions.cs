namespace Assister.Intents;

public sealed record IntentActionInput(string Name, string Label, string Type, bool Required);
public sealed record IntegrationDescriptor(string Id, string Name, string Description, bool BuiltIn);
public sealed record IntentActionDescriptor(string Id, string IntegrationId, string IntegrationName, string Name,
    bool StateChanging, IntentActionInput[] Inputs, DirectIntentKind? DeviceIntent = null);

// Integrations register their actions here; the editor consumes the same catalog as matching.
public interface IIntentActionProvider
{
    IntegrationDescriptor Integration { get; }
    IReadOnlyList<IntentActionDescriptor> Actions { get; }
}

public sealed class HomeAssistantIntentActions : IIntentActionProvider
{
    public IntegrationDescriptor Integration { get; } = new("home-assistant", "Home Assistant", "Device control and state from your configured Home Assistant connection.", true);
    private static readonly IntentActionInput Target = new("target", "Device", "entity", true);
    private static readonly IntentActionInput Area = new("area", "Area", "area", false);
    public IReadOnlyList<IntentActionDescriptor> Actions { get; } =
    [
        new("home-assistant.turn-on", "home-assistant", "Home Assistant", "Turn on", true, [Target, Area], DirectIntentKind.TurnOn),
        new("home-assistant.turn-off", "home-assistant", "Home Assistant", "Turn off", true, [Target, Area], DirectIntentKind.TurnOff),
        new("home-assistant.set-brightness", "home-assistant", "Home Assistant", "Set light brightness", true,
            [Target, new("brightness", "Brightness", "percent", true), Area], DirectIntentKind.SetBrightness),
        new("home-assistant.query-state", "home-assistant", "Home Assistant", "Read device state", false, [Target, Area], DirectIntentKind.QueryState),
        new("home-assistant.set-fan-speed", "home-assistant", "Home Assistant", "Set fan speed", true,
            [Target, new("speed", "Speed", "percent", true), Area], DirectIntentKind.SetFanSpeed),
        new("home-assistant.query-temperature", "home-assistant", "Home Assistant", "Read temperature", false, [Area], DirectIntentKind.QueryTemperature)
    ];
}

public sealed class AssisterIntentActions : IIntentActionProvider
{
    public IntegrationDescriptor Integration { get; } = new("assister", "Assister", "Local replies, time and date; no external service needed.", true);
    public IReadOnlyList<IntentActionDescriptor> Actions { get; } =
    [
        new("assister.reply", "assister", "Assister", "Reply with text", false, []),
        new("assister.time", "assister", "Assister", "Current local time", false, []),
        new("assister.date", "assister", "Assister", "Current local date", false, [])
    ];
}

public sealed class IntentActionRegistry
{
    public static IntentActionRegistry Default { get; } = new([new HomeAssistantIntentActions(), new AssisterIntentActions()]);
    public IntentActionDescriptor[] Actions { get; }
    public IntegrationDescriptor[] Integrations { get; }
    private readonly Dictionary<string, IntentActionDescriptor> ById;

    public IntentActionRegistry(IEnumerable<IIntentActionProvider> Providers)
    {
        var Registered = Providers.ToArray();
        Integrations = Registered.Select(Provider => Provider.Integration).ToArray();
        _ = Integrations.ToDictionary(Integration => Integration.Id, StringComparer.Ordinal);
        Actions = Registered.SelectMany(Provider => Provider.Actions).ToArray();
        ById = Actions.ToDictionary(Action => Action.Id, StringComparer.Ordinal);
        foreach (var Action in Actions)
        {
            if (!Action.Id.StartsWith(Action.IntegrationId + ".", StringComparison.Ordinal)
                || !Registered.Any(Provider => Provider.Integration.Id == Action.IntegrationId && Provider.Actions.Contains(Action)))
            {
                throw new ArgumentException("An action identifier must be qualified by its integration.");
            }
        }
    }

    public IntentActionDescriptor Get(string? Id) => Id is not null && ById.TryGetValue(Id, out var Action) ? Action
        : throw new ArgumentException("Choose an action registered by an available integration.");

    // Only used when upgrading persisted definitions from the original workbench format.
    public static string UpgradeLegacy(string Handler) => Handler switch
    {
        "TurnOn" => "home-assistant.turn-on", "TurnOff" => "home-assistant.turn-off",
        "SetBrightness" => "home-assistant.set-brightness", "QueryState" => "home-assistant.query-state",
        "QueryTemperature" => "home-assistant.query-temperature", "Reply" => "assister.reply",
        "Time" => "assister.time", "Date" => "assister.date", _ => throw new ArgumentException("Unknown legacy intent handler.")
    };
}
