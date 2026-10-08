using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;

namespace Assister.Tools;

public sealed class SatelliteVolumeTool(SatelliteManager Satellites) : IAssisterTool
{
    public bool StateChanging => true;
    public LlmTool Definition => new(new("satellite_set_volume",
        "Adjust only the speaker on the current device when the current user asks. Supply exactly one of percent (0–100) or delta_percent (-100–100) for louder/quieter. Bare voice levels 0–10 map to percent by multiplying by 10. Use a delta of 10 or -10 for an unspecified louder/quieter request. Success means the command was sent, not verified physical volume.",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"percent":{"type":"integer","minimum":0,"maximum":100},"delta_percent":{"type":"integer","minimum":-100,"maximum":100}},"additionalProperties":false}""")));

    public async Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken Token)
    {
        ToolBroker.Validate(Arguments, Definition.Function.Parameters);
        var Absolute = Arguments.TryGetProperty("percent", out var Level);
        var Relative = Arguments.TryGetProperty("delta_percent", out var Delta);
        if (Absolute == Relative) return "{\"error\":\"Supply exactly one volume level or adjustment.\"}";
        var Id = Context.Request.SatelliteId;
        if (!Satellites.TryGet(Id, out var Connection))
            return "{\"error\":\"There is no connected speaker to adjust.\"}";
        if (!Satellites.State(Id).Capabilities.VolumeControl)
            return "{\"error\":\"Volume control is unavailable on this speaker.\"}";
        if (Relative && Satellites.State(Id).CurrentVolume is null)
            return "{\"error\":\"Current volume is unknown. Ask for an absolute volume level.\"}";
        if (!Context.AttemptedControls.Add("satellite-volume"))
            return "{\"error\":\"Volume was already attempted for this request. Do not retry.\"}";
        var Percent = Absolute ? Level.GetInt32() : (int)Math.Clamp(Math.Round(Satellites.State(Id).CurrentVolume!.Value * 100) + Delta.GetInt32(), 0, 100);
        await Connection!.SendEventAsync(new("set-volume", (Percent / 100d).ToString(System.Globalization.CultureInfo.InvariantCulture)), Token);
        return JsonSerializer.Serialize(new { status = "sent", percent = Percent });
    }
}
