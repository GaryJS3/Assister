using System.Diagnostics;

namespace Assister.Diagnostics;

// Only explicit application spans are retained; HTTP headers and credentials are never collected.
public static class RunTracing
{
    public static readonly ActivitySource Source = new("Assister.Runs");
    public static TraceStep Start(string Name, string Reason) => new(Source.StartActivity(Name), Reason);
}

public sealed class TraceStep : IDisposable
{
    private readonly Activity? Activity;
    public TraceStep(Activity? Activity, string Reason)
    {
        this.Activity = Activity;
        Activity?.SetTag("reason", Reason);
        Activity?.SetTag("outcome", "interrupted-or-failed");
    }
    public void Detail(string Key, object? Value)
    {
        var Text = Value?.ToString();
        Activity?.SetTag(Key, Text?.Length > 16384 ? Text[..16384] + " [truncated]" : Text);
    }
    public void Complete(string Outcome = "succeeded") => Activity?.SetTag("outcome", Outcome);
    public void Dispose() => Activity?.Dispose();
}

public sealed record StepSnapshot(string Id, string? ParentId, string Name, DateTime StartedAt,
    double DurationMilliseconds, bool Active, IReadOnlyDictionary<string, string?> Details);
public sealed record RunSnapshot(string Id, DateTime StartedAt, IReadOnlyList<StepSnapshot> Steps);

public sealed class RunStore : IDisposable
{
    private readonly object Gate = new();
    private readonly Dictionary<string, List<Activity>> Runs = new();
    private readonly ActivityListener Listener;
    public RunStore()
    {
        Listener = new ActivityListener
        {
            ShouldListenTo = Source => Source.Name == "Assister.Runs",
            Sample = (ref ActivityCreationOptions<ActivityContext> Options) => ActivitySamplingResult.AllData,
            ActivityStarted = Activity =>
            {
                lock (Gate)
                {
                    var Id = Activity.TraceId.ToString();
                    if (!Runs.TryGetValue(Id, out var Steps))
                    {
                        if (Runs.Count >= 200) { Runs.Remove(Runs.Keys.First()); }
                        Runs[Id] = Steps = [];
                    }
                    if (Steps.Count < 256) { Steps.Add(Activity); }
                }
            }
        };
        ActivitySource.AddActivityListener(Listener);
    }
    public IReadOnlyList<RunSnapshot> Snapshot()
    {
        lock (Gate)
        {
            return Runs.Select(Run => new RunSnapshot(Run.Key, Run.Value[0].StartTimeUtc,
                Run.Value.Select(Step => new StepSnapshot(Step.SpanId.ToString(), Step.ParentSpanId == default ? null : Step.ParentSpanId.ToString(),
                    Step.DisplayName, Step.StartTimeUtc, Step.Duration == TimeSpan.Zero
                        ? (DateTime.UtcNow - Step.StartTimeUtc).TotalMilliseconds : Step.Duration.TotalMilliseconds,
                    Step.Duration == TimeSpan.Zero, Step.TagObjects.ToDictionary(Tag => Tag.Key, Tag => Tag.Value?.ToString()))).ToArray()))
                .OrderByDescending(Run => Run.StartedAt).ToArray();
        }
    }
    public void Dispose() => Listener.Dispose();
}
