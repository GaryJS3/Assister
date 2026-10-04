using Assister.Contracts;
using Assister.Satellites;
using Assister.Voice;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class VoicePipelineTests
{
    [Fact]
    public async Task SuccessfulVoiceRunUsesSharedRoutingAndRetainsConversation()
    {
        var Satellite = new FakeSatellite();
        var Coordinator = new FakeCoordinator();
        var Manager = new SatelliteManager();
        var Result = await new VoicePipeline(new FakeStt(), new FakeTts(), Coordinator, Manager, new ConfigurationBuilder().Build())
            .RunAsync(Satellite, null, CancellationToken.None);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal("bedroom", Coordinator.Request!.SatelliteId);
        Assert.Equal("Bedroom", Coordinator.Request.Area);
        Assert.Equal(Result.Request!.ConversationId, Result.Session.ConversationId);
        Assert.Equal(1, Satellite.AudioChunks);
        Assert.Contains(Satellite.Events, Event => Event.Type == "response" && Event.Text == "74°F");
        Assert.Equal(0, Manager.ActiveSessionCount);
    }

    [Fact]
    public async Task SttFailureCannotInvokeRoutingAndTtsFailureKeepsCompletedText()
    {
        var Manager = new SatelliteManager();
        var Coordinator = new FakeCoordinator();
        var Config = new ConfigurationBuilder().Build();
        var Result = await new VoicePipeline(new FakeStt(true), new FakeTts(), Coordinator, Manager, Config).RunAsync(new FakeSatellite(), null, CancellationToken.None);
        Assert.Equal("stt-failed", Result.Outcome);
        Assert.Null(Coordinator.Request);
        Result = await new VoicePipeline(new FakeStt(), new FakeTts(true), Coordinator, Manager, Config).RunAsync(new FakeSatellite(), null, CancellationToken.None);
        Assert.Equal("playback-failed", Result.Outcome);
        Assert.Equal("74°F", Result.Request!.Response);
        Assert.Equal(0, Manager.ActiveSessionCount);
    }

    private sealed class FakeCoordinator : IRequestCoordinator
    {
        public UserRequest? Request { get; private set; }
        public Task<RequestResult> ProcessAsync(UserRequest Value, CancellationToken CancellationToken)
        {
            Request = Value;
            return Task.FromResult(new RequestResult("74°F", Guid.NewGuid(), "direct-intent", Guid.NewGuid(), "succeeded", [], 1, 1));
        }
    }
    private sealed class FakeStt(bool Fail = false) : ISpeechToTextProvider
    {
        public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken CancellationToken)
        {
            await foreach (var _ in Audio.WithCancellation(CancellationToken)) { }
            if (Fail) { throw new IOException(); }
            return new("what is the temperature in the bedroom", "en");
        }
    }
    private sealed class FakeTts(bool Fail = false) : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken CancellationToken)
        {
            await Task.Yield();
            if (Fail) { throw new IOException(); }
            Assert.Equal("74 degrees Fahrenheit", Text);
            yield return new(new byte[2], 16000, 2, 1);
        }
    }
    private sealed class FakeSatellite : ISatelliteConnection
    {
        public string SatelliteId => "bedroom";
        public string Name => "Bedroom";
        public string? Area => "Bedroom";
        public List<SatelliteEvent> Events { get; } = [];
        public int AudioChunks { get; private set; }
        public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken CancellationToken)
        {
            await Task.Yield();
            yield return new(new byte[2], 16000, 2, 1);
        }
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken CancellationToken)
        {
            await foreach (var _ in Audio.WithCancellation(CancellationToken)) { AudioChunks++; }
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken CancellationToken)
        {
            Events.Add(Event);
            return Task.CompletedTask;
        }
    }
}
