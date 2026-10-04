using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Voice;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class StreamingVoiceTests
{
    [Fact]
    public async Task IncompleteModelStreamCannotDeliverACompletedBufferedReply()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Coordinator = new StreamingCoordinator(Fails: true);
        var Satellite = new OutputSatellite(true);
        var Tts = new RecordingTts();
        var Manager = new SatelliteManager();
        Manager.Register(Satellite);
        var Work = new VoicePipeline(new Stt(), Tts, Coordinator, Manager, Config()).RunAsync(Satellite, null, Timeout.Token);
        await Tts.Started.Task.WaitAsync(Timeout.Token);
        Coordinator.Release.TrySetResult();
        var Result = await Work;
        Assert.Equal("processing-failed", Result.Outcome);
        Assert.Equal("The language model is unavailable.", Result.Request!.Response);
        Assert.False(Satellite.FirstAudio.Task.IsCompleted);
        Assert.DoesNotContain(Satellite.Events, Event => Event.Type == "finished");
        Assert.Equal(0, Manager.ActiveSessionCount);
    }
    [Fact]
    public void SentenceBoundariesPreserveDecimalsAndSplitDeltas()
    {
        var Buffer = new SentenceBuffer();
        Assert.Empty(Buffer.Append("It is 74."));
        Assert.Empty(Buffer.Append("5°F."));
        Assert.Equal(["It is 74.5°F."], Buffer.Append(" Next"));
        Assert.Equal(["Next sentence!", "Last fragment"], Buffer.Append(" sentence! Last fragment", Final: true));
        Assert.Empty(Buffer.Append("", Final: true));
        Assert.All(new SentenceBuffer().Append(new string('a', 1400), Final: true), Part => Assert.InRange(Part.Length, 1, 512));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynthesisStartsBeforeModelCompletesAndReplyIsNotDuplicated(bool BufferedPlayback)
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Coordinator = new StreamingCoordinator();
        var Tts = new RecordingTts();
        var Satellite = new OutputSatellite(BufferedPlayback);
        var Manager = new SatelliteManager();
        Manager.Register(Satellite);
        var Work = new VoicePipeline(new Stt(), Tts, Coordinator, Manager, Config()).RunAsync(Satellite, null, Timeout.Token);
        await Tts.Started.Task.WaitAsync(Timeout.Token);
        Assert.False(Coordinator.Finished);
        if (!BufferedPlayback) { await Satellite.FirstAudio.Task.WaitAsync(Timeout.Token); }
        Coordinator.Release.TrySetResult();
        var Result = await Work;
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal(["It is 74.5 degrees Fahrenheit.", "Have a nice day."], Tts.Text);
        Assert.Equal(2, Satellite.Chunks);
        Assert.Equal(0, Manager.ActiveSessionCount);
        Assert.Single(Satellite.Events, Event => Event.Type == "response");
        Assert.Equal(Result.Request!.ConversationId, Result.Session.ConversationId);
    }

    [Fact]
    public async Task SynthesisFailureRetainsTextAndDoesNotBlockGeneration()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Coordinator = new StreamingCoordinator();
        var Tts = new RecordingTts(Fail: true);
        var Satellite = new OutputSatellite(false);
        var Manager = new SatelliteManager();
        Manager.Register(Satellite);
        var Work = new VoicePipeline(new Stt(), Tts, Coordinator, Manager, Config()).RunAsync(Satellite, null, Timeout.Token);
        await Tts.Started.Task.WaitAsync(Timeout.Token);
        Coordinator.Release.TrySetResult();
        var Result = await Work;
        Assert.Equal("tts-failed", Result.Outcome);
        Assert.Equal("It is 74.5°F. Have a nice day.", Result.Request!.Response);
        Assert.Equal(0, Manager.ActiveSessionCount);
    }

    [Fact]
    public async Task StopCancelsStreamingAndNextTurnCanSucceed()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Coordinator = new StreamingCoordinator();
        var Tts = new RecordingTts();
        var Satellite = new OutputSatellite(false);
        var Manager = new SatelliteManager();
        Manager.Register(Satellite);
        var Pipeline = new VoicePipeline(new Stt(), Tts, Coordinator, Manager, Config());
        var Work = Pipeline.RunAsync(Satellite, null, Timeout.Token);
        await Satellite.FirstAudio.Task.WaitAsync(Timeout.Token);
        Assert.True(await Manager.StopAsync("stream", Timeout.Token));
        Assert.Equal("cancelled", (await Work).Outcome);
        Assert.Equal(0, Manager.ActiveSessionCount);
        Coordinator.Release.TrySetResult();
        Assert.Equal("succeeded", (await Pipeline.RunAsync(Satellite, null, Timeout.Token)).Outcome);
        Assert.Equal(0, Manager.ActiveSessionCount);
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Voice:StreamingEnabled"] = "true" }).Build();
    private sealed class Stt : ISpeechToTextProvider
    {
        public Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
            => Task.FromResult(new TranscriptionResult("hello", "en"));
    }
    private sealed class StreamingCoordinator(bool Fails = false) : IStreamingRequestCoordinator
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finished { get; private set; }
        public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken Token) => throw new NotSupportedException();
        public async Task<RequestResult> ProcessStreamingAsync(UserRequest Request, Func<string, CancellationToken, Task> OnText, CancellationToken Token)
        {
            await OnText("It is 74.5°F. ", Token);
            await Release.Task.WaitAsync(Token);
            if (Fails) { return new("The language model is unavailable.", Guid.NewGuid(), "language-model", Guid.NewGuid(), "unavailable", [], null, 1); }
            await OnText("Have a nice day.", Token);
            Finished = true;
            return new("It is 74.5°F. Have a nice day.", Guid.NewGuid(), "language-model", Guid.NewGuid(), "succeeded", [], null, 1);
        }
    }
    private sealed class RecordingTts(bool Fail = false) : ITextToSpeechProvider
    {
        public List<string> Text { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Value, TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken Token)
        {
            Text.Add(Value);
            Started.TrySetResult();
            await Task.Yield();
            if (Fail) { throw new IOException(); }
            Token.ThrowIfCancellationRequested();
            yield return new(new byte[2], 16000, 2, 1);
        }
    }
    private sealed class OutputSatellite(bool BufferedPlayback) : ISatelliteConnection, IStreamingAudioPlayback
    {
        public string SatelliteId => "stream";
        public string Name => "Stream";
        public string? Area => null;
        public bool SupportsStreamingPlayback => !BufferedPlayback;
        public int Chunks { get; private set; }
        public List<SatelliteEvent> Events { get; } = [];
        public TaskCompletionSource FirstAudio { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[2], 16000, 2, 1); }
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token)
        {
            await foreach (var _ in Audio.WithCancellation(Token))
            { Chunks++; if (!BufferedPlayback) { FirstAudio.TrySetResult(); } }
            if (BufferedPlayback)
            {
                Assert.Contains(Events, Event => Event.Type == "response");
                FirstAudio.TrySetResult();
            }
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { Events.Add(Event); return Task.CompletedTask; }
    }
}
