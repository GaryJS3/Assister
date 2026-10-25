using System.Buffers.Binary;
using Assister.Voice;
using Assister.Contracts;
using Assister.Satellites;
using System.Runtime.CompilerServices;

namespace Assister.Tests;

public sealed class AdaptiveEndpointingTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task ShadowNeverEndsCaptureAndRemoteCaptureWaitsForProviderEnd(bool Remote, bool Shadow)
    {
        var Satellite = new FakeSatellite(Remote);
        var Classifier = new FakeClassifier(.99);
        var Frames = 0;
        await foreach (var _ in AdaptiveEndpointing.CaptureAsync(Satellite, Classifier, new(), Shadow, default)) Frames++;
        if (Shadow)
        {
            Assert.Equal(120, Frames); Assert.Equal(0, Satellite.EndRequests); Assert.False(Satellite.Configured);
        }
        else if (Remote)
        {
            Assert.Equal(1, Satellite.EndRequests); Assert.True(Satellite.Configured);
            Assert.Equal(Satellite.FramesAtRequest + 3, Frames);
        }
        else { Assert.InRange(Frames, 35, 38); Assert.Equal(1, Satellite.LocalEnds); }
    }
    [Fact]
    public async Task ModelFailureUsesNineHundredMillisecondsOfQuiet()
    {
        var Satellite = new FakeSatellite(false);
        var Frames = 0;
        await foreach (var _ in AdaptiveEndpointing.CaptureAsync(Satellite, new FakeClassifier(null), new(), false, default)) Frames++;
        Assert.Equal(65, Frames); // 400 ms speech + 900 ms fallback.
    }
    [Fact]
    public async Task UnsupportedAudioInShadowPassesThroughWithoutAnalysis()
    {
        var Satellite = new FakeSatellite(true) { SampleRate = 48000 };
        var Model = new FakeClassifier(.99);
        var Count = 0;
        await foreach (var Chunk in AdaptiveEndpointing.CaptureAsync(Satellite, Model, new(), true, default))
        { Assert.Equal(48000, Chunk.SampleRate); Count++; }
        Assert.Equal(120, Count); Assert.Equal(0, Model.Calls); Assert.Equal(0, Satellite.EndRequests);
    }
    [Fact]
    public async Task ShadowPreservesAnExistingLocalConservativeEndpoint()
    {
        var Satellite = new FakeSatellite(false);
        var Count = 0;
        var ReferenceCount = 0;
        await foreach (var _ in VoiceActivityDetector.UntilSilenceAsync(new FakeSatellite(false), .004, default)) ReferenceCount++;
        var Baseline = VoiceActivityDetector.UntilSilenceAsync(Satellite, .004, default);
        await foreach (var _ in AdaptiveEndpointing.CaptureAsync(Satellite, new FakeClassifier(.99), new(), true, default, Source: Baseline)) Count++;
        Assert.Equal(ReferenceCount, Count);
        Assert.Equal(1, Satellite.LocalEnds);
        Assert.Equal(0, Satellite.EndRequests);
    }
    [Fact]
    public async Task UnsupportedProviderCannotBeControlled()
    {
        var Satellite = new FakeSatellite(true) { SupportsAdaptiveEndpointing = false };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await foreach (var _ in AdaptiveEndpointing.CaptureAsync(Satellite, new FakeClassifier(.99), new(), false, default)) { } });
        Assert.False(Satellite.Configured);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EndOfCaptureDoesNotWaitForeverForAnalysis(bool Cooperative)
    {
        var Model = new DelayedClassifier(Cooperative);
        async Task Drain()
        {
            await foreach (var _ in AdaptiveEndpointing.CaptureAsync(new FakeSatellite(false), Model,
                new(InferenceTimeoutMs: 100), true, default)) { }
        }
        await Drain().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Model.Started);
        if (Cooperative) Assert.True(Model.Cancelled);
    }
    [Theory]
    [InlineData(.99, 300, "semantic-complete")]
    [InlineData(.5, 900, "silence-fallback")]
    [InlineData(.01, 1600, "incomplete-timeout")]
    [InlineData(null, 900, "silence-fallback")]
    public void ConfidenceControlsPauseAndFailuresRetainConservativeFallback(double? Probability, int ExpectedQuiet, string Reason)
    {
        var State = new AdaptiveTurnState(new());
        Feed(State, 400, true); Feed(State, 300, false);
        Assert.True(State.ShouldScore);
        var Snapshot = State.BeginScore();
        Assert.True(State.Apply(Snapshot.Revision, Probability));
        while (State.QuietMs < ExpectedQuiet)
        {
            Assert.Null(State.Decision);
            Feed(State, 20, false);
        }
        Assert.Equal(Reason, State.Decision);
        Assert.False(State.ShouldScore);
    }
    [Fact]
    public void ResumedSpeechInvalidatesAnInFlightCompleteDecision()
    {
        var State = new AdaptiveTurnState(new());
        Feed(State, 400, true); Feed(State, 300, false);
        var Old = State.BeginScore();
        Feed(State, 200, true);
        Assert.False(State.Apply(Old.Revision, .999));
        Assert.Null(State.Decision);
        Feed(State, 300, false);
        Assert.True(State.ShouldScore);
        Assert.True(State.Apply(State.BeginScore().Revision, .1));
        Feed(State, 600, false);
        Assert.Null(State.Decision);
    }
    [Fact]
    public void BackgroundDcAndBriefClicksCannotOpenSpeech()
    {
        var State = new AdaptiveTurnState(new());
        Feed(State, 80, true);
        var Dc = new byte[320 * 2];
        for (var I = 0; I < Dc.Length; I += 2) BinaryPrimitives.WriteInt16LittleEndian(Dc.AsSpan(I), 12000);
        for (var I = 0; I < 246; I++) State.Observe(Dc);
        Assert.False(State.SpeechSeen);
        Assert.False(State.ShouldScore);
        Assert.Equal("no-speech", State.Decision);
    }
    [Fact]
    public void SteadyBackgroundEnergyBelowTheRelativeNoiseThresholdIsNotSpeech()
    {
        var State = new AdaptiveTurnState(new());
        var Noise = Samples(20, true);
        for (var I = 0; I < Noise.Length; I += 2)
        {
            var Value = BinaryPrimitives.ReadInt16LittleEndian(Noise.AsSpan(I));
            BinaryPrimitives.WriteInt16LittleEndian(Noise.AsSpan(I), (short)(Value / 10));
        }
        for (var I = 0; I < 250; I++) State.Observe(Noise);
        Assert.False(State.SpeechSeen);
        Assert.Equal("no-speech", State.Decision);
    }
    [Fact]
    public void ContextIsBoundedAndSampleChunkingDoesNotChangeDecisions()
    {
        var Whole = new AdaptiveTurnState(new()); var Split = new AdaptiveTurnState(new());
        var Pcm = Samples(10000, true);
        Whole.Observe(Pcm);
        for (var I = 0; I < Pcm.Length; I += 2) Split.Observe(Pcm.AsSpan(I, 2));
        Feed(Whole, 300, false); Feed(Split, 300, false);
        Assert.Equal(Whole.SpeechRevision, Split.SpeechRevision);
        Assert.Equal(Whole.BeginScore().Pcm, Split.BeginScore().Pcm);
        Assert.Equal(128000, Whole.BeginScore().Pcm.Length);
        Assert.Equal(Whole.DurationMs, Split.DurationMs);
    }
    [Fact]
    public void DurationLimitAndMalformedPcmAreExplicit()
    {
        var State = new AdaptiveTurnState(new());
        Feed(State, 20000, true);
        Assert.Equal("duration-limit", State.Decision);
        Assert.Throws<InvalidDataException>(() => State.Observe(new byte[1]));
    }
    [Fact]
    public void SilentWhisperFeaturesAreFiniteAndHaveTheExpectedFloor()
    {
        var Features = SmartTurnFeatures.Extract([]);
        Assert.Equal(64000, Features.Length);
        Assert.All(Features, X => Assert.Equal(-1.5f, X));
    }
    [Fact]
    public void WhisperFeaturesMatchIndependentUpstreamGoldenValues()
    {
        using var Golden = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "smart-turn-features.json")));
        var Indices = Golden.RootElement.GetProperty("indices").EnumerateArray().Select(X => X.GetInt32()).ToArray();
        var Expected = Golden.RootElement.GetProperty("values").EnumerateArray().Select(X => X.GetSingle()).ToArray();
        var Bytes = Samples(1000, true).Concat(Samples(300, false)).ToArray();
        var Pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(Bytes).ToArray();
        var Features = SmartTurnFeatures.Extract(Pcm);
        for (var I = 0; I < Indices.Length; I++) Assert.InRange(Math.Abs(Features[Indices[I]] - Expected[I]), 0, 3e-5f);
    }
    internal static void Feed(AdaptiveTurnState State, int Ms, bool Speech) => State.Observe(Samples(Ms, Speech));
    internal static byte[] Samples(int Ms, bool Speech)
    {
        var Pcm = new byte[Ms * 32];
        if (Speech) for (var I = 0; I < Pcm.Length / 2; I++)
            BinaryPrimitives.WriteInt16LittleEndian(Pcm.AsSpan(I * 2), (short)(Math.Sin(2 * Math.PI * 240 * I / 16000) * 4000));
        return Pcm;
    }
    private sealed class FakeClassifier(double? Probability) : ITurnCompletionClassifier
    {
        public int Calls;
        public Task<TurnCompletionScore> ScoreAsync(ReadOnlyMemory<short> Pcm, CancellationToken Token)
        {
            Calls++;
            return Probability is { } Score ? Task.FromResult(new TurnCompletionScore(Score, 1, 1))
                : Task.FromException<TurnCompletionScore>(new IOException("model unavailable"));
        }
    }
    private sealed class DelayedClassifier(bool Cooperative) : ITurnCompletionClassifier
    {
        public bool Started, Cancelled;
        public async Task<TurnCompletionScore> ScoreAsync(ReadOnlyMemory<short> Pcm, CancellationToken Token)
        {
            Started = true;
            try { await Task.Delay(Timeout.Infinite, Cooperative ? Token : default); return new(.99, 1, 1); }
            finally { Cancelled = Token.IsCancellationRequested; }
        }
    }
    private sealed class FakeSatellite(bool Remote) : ISatelliteConnection, IProviderEndpointing, IAdaptiveEndpointingControl
    {
        public string SatelliteId => "fake";
        public string Name => "Fake";
        public string? Area => null;
        public bool OwnsEndpointing => Remote;
        public int SampleRate { get; init; } = 16000;
        public bool SupportsAdaptiveEndpointing { get; init; } = true;
        public bool Configured;
        public int EndRequests, LocalEnds, FramesAtRequest, Produced;
        public Task ConfigureEndpointingAsync(int MaximumQuietMs, CancellationToken Token)
        { Assert.InRange(MaximumQuietMs, 900, 2000); Configured = true; return Task.CompletedTask; }
        public Task<bool> RequestInputEndAsync(long Bytes, CancellationToken Token)
        { Assert.Equal(Produced * 640L, Bytes); FramesAtRequest = Produced; EndRequests++; return Task.FromResult(true); }
        public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([EnumeratorCancellation] CancellationToken Token)
        {
            for (var I = 0; I < 120; I++)
            {
                Token.ThrowIfCancellationRequested();
                if (EndRequests > 0 && Produced >= FramesAtRequest + 3) yield break;
                Produced++;
                yield return new(Samples(20, I < 20), SampleRate, 2, 1);
                await Task.Yield();
            }
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { LocalEnds++; return Task.CompletedTask; }
        public Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) => throw new NotSupportedException();
    }
}
