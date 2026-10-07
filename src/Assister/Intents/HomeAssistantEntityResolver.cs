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
        if (Intent.Kind is DirectIntentKind.SetBrightness or DirectIntentKind.SetFanSpeed or DirectIntentKind.TurnOn or DirectIntentKind.TurnOff
            && Target.Contains(" and ", StringComparison.Ordinal))
        {
            // Preserve real names containing "and" before interpreting a target list.
            var Whole = ResolveSingle(Intent, SatelliteArea, Snapshot);
            if (Whole.Entities.Count > 0) { return Whole; }
            var Parts = Target.Split(" and ", StringSplitOptions.None);
            if (Parts.Length > 8 || Parts.Any(string.IsNullOrWhiteSpace)) { return new([], 0, []); }
            if (Parts.Any(Part => System.Text.RegularExpressions.Regex.IsMatch(Part, @"\b(?:on|off)\b"))) { return new([], 0, []); }
            var Resolved = Parts.Select(Part => ResolveSingle(Intent with { Target = LanguageParser.Noun(Part) }, SatelliteArea, Snapshot)).ToArray();
            if (Resolved.Any(Result => Result.Entities.Count == 0))
                return new([], 0, Resolved.Where(Result => Result.Entities.Count == 0).SelectMany(Result => Result.Alternatives)
                    .DistinctBy(Entity => Entity.EntityId).Take(5).ToArray(), Intent.ExplicitArea, Intent.TargetDomain);
            var Entities = Resolved.SelectMany(Result => Result.Entities).DistinctBy(Entity => Entity.EntityId).ToArray();
            return Entities.Length <= 64 ? new(Entities, Resolved.Min(Result => Result.Confidence), [], Intent.ExplicitArea,
                Intent.TargetDomain ?? (Intent.Kind == DirectIntentKind.SetBrightness ? "light" : null)) : new([], 0, []);
        }
        return ResolveSingle(Intent, SatelliteArea, Snapshot);
    }

    private EntityResolutionResult ResolveSingle(IntentMatch Intent, string? SatelliteArea, HomeAssistantSnapshot Snapshot)
    {
        var Target = LanguageParser.Noun(Intent.Target);
        var Candidates = Snapshot.Entities.Where(Entity => Eligible(Entity, Intent.Kind)).ToArray();
        var Area = Intent.ExplicitArea;
        var Domain = Intent.TargetDomain ?? (Intent.Kind == DirectIntentKind.SetBrightness ? "light" : Intent.Kind == DirectIntentKind.SetFanSpeed ? "fan" : null);
        EntityResolutionResult Result(IReadOnlyList<HomeAssistantEntity> Entities, double Confidence, IReadOnlyList<HomeAssistantEntity> Alternatives)
            => new(Entities, Confidence, Alternatives, Area ?? Entities.FirstOrDefault()?.AreaName,
                Domain ?? (Intent.Kind == DirectIntentKind.QueryTemperature ? "temperature sensor" : Intent.Kind is DirectIntentKind.TurnOn or DirectIntentKind.TurnOff ? "light or switch" : null));
        EntityResolutionResult Unique(HomeAssistantEntity[] Entities, double Confidence) => Entities.Length == 1
            ? Result(Entities, Confidence, []) : Result([], 0, Entities.Take(5).ToArray());
        var Plural = Target is "lights" or "switches" or "fans" || Target.EndsWith(" lights", StringComparison.Ordinal) || Target.EndsWith(" switches", StringComparison.Ordinal) || Target.EndsWith(" fans", StringComparison.Ordinal);

        if (Area is null)
        {
            // A room name inside a device's full name does not require an HA area assignment.
            // Prefer that named device before interpreting "living room light" as an area query.
            var NamedDomain = Domain ?? (Target.EndsWith(" light", StringComparison.Ordinal) || Target.EndsWith(" lights", StringComparison.Ordinal)
                ? "light" : Target.EndsWith(" switch", StringComparison.Ordinal) || Target.EndsWith(" switches", StringComparison.Ordinal) ? "switch" : null);
            var Specific = Target is not ("light" or "lights" or "switch" or "switches" or "fan" or "fans" or "temperature");
            var Named = Candidates.Where(Entity => Specific && (NamedDomain is null || Entity.Domain == NamedDomain)
                && Names(Entity).Any(Name => LanguageParser.Noun(Name) == Target)).ToArray();
            if (Specific && Named.Length == 0 && Target.Contains(' '))
            {
                Named = Candidates.Where(Entity => (NamedDomain is null || Entity.Domain == NamedDomain)
                    && Names(Entity).Any(Name => StripDomain(LanguageParser.Noun(Name), NamedDomain) == StripDomain(Target, NamedDomain))).ToArray();
            }
            if (Named.Length > 0)
            {
                if (Named.Length > 1 && SatelliteArea is not null) { Named = InArea(Named, SatelliteArea); }
                return Unique(Named, 1);
            }
            var EmbeddedAreas = Snapshot.Entities.Select(Entity => Entity.AreaName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Name => Target.StartsWith(LanguageParser.Normalize(Name) + " ", StringComparison.Ordinal)).ToArray();
            if (EmbeddedAreas.Length == 1)
            {
                Area = EmbeddedAreas[0];
                Target = Target[(LanguageParser.Normalize(Area).Length + 1)..];
            }
        }

        var Words = Target.Split(' ');
        if (Domain is null && Words.LastOrDefault() is "light" or "lights") { Domain = "light"; }
        else if (Domain is null && Words.LastOrDefault() is "switch" or "switches") { Domain = "switch"; }
        if (Domain is null && Words.LastOrDefault() is "fan" or "fans") { Domain = "fan"; }
        if (Domain is not null) { Candidates = Candidates.Where(Entity => Entity.Domain == Domain).ToArray(); }

        if (Area is not null)
        {
            var Assigned = InArea(Candidates, Area);
            Candidates = Assigned.Length > 0 ? Assigned : HomeAssistantEntitySearch.Find(Candidates.Where(Entity => string.IsNullOrWhiteSpace(Entity.AreaId)),
                Area + " " + Target, Domains: Domain is null ? null : [Domain]);
        }

        var Generic = Target is "light" or "lights" or "switch" or "switches" or "fan" or "fans" || (Intent.Kind == DirectIntentKind.QueryTemperature && Target == "temperature");
        if (Generic)
        {
            Area ??= SatelliteArea;
            if (Area is null) { return Result([], 0, Candidates.Take(5).ToArray()); }
            var Assigned = InArea(Candidates, Area);
            Candidates = Assigned.Length > 0 ? Assigned : HomeAssistantEntitySearch.Find(Candidates.Where(Entity => string.IsNullOrWhiteSpace(Entity.AreaId)),
                Area + " " + Target, Domains: Domain is null ? null : [Domain]);
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
        if (Domain is not null && Target.Contains(' '))
        {
            var Semantic = HomeAssistantEntitySearch.Find(Candidates, Target, Area, [Domain]);
            if (Semantic.Length > 0) { return Unique(Semantic, 0.9); }
        }
        var Alternatives = Candidates.Where(Entity => Names(Entity).Any(Name => LanguageParser.Normalize(Name).Contains(Target, StringComparison.Ordinal)))
            .Take(5).ToArray();
        return Result([], 0, Alternatives);
    }

    private static bool Eligible(HomeAssistantEntity Entity, DirectIntentKind Kind) => Kind switch
    {
        DirectIntentKind.QueryTemperature => Entity.IsTemperature,
        DirectIntentKind.SetBrightness => Entity.Domain == "light",
        DirectIntentKind.SetFanSpeed => Entity.Domain == "fan",
        DirectIntentKind.TurnOn or DirectIntentKind.TurnOff => Entity.Domain is "light" or "switch" or "fan",
        _ => true
    };

    private static IEnumerable<string> Names(HomeAssistantEntity Entity) => new[] { Entity.EntityId, Entity.EntityId.Split('.').Last().Replace('_', ' '), Entity.Name }
        .Concat(Entity.Aliases).Concat(Entity.DeviceName is null ? [] : new[] { Entity.DeviceName });

    private static HomeAssistantEntity[] InArea(IEnumerable<HomeAssistantEntity> Entities, string Area) => Entities.Where(Entity =>
        string.Equals(Entity.AreaId, Area, StringComparison.OrdinalIgnoreCase) || (Entity.AreaName is not null && LanguageParser.Normalize(Entity.AreaName) == LanguageParser.Noun(Area))).ToArray();

    private static string StripDomain(string Name, string? Domain)
    {
        if (Domain is null) { return Name; }
        var Suffixes = Domain == "light" ? new[] { " lights", " light" } : Domain == "fan" ? new[] { " fans", " fan" } : new[] { " switches", " switch" };
        foreach (var Suffix in Suffixes)
        {
            if (Name.EndsWith(Suffix, StringComparison.Ordinal)) { return Name[..^Suffix.Length]; }
        }
        return Name;
    }
}
