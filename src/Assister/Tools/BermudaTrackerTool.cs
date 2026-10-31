using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.HomeAssistant;

namespace Assister.Tools;

public sealed class BermudaTrackerTool(HomeAssistantStateCache Cache) : IAssisterTool
{
    public bool StateChanging => false;
    public LlmTool Definition => new(new("ha_locate_tracker",
        "Locate a Bermuda Bluetooth tracker by device name, alias or exact area sensor entity ID (for example Gary's keys or Meg's phone). Searches and reads together. Returns current area, visibility and elapsed time when available; last seen area is provided only when current location is unavailable. Mention last seen location only when there is no current location. Use Response to preserve uncertainty: an area-loss timestamp is not an exact last detection time. Never use a device's assigned HA area as its tracked location. If ambiguous, ask which listed tracker; if no match, say so.",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""")));
    public Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
    {
        var Snapshot = Cache.Snapshot();
        if (Snapshot.IsStale) { throw new InvalidOperationException("Home Assistant is unavailable."); }
        var Matches = BermudaTracker.Find(Snapshot, Arguments.GetProperty("query").GetString()!);
        return Task.FromResult(Matches.Length switch
        {
            0 => JsonSerializer.Serialize(new { error = "No matching Bermuda tracker found.", code = "not_found" }),
            1 => JsonSerializer.Serialize(BermudaTracker.Read(Snapshot, Matches[0])),
            _ => JsonSerializer.Serialize(new { error = "Which tracker do you mean?", code = "ambiguous",
                candidates = Matches.Take(10).Select(Item => new { Item.EntityId, Item.Name, Item.DeviceName }) })
        });
    }
}
