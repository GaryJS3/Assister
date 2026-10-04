using Assister.Modules.HomeAssistant;

namespace Assister.Intents;

public sealed record EntityResolutionResult(IReadOnlyList<HomeAssistantEntity> Entities, double Confidence,
    IReadOnlyList<HomeAssistantEntity> Alternatives, string? EffectiveArea = null, string? RequiredDomain = null);

public interface IEntityResolver
{
    EntityResolutionResult Resolve(IntentMatch Intent, string? SatelliteArea, HomeAssistantSnapshot Snapshot);
}

public sealed class HomeAssistantEntityResolver : IEntityResolver
{
    public EntityResolutionResult Resolve(IntentMatch Intent, string? SatelliteArea, HomeAssistantSnapshot Snapshot)
    {
        var Target = LanguageParser.Noun(Intent.Target);
        var Candidates = Snapshot.Entities.Where(Entity => Eligible(Entity, Intent.Kind)).ToArray();
        var Area = Intent.ExplicitArea;
        var Domain = Intent.Kind == DirectIntentKind.SetBrightness ? "light" : null;
        EntityResolutionResult Result(IReadOnlyList<HomeAssistantEntity> Entities, double Confidence, IReadOnlyList<HomeAssistantEntity> Alternatives)
            => new(Entities, Confidence, Alternatives, Area ?? Entities.FirstOrDefault()?.AreaName,
                Domain ?? (Intent.Kind == DirectIntentKind.QueryTemperature ? "temperature sensor" : Intent.Kind is DirectIntentKind.TurnOn or DirectIntentKind.TurnOff ? "light or switch" : null));
        EntityResolutionResult Unique(HomeAssistantEntity[] Entities, double Confidence) => Entities.Length == 1
            ? Result(Entities, Confidence, []) : Result([], 0, Entities.Take(5).ToArray());
        var Plural = Target is "lights" or "switches" || Target.EndsWith(" lights", StringComparison.Ordinal) || Target.EndsWith(" switches", StringComparison.Ordinal);

        if (Area is null)
        {
            var EmbeddedAreas = Snapshot.Entities.Select(Entity => Entity.AreaName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Name => Target.StartsWith(LanguageParser.Normalize(Name) + " ", StringComparison.Ordinal)).ToArray();
            if (EmbeddedAreas.Length == 1)
            {
                Area = EmbeddedAreas[0];
                Target = Target[(LanguageParser.Normalize(Area).Length + 1)..];
            }
        }

        var Words = Target.Split(' ');
        if (Words.LastOrDefault() is "light" or "lights") { Domain = "light"; }
        else if (Words.LastOrDefault() is "switch" or "switches") { Domain = "switch"; }
        if (Domain is not null) { Candidates = Candidates.Where(Entity => Entity.Domain == Domain).ToArray(); }

        if (Area is not null)
        {
            Candidates = InArea(Candidates, Area);
        }

        var Generic = Target is "light" or "lights" or "switch" or "switches" || (Intent.Kind == DirectIntentKind.QueryTemperature && Target == "temperature");
        if (Generic)
        {
            Area ??= SatelliteArea;
            if (Area is null) { return Result([], 0, Candidates.Take(5).ToArray()); }
            Candidates = InArea(Candidates, Area);
            if (Plural && Candidates.Length is > 0 and <= 64 && Intent.Kind != DirectIntentKind.QueryState)
            {
                return Result(Candidates, 1, []);
            }
            return Unique(Candidates, 1);
        }

        var Exact = Candidates.Where(Entity => Names(Entity).Any(Name => LanguageParser.Noun(Name) == Target)).ToArray();
        if (Exact.Length > 0)
        {
            if (Exact.Length > 1 && Area is null && SatelliteArea is not null) { Exact = InArea(Exact, SatelliteArea); }
            return Unique(Exact, 1);
        }

        // Only whole, equivalent names qualify. Partial/fuzzy matches are alternatives requiring clarification.
        var ShortTarget = StripDomain(Target, Domain);
        var Equivalent = Candidates.Where(Entity => Names(Entity).Any(Name => StripDomain(LanguageParser.Noun(Name), Domain) == ShortTarget)).ToArray();
        if (Equivalent.Length > 1 && Area is null && SatelliteArea is not null) { Equivalent = InArea(Equivalent, SatelliteArea); }
        if (Equivalent.Length > 0) { return Unique(Equivalent, 0.95); }
        var Alternatives = Candidates.Where(Entity => Names(Entity).Any(Name => LanguageParser.Normalize(Name).Contains(Target, StringComparison.Ordinal)))
            .Take(5).ToArray();
        return Result([], 0, Alternatives);
    }

    private static bool Eligible(HomeAssistantEntity Entity, DirectIntentKind Kind) => Kind switch
    {
        DirectIntentKind.QueryTemperature => Entity.IsTemperature,
        DirectIntentKind.SetBrightness => Entity.Domain == "light",
        DirectIntentKind.TurnOn or DirectIntentKind.TurnOff => Entity.Domain is "light" or "switch",
        _ => true
    };

    private static IEnumerable<string> Names(HomeAssistantEntity Entity) => new[] { Entity.EntityId, Entity.EntityId.Split('.').Last().Replace('_', ' '), Entity.Name }
        .Concat(Entity.Aliases).Concat(Entity.DeviceName is null ? [] : new[] { Entity.DeviceName });

    private static HomeAssistantEntity[] InArea(IEnumerable<HomeAssistantEntity> Entities, string Area) => Entities.Where(Entity =>
        string.Equals(Entity.AreaId, Area, StringComparison.OrdinalIgnoreCase) || (Entity.AreaName is not null && LanguageParser.Normalize(Entity.AreaName) == LanguageParser.Noun(Area))).ToArray();

    private static string StripDomain(string Name, string? Domain)
    {
        if (Domain is null) { return Name; }
        var Suffixes = Domain == "light" ? new[] { " lights", " light" } : new[] { " switches", " switch" };
        foreach (var Suffix in Suffixes)
        {
            if (Name.EndsWith(Suffix, StringComparison.Ordinal)) { return Name[..^Suffix.Length]; }
        }
        return Name;
    }
}
