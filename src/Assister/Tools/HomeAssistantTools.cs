using System.Globalization;
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
        "ha_search" => "Find Home Assistant entities by name, device name, alias or area. Device nouns infer domains; use domains for light/switch lists. Unassigned devices can match a room in their names. An empty array is no match; an error is a failed search, not proof devices do not exist.",
        "ha_get_state" => "Read current compact state for entities found by search.",
        "ha_control" => "Control searched lights, switches or fans for the current authorized action. Use entity_ids to control multiple targets together, or entity_id for one. Search in this turn before controlling. Never replay earlier commands during questions. Targets and action are checked against the current request by the server.",
        _ => "Get numeric range statistics (count, min, max, sample mean, sum, median) for searched entities. Optional top_count/bottom_count return ranked readings; include_samples returns a chronological page using sample_offset/sample_limit. include_timestamps defaults true; extrema ties use the earliest timestamp. Statistics cover the full range, never just the page. At most 50 detailed readings across all entities per call. Times need UTC offsets; end cannot be future; maximum range seven days."
    }, Schema(Name switch
    {
        "ha_search" => """{"type":"object","properties":{"query":{"type":"string"},"area":{"type":"string"},"domains":{"type":"array","items":{"type":"string"}},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["query"],"additionalProperties":false}""",
        "ha_get_state" => """{"type":"object","properties":{"entity_ids":{"type":"array","items":{"type":"string"}}},"required":["entity_ids"],"additionalProperties":false}""",
        "ha_control" => """{"type":"object","properties":{"entity_id":{"type":"string"},"entity_ids":{"type":"array","items":{"type":"string"}},"action":{"type":"string","enum":["turn_on","turn_off","set_brightness","set_fan_speed"]},"brightness_pct":{"type":"integer","minimum":0,"maximum":100},"speed_pct":{"type":"integer","minimum":0,"maximum":100}},"required":["action"],"additionalProperties":false}""",
        _ => """{"type":"object","properties":{"entity_ids":{"type":"array","items":{"type":"string"}},"start":{"type":"string"},"end":{"type":"string"},"include_timestamps":{"type":"boolean"},"top_count":{"type":"integer","minimum":0,"maximum":50},"bottom_count":{"type":"integer","minimum":0,"maximum":50},"include_samples":{"type":"boolean"},"sample_offset":{"type":"integer","minimum":0},"sample_limit":{"type":"integer","minimum":1,"maximum":50}},"required":["entity_ids","start","end"],"additionalProperties":false}"""
    })));

    public async Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
    {
        var Snapshot = Cache.Snapshot();
        if (Snapshot.IsStale) { throw new InvalidOperationException(); }
        if (Name == "ha_search")
        {
            var Area = Arguments.TryGetProperty("area", out var AreaValue) ? AreaValue.GetString() : null;
            var Domains = Arguments.TryGetProperty("domains", out var DomainValue) ? DomainValue.EnumerateArray().Select(Item => Item.GetString()).ToArray() : null;
            var Matches = HomeAssistantEntitySearch.Find(Snapshot.Entities, Arguments.GetProperty("query").GetString()!, Area, Domains!)
                .Take(Arguments.TryGetProperty("limit", out var Limit) ? Limit.GetInt32() : 10).ToArray();
            // Materialize before recording observations: a failed projection must not leave
            // half-observed targets available to later controls.
            var Result = JsonSerializer.Serialize(Matches.Select(Compact).ToArray());
            foreach (var Entity in Matches) { Context.ObservedEntities.Add(Entity.EntityId); }
            return Result;
        }
        if (Name == "ha_control" && Arguments.TryGetProperty("entity_id", out _) == Arguments.TryGetProperty("entity_ids", out _))
            return "{\"error\":\"Provide exactly one of entity_id or entity_ids.\",\"code\":\"invalid_targets\"}";
        var Ids = Name == "ha_control" && Arguments.TryGetProperty("entity_id", out var SingleId) ? new[] { SingleId.GetString()! }
            : Arguments.GetProperty("entity_ids").EnumerateArray().Select(Item => Item.GetString()!).Distinct().ToArray();
        if (Ids.Any(Id => !Context.ObservedEntities.Contains(Id)))
        {
            return "{\"error\":\"Search for every requested entity with ha_search in this request first. Copy its exact entity_id; do not reconstruct IDs from names or earlier turns.\",\"code\":\"search_required\"}";
        }
        var Entities = Ids.Select(Id => Snapshot.Entities.SingleOrDefault(Entity => Entity.EntityId == Id) ?? throw new InvalidOperationException()).ToArray();
        if (Name == "ha_get_state") { return JsonSerializer.Serialize(Entities.Select(Compact)); }
        if (Name == "ha_control")
        {
            var Authorized = Context.Control;
            if (Authorized is null || Arguments.GetProperty("action").GetString() != Authorized.Action)
                return "{\"error\":\"This action is not authorized by the current request.\",\"code\":\"control_not_authorized\"}";
            var Allowed = Authorized.Resolve(Snapshot, Context.ControlConversation, Context.Request.Area);
            var Plural = ControlRequest.IsReference(Authorized.Target) || Authorized.Target.Contains(" and ", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(Authorized.Target, @"\b(?:both|all|lights|switches)\b");
            if (Allowed.Length == 0 || !Plural && Allowed.Length > 1 || Entities.Any(Entity => !Allowed.Any(Item => Item.EntityId == Entity.EntityId)))
                return "{\"error\":\"The requested targets are unresolved or ambiguous. Ask which devices the user means.\",\"code\":\"ambiguous_targets\"}";
            if (Entities.Any(Entity => Entity.Domain is not ("light" or "switch" or "fan") || Entity.IsUnavailable)
                || Entities.Select(Entity => Entity.Domain).Distinct().Count() != 1) { throw new InvalidOperationException(); }
            foreach (var Entity in Allowed) { Context.RequestedControls.Add(Entity.EntityId); }
            var Action = Arguments.GetProperty("action").GetString() switch
            {
                "turn_on" => HomeAssistantAction.TurnOn,
                "turn_off" => HomeAssistantAction.TurnOff,
                "set_brightness" => HomeAssistantAction.SetBrightness,
                "set_fan_speed" => HomeAssistantAction.SetFanSpeed,
                _ => throw new InvalidOperationException()
            };
            int? Brightness = Arguments.TryGetProperty("brightness_pct", out var Value) ? Value.GetInt32() : null;
            int? Speed = Arguments.TryGetProperty("speed_pct", out var SpeedValue) ? SpeedValue.GetInt32() : null;
            if (Action == HomeAssistantAction.SetFanSpeed && (Speed is null || Speed != Authorized.SpeedPercent || Entities.Any(Entity => !Entity.SupportsFanSpeed))
                || Action != HomeAssistantAction.SetFanSpeed && Speed is not null) { throw new InvalidOperationException(); }
            if (Action == HomeAssistantAction.SetBrightness && (Brightness is null || Brightness != Authorized.Brightness || Entities.Any(Entity => !Entity.SupportsBrightness))
                || Action != HomeAssistantAction.SetBrightness && Brightness is not null) { throw new InvalidOperationException(); }
            if (Ids.Any(Context.AttemptedControls.Contains))
                return "{\"error\":\"A control was already attempted for this device in this request. Do not retry.\",\"code\":\"control_already_attempted\"}";
            foreach (var Id in Ids) { Context.AttemptedControls.Add(Id); }
            if (Context.Conversation is { } Active)
                Active.LastAttempted = new(Authorized.Action, Ids, "unconfirmed", DateTimeOffset.UtcNow, Brightness, Speed);
            await Actions.ControlAsync(new(Action, Ids, Brightness, Speed), CancellationToken);
            foreach (var Id in Ids) { Context.CompletedControls.Add(Id); }
            if (Context.Conversation is { } Conversation)
            {
                Conversation.LastCompleted = new(Authorized.Action, Context.CompletedControls.Order().ToArray(), DateTimeOffset.UtcNow, Brightness, Speed);
                Conversation.LastAttempted = new(Authorized.Action, Ids, "completed", DateTimeOffset.UtcNow, Brightness, Speed);
            }
            return JsonSerializer.Serialize(new { status = "completed", action = Authorized.Action, entity_ids = Ids, requested_speed_pct = Speed,
                fan_states = Action == HomeAssistantAction.SetFanSpeed ? Cache.Snapshot().Entities.Where(Entity => Ids.Contains(Entity.EntityId)).Select(Compact).ToArray() : null });
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
        return await NumericHistory.ReadAsync(Arguments, Entities, Start, End, Http, Configuration, CancellationToken);
    }
    private static bool HasOffset(string Text) => Text.EndsWith('Z') || Text.Length >= 6 && Text[^3] == ':' && Text[^6] is '+' or '-';
    private static object Compact(HomeAssistantEntity Entity) => new
    {
        entity_id = Entity.EntityId, name = Entity.Name[..Math.Min(Entity.Name.Length, 128)], area = Entity.AreaName,
        state = Entity.State.GetProperty("state").GetString(),
        unit = Entity.State.GetProperty("attributes").TryGetProperty("unit_of_measurement", out var Unit)
            && Unit.ValueKind == JsonValueKind.String ? Unit.GetString() : null,
        supports_brightness = Entity.SupportsBrightness,
        supports_fan_speed = Entity.SupportsFanSpeed,
        speed_pct = Entity.FanSpeedPercent,
        percentage_step = Entity.FanPercentageStep,
        is_temperature = Entity.IsTemperature,
        brightness_pct = Entity.State.GetProperty("attributes").TryGetProperty("brightness", out var Brightness)
            && Brightness.ValueKind == JsonValueKind.Number && Brightness.TryGetInt32(out var Value) ? (int?)Math.Round(Value * 100.0 / 255) : null
    };
}
