using System.Text.Json;
using System.Text.RegularExpressions;
using Assister.Modules.HomeAssistant;

namespace Assister.Tools;

public static class SolarEnergyPolicy
{
    private static bool Matches(string Text, string Pattern) => Regex.IsMatch(Text, Pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool ActualToday(string Message) => Matches(Message, @"\b(?:solar|pv|photovoltaic)\b")
        && Matches(Message, @"\b(?:today|so far today)\b")
        && Matches(Message, @"\b(?:generated?|generating|generation|produced?|production|yield|energy|how much)\b")
        && !Matches(Message, @"\b(?:forecast|estimate[ds]?|expected|predict\w*|will|tomorrow|yesterday)\b");

    public static bool IsDailyGeneration(HomeAssistantEntity Entity)
    {
        if (Entity.Domain != "sensor") return false;
        var Attributes = Entity.State.GetProperty("attributes");
        var Unit = Attributes.TryGetProperty("unit_of_measurement", out var Value) && Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
        if (Unit is not ("Wh" or "kWh" or "MWh")) return false;
        var Text = string.Join(' ', Entity.EntityId.Replace('_', ' '), Entity.Name, Entity.DeviceName, string.Join(' ', Entity.Aliases));
        return Matches(Text, @"\b(?:solar|pv|photovoltaic|inverter\w*)\b")
            && Matches(Text, @"\b(?:generation|generated|production|produced|yield|energy)\b")
            && Matches(Text, @"\b(?:today|daily|of the day)\b")
            && !Matches(Text, @"\b(?:forecast|estimate[ds]?|prediction|expected|consumption|consumed|load|battery|yesterday|tomorrow|remaining|lifetime|accumulated)\b");
    }

    public static HomeAssistantEntity[] FindDailyGeneration(IEnumerable<HomeAssistantEntity> Entities, string Query, string? Area, IReadOnlyList<string>? Domains)
    {
        var Candidates = Entities.Where(IsDailyGeneration);
        // Solar and PV are common request terms absent from inverter daily-counter names.
        var Search = Query.Contains('.') ? Query : Regex.Replace(Query,
            @"\b(?:solar|pv|photovoltaic|inverter|generation|generated|production|produced|energy|yield|power|today|daily|of|the|day|sensor|sensors)\b", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return HomeAssistantEntitySearch.Find(Candidates, Search, Area, Domains);
    }
}
