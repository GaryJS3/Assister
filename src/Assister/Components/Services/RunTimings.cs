using System.Globalization;
using System.Text.Json;
namespace Assister.Components.Services;

public sealed record TimelineLane(string Name, string Kind, double Offset, double Duration, bool Estimated = false, bool Nested = false);
public static class RunTimings
{
    public static double? Stamp(JsonElement value, string property) => DateTimeOffset.TryParse(value.Text(property), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToUnixTimeMilliseconds() : null;
    public static IReadOnlyList<TimelineLane> Lanes(JsonElement run)
    {
        var start = Stamp(run,"startedAt"); if(start is null)return [];
        var steps=run.Prop("steps").Items().OrderBy(s=>s.Number("sequence")).ToArray();
        var lanes=new List<TimelineLane>();
        var audio=steps.FirstOrDefault(s=>s.Text("name")=="Microphone audio");
        var stt=steps.FirstOrDefault(s=>s.Text("kind")=="SpeechToText");
        var playback=steps.FirstOrDefault(s=>s.Text("name")=="Satellite playback");
        var ended=Stamp(audio.Prop("output"),"audioInputCompletedAt")??Stamp(stt.Prop("metadata"),"audioInputCompletedAt");
        var measured=Stamp(playback.Prop("metadata"),"playbackStartedAt");
        var began=measured??Stamp(playback.Prop("metadata"),"audioReadyAt")??Stamp(playback,"startedAt");
        if(ended is not null && began>=ended)lanes.Add(new("Request → response","latency",Math.Max(0,ended.Value-start.Value),began.Value-ended.Value,measured is null));
        if(ended is null && run.Text("source")!="voice")
        {
            var formatted=steps.LastOrDefault(s=>s.Text("kind")=="ResponseFormatting");
            var formattedAt=Stamp(formatted,"startedAt");
            if(formattedAt is not null)lanes.Add(new("Request → text ready","latency",0,Math.Max(0,formattedAt.Value-start.Value+formatted.Number("durationMilliseconds"))));
        }
        if(audio.ValueKind==JsonValueKind.Object)lanes.Add(new("Audio capture","Input",0,Math.Max(0,audio.Prop("output").Number("audioDurationMilliseconds"))));
        if(ended is not null && stt.Prop("metadata").Prop("postAudioLatencyMilliseconds").ValueKind==JsonValueKind.Number)
            lanes.Add(new("STT finalization","SpeechToText",Math.Max(0,ended.Value-start.Value),Math.Max(0,stt.Prop("metadata").Number("postAudioLatencyMilliseconds"))));
        foreach(var step in steps)
        {
            if(step.Text("name") is "Microphone audio" or "Streaming delivery" or "Streaming speech synthesis")continue;
            var offset=Stamp(step,"startedAt");if(offset is null)continue;
            lanes.Add(new(step.Text("name"),step.Text("kind"),Math.Max(0,offset.Value-start.Value),Math.Max(0,step.Number("durationMilliseconds")),Nested:!string.IsNullOrEmpty(step.Text("parentId"))));
        }
        return lanes;
    }
    public static string Duration(double milliseconds) => milliseconds<1000 ? $"{milliseconds:0} ms" : $"{milliseconds/1000:0.00} s";
    public static string Percent(double value,double total) => (100*Math.Clamp(value/Math.Max(1,total),0,1)).ToString("0.###",CultureInfo.InvariantCulture)+"%";
}
