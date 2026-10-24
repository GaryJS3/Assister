using System.Diagnostics;

namespace Assister.Diagnostics;

// Elapsed times share a server monotonic clock. Device playback is deliberately not inferred.
public sealed class VoiceLatency : IDisposable
{
    private readonly long Started = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, double> Milestones = new();
    private readonly object Gate = new();
    public void Mark(string Name)
    {
        lock (Gate) Milestones.TryAdd(Name, Stopwatch.GetElapsedTime(Started).TotalMilliseconds);
    }
    public void Dispose()
    {
        Mark("pipelineFinished");
        using var Step = RunTracing.Start("Playback", "Voice latency", "Server monotonic milestones; PCM availability and transport completion do not establish audible playback.");
        lock (Gate)
            Step.Metadata(new
            {
                clock = "server-monotonic", unit = "milliseconds-from-instrumentation-start", milestones = new Dictionary<string, double>(Milestones),
                actualSpeechEndMilliseconds = (double?)null, endpointDecisionMilliseconds = (double?)null,
                firstAudioDeliveredMilliseconds = (double?)null, firstAudioPlayedMilliseconds = (double?)null,
                limitation = "Capture completion includes provider endpointing/transport delay. PCM offered to a transport may still be buffered. No device clock synchronization or physical playback measurement."
            });
        Step.Complete();
    }
}
