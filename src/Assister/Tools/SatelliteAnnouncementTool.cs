using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;

namespace Assister.Tools;

public sealed class SatelliteAnnouncementTool(AnnouncementHandler Announcements) : IAssisterTool
{
    public bool StateChanging => true;
    public LlmTool Definition => new(new("satellite_announce",
        "Announce a message only when the current user requests it. target is the explicit satellite name or all. Omit target when unspecified; return the resulting numbered destination question verbatim and wait for the user's selection. Never default to the current satellite or all. Report playback failures accurately.",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"message":{"type":"string"},"target":{"type":"string"}},"required":["message"],"additionalProperties":false}""")));
    public async Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken Token)
    {
        ToolBroker.Validate(Arguments, Definition.Function.Parameters);
        if (!Context.AttemptedControls.Add("satellite-announcement")) return "{\"error\":\"Announcement already attempted. Do not retry.\"}";
        return JsonSerializer.Serialize(new { response = await Announcements.ExecuteAsync(Arguments.GetProperty("message").GetString()!,
            Arguments.TryGetProperty("target", out var Target) ? Target.GetString() : null, Context.Conversation, Token, Context.Request.SatelliteId) });
    }
}
