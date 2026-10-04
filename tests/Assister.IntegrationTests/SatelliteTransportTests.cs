using System.Threading.Channels;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Satellites.Protocol;
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
        await Call.RequestStream.WriteAsync(new() { Type = "start", SessionId = "transport-session" });
        await Call.RequestStream.WriteAsync(new() { Type = "audio", SessionId = "transport-session", Pcm = Google.Protobuf.ByteString.CopyFrom(new byte[2]), SampleRate = 16000, SampleWidth = 2, Channels = 1 });
        await Call.RequestStream.WriteAsync(new() { Type = "stop", SessionId = "transport-session" });
        var Events = new List<BridgeFrame>();
        while (await Call.ResponseStream.MoveNext(Timeout.Token))
        {
            Events.Add(Call.ResponseStream.Current);
            if (Call.ResponseStream.Current.Type == "session-result") { break; }
        }
        Assert.Contains(Events, Frame => Frame.Type == "response" && Frame.Text == "Done.");
        var Audio = Assert.Single(Events, Frame => Frame.Type == "audio-ready");
        var Wave = await Http.GetByteArrayAsync(new Uri(Audio.Url).PathAndQuery, Timeout.Token);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(Wave, 0, 4));
        Assert.Equal("succeeded", Events.Last().Text);
        await Call.RequestStream.CompleteAsync();
    }
    private sealed class Application : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString()),
                ["SatelliteBridge:Enabled"] = "true", ["SatelliteBridge:Token"] = "test-bridge-secret",
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
