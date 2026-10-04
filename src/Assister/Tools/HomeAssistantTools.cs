using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.HomeAssistant;

namespace Assister.Tools;

public sealed class HomeAssistantTool(string Name, HomeAssistantStateCache Cache, IHomeAssistantClient Actions,
    HttpClient Http, IConfiguration Configuration) : IAssisterTool
{
    private static JsonElement Schema(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);
    public bool StateChanging => Name == "ha_control";
    public LlmTool Definition => new(new(Name, Name switch
    {
        "ha_search" => "Find a small set of relevant Home Assistant entities by name or area. Never assume entity IDs.",
        "ha_get_state" => "Read current compact state for entities found by search.",
        "ha_control" => "Control a single light or switch found by search. Only use for an explicit user control request.",
        _ => "Get bounded numeric history summaries for searched entities. Times must include the correct UTC offset; end cannot be in the future. Maximum range is seven days."
    }, Schema(Name switch
    {
        "ha_search" => """{"type":"object","properties":{"query":{"type":"string"},"area":{"type":"string"},"domains":{"type":"array","items":{"type":"string"}},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["query"],"additionalProperties":false}""",
        "ha_get_state" => """{"type":"object","properties":{"entity_ids":{"type":"array","items":{"type":"string"}}},"required":["entity_ids"],"additionalProperties":false}""",
        "ha_control" => """{"type":"object","properties":{"entity_id":{"type":"string"},"action":{"type":"string","enum":["turn_on","turn_off","set_brightness"]},"brightness_pct":{"type":"integer","minimum":0,"maximum":100}},"required":["entity_id","action"],"additionalProperties":false}""",
        _ => """{"type":"object","properties":{"entity_ids":{"type":"array","items":{"type":"string"}},"start":{"type":"string"},"end":{"type":"string"}},"required":["entity_ids","start","end"],"additionalProperties":false}"""
    })));

    public async Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
    {
        var Snapshot = Cache.Snapshot();
        if (Snapshot.IsStale) { throw new InvalidOperationException(); }
        if (Name == "ha_search")
        {
            var Words = Arguments.GetProperty("query").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var Area = Arguments.TryGetProperty("area", out var AreaValue) ? AreaValue.GetString() : null;
            var Domains = Arguments.TryGetProperty("domains", out var DomainValue) ? DomainValue.EnumerateArray().Select(Item => Item.GetString()).ToArray() : null;
            var Ranked = Snapshot.Entities.Where(Entity => (Area is null || string.Equals(Area, Entity.AreaId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Area, Entity.AreaName, StringComparison.OrdinalIgnoreCase)) && (Domains is null || Domains.Contains(Entity.Domain)))
                .Select(Entity => new { Entity, Score = Words.Count(Word => string.Join(' ', Entity.EntityId, Entity.Name, Entity.AreaName, string.Join(' ', Entity.Aliases)).Contains(Word, StringComparison.OrdinalIgnoreCase)
                    || Word.Equals("temperature", StringComparison.OrdinalIgnoreCase) && Entity.IsTemperature) })
                .Where(Item => Item.Score > 0).OrderByDescending(Item => Item.Score).ThenBy(Item => Item.Entity.IsUnavailable).ThenBy(Item => Item.Entity.EntityId).ToArray();
            // Keep equally strong alternatives, but do not mix a full target match with incidental
            // matches on a generic word such as "light"; those make a unique control look ambiguous.
            var Matches = Ranked.Where(Item => Item.Score == Ranked[0].Score)
                .Take(Arguments.TryGetProperty("limit", out var Limit) ? Limit.GetInt32() : 10).Select(Item => Item.Entity).ToArray();
            foreach (var Entity in Matches) { Context.ObservedEntities.Add(Entity.EntityId); }
            return JsonSerializer.Serialize(Matches.Select(Compact));
        }
        var Ids = Name == "ha_control" ? new[] { Arguments.GetProperty("entity_id").GetString()! }
            : Arguments.GetProperty("entity_ids").EnumerateArray().Select(Item => Item.GetString()!).Distinct().ToArray();
        if (Ids.Any(Id => !Context.ObservedEntities.Contains(Id)))
        {
            if (Name == "ha_control") { throw new InvalidOperationException(); }
            return "{\"error\":\"Search for every requested entity with ha_search in this request first. Copy its exact entity_id; do not reconstruct IDs from names or earlier turns.\"}";
        }
        var Entities = Ids.Select(Id => Snapshot.Entities.SingleOrDefault(Entity => Entity.EntityId == Id) ?? throw new InvalidOperationException()).ToArray();
        if (Name == "ha_get_state") { return JsonSerializer.Serialize(Entities.Select(Compact)); }
        if (Name == "ha_control")
        {
            var Target = Entities[0];
            if (Target.Domain is not ("light" or "switch") || Target.IsUnavailable) { throw new InvalidOperationException(); }
            var Explicit = Context.Request.Message.Contains(Target.EntityId, StringComparison.OrdinalIgnoreCase)
                || Target.Name.Length >= 3 && Context.Request.Message.Contains(Target.Name, StringComparison.OrdinalIgnoreCase);
            if (!Explicit && Snapshot.Entities.Count(Entity => Context.ObservedEntities.Contains(Entity.EntityId) && Entity.Domain == Target.Domain) != 1)
            { throw new InvalidOperationException(); }
            var Action = Arguments.GetProperty("action").GetString() switch
            {
                "turn_on" => HomeAssistantAction.TurnOn,
                "turn_off" => HomeAssistantAction.TurnOff,
                "set_brightness" => HomeAssistantAction.SetBrightness,
                _ => throw new InvalidOperationException()
            };
            int? Brightness = Arguments.TryGetProperty("brightness_pct", out var Value) ? Value.GetInt32() : null;
            if (Action == HomeAssistantAction.SetBrightness && (Brightness is null || !Target.SupportsBrightness)
                || Action != HomeAssistantAction.SetBrightness && Brightness is not null) { throw new InvalidOperationException(); }
            await Actions.ControlAsync(new(Action, Ids, Brightness), CancellationToken);
            return "{\"status\":\"completed\"}";
        }
        var StartText = Arguments.GetProperty("start").GetString()!;
        var EndText = Arguments.GetProperty("end").GetString()!;
        if (!HasOffset(StartText) || !HasOffset(EndText)
            || !DateTimeOffset.TryParse(StartText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var Start)
            || !DateTimeOffset.TryParse(EndText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var End)
            || Start >= End || End - Start > TimeSpan.FromDays(7) || End > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return JsonSerializer.Serialize(new { error = "History timestamps must include a UTC offset, start must precede end, range must not exceed seven days, and end cannot be in the future. Correct the range using the supplied local clock.", current_utc = DateTimeOffset.UtcNow });
        }
        var Path = $"api/history/period/{Uri.EscapeDataString(Start.ToString("O"))}?filter_entity_id={Uri.EscapeDataString(string.Join(',', Ids))}&end_time={Uri.EscapeDataString(End.ToString("O"))}&minimal_response&no_attributes&significant_changes_only";
        using var Request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(Configuration["HomeAssistant:Url"]!), Path));
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration["HomeAssistant:Token"]);
        using var Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        Response.EnsureSuccessStatusCode();
        await using var Stream = await Response.Content.ReadAsStreamAsync(CancellationToken);
        using var Buffer = new MemoryStream();
        var Block = new byte[8192];
        int Count;
        while ((Count = await Stream.ReadAsync(Block, CancellationToken)) > 0)
        {
            if (Buffer.Length + Count > 1024 * 1024) { throw new InvalidDataException(); }
            Buffer.Write(Block, 0, Count);
        }
        using var Document = JsonDocument.Parse(Buffer.ToArray());
        var Results = new List<object>();
        foreach (var Series in Document.RootElement.EnumerateArray())
        {
            if (Series.GetArrayLength() == 0) { continue; }
            var Id = Series[0].GetProperty("entity_id").GetString()!;
            if (!Ids.Contains(Id)) { continue; }
            var Values = Series.EnumerateArray().Select(Point => double.TryParse(Point.GetProperty("state").GetString(), CultureInfo.InvariantCulture, out var Number) && double.IsFinite(Number) ? (double?)Number : null)
                .Where(Number => Number.HasValue).Select(Number => Number!.Value).ToArray();
            Results.Add(new { entity_id = Id, start = Start, end = End, numeric_samples = Values.Length,
                unit = Entities.Single(Entity => Entity.EntityId == Id).State.GetProperty("attributes").TryGetProperty("unit_of_measurement", out var Unit) ? Unit.GetString() : null,
                minimum = Values.Length > 0 ? (double?)Values.Min() : null, maximum = Values.Length > 0 ? (double?)Values.Max() : null,
                sample_mean = Values.Length > 0 ? (double?)Values.Average() : null,
                note = "Mean is across numeric state-change samples, not time-weighted. Missing/unavailable samples are excluded." });
        }
        return JsonSerializer.Serialize(Results);
    }

    private static bool HasOffset(string Text) => Text.EndsWith('Z') || Text.Length >= 6 && Text[^3] == ':' && Text[^6] is '+' or '-';
    private static object Compact(HomeAssistantEntity Entity) => new
    {
        entity_id = Entity.EntityId, name = Entity.Name[..Math.Min(Entity.Name.Length, 128)], area = Entity.AreaName,
        state = Entity.State.GetProperty("state").GetString(),
        unit = Entity.State.GetProperty("attributes").TryGetProperty("unit_of_measurement", out var Unit) ? Unit.GetString() : null,
        supports_brightness = Entity.SupportsBrightness,
        is_temperature = Entity.IsTemperature,
        brightness_pct = Entity.State.GetProperty("attributes").TryGetProperty("brightness", out var Brightness)
            && Brightness.TryGetInt32(out var Value) ? (int?)Math.Round(Value * 100.0 / 255) : null
    };
}
