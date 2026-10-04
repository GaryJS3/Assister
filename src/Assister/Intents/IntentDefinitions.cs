using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assister.Intents;

public sealed record IntentDefinition(string Id, string Name, string ActionId, bool Enabled,
    string[] Patterns, string? Target = null, int? Brightness = null, string? Area = null,
    string Response = "{response}", bool BuiltIn = false, long Version = 0);

public sealed record IntentExample(string Id, string Name, string Text, string? Area,
    string? ExpectedRuleId, string MatchStatus = "matched", string? ExpectedTarget = null,
    int? ExpectedBrightness = null);

public sealed class IntentDefinitionRow
{
    public string Id { get; set; } = "";
    public string Payload { get; set; } = "";
    public long Version { get; set; }
}

public sealed class IntentExampleRow
{
    public string Id { get; set; } = "";
    public string Payload { get; set; } = "";
}

public static class IntentCatalog
{
    public static readonly Dictionary<string, string[]> NativePhrases = new()
    {
        ["builtin-turn-on"] = ["turn on {target}", "turn {target} on"],
        ["builtin-turn-off"] = ["turn off {target}", "turn {target} off"],
        ["builtin-brightness"] = ["set {target} to {brightness} percent", "set living room light to 100"],
        ["builtin-state"] = ["what is the state of {target}", "what's the status of {target}", "is {target} on"],
        ["builtin-temperature"] = ["what is the temperature in {area}", "what's the temperature"]
    };
    public static readonly IntentDefinition[] BuiltIns =
    [
        new("builtin-turn-on", "Turn power on", "home-assistant.turn-on", true, [], BuiltIn: true),
        new("builtin-turn-off", "Turn power off", "home-assistant.turn-off", true, [], BuiltIn: true),
        new("builtin-brightness", "Set light brightness", "home-assistant.set-brightness", true, [], BuiltIn: true),
        new("builtin-state", "Read device state", "home-assistant.query-state", true, [], BuiltIn: true),
        new("builtin-temperature", "Read temperature", "home-assistant.query-temperature", true, [], BuiltIn: true),
        new("builtin-time", "Current local time", "assister.time", true, ["what time is it", "what is the time"], BuiltIn: true),
        new("builtin-date", "Current local date", "assister.date", true, ["what is today's date", "what is the date", "what day is it"], BuiltIn: true)
    ];

    public static readonly IntentExample[] Examples =
    [
        new("example-brightness", "Bare brightness regression", "Set living room light to 100.", null, "builtin-brightness", ExpectedTarget: "living room light", ExpectedBrightness: 100),
        new("example-power", "Named light power", "Turn light.desk off", null, "builtin-turn-off", ExpectedTarget: "light.desk"),
        new("example-temperature", "Area temperature", "What's the temperature in the office?", null, "builtin-temperature", ExpectedTarget: "temperature"),
        new("example-time", "Local time", "What time is it?", null, "builtin-time"),
        new("example-unmatched", "Reasoning stays with the model", "Why was the office warmer yesterday?", null, null, "unmatched")
    ];

    public static string NativeId(DirectIntentKind Kind) => Kind switch
    {
        DirectIntentKind.TurnOn => "builtin-turn-on", DirectIntentKind.TurnOff => "builtin-turn-off",
        DirectIntentKind.SetBrightness => "builtin-brightness", DirectIntentKind.QueryState => "builtin-state", _ => "builtin-temperature"
    };
}

public sealed class IntentStore(AssisterDbContext Database, IntentActionRegistry Registry)
{
    public async Task<IntentDefinition[]> DefinitionsAsync(CancellationToken Token)
    {
        var Rows = await Database.IntentDefinitions.AsNoTracking().ToArrayAsync(Token);
        var Overrides = Rows.Where(Row => Row.Id != "catalog-initialized").Select(Row => JsonSerializer.Deserialize<IntentDefinition>(Row.Payload)! with { Version = Row.Version }).ToDictionary(Row => Row.Id);
        return IntentCatalog.BuiltIns.Select(Row => Overrides.GetValueOrDefault(Row.Id, Row))
            .Concat(Overrides.Values.Where(Row => !Row.BuiltIn)).OrderBy(Row => Row.Name).ToArray();
    }

    public async Task SaveAsync(IntentDefinition Definition, CancellationToken Token)
    {
        Validate(Definition, Registry);
        var Native = IntentCatalog.BuiltIns.SingleOrDefault(Row => Row.Id == Definition.Id);
        if (Native is not null && (!Definition.BuiltIn || Definition.ActionId != Native.ActionId || Definition.Target is not null
            || Definition.Brightness is not null || Definition.Area is not null))
        {
            throw new ArgumentException("Built-in actions and fixed slots cannot be changed. Add aliases or create a custom intent.");
        }
        if (Native is null && (Definition.BuiltIn || Definition.Id.StartsWith("builtin-", StringComparison.Ordinal)))
        {
            throw new ArgumentException("The built-in identifier is reserved.");
        }
        var Row = await Database.IntentDefinitions.SingleOrDefaultAsync(Row => Row.Id == Definition.Id, Token);
        if (Row is null)
        {
            if (Definition.Version != 0) { throw new DbUpdateConcurrencyException(); }
            if (await Database.IntentDefinitions.CountAsync(Token) >= 200) { throw new ArgumentException("The intent limit is 200 definitions."); }
            Database.IntentDefinitions.Add(new() { Id = Definition.Id, Payload = JsonSerializer.Serialize(Definition), Version = 1 });
        }
        else
        {
            if (Row.Version != Definition.Version) { throw new DbUpdateConcurrencyException(); }
            Row.Payload = JsonSerializer.Serialize(Definition);
            Row.Version++;
        }
        await Database.SaveChangesAsync(Token);
    }

    public async Task<IntentExample[]> ExamplesAsync(CancellationToken Token) =>
        (await Database.IntentExamples.AsNoTracking().OrderBy(Row => Row.Id).ToArrayAsync(Token))
            .Select(Row => JsonSerializer.Deserialize<IntentExample>(Row.Payload)!).ToArray();

    public async Task SaveExampleAsync(IntentExample Example, CancellationToken Token)
    {
        if (!ValidId(Example.Id) || string.IsNullOrWhiteSpace(Example.Name) || Example.Name.Length > 128
            || string.IsNullOrWhiteSpace(Example.Text) || Example.Text.Length > 1000 || Example.Area?.Length > 128
            || Example.ExpectedRuleId?.Length > 128 || Example.ExpectedTarget?.Length > 256
            || Example.MatchStatus is not ("matched" or "unmatched" or "ambiguous" or "invalid-request" or "timer-route"))
        {
            throw new ArgumentException("The example has invalid fields.");
        }
        var Row = await Database.IntentExamples.FindAsync([Example.Id], Token);
        if (Row is null)
        {
            if (await Database.IntentExamples.CountAsync(Token) >= 200) { throw new ArgumentException("The example limit is 200."); }
            Database.IntentExamples.Add(new() { Id = Example.Id, Payload = JsonSerializer.Serialize(Example) });
        }
        else { Row.Payload = JsonSerializer.Serialize(Example); }
        await Database.SaveChangesAsync(Token);
    }

    public async Task InitializeAsync(CancellationToken Token)
    {
        // Upgrade the old JSON contract in place, preserving phrases, responses and optimistic versions.
        foreach (var Existing in await Database.IntentDefinitions.ToArrayAsync(Token))
        {
            var Payload = JsonNode.Parse(Existing.Payload)!.AsObject();
            if (Payload["ActionId"] is null && Payload["Handler"] is { } Legacy)
            {
                Payload["ActionId"] = IntentActionRegistry.UpgradeLegacy(Legacy.GetValue<string>());
                Payload.Remove("Handler");
                Existing.Payload = Payload.ToJsonString();
                Existing.Version++;
            }
        }
        await Database.SaveChangesAsync(Token);
        // Seed once per installation; deleting all examples must not re-create them on restart.
        if (await Database.IntentExamples.AnyAsync(Token) || await Database.IntentDefinitions.AnyAsync(Row => Row.Id == "catalog-initialized", Token)) { return; }
        Database.IntentExamples.AddRange(IntentCatalog.Examples.Select(Row => new IntentExampleRow { Id = Row.Id, Payload = JsonSerializer.Serialize(Row) }));
        Database.IntentDefinitions.Add(new() { Id = "catalog-initialized", Payload = JsonSerializer.Serialize(new IntentDefinition("catalog-initialized", "Initialization marker", "assister.reply", false, ["internal marker"], Response: "internal")), Version = 1 });
        await Database.SaveChangesAsync(Token);
    }

    public static bool ValidId(string? Id) => Id is not null && Regex.IsMatch(Id, @"^[a-z0-9][a-z0-9-]{0,95}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static void Validate(IntentDefinition Definition, IntentActionRegistry? Registry = null)
    {
        var Action = (Registry ?? IntentActionRegistry.Default).Get(Definition.ActionId);
        if (!ValidId(Definition.Id) || Definition.Id == "catalog-initialized" || string.IsNullOrWhiteSpace(Definition.Name) || Definition.Name.Length > 128
            || Definition.Patterns is null || Definition.Patterns.Length > 24
            || !Definition.BuiltIn && Definition.Patterns.Length == 0 || Definition.Target?.Length > 256 || Definition.Area?.Length > 128
            || Definition.Brightness is < 0 or > 100 || Definition.Response is null || Definition.Response.Length is < 1 or > 1000)
        {
            throw new ArgumentException("Invalid intent fields. Use a registered action, at most 24 phrases and brightness from 0 to 100.");
        }
        foreach (var Pattern in Definition.Patterns)
        {
            var Slots = IntentTemplate.Compile(Pattern).Slots;
            foreach (var Slot in Slots)
            {
                if (!Action.Inputs.Any(Input => Input.Name == Slot)) { throw new ArgumentException($"{Action.IntegrationName} / {Action.Name} does not accept {Slot}."); }
            }
            foreach (var Input in Action.Inputs.Where(Input => Input.Required))
            {
                var Fixed = Input.Name switch { "target" => Definition.Target, "brightness" => Definition.Brightness?.ToString(), "area" => Definition.Area, _ => null };
                if (string.IsNullOrWhiteSpace(Fixed) && !Slots.Contains(Input.Name)) { throw new ArgumentException($"{Input.Label} needs a phrase slot or a fixed value."); }
            }
        }
        if (Definition.Target is not null && !Action.Inputs.Any(Input => Input.Name == "target")
            || Definition.Brightness is not null && !Action.Inputs.Any(Input => Input.Name == "brightness")
            || Definition.Area is not null && !Action.Inputs.Any(Input => Input.Name == "area")) { throw new ArgumentException("A fixed value is not supported by this action."); }
        if (Definition.ActionId == "assister.reply" && Definition.Response.Contains("{response}", StringComparison.Ordinal))
        {
            throw new ArgumentException("A fixed reply needs its own response text instead of {response}.");
        }
        var Remainder = Regex.Replace(Definition.Response, @"\{(?:response|target|brightness|area|time|date)\}", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (Remainder.Contains('{') || Remainder.Contains('}')) { throw new ArgumentException("Unknown response placeholder."); }
    }
}
