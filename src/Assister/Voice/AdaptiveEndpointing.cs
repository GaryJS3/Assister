using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Satellites;

namespace Assister.Voice;

public interface IAdaptiveEndpointingControl
{
    bool SupportsAdaptiveEndpointing { get; }
    Task ConfigureEndpointingAsync(int MaximumQuietMs, CancellationToken Token);
    Task<bool> RequestInputEndAsync(long ObservedAudioBytes, CancellationToken Token);
}

public static class AdaptiveEndpointing
{
    public static async IAsyncEnumerable<AudioChunk> CaptureAsync(ISatelliteConnection Satellite, ITurnCompletionClassifier Classifier,
        AdaptiveTurnOptions Options, bool Shadow, [EnumeratorCancellation] CancellationToken Token)
    {
        var Control = Satellite as IAdaptiveEndpointingControl;
        var Remote = Satellite is IProviderEndpointing { OwnsEndpointing: true };
        if (!Shadow && Remote && Control is not { SupportsAdaptiveEndpointing: true })
            throw new InvalidOperationException("Provider has not negotiated adaptive endpointing.");
        if (!Shadow && Remote) await Control!.ConfigureEndpointingAsync(Options.IncompleteQuietMs, Token);
        using var Lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var Trace = RunTracing.Start("Input", "Adaptive endpointing", "Analyze quiet pauses with a CPU semantic model; reject stale results when speech resumes.");
        var State = new AdaptiveTurnState(Options);
        Task<TurnCompletionScore>? Pending = null;
        CancellationTokenSource? Analysis = null;
        long Revision = -1;
        var Requested = false;
        long LastRequestRevision = -1;
        var ShadowDecision = false;
        var Bypassed = false;
        var Calls = 0; var Failures = 0; var Stale = 0;
        var CleanupTimedOut = false;
        double FeatureMs = 0, InferenceMs = 0;
        try
        {
            await foreach (var Chunk in Satellite.ReceiveAudioAsync(Token).WithCancellation(Token))
            {
                if (Chunk.SampleRate != 16000 || Chunk.SampleWidth != 2 || Chunk.Channels != 1)
                {
                    if (!Shadow && !Remote) throw new InvalidDataException("Adaptive endpointing requires 16 kHz mono S16 PCM.");
                    if (!Bypassed) Trace.Metadata(new { bypassed = true, reason = "unsupported-pcm-format", shadow = Shadow });
                    Bypassed = true; Analysis?.Cancel();
                }
                if (Bypassed) { yield return Chunk; continue; }
                State.Observe(Chunk.Pcm.Span);
                if (Pending is { IsCompleted: true })
                {
                    try
                    {
                        var Score = await Pending;
                        FeatureMs += Score.FeatureMilliseconds; InferenceMs += Score.InferenceMilliseconds;
                        if (!State.Apply(Revision, Score.Probability)) Stale++;
                    }
                    catch (Exception Error) when (!Token.IsCancellationRequested && Error is not OutOfMemoryException)
                    { if (Revision != State.SpeechRevision) Stale++; else Failures++; State.Apply(Revision, null); }
                    finally { Pending = null; Analysis?.Dispose(); Analysis = null; }
                }
                if (Pending is not null && Revision != State.SpeechRevision) Analysis!.Cancel();
                if (!Requested && Pending is null && State.ShouldScore)
                {
                    var Snapshot = State.BeginScore(); Revision = Snapshot.Revision;
                    Analysis = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                    // Observation on slower CPUs needs a useful score, while active commits
                    // retain the stricter deadline. Neither mode blocks capture on inference.
                    Analysis.CancelAfter(Shadow ? Math.Max(1000, Options.InferenceTimeoutMs) : Options.InferenceTimeoutMs);
                    Calls++;
                    try { Pending = Classifier.ScoreAsync(Snapshot.Pcm, Analysis.Token); }
                    catch (Exception Error) when (!Token.IsCancellationRequested && Error is not OutOfMemoryException)
                    { Failures++; State.Apply(Revision, null); Analysis.Dispose(); Analysis = null; }
                }
                yield return Chunk;
                if (!Requested && State.Decision is { } Decision)
                {
                    if (!ShadowDecision)
                    {
                        Trace.Metadata(new { shadow = Shadow, decision = Decision, audioMilliseconds = State.DurationMs,
                            quietMilliseconds = State.QuietMs, completionProbability = State.CompletionProbability,
                            decisionObservedAt = DateTimeOffset.UtcNow, observedAudioBytes = State.AudioBytes, providerOwned = Remote });
                        ShadowDecision = true;
                    }
                    if (!Shadow)
                    {
                        if (!Remote)
                        {
                            await Satellite.SendEventAsync(new("end-of-speech", Decision), Token);
                            Trace.Complete();
                            yield break;
                        }
                        // Provider confirms the request and still supplies its own audio_end.
                        // A rejected stale request cannot truncate local capture.
                        if (LastRequestRevision != State.SpeechRevision)
                        {
                            LastRequestRevision = State.SpeechRevision;
                            Requested = await Control!.RequestInputEndAsync(State.AudioBytes, Token);
                        }
                    }
                }
            }
            Trace.Complete();
        }
        finally
        {
            Lifetime.Cancel();
            if (Pending is not null)
            {
                try { await Pending.WaitAsync(TimeSpan.FromMilliseconds(Options.InferenceTimeoutMs + 100)); }
                catch (TimeoutException)
                {
                    CleanupTimedOut = true;
                    _ = Pending.ContinueWith(Task => _ = Task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception Error) when (Error is not OutOfMemoryException) { }
            }
            Analysis?.Dispose();
            Trace.Output(new { calls = Calls, modelFailures = Failures, staleResults = Stale,
                featureMilliseconds = FeatureMs, inferenceMilliseconds = InferenceMs, providerAcknowledged = Requested,
                analysisCleanupTimedOut = CleanupTimedOut });
        }
    }
}
