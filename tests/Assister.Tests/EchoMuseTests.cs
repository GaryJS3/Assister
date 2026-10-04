using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Satellites;
using Assister.Voice;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class EchoMuseTests
{
    [Theory]
    [InlineData("processing", false)]
    [InlineData("synthesis", false)]
    [InlineData("playback", false)]
    [InlineData("playback", true)]
    public async Task StopCancelsEachOutputStageAndLateEventsCannotRestartPlayback(string Stage, bool Button)
    {
        using var Audio = new VoiceAudioStore();
        var Manager = new SatelliteManager();
        var Reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Messages = new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
        var Device = new EchoMuseConnection("device", "logical", "Bedroom", null, (Value, _) =>
        {
            var Json = Message(Value);
            Messages.Enqueue(Json);
            if (EchoMuseConnection.Text(Json, "type") == "turn_response") { Reached.TrySetResult(); }
            return Task.CompletedTask;
        }, Audio, Config, Manager);
        Manager.Register(Device);
        using var Turn = Device.Start("session", null, CancellationToken.None)!;
        var Work = new VoicePipeline(new Stt(), new StageTts(Stage, Reached), new StageCoordinator(Stage, Reached),
            Manager, Config).RunAsync(Turn, null, Turn.Token);
        Device.Handle(Message(new { type = "audio", sessionId = "session", deviceId = "device", data = "AAA=" }));
        Device.Handle(Message(new { type = "audio_end", sessionId = "session", deviceId = "device", reason = "speech_end" }));
        await Reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (Button) { Device.Handle(Message(new { type = "turn_cancel", sessionId = "session", deviceId = "device" })); }
        else { await Manager.StopAsync("logical", CancellationToken.None); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, Manager.ActiveSessionCount);
        Assert.Equal(VoiceSessionState.Cancelled, Manager.State("logical").Activity);
        Assert.False(Device.Handle(Message(new { type = "play_started", sessionId = "session", deviceId = "device" })));
        Assert.False(Device.Handle(Message(new { type = "play_finished", sessionId = "session", deviceId = "device" })));
        if (Stage == "playback")
        {
            var Response = Messages.Single(Item => EchoMuseConnection.Text(Item, "type") == "turn_response");
            var AudioId = Guid.Parse(Path.GetFileNameWithoutExtension(new Uri(Response.GetProperty("audioUrl").GetString()!).AbsolutePath));
            Assert.Null(Audio.Get(AudioId));
        }
        Assert.Equal(Button ? 0 : 1, Messages.Count(Item => EchoMuseConnection.Text(Item, "type") == "turn_cancel"));
        using var Next = Device.Start("next", null, CancellationToken.None);
        Assert.NotNull(Next);
        Device.Release(Turn);
        Assert.False(Device.Handle(Message(new { type = "turn_cancel", sessionId = "session", deviceId = "device" })));
        Assert.False(Next!.Token.IsCancellationRequested);
    }

    private sealed class StageCoordinator(string Stage, TaskCompletionSource Reached) : IRequestCoordinator
    {
        public async Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken Token)
        {
            if (Stage == "processing") { Reached.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, Token); }
            return new("Reply", null, "direct-intent", Guid.NewGuid(), "succeeded", [], null, 1);
        }
    }
    private sealed class StageTts(string Stage, TaskCompletionSource Reached) : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [EnumeratorCancellation] CancellationToken Token)
        {
            if (Stage == "synthesis") { Reached.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, Token); }
            yield return new(new byte[2], 16000, 2, 1);
        }
    }
    private static IConfiguration Config => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Assister:PublicUrl"] = "http://assister:8080", ["SatelliteBridge:UseEnergyVad"] = "true" }).Build();
    private static JsonElement Message(object Value) => JsonSerializer.SerializeToElement(Value);
    [Fact]
    public async Task VoiceUsesSharedPipelineExactIdsAndAcknowledged48kWav()
    {
        using var Audio = new VoiceAudioStore(); var Manager = new SatelliteManager();
        var Messages = new List<JsonElement>(); var Coordinator = new Coordinator(); var Store = new RunStore();
        EchoMuseConnection? Device = null;
        Guid AudioId = default;
        Device = new("native-device", "logical", "Bedroom", "bedroom", (Value, _) =>
        {
            var Json = Message(Value); Messages.Add(Json);
            if (EchoMuseConnection.Text(Json, "type") == "turn_response")
            {
                Assert.Equal("session", EchoMuseConnection.Text(Json, "sessionId"));
                Assert.Equal("native-device", EchoMuseConnection.Text(Json, "deviceId"));
                AudioId = Guid.Parse(Path.GetFileNameWithoutExtension(new Uri(Json.GetProperty("audioUrl").GetString()!).AbsolutePath));
                var Wave = Audio.Get(AudioId)!;
                Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(Wave.AsSpan(24)));
                Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(Wave.AsSpan(22)));
                Assert.Equal(44 + 4800 * 2, Wave.Length);
                Assert.False(Device!.Handle(Message(new { type = "play_finished", sessionId = "session", deviceId = "other" })));
                Assert.False(Device.Handle(Message(new { type = "play_finished", sessionId = "old", deviceId = "native-device" })));
                Device.Handle(Message(new { type = "play_started", sessionId = "session", deviceId = "native-device" }));
                Device.Handle(Message(new { type = "play_finished", sessionId = "session", deviceId = "native-device" }));
            }
            return Task.CompletedTask;
        }, Audio, Config, Manager);
        Manager.Register(Device);
        using var Turn = Device.Start("session", "hey_jarvis", CancellationToken.None)!;
        var Pipeline = new VoicePipeline(new Stt(), new Tts(), Coordinator, Manager, Config, Store);
        var Work = Pipeline.RunAsync(Turn, null, Turn.Token);
        Assert.Null(Device.Start("duplicate", null, CancellationToken.None));
        Assert.False(Device.Handle(Message(new { type = "audio", sessionId = "old", deviceId = "native-device", data = "AAA=" })));
        Assert.True(Device.Handle(Message(new { type = "audio", sessionId = "session", deviceId = "native-device", data = "AAA=" })));
        Assert.Equal(0, Coordinator.Calls);
        Assert.True(Device.Handle(Message(new { type = "audio_end", sessionId = "session", deviceId = "native-device", reason = "speech_end" })));
        var Result = await Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal(1, Coordinator.Calls);
        Assert.Single(Messages);
        Assert.Null(Audio.Get(AudioId));
        var Trace = Store.Get(Assert.Single(Store.Snapshot()).RunId)!;
        Assert.Equal("hey_jarvis", Trace.Steps.Single(Step => Step.Name == "Voice activation").Metadata!.Value.GetProperty("wakeWord").GetString());
        Assert.DoesNotContain(Trace.Steps, Step => Step.Name == "Speech endpointing");
        Assert.Equal(0, Manager.ActiveSessionCount);
        Device.Release(Turn);
        Assert.False(Device.Handle(Message(new { type = "turn_cancel", sessionId = "session", deviceId = "native-device" })));
    }
    [Theory]
    [InlineData("audio_end")]
    [InlineData("turn_cancel")]
    public async Task NoSpeechAndCancellationDoNotRouteOrRespond(string Kind)
    {
        using var Audio = new VoiceAudioStore(); var Manager = new SatelliteManager(); var Messages = new List<object>();
        var Device = new EchoMuseConnection("device", "logical", "Bedroom", null, (Value, _) => { Messages.Add(Value); return Task.CompletedTask; }, Audio, Config, Manager);
        Manager.Register(Device); using var Turn = Device.Start("session", null, CancellationToken.None)!;
        var Coordinator = new Coordinator();
        var Work = new VoicePipeline(new Stt(), new Tts(), Coordinator, Manager, Config).RunAsync(Turn, null, Turn.Token);
        Device.Handle(Message(new { type = Kind, sessionId = "session", deviceId = "device", reason = "no_speech_timeout" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Work);
        Assert.Equal(0, Coordinator.Calls); Assert.Empty(Messages); Assert.Equal(0, Manager.ActiveSessionCount);
    }
    [Fact]
    public async Task AnnouncementsHaveSeparateRequestIdsAndStopOnlyMatchingOutput()
    {
        using var Audio = new VoiceAudioStore(); var Manager = new SatelliteManager();
        var Messages = new List<JsonElement>();
        var Device = new EchoMuseConnection("device", "logical", "Bedroom", null, (Value, _) => { Messages.Add(Message(Value)); return Task.CompletedTask; }, Audio, Config, Manager);
        var Work = Device.SendAudioAsync(Chunks(), CancellationToken.None);
        while (Messages.Count == 0) await Task.Delay(1);
        var Play = Messages[0]; var Request = Play.GetProperty("requestId").GetString();
        Assert.Equal("announcement", Play.GetProperty("kind").GetString());
        Assert.Null(Device.Start("voice", null, CancellationToken.None));
        Assert.False(Device.Handle(Message(new { type = "play_finished", requestId = "old", deviceId = "device" })));
        Assert.False(Work.IsCompleted);
        await Device.SendEventAsync(new("stop-playback"), CancellationToken.None);
        Assert.Equal(Request, Messages[1].GetProperty("requestId").GetString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Messages.Count);
        Assert.False(Device.Handle(Message(new { type = "play_finished", requestId = Request, deviceId = "device", reason = "cancelled" })));
        using var Next = Device.Start("next", null, CancellationToken.None);
        Assert.NotNull(Next);
    }
    [Fact]
    public async Task DisconnectAndInvalidAudioCancelOnlyTheOwnedTurn()
    {
        using var Audio = new VoiceAudioStore(); var Manager = new SatelliteManager();
        var Device = new EchoMuseConnection("device", "logical", "Bedroom", null, (_, _) => Task.CompletedTask, Audio, Config, Manager);
        using var Turn = Device.Start("session", null, CancellationToken.None)!;
        Assert.False(Device.Handle(Message(new { type = "audio", sessionId = "session", deviceId = "device", data = "invalid" })));
        Assert.True(Turn.Token.IsCancellationRequested);
        Device.Release(Turn);
        using var Next = Device.Start("next", null, CancellationToken.None)!;
        Assert.False(Device.Handle(Message(new { type = "turn_cancel", sessionId = "session", deviceId = "device" })));
        Assert.False(Next.Token.IsCancellationRequested);
        Device.Disconnect(); Assert.True(Next.Token.IsCancellationRequested);
        await Task.CompletedTask;
    }
    [Fact]
    public void UnsupportedAudioFormatIsRejected()
    {
        Assert.True(EchoMuseProvider.ValidAudio(Message(new { audio = new { sampleRate = 16000, sampleWidth = 2, channels = 1, encoding = "pcm_s16le" } })));
        Assert.False(EchoMuseProvider.ValidAudio(Message(new { audio = new { sampleRate = 48000, sampleWidth = 2, channels = 1, encoding = "pcm_s16le" } })));
    }
    [Fact]
    public async Task ResamplingRealLengthSpeechPreservesDurationAndSampleValues()
    {
        var Pcm = new byte[22050 * 5 * 2];
        for (var Index = 0; Index < Pcm.Length; Index += 2) BinaryPrimitives.WriteInt16LittleEndian(Pcm.AsSpan(Index), 1000);
        var Wave = await VoiceAudioStore.Wave48kAsync(Input(), CancellationToken.None);
        Assert.Equal(44 + 48000 * 5 * 2, Wave.Length);
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(Wave.AsSpan(44)));
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(Wave.AsSpan(Wave.Length - 2)));
        async IAsyncEnumerable<AudioChunk> Input() { await Task.Yield(); yield return new(Pcm, 22050, 2, 1); }
    }
    private static async IAsyncEnumerable<AudioChunk> Chunks([EnumeratorCancellation] CancellationToken Token = default)
    { await Task.Yield(); Token.ThrowIfCancellationRequested(); yield return new(new byte[2205 * 2], 22050, 2, 1); }
    private sealed class Tts : ITextToSpeechProvider
    { public IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, CancellationToken Token) => Chunks(Token); }
    private sealed class Stt : ISpeechToTextProvider
    {
        public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
        { await foreach (var _ in Audio.WithCancellation(Token)) { } Token.ThrowIfCancellationRequested(); return new("what time is it", "en"); }
    }
    private sealed class Coordinator : IRequestCoordinator
    {
        public int Calls;
        public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken Token)
        { Calls++; return Task.FromResult(new RequestResult("Test reply", Guid.NewGuid(), "direct-intent", Guid.NewGuid(), "succeeded", [], null, 1)); }
    }
}
