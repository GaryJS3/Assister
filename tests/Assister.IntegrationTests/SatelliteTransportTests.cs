using System.Threading.Channels;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Satellites;
using Assister.Satellites.Protocol;
using Protocol = Assister.Satellites.Protocol;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assister.IntegrationTests;

public sealed class SatelliteTransportTests
{
    [Fact]
    public async Task AuthenticatedBridgeRoutesAudioAndRejectsMissingCredentials()
    {
        await using var Factory = new Application();
        using var Http = Factory.CreateDefaultClient();
        using var Channel = GrpcChannel.ForAddress(Http.BaseAddress!, new() { HttpClient = Http });
        var Client = new SatelliteTransport.SatelliteTransportClient(Channel);
        using (var Unauthorized = Client.Connect())
        {
            await Unauthorized.RequestStream.WriteAsync(new() { Type = "register", SatelliteId = "voice" });
            var Error = await Assert.ThrowsAsync<RpcException>(async () => await Unauthorized.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal(StatusCode.Unauthenticated, Error.StatusCode);
        }
        using var Call = Client.Connect(new Metadata { { "authorization", "Bearer test-bridge-secret" } });
        await Call.RequestStream.WriteAsync(new() { Type = "register", SatelliteId = "voice", Name = "HA Voice", Area = "Bedroom" });
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await Call.ResponseStream.MoveNext(Timeout.Token));
        Assert.Equal("registered", Call.ResponseStream.Current.Type);
        await Call.RequestStream.WriteAsync(new() { Type = "start", SessionId = "transport-session", WakeWord = "Hey Jarvis" });
        await Call.RequestStream.WriteAsync(new() { Type = "start", SessionId = "transport-session" });
        await Call.RequestStream.WriteAsync(new() { Type = "audio", SessionId = "old-session", Pcm = Google.Protobuf.ByteString.CopyFrom(new byte[2]), SampleRate = 16000, SampleWidth = 2, Channels = 1 });
        await Call.RequestStream.WriteAsync(new() { Type = "audio", SessionId = "transport-session", Pcm = Google.Protobuf.ByteString.CopyFrom(new byte[2]), SampleRate = 16000, SampleWidth = 2, Channels = 1 });
        await Call.RequestStream.WriteAsync(new() { Type = "stop", SessionId = "transport-session" });
        var Events = new List<BridgeFrame>();
        DateTimeOffset? StartSentAt = null;
        Guid RunId = default;
        while (await Call.ResponseStream.MoveNext(Timeout.Token))
        {
            Events.Add(Call.ResponseStream.Current);
            if (Call.ResponseStream.Current.Type == "audio-ready")
            {
                var Id = Call.ResponseStream.Current.PlaybackId;
                RunId = Guid.Parse(Call.ResponseStream.Current.TraceId);
                Assert.False(string.IsNullOrWhiteSpace(Id));
                await Call.RequestStream.WriteAsync(new() { Type = "playback-started", PlaybackId = "old-playback", SessionId = "transport-session" });
                await Call.RequestStream.WriteAsync(new() { Type = "playback-finished", PlaybackId = "old-playback", SessionId = "old-session", Text = "succeeded" });
                await Call.RequestStream.WriteAsync(new() { Type = "playback-finished", PlaybackId = "old-playback", SessionId = "transport-session", Text = "succeeded" });
                // An old acknowledgement must not complete the current delivery.
                var Next = Call.ResponseStream.MoveNext(Timeout.Token);
                Assert.NotSame(Next, await Task.WhenAny(Next, Task.Delay(100, Timeout.Token)));
                var Store = Factory.Services.GetRequiredService<RunStore>();
                var BeforeStart = Assert.Single(Store.Get(RunId)!.Steps, Step => Step.Name == "Satellite playback");
                Assert.False(BeforeStart.Metadata!.Value.TryGetProperty("playbackStartedAt", out _));
                StartSentAt = DateTimeOffset.UtcNow;
                await Call.RequestStream.WriteAsync(new() { Type = "playback-started", PlaybackId = Id, SessionId = "transport-session" });
                await Call.RequestStream.WriteAsync(new() { Type = "playback-started", PlaybackId = Id, SessionId = "transport-session" });
                await Call.RequestStream.WriteAsync(new() { Type = "playback-finished", PlaybackId = Id, SessionId = "transport-session", Text = "succeeded" });
                Assert.True(await Next);
                Events.Add(Call.ResponseStream.Current);
            }
            if (Call.ResponseStream.Current.Type == "session-result") { break; }
        }
        Assert.Contains(Events, Frame => Frame.Type == "response" && Frame.Text == "Done.");
        var Audio = Assert.Single(Events, Frame => Frame.Type == "audio-ready");
        var Wave = await Http.GetByteArrayAsync(new Uri(Audio.Url).PathAndQuery, Timeout.Token);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(Wave, 0, 4));
        Assert.Equal("succeeded", Events.Last().Text);
        var Run = Factory.Services.GetRequiredService<RunStore>().Get(RunId)!;
        var Activation = Assert.Single(Run.Steps, Step => Step.Name == "Voice activation");
        Assert.Equal("Hey Jarvis", Activation.Metadata!.Value.GetProperty("wakeWord").GetString());
        Assert.Equal("transport-session", Activation.Metadata.Value.GetProperty("transportSessionId").GetString());
        Assert.Equal(Run.VoiceSessionId, Activation.Metadata.Value.GetProperty("voiceSessionId").GetGuid());
        Assert.True(Activation.Metadata.Value.GetProperty("dispatchLatencyMilliseconds").GetDouble() >= 0);
        Assert.True(Activation.Sequence < Run.Steps.Single(Step => Step.Name == "Speech to text").Sequence);
        Assert.All(Run.Steps, Step => Assert.True(Step.DurationMilliseconds >= 0));
        var History = Factory.Services.GetRequiredService<SatelliteManager>().Events("voice");
        var Created = Assert.Single(History, Event => Event.Type == "Voice session created");
        Assert.Equal(RunId, Created.TraceId);
        Assert.Equal(Run.VoiceSessionId, Created.SessionId);
        Assert.Equal("Hey Jarvis", Created.Detail);
        Assert.Equal(RunId, Assert.Single(History, Event => Event.Type == "Playback finished").TraceId);
        Assert.Equal(RunId, Assert.Single(History, Event => Event.Type == "Playback started").TraceId);
        Assert.Single(History, Event => Event.Type == "Microphone started");
        Assert.Single(History, Event => Event.Type == "Microphone stopped");
        var Playback = Assert.Single(Run.Steps, Step => Step.Name == "Satellite playback");
        var StartedAt = Playback.Metadata!.Value.GetProperty("playbackStartedAt").GetDateTimeOffset();
        Assert.True(StartedAt >= StartSentAt);
        var Input = Assert.Single(Run.Steps, Step => Step.Name == "Microphone audio");
        Assert.True(StartedAt >= Input.Output!.Value.GetProperty("audioInputCompletedAt").GetDateTimeOffset());
        await Call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task OwnershipMetadataConfigurationAndRedactedEventsReachTheProviderIndependentApi()
    {
        await using var Factory = new Application();
        using var Http = Factory.CreateDefaultClient();
        using var Channel = GrpcChannel.ForAddress(Http.BaseAddress!, new() { HttpClient = Http });
        var Client = new SatelliteTransport.SatelliteTransportClient(Channel);
        using var Call = Client.Connect(new Metadata { { "authorization", "Bearer test-bridge-secret" } });
        await Call.RequestStream.WriteAsync(new() { Type = "register", SatelliteId = "voice" });
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await Call.ResponseStream.MoveNext(Timeout.Token));
        var Manager = Factory.Services.GetRequiredService<SatelliteManager>();
        Assert.Equal(VoiceOwnership.Unknown, Manager.State("voice").VoiceOwnership);
        await Call.RequestStream.WriteAsync(new() { Type = "metadata", Device = new() { Model = "Fake", EsphomeVersion = "2026.test", ApiVersion = "1.12" },
            Capabilities = new() { Microphone = true, VoiceAssistant = true, MultiChannelMicrophone = true, AnnouncementPlayback = true } });
        await Call.RequestStream.WriteAsync(new() { Type = "configuration", Configuration = new()
            { AvailableWakeWords = { new Protocol.WakeWord { Id = "nabu", Name = "Okay Nabu" }, new Protocol.WakeWord { Id = "jarvis", Name = "Hey Jarvis" } },
                ActiveWakeWords = { "nabu" }, MaxActiveWakeWords = 1 } });
        await Call.RequestStream.WriteAsync(new() { Type = "ownership", Ownership = "Conflict" });
        await Call.RequestStream.WriteAsync(new() { Type = "device-log", Text = "authorization=Bearer test-bridge-secret" });
        await WaitAsync(() => Manager.Events("voice").Any(Event => Event.Type == "ESPHome log"), Timeout.Token);
        Assert.Equal(1, Manager.Count);
        Assert.Equal(VoiceOwnership.Conflict, Manager.State("voice").VoiceOwnership);
        Assert.Contains("Another ESPHome", Manager.State("voice").LastError);
        Assert.True(Manager.State("voice").Capabilities.MultiChannelMicrophone);
        var Events = await Http.GetStringAsync("/api/satellites/voice/events", Timeout.Token);
        Assert.DoesNotContain("test-bridge-secret", Events);
        var Rejected = await Http.PutAsync("/api/satellites/voice/wake-words", new StringContent("[\"jarvis\"]", System.Text.Encoding.UTF8, "application/json"), Timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, Rejected.StatusCode);
        await Call.RequestStream.WriteAsync(new() { Type = "ownership", Ownership = "OwnedByAssister" });
        await WaitAsync(() => Manager.State("voice").VoiceOwnership == VoiceOwnership.OwnedByAssister, Timeout.Token);
        foreach (var Words in new[] { "[\"missing\"]", "[\"nabu\",\"jarvis\"]" })
        {
            var Response = await Http.PutAsync("/api/satellites/voice/wake-words", new StringContent(Words, System.Text.Encoding.UTF8, "application/json"), Timeout.Token);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, Response.StatusCode);
        }
        var Accepted = await Http.PutAsync("/api/satellites/voice/wake-words", new StringContent("[\"jarvis\"]", System.Text.Encoding.UTF8, "application/json"), Timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, Accepted.StatusCode);
        Assert.True(await Call.ResponseStream.MoveNext(Timeout.Token));
        Assert.Equal("set-wake-words", Call.ResponseStream.Current.Type);
        Assert.Equal("[\"jarvis\"]", Call.ResponseStream.Current.Text);
        await Call.RequestStream.CompleteAsync();
        Assert.False(await Call.ResponseStream.MoveNext(Timeout.Token));
        Assert.Equal("Offline", Manager.State("voice").ConnectionState);
        var Persisted = await Http.GetStringAsync("/api/satellites", Timeout.Token);
        Assert.Contains("jarvis", Persisted);
        Assert.DoesNotContain("test-bridge-secret", Persisted);
    }

    private static async Task WaitAsync(Func<bool> Ready, CancellationToken Token)
    {
        while (!Ready()) { await Task.Delay(10, Token); }
    }

    [Fact]
    public async Task DisconnectCancelsCaptureAndReconnectRefreshesObservedCapabilities()
    {
        await using var Factory = new Application();
        using var Http = Factory.CreateDefaultClient();
        using var Channel = GrpcChannel.ForAddress(Http.BaseAddress!, new() { HttpClient = Http });
        var Client = new SatelliteTransport.SatelliteTransportClient(Channel);
        var Manager = Factory.Services.GetRequiredService<SatelliteManager>();
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var First = Client.Connect(new Metadata { { "authorization", "Bearer test-bridge-secret" } }))
        {
            await First.RequestStream.WriteAsync(new() { Type = "register", SatelliteId = "voice" });
            Assert.True(await First.ResponseStream.MoveNext(Timeout.Token));
            await First.RequestStream.WriteAsync(new() { Type = "metadata", Capabilities = new() { MultiChannelMicrophone = true }, Device = new() { EsphomeVersion = "old" } });
            await First.RequestStream.WriteAsync(new() { Type = "start", SessionId = "disconnected-session", WakeWord = "authorization=test-bridge-secret" });
            Assert.True(await First.ResponseStream.MoveNext(Timeout.Token));
            Assert.Equal("transcribing", First.ResponseStream.Current.Type);
            Assert.Equal(1, Manager.ActiveSessionCount);
            await First.RequestStream.CompleteAsync();
            Assert.False(await First.ResponseStream.MoveNext(Timeout.Token));
        }
        Assert.Equal(0, Manager.ActiveSessionCount);
        Assert.Equal(VoiceSessionState.Disconnected, Manager.State("voice").Activity);
        Assert.Null(Manager.State("voice").CurrentVoiceSessionId);
        var Cancelled = Assert.Single(Factory.Services.GetRequiredService<RunStore>().Snapshot());
        Assert.Equal("cancelled", Cancelled.Outcome);
        Assert.DoesNotContain("test-bridge-secret", System.Text.Json.JsonSerializer.Serialize(Factory.Services.GetRequiredService<RunStore>().Get(Cancelled.RunId)));
        Assert.DoesNotContain("test-bridge-secret", await Http.GetStringAsync("/api/satellites/voice/events", Timeout.Token));
        Assert.DoesNotContain("test-bridge-secret", await Http.GetStringAsync("/api/satellites/voice", Timeout.Token));
        using var Second = Client.Connect(new Metadata { { "authorization", "Bearer test-bridge-secret" } });
        await Second.RequestStream.WriteAsync(new() { Type = "register", SatelliteId = "voice" });
        Assert.True(await Second.ResponseStream.MoveNext(Timeout.Token));
        Assert.Equal(VoiceOwnership.Unknown, Manager.State("voice").VoiceOwnership);
        Assert.False(Manager.State("voice").Capabilities.MultiChannelMicrophone);
        await Second.RequestStream.WriteAsync(new() { Type = "metadata", Capabilities = new() { Microphone = true }, Device = new() { EsphomeVersion = "new" } });
        await WaitAsync(() => Manager.State("voice").ESPHomeVersion == "new", Timeout.Token);
        Assert.True(Manager.State("voice").Capabilities.Microphone);
        Assert.False(Manager.State("voice").Capabilities.MultiChannelMicrophone);
        await Second.RequestStream.CompleteAsync();
        Assert.False(await Second.ResponseStream.MoveNext(Timeout.Token));
    }

    [Fact]
    public async Task CancellationAcknowledgesTheAbortedSessionAndNextSessionIsIndependent()
    {
        await using var Factory = new Application();
        using var Http = Factory.CreateDefaultClient();
        using var Channel = GrpcChannel.ForAddress(Http.BaseAddress!, new() { HttpClient = Http });
        var Client = new SatelliteTransport.SatelliteTransportClient(Channel);
        using var Call = Client.Connect(new Metadata { { "authorization", "Bearer test-bridge-secret" } });
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Call.RequestStream.WriteAsync(new() { Type = "register", SatelliteId = "voice" });
        Assert.True(await Call.ResponseStream.MoveNext(Timeout.Token));
        await Call.RequestStream.WriteAsync(new() { Type = "start", SessionId = "cancelled-session" });
        Assert.True(await Call.ResponseStream.MoveNext(Timeout.Token));
        Assert.Equal("transcribing", Call.ResponseStream.Current.Type);
        await Call.RequestStream.WriteAsync(new() { Type = "cancel", SessionId = "cancelled-session" });
        Assert.True(await Call.ResponseStream.MoveNext(Timeout.Token));
        Assert.Equal("session-result", Call.ResponseStream.Current.Type);
        Assert.Equal("cancelled", Call.ResponseStream.Current.Text);
        Assert.Equal("cancelled-session", Call.ResponseStream.Current.SessionId);
        Assert.Equal(0, Factory.Services.GetRequiredService<SatelliteManager>().ActiveSessionCount);
        await Call.RequestStream.WriteAsync(new() { Type = "start", SessionId = "next-session" });
        await Call.RequestStream.WriteAsync(new() { Type = "audio", SessionId = "cancelled-session", Pcm = Google.Protobuf.ByteString.CopyFrom(new byte[2]), SampleRate = 16000, SampleWidth = 2, Channels = 1 });
        await Call.RequestStream.WriteAsync(new() { Type = "audio", SessionId = "next-session", Pcm = Google.Protobuf.ByteString.CopyFrom(new byte[2]), SampleRate = 16000, SampleWidth = 2, Channels = 1 });
        await Call.RequestStream.WriteAsync(new() { Type = "stop", SessionId = "next-session" });
        while (await Call.ResponseStream.MoveNext(Timeout.Token))
        {
            var Frame = Call.ResponseStream.Current;
            if (Frame.Type == "audio-ready") await Call.RequestStream.WriteAsync(new() { Type = "playback-finished", SessionId = Frame.SessionId, PlaybackId = Frame.PlaybackId, Text = "succeeded" });
            if (Frame.Type == "session-result") { Assert.Equal("succeeded", Frame.Text); break; }
        }
        // A repeated old start cannot reopen a previously completed session.
        await Call.RequestStream.WriteAsync(new() { Type = "start", SessionId = "cancelled-session" });
        await Call.RequestStream.CompleteAsync();
        Assert.False(await Call.ResponseStream.MoveNext(Timeout.Token));
    }
    private sealed class Application : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString()),
                ["SatelliteBridge:Enabled"] = "true", ["SatelliteBridge:Token"] = "test-bridge-secret",
                ["SatelliteBridge:UseFlac"] = "false",
                ["EspHome:SatelliteId"] = "voice", ["Assister:PublicUrl"] = "http://localhost"
            }));
            Builder.ConfigureServices(Services =>
            {
                Services.RemoveAll<IRequestCoordinator>(); Services.AddScoped<IRequestCoordinator, Coordinator>();
                Services.RemoveAll<ISpeechToTextProvider>(); Services.AddTransient<ISpeechToTextProvider, Stt>();
                Services.RemoveAll<ITextToSpeechProvider>(); Services.AddTransient<ITextToSpeechProvider, Tts>();
            });
        }
    }
    private sealed class Coordinator : IRequestCoordinator
    {
        public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken Token) => Task.FromResult(new RequestResult("Done.", Guid.NewGuid(), "direct-intent", Guid.NewGuid(), "succeeded", [], null, 1));
    }
    private sealed class Stt : ISpeechToTextProvider
    {
        public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
        {
            var Chunks = 0;
            await foreach (var _ in Audio.WithCancellation(Token)) { Chunks++; }
            Assert.Equal(1, Chunks);
            return new("turn the light off", "en");
        }
    }
    private sealed class Tts : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        {
            await Task.Yield();
            yield return new(new byte[2], 22050, 2, 1);
        }
    }
}
