using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;

namespace Assister.Tools;

public sealed class SatelliteAnnouncementTool(AnnouncementHandler Announcements) : IAssisterTool
{
    public bool StateChanging => true;
    public LlmTool Definition => new(new("satellite_announce",
        "Announce a message only when the current user requests it. target is the explicit destination name, all, or 'group NAME' for a mixed device group. Groups can contain satellites, Windows and Android clients and are scoped to an owner. Rich requests otherwise send notifications to clients of the same owner; satellite requests otherwise use speakers. Omit target when unspecified; return the resulting numbered destination question verbatim and wait for the user's selection. Never default to the current destination or all. Group delivery is queued, not confirmed playback. For satellites, high-importance adds beeps and repeats once; acknowledge repeats every ten minutes until acknowledged; alarm repeats every ten seconds until acknowledged.",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"message":{"type":"string"},"target":{"type":"string"},"importance":{"type":"string","enum":["notify","high-importance","acknowledge","alarm"]}},"required":["message"],"additionalProperties":false}""")));
    public async Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken Token)
    {
        ToolBroker.Validate(Arguments, Definition.Function.Parameters);
        if (!Context.AttemptedControls.Add("satellite-announcement")) return "{\"error\":\"Announcement already attempted. Do not retry.\"}";
        if (!AnnouncementLevels.TryParse(Arguments.TryGetProperty("importance", out var Level) ? Level.GetString() : null, out var Importance)) throw new ArgumentException("Invalid announcement importance.");
        return JsonSerializer.Serialize(new { response = await Announcements.ExecuteAsync(Arguments.GetProperty("message").GetString()!,
            Arguments.TryGetProperty("target", out var Target) ? Target.GetString() : null, Context.Conversation, Token, Context.Request.SatelliteId, Importance) });
    }
}
