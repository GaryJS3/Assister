using Assister.Diagnostics;

namespace Assister.Tests;

public sealed class VoiceLatencyTests
{
    [Fact]
    public void MilestonesUseFirstObservationAndDoNotInventDeviceTiming()
    {
        using var Store = new RunStore();
        Guid Id;
        using (var Run = RunTracing.BeginRun(Store, "voice"))
        {
            Id = RunTracing.RunId;
            using (var Latency = new VoiceLatency())
            {
                Latency.Mark("captureCompleted");
                Latency.Mark("transcriptFinal");
                Latency.Mark("firstSynthesizedPcm");
                Latency.Mark("captureCompleted");
            }
            Run.Complete("succeeded");
        }
        var Step = Assert.Single(Store.Get(Id)!.Steps);
        var Metadata = Step.Metadata!.Value;
        var Marks = Metadata.GetProperty("milestones");
        Assert.True(Marks.GetProperty("captureCompleted").GetDouble() <= Marks.GetProperty("transcriptFinal").GetDouble());
        Assert.True(Marks.GetProperty("firstSynthesizedPcm").GetDouble() <= Marks.GetProperty("pipelineFinished").GetDouble());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Metadata.GetProperty("firstAudioPlayedMilliseconds").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Metadata.GetProperty("endpointDecisionMilliseconds").ValueKind);
    }

    [Fact]
    public void MissingMilestonesStayAbsentOnEarlyFailure()
    {
        using var Store = new RunStore();
        Guid Id;
        using (var Run = RunTracing.BeginRun(Store, "voice"))
        {
            Id = RunTracing.RunId;
            using (var Latency = new VoiceLatency()) Latency.Mark("captureCompleted");
            Run.Complete("stt-failed");
        }
        var Marks = Assert.Single(Store.Get(Id)!.Steps).Metadata!.Value.GetProperty("milestones");
        Assert.False(Marks.TryGetProperty("firstSynthesizedPcm", out _));
        Assert.True(Marks.TryGetProperty("pipelineFinished", out _));
    }
}
