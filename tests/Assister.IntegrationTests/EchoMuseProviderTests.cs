using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Assister.Contracts;
using Assister.Satellites;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assister.IntegrationTests;

public sealed class EchoMuseProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneAuthenticatedSocketMultiplexesDevicesAndExactSessionPairs(bool Tones)
    {
        var Builder = WebApplication.CreateBuilder(); Builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var Controller = Builder.Build(); Controller.UseWebSockets();
        var SocketReady = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var Responses = Channel.CreateUnbounded<JsonElement>(); var Logins = 0; var Connections = 0;
        Controller.MapPost("/api/auth/login", async (HttpContext Context) =>
        {
            using var Document = await JsonDocument.ParseAsync(Context.Request.Body);
            Assert.Equal("test-admin", Document.RootElement.GetProperty("username").GetString());
            Assert.Equal("test-password", Document.RootElement.GetProperty("password").GetString());
            Interlocked.Increment(ref Logins);
            return Results.Json(new { token = "test-controller-token" });
        });
        Controller.MapGet("/api/devices", (HttpContext Context) =>
        {
            Assert.Equal("Bearer test-controller-token", Context.Request.Headers.Authorization.ToString());
            return Results.Json(new[] {
                new { device_id = "first", label = "First", approved = true, connected = true, muted = false, firmware_ver = "fake", volume = 0.5 },
                new { device_id = "second", label = "Second", approved = true, connected = true, muted = false, firmware_ver = "fake", volume = 0.5 } });
        });
        Controller.Map("/api/voice", async Context =>
        {
            Assert.Equal("Bearer test-controller-token", Context.Request.Headers.Authorization.ToString());
            Interlocked.Increment(ref Connections);
            using var Socket = await Context.WebSockets.AcceptWebSocketAsync();
            await Send(Socket, new { type = "hello", protocolVersion = 1, voiceBackend = "external", feedbackPlayback = Tones });
            SocketReady.TrySetResult(Socket);
            try
            {
                while (Socket.State == WebSocketState.Open)
                {
                    using var Buffer = new MemoryStream(); var Bytes = new byte[16384];
                    WebSocketReceiveResult Part;
                    do { Part = await Socket.ReceiveAsync(Bytes, Context.RequestAborted); if (Part.MessageType == WebSocketMessageType.Close) return; Buffer.Write(Bytes, 0, Part.Count); } while (!Part.EndOfMessage);
                    using var Message = JsonDocument.Parse(Buffer.ToArray());
                    await Responses.Writer.WriteAsync(Message.RootElement.Clone());
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
        });
        await Controller.StartAsync();
        await using var Factory = new Application(Controller.Urls.Single(), Tones);
        using var Http = Factory.CreateDefaultClient(); using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var Socket = await SocketReady.Task.WaitAsync(Timeout.Token);
        var Manager = Factory.Services.GetRequiredService<SatelliteManager>();
        while (Manager.Count != 2) await Task.Delay(10, Timeout.Token);
        foreach (var Device in new[] { "first", "second" })
        {
            await Send(Socket, new { type = "turn_start", deviceId = Device, sessionId = "shared-session-id", wakeWord = "jarvis",
                audio = new { sampleRate = 16000, sampleWidth = 2, channels = 1, encoding = "pcm_s16le" } });
            await Send(Socket, new { type = "audio", deviceId = Device, sessionId = "shared-session-id", data = "AAA=" });
            await Send(Socket, new { type = "audio_end", deviceId = Device, sessionId = "shared-session-id", reason = "speech_end" });
        }
        var CueCount = 0;
        async Task<JsonElement> NextResponse()
        {
            while (true)
            {
                var Message = await Responses.Reader.ReadAsync(Timeout.Token);
                if (Message.GetProperty("type").GetString() != "tone") { return Message; }
                Assert.True(Tones);
                using var CueAudio = await Http.GetAsync(new Uri(Message.GetProperty("audioUrl").GetString()!).PathAndQuery, Timeout.Token);
                if (CueAudio.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Assert.Equal("capture-to-cancel", Message.GetProperty("sessionId").GetString());
                    continue; // Stop already released this cue's bearer URL.
                }
                CueAudio.EnsureSuccessStatusCode();
                var Audio = await CueAudio.Content.ReadAsByteArrayAsync(Timeout.Token);
                Assert.Equal(48000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Audio.AsSpan(24)));
                CueCount++;
                await Send(Socket, new { type = "play_finished", deviceId = Message.GetProperty("deviceId").GetString(),
                    sessionId = Message.GetProperty("sessionId").GetString(), requestId = Message.GetProperty("requestId").GetString() });
            }
        }
        var Targets = new HashSet<string>();
        for (var Index = 0; Index < 2; Index++)
        {
            var Response = await NextResponse();
            Assert.Equal("turn_response", Response.GetProperty("type").GetString());
            Assert.Equal("shared-session-id", Response.GetProperty("sessionId").GetString());
            var Device = Response.GetProperty("deviceId").GetString()!; Targets.Add(Device);
            var Wave = await Http.GetByteArrayAsync(new Uri(Response.GetProperty("audioUrl").GetString()!).PathAndQuery, Timeout.Token);
            Assert.Equal(48000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Wave.AsSpan(24)));
            if (Tones) { Assert.True(Wave.Length > 9644, "Goodbye must be included in the response audio before the controller closes its turn."); }
            await Send(Socket, new { type = "play_started", deviceId = Device, sessionId = "shared-session-id" });
            await Send(Socket, new { type = "play_finished", deviceId = Device, sessionId = "shared-session-id" });
            await Send(Socket, new { type = "turn_finished", deviceId = Device, sessionId = "shared-session-id", outcome = "ok" });
        }
        Assert.Equal(Tones ? 6 : 0, CueCount);
        Assert.Equal(2, Targets.Count); Assert.Equal(1, Logins); Assert.Equal(1, Connections);
        while (Manager.ActiveSessionCount != 0) await Task.Delay(10, Timeout.Token);
        Assert.Equal(VoiceOwnership.OwnedByAssister, Manager.State("echomuse-first").VoiceOwnership);
        // Cancel through the public API during capture, then prove the next turn can play.
        await Send(Socket, new { type = "turn_start", deviceId = "first", sessionId = "capture-to-cancel",
            audio = new { sampleRate = 16000, sampleWidth = 2, channels = 1, encoding = "pcm_s16le" } });
        while (Manager.ActiveSessionCount != 1) await Task.Delay(10, Timeout.Token);
        using var Stop = await Http.PostAsync("/api/satellites/echomuse-first/stop", null, Timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, Stop.StatusCode);
        var Cancel = await NextResponse();
        Assert.Equal("turn_cancel", Cancel.GetProperty("type").GetString());
        Assert.Equal("capture-to-cancel", Cancel.GetProperty("sessionId").GetString());
        while (Manager.ActiveSessionCount != 0) await Task.Delay(10, Timeout.Token);
        await Send(Socket, new { type = "turn_start", deviceId = "first", sessionId = "playback-to-cancel",
            audio = new { sampleRate = 16000, sampleWidth = 2, channels = 1, encoding = "pcm_s16le" } });
        await Send(Socket, new { type = "audio", deviceId = "first", sessionId = "playback-to-cancel", data = "AAA=" });
        await Send(Socket, new { type = "audio_end", deviceId = "first", sessionId = "playback-to-cancel", reason = "speech_end" });
        var NextReply = await NextResponse();
        Assert.Equal("turn_response", NextReply.GetProperty("type").GetString());
        var NextAudioUrl = new Uri(NextReply.GetProperty("audioUrl").GetString()!).PathAndQuery;
        await Send(Socket, new { type = "play_started", deviceId = "first", sessionId = "playback-to-cancel" });
        using var StopPlayback = await Http.PostAsync("/api/satellites/echomuse-first/stop", null, Timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, StopPlayback.StatusCode);
        var CancelPlayback = await NextResponse();
        Assert.Equal("turn_cancel", CancelPlayback.GetProperty("type").GetString());
        Assert.Equal("playback-to-cancel", CancelPlayback.GetProperty("sessionId").GetString());
        while (Manager.ActiveSessionCount != 0) await Task.Delay(10, Timeout.Token);
        var Preview = Http.PostAsync("/api/satellites/echomuse-first/tones/awake", null, Timeout.Token);
        var Announcement = await NextResponse();
        Assert.Equal("play", Announcement.GetProperty("type").GetString());
        var PreviewWave = await Http.GetByteArrayAsync(new Uri(Announcement.GetProperty("audioUrl").GetString()!).PathAndQuery, Timeout.Token);
        Assert.True(PreviewWave.Length > 44);
        await Send(Socket, new { type = "play_finished", deviceId = "first", requestId = Announcement.GetProperty("requestId").GetString() });
        using var PreviewResult = await Preview;
        Assert.True(PreviewResult.IsSuccessStatusCode);
        using var RemovedAudio = await Http.GetAsync(NextAudioUrl, Timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, RemovedAudio.StatusCode);
        Assert.Equal("Assister output idle", Manager.State("echomuse-first").CurrentPlaybackState);
        await Factory.DisposeAsync(); Socket.Abort();
        await Controller.StopAsync(Timeout.Token);
    }
    private static Task Send(WebSocket Socket, object Message) => Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(Message), WebSocketMessageType.Text, true, CancellationToken.None);
    private sealed class Application(string Base, bool Tones) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString()),
                ["EchoMuse:Enabled"] = "true", ["EchoMuse:ControllerUrl"] = Base,
                ["Voice:Tones:Enabled"] = Tones.ToString(),
                ["EchoMuse:Username"] = "test-admin", ["EchoMuse:Password"] = "test-password", ["Assister:PublicUrl"] = "http://localhost"
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
        { await foreach (var _ in Audio.WithCancellation(Token)) { } return new("turn the light off", "en"); }
    }
    private sealed class Tts : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[2205 * 2], 22050, 2, 1); }
    }
}
