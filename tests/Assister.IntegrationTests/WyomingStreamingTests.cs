using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Speech.Wyoming;

namespace Assister.IntegrationTests;

public sealed class WyomingStreamingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeStreamingReadsAudioWhileTextIsStillArrivingAndRequiresCompletion(bool Complete)
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        var Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Endpoint = new WyomingEndpoint("127.0.0.1", ((IPEndPoint)Listener.LocalEndpoint).Port);
        var Server = ServeAsync();
        await using var Output = new WyomingTextToSpeechProvider(Endpoint).SynthesizeStreamAsync(Text(), new("test-voice"), Timeout.Token).GetAsyncEnumerator(Timeout.Token);
        Assert.True(await Output.MoveNextAsync());
        Assert.Equal(new byte[] { 1, 2 }, Output.Current.Pcm.ToArray());
        Release.TrySetResult();
        if (Complete)
        {
            Assert.True(await Output.MoveNextAsync());
            Assert.False(await Output.MoveNextAsync());
        }
        else
        {
            Assert.True(await Output.MoveNextAsync());
            await Assert.ThrowsAsync<EndOfStreamException>(async () => await Output.MoveNextAsync());
        }
        await Server;
        async IAsyncEnumerable<string> Text([EnumeratorCancellation] CancellationToken Token = default)
        {
            yield return "First sentence.";
            await Release.Task.WaitAsync(Token);
            yield return "Second sentence.";
        }
        async Task ServeAsync()
        {
            using var Client = await Listener.AcceptTcpClientAsync(Timeout.Token);
            await using var Stream = Client.GetStream();
            var Reader = new WyomingEventReader(Stream);
            var Writer = new WyomingEventWriter(Stream);
            Assert.Equal("describe", (await Reader.ReadAsync(Timeout.Token))!.Type);
            await Writer.WriteAsync(WyomingEvent.Create("info", new { tts = new[] { new { name = "fake", supports_synthesize_streaming = true } } }), Timeout.Token);
            var Start = await Reader.ReadAsync(Timeout.Token);
            Assert.Equal("synthesize-start", Start!.Type);
            Assert.Equal("test-voice", Start.Data.GetProperty("voice").GetProperty("name").GetString());
            var FirstSentence = await Reader.ReadAsync(Timeout.Token);
            Assert.Equal("synthesize-chunk", FirstSentence!.Type);
            Assert.Equal("First sentence.\n\n", FirstSentence.Data.GetProperty("text").GetString());
            await Writer.WriteAsync(WyomingEvent.Create("audio-start", new { rate = 16000, width = 2, channels = 1 }), Timeout.Token);
            await Writer.WriteAsync(WyomingEvent.Create("audio-chunk", Payload: new byte[] { 1, 2 }), Timeout.Token);
            await Writer.WriteAsync(WyomingEvent.Create("audio-stop"), Timeout.Token);
            Assert.Equal("synthesize-chunk", (await Reader.ReadAsync(Timeout.Token))!.Type);
            Assert.Equal("synthesize-stop", (await Reader.ReadAsync(Timeout.Token))!.Type);
            await Writer.WriteAsync(WyomingEvent.Create("audio-start", new { rate = 16000, width = 2, channels = 1 }), Timeout.Token);
            await Writer.WriteAsync(WyomingEvent.Create("audio-chunk", Payload: new byte[] { 3, 4 }), Timeout.Token);
            await Writer.WriteAsync(WyomingEvent.Create("audio-stop"), Timeout.Token);
            if (Complete) { await Writer.WriteAsync(WyomingEvent.Create("synthesize-stopped"), Timeout.Token); }
        }
    }

    [Fact]
    public async Task LegacyServerReceivesOrdinarySentenceRequests()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        var Server = ServeAsync();
        var Chunks = new List<AudioChunk>();
        await foreach (var Chunk in new WyomingTextToSpeechProvider(new("127.0.0.1", ((IPEndPoint)Listener.LocalEndpoint).Port))
            .SynthesizeStreamAsync(Text(), new(), Timeout.Token)) { Chunks.Add(Chunk); }
        Assert.Equal(2, Chunks.Count);
        await Server;
        async IAsyncEnumerable<string> Text() { await Task.Yield(); yield return "First."; yield return "Second."; }
        async Task ServeAsync()
        {
            for (var Index = 0; Index < 3; Index++)
            {
                using var Client = await Listener.AcceptTcpClientAsync(Timeout.Token);
                await using var Stream = Client.GetStream();
                var Reader = new WyomingEventReader(Stream);
                var Writer = new WyomingEventWriter(Stream);
                Assert.Equal("describe", (await Reader.ReadAsync(Timeout.Token))!.Type);
                await Writer.WriteAsync(WyomingEvent.Create("info", new { tts = new[] { new { name = "legacy" } } }), Timeout.Token);
                if (Index == 0) { Assert.Null(await Reader.ReadAsync(Timeout.Token)); continue; }
                var Request = await Reader.ReadAsync(Timeout.Token);
                Assert.Equal("synthesize", Request!.Type);
                Assert.Equal(Index == 1 ? "First." : "Second.", Request.Data.GetProperty("text").GetString());
                await Writer.WriteAsync(WyomingEvent.Create("audio-start", new { rate = 22050, width = 2, channels = 1 }), Timeout.Token);
                await Writer.WriteAsync(WyomingEvent.Create("audio-chunk", Payload: new byte[] { 1, 2 }), Timeout.Token);
                await Writer.WriteAsync(WyomingEvent.Create("audio-stop"), Timeout.Token);
            }
        }
    }
}
