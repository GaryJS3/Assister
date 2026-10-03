using System.Net;
using System.Net.Sockets;
using Assister.Contracts;
using Assister.Speech.Wyoming;

namespace Assister.IntegrationTests;

public sealed class WyomingProviderTests
{
    [Fact]
    public async Task SttSendsOrderedPcmAndReceivesTranscript()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        var Server = ServeAsync();
        var Endpoint = new WyomingEndpoint("127.0.0.1", ((IPEndPoint)Listener.LocalEndpoint).Port);
        var Result = await new WyomingSpeechToTextProvider(Endpoint).TranscribeAsync(Audio(), new(), Timeout.Token);
        Assert.Equal("turn the light off", Result.Text);
        await Server;

        async Task ServeAsync()
        {
            using var Client = await Listener.AcceptTcpClientAsync(Timeout.Token);
            await using var Stream = Client.GetStream();
            var Reader = new WyomingEventReader(Stream);
            var Writer = new WyomingEventWriter(Stream);
            Assert.Equal("describe", (await Reader.ReadAsync(Timeout.Token))!.Type);
            await Writer.WriteAsync(WyomingEvent.Create("info", new { asr = new[] { new { name = "fake" } } }), Timeout.Token);
            foreach (var Type in new[] { "transcribe", "audio-start", "audio-chunk", "audio-stop" })
            {
                var Event = await Reader.ReadAsync(Timeout.Token);
                Assert.Equal(Type, Event!.Type);
                if (Type == "audio-chunk")
                {
                    Assert.Equal(new byte[] { 1, 2, 3, 4 }, Event.Payload.ToArray());
                    Assert.Equal(16000, Event.Data.GetProperty("rate").GetInt32());
                }
            }
            await Writer.WriteAsync(WyomingEvent.Create("transcript", new { text = "turn the light off" }), Timeout.Token);
        }
    }

    [Fact]
    public async Task TtsStreamsAudioAndPreservesServerFormat()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        var Server = ServeAsync();
        var Endpoint = new WyomingEndpoint("127.0.0.1", ((IPEndPoint)Listener.LocalEndpoint).Port);
        var Chunks = new List<AudioChunk>();
        await foreach (var Chunk in new WyomingTextToSpeechProvider(Endpoint).SynthesizeAsync("Done.", new("test-voice"), Timeout.Token))
        {
            Chunks.Add(Chunk);
        }
        var Audio = Assert.Single(Chunks);
        Assert.Equal(22050, Audio.SampleRate);
        Assert.Equal(2, Audio.SampleWidth);
        Assert.Equal(new byte[] { 1, 2 }, Audio.Pcm.ToArray());
        await Server;

        async Task ServeAsync()
        {
            using var Client = await Listener.AcceptTcpClientAsync(Timeout.Token);
            await using var Stream = Client.GetStream();
            var Reader = new WyomingEventReader(Stream);
            var Writer = new WyomingEventWriter(Stream);
            Assert.Equal("describe", (await Reader.ReadAsync(Timeout.Token))!.Type);
            await Writer.WriteAsync(WyomingEvent.Create("info", new { tts = new[] { new { name = "fake" } } }), Timeout.Token);
            var Request = await Reader.ReadAsync(Timeout.Token);
            Assert.Equal("synthesize", Request!.Type);
            Assert.Equal("test-voice", Request.Data.GetProperty("voice").GetProperty("name").GetString());
            await Writer.WriteAsync(WyomingEvent.Create("audio-start", new { rate = 22050, width = 2, channels = 1 }), Timeout.Token);
            await Writer.WriteAsync(WyomingEvent.Create("audio-chunk", Payload: new byte[] { 1, 2 }), Timeout.Token);
            await Writer.WriteAsync(WyomingEvent.Create("audio-stop"), Timeout.Token);
        }
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        await Task.Yield();
        yield return new(new byte[] { 1, 2, 3, 4 }, 16000, 2, 1);
    }
}
