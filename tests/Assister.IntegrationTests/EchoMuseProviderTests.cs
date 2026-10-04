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
    [Fact]
    public async Task OneAuthenticatedSocketMultiplexesDevicesAndExactSessionPairs()
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
            await Send(Socket, new { type = "hello", protocolVersion = 1, voiceBackend = "external" });
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
        await using var Factory = new Application(Controller.Urls.Single());
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
        var Targets = new HashSet<string>();
        for (var Index = 0; Index < 2; Index++)
        {
            var Response = await Responses.Reader.ReadAsync(Timeout.Token);
            Assert.Equal("turn_response", Response.GetProperty("type").GetString());
            Assert.Equal("shared-session-id", Response.GetProperty("sessionId").GetString());
            var Device = Response.GetProperty("deviceId").GetString()!; Targets.Add(Device);
            var Wave = await Http.GetByteArrayAsync(new Uri(Response.GetProperty("audioUrl").GetString()!).PathAndQuery, Timeout.Token);
            Assert.Equal(48000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Wave.AsSpan(24)));
            await Send(Socket, new { type = "play_started", deviceId = Device, sessionId = "shared-session-id" });
            await Send(Socket, new { type = "play_finished", deviceId = Device, sessionId = "shared-session-id" });
            await Send(Socket, new { type = "turn_finished", deviceId = Device, sessionId = "shared-session-id", outcome = "ok" });
        }
        Assert.Equal(2, Targets.Count); Assert.Equal(1, Logins); Assert.Equal(1, Connections);
        while (Manager.ActiveSessionCount != 0) await Task.Delay(10, Timeout.Token);
        Assert.Equal(VoiceOwnership.OwnedByAssister, Manager.State("echomuse-first").VoiceOwnership);
        await Factory.DisposeAsync(); Socket.Abort();
        await Controller.StopAsync(Timeout.Token);
    }
    private static Task Send(WebSocket Socket, object Message) => Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(Message), WebSocketMessageType.Text, true, CancellationToken.None);
    private sealed class Application(string Base) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString()),
                ["EchoMuse:Enabled"] = "true", ["EchoMuse:ControllerUrl"] = Base,
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
