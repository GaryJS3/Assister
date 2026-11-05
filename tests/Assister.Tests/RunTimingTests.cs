using System.Text.Json;
using Assister.Components.Services;
namespace Assister.Tests;
public class RunTimingTests
{
    [Theory]
    [InlineData(true, 3500, false)]
    [InlineData(false, 3000, true)]
    public void RequestLatencyExcludesCaptureAndLabelsEstimates(bool measured, double duration, bool estimated)
    {
        var playbackMetadata = measured ? new { audioReadyAt="2026-11-04T12:00:05Z",playbackStartedAt="2026-11-04T12:00:05.500Z" } : (object)new {audioReadyAt="2026-11-04T12:00:05Z"};
        var run=JsonSerializer.SerializeToElement(new{startedAt="2026-11-04T12:00:00Z",steps=new object[]{
            new{name="Microphone audio",kind="Input",sequence=1,startedAt="2026-11-04T12:00:00Z",durationMilliseconds=2000,output=new{audioInputCompletedAt="2026-11-04T12:00:02Z",audioDurationMilliseconds=2000}},
            new{name="Satellite playback",kind="Playback",sequence=2,startedAt="2026-11-04T12:00:05Z",durationMilliseconds=1000,metadata=playbackMetadata}}});
        var latency=Assert.Single(RunTimings.Lanes(run),l=>l.Kind=="latency");
        Assert.Equal(2000,latency.Offset);Assert.Equal(duration,latency.Duration);Assert.Equal(estimated,latency.Estimated);
    }
    [Fact]
    public void MissingTimesDoNotInventLatencyAndBarsStayBounded()
    {
        using var doc=JsonDocument.Parse("{\"steps\":[]}");
        Assert.Empty(RunTimings.Lanes(doc.RootElement));
        Assert.Equal("0%",RunTimings.Percent(-20,100));Assert.Equal("100%",RunTimings.Percent(200,100));
    }
}
