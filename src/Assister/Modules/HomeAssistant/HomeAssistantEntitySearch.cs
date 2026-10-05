using System.Text.RegularExpressions;

namespace Assister.Modules.HomeAssistant;

// Shared semantic matching for discovery and deterministic resolution. Generic device
// nouns describe a domain, rather than matching trackers/helpers with similar names.
public static class HomeAssistantEntitySearch
{
    public static string[] Words(string Text) => Regex.Matches(Text.ToLowerInvariant(), "[a-z0-9]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Select(Match => Match.Value switch
        {
            "lights" or "lamp" or "lamps" => "light",
            "switches" => "switch",
            "sensors" => "sensor",
            "fans" => "fan",
            _ => Match.Value
        }).Where(Word => Word is not ("the" or "a" or "in" or "of")).Distinct().ToArray();

    public static HomeAssistantEntity[] Find(IEnumerable<HomeAssistantEntity> Entities, string Query,
        string? Area = null, IReadOnlyList<string>? Domains = null)
    {
        var Terms = Words(Query);
        var Inferred = Terms.Where(Word => Word is "light" or "switch" or "sensor" or "fan").ToArray();
        var DomainFilter = Domains ?? (Inferred.Length == 1 ? Inferred : null);
        var Names = Terms.Where(Word => DomainFilter is null || !DomainFilter.Contains(Word)).ToArray();
        var Candidates = Entities.Where(Entity => DomainFilter is null || DomainFilter.Contains(Entity.Domain)).ToArray();
        bool Matches(HomeAssistantEntity Entity) => Names.All(Word => Words(SearchText(Entity)).Contains(Word)
            || Word == "temperature" && Entity.IsTemperature);
        bool InArea(HomeAssistantEntity Entity) => string.Equals(Area, Entity.AreaId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Area, Entity.AreaName, StringComparison.OrdinalIgnoreCase);
        var Found = Candidates.Where(Entity => (Area is null || InArea(Entity)) && Matches(Entity)).ToArray();
        if (Area is not null && Found.Length == 0)
        {
            // Never widen to another assigned area. Only discover unassigned devices whose
            // actual name, alias, ID or device name contains the requested room.
            var RoomWords = Words(Area);
            Found = Candidates.Where(Entity => string.IsNullOrWhiteSpace(Entity.AreaId) && Matches(Entity)
                && RoomWords.All(Word => Words(SearchText(Entity)).Contains(Word))).ToArray();
        }
        var Exact = Found.Where(Entity => new[] { Entity.Name, Entity.EntityId }.Concat(Entity.Aliases)
            .Any(Name => Words(Name).Order().SequenceEqual(Terms.Order()))).ToArray();
        return (Exact.Length > 0 ? Exact : Found).OrderBy(Entity => Entity.IsUnavailable)
            .ThenBy(Entity => Entity.EntityId, StringComparer.Ordinal).ToArray();
    }

    private static string SearchText(HomeAssistantEntity Entity) => string.Join(' ', Entity.EntityId,
        Entity.Name, Entity.DeviceName, Entity.AreaName, string.Join(' ', Entity.Aliases));
}
