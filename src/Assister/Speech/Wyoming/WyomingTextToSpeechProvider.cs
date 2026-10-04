using Assister.Diagnostics;
using Assister.Contracts;
using System.Runtime.CompilerServices;

namespace Assister.Speech.Wyoming;

public sealed class WyomingTextToSpeechProvider(WyomingEndpoint Endpoint) : IStreamingTextToSpeechProvider
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeStreamAsync(IAsyncEnumerable<string> Text,
        TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken CancellationToken)
    {
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        Timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Endpoint.TimeoutSeconds * 3, 1, 120)));
        var Token = Timeout.Token;
        await using var Connection = await WyomingConnection.ConnectAsync(Endpoint, Token);
        var Info = await Connection.DescribeAsync(Token);
        if (!Info.Supports("tts")) { throw new InvalidOperationException("Wyoming endpoint does not advertise speech synthesis."); }
        var Streaming = Info.Data.GetProperty("tts")[0].TryGetProperty("supports_synthesize_streaming", out var Supported)
            && Supported.ValueKind == System.Text.Json.JsonValueKind.True;
        if (!Streaming)
        {
            await Connection.DisposeAsync();
            await foreach (var Sentence in Text.WithCancellation(Token))
                await foreach (var Chunk in SynthesizeAsync(Sentence, Options, Token).WithCancellation(Token)) { yield return Chunk; }
            yield break;
        }
        var Data = new Dictionary<string, object?>();
        if (Options.Voice is not null) { Data["voice"] = new { name = Options.Voice }; }
        await Connection.Writer.WriteAsync(WyomingEvent.Create("synthesize-start", Data), Token);
        var InputFinished = false;
        var Sender = SendAsync();
        var Rate = 0;
        var Width = 0;
        var Channels = 0;
        AudioChunk? Format = null;
        var InAudio = false;
        var HasAudio = false;
        try
        {
            while (await Connection.Reader.ReadAsync(Token) is { } Event)
            {
                if (Event.Type == "audio-start")
                {
                    Rate = Event.Data.GetProperty("rate").GetInt32();
                    Width = Event.Data.GetProperty("width").GetInt32();
                    Channels = Event.Data.GetProperty("channels").GetInt32();
                    if (InAudio || Rate is < 8000 or > 96000 || Width is < 1 or > 4 || Channels is < 1 or > 2
                        || Format is not null && (Format.SampleRate != Rate || Format.SampleWidth != Width || Format.Channels != Channels))
                        throw new InvalidDataException("Invalid streaming audio format.");
                    Format ??= new(ReadOnlyMemory<byte>.Empty, Rate, Width, Channels);
                    InAudio = true;
                }
                else if (Event.Type == "audio-chunk")
                {
                    if (!InAudio || Event.Payload.Length == 0 || Event.Payload.Length % (Width * Channels) != 0)
                        throw new InvalidDataException("Invalid streaming audio chunk.");
                    HasAudio = true;
                    yield return new(Event.Payload, Rate, Width, Channels);
                }
                else if (Event.Type == "audio-stop")
                {
                    if (!InAudio) { throw new InvalidDataException("Audio stopped before start."); }
                    InAudio = false;
                }
                else if (Event.Type == "synthesize-stopped")
                {
                    if (InAudio || !HasAudio || !InputFinished) { throw new InvalidDataException("Incomplete streaming synthesis."); }
                    await Sender;
                    yield break;
                }
                else if (Event.Type == "error") { throw new InvalidOperationException("Wyoming streaming synthesis failed."); }
            }
            throw new EndOfStreamException("Wyoming connection ended before synthesize-stopped.");
        }
        finally
        {
            Timeout.Cancel();
            try { await Sender; } catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }
        async Task SendAsync()
        {
            try
            {
                var Characters = 0;
                await foreach (var Sentence in Text.WithCancellation(Token))
                {
                    Characters += Sentence.Length;
                    if (Sentence.Length is < 1 or > 4096 || Characters > 16000) { throw new InvalidDataException("Streaming synthesis text limit exceeded."); }
                    // A blank line flushes an already bounded sentence without waiting for the next sentence's first letter.
                    await Connection.Writer.WriteAsync(WyomingEvent.Create("synthesize-chunk", new { text = Sentence + "\n\n" }), Token);
                }
                InputFinished = true;
                await Connection.Writer.WriteAsync(WyomingEvent.Create("synthesize-stop"), Token);
            }
            catch { Timeout.Cancel(); throw; }
        }
    }

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text,
        TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken CancellationToken)
    {
        using var Trace = RunTracing.CurrentKind == "TextToSpeech" ? null : RunTracing.Start("TextToSpeech", "Text to speech", "Invoke the Wyoming synthesis provider.");
        Trace?.Input(new { spokenText = Text, voice = Options.Voice });
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        Timeout.CancelAfter(TimeSpan.FromSeconds(Endpoint.TimeoutSeconds));
        var Token = Timeout.Token;
        await using var Connection = await WyomingConnection.ConnectAsync(Endpoint, Token);
        var Info = await Connection.DescribeAsync(Token);
        if (!Info.Supports("tts"))
        {
            throw new InvalidOperationException("Wyoming endpoint does not advertise speech synthesis.");
        }
        var Data = new Dictionary<string, object?> { ["text"] = Text };
        if (Options.Voice is not null)
        {
            Data["voice"] = new { name = Options.Voice };
        }
        await Connection.Writer.WriteAsync(WyomingEvent.Create("synthesize", Data), Token);
        var Rate = 0;
        var Width = 0;
        var Channels = 0;
        while (await Connection.Reader.ReadAsync(Token) is { } Event)
        {
            if (Event.Type == "audio-start")
            {
                Rate = Event.Data.GetProperty("rate").GetInt32();
                Width = Event.Data.GetProperty("width").GetInt32();
                Channels = Event.Data.GetProperty("channels").GetInt32();
                if (Rate <= 0 || Width is < 1 or > 4 || Channels <= 0)
                {
                    throw new InvalidDataException("Invalid Wyoming audio format.");
                }
            }
            else if (Event.Type == "audio-chunk")
            {
                if (Rate <= 0 || Event.Payload.Length % (Width * Channels) != 0)
                {
                    throw new InvalidDataException("Invalid Wyoming audio chunk.");
                }
                yield return new(Event.Payload, Rate, Width, Channels);
            }
            else if (Event.Type == "audio-stop")
            {
                if (Rate <= 0)
                {
                    throw new InvalidDataException("Wyoming audio stopped before start.");
                }
                Trace?.Output(new { encoding = "PCM", sampleRate = Rate, sampleWidth = Width, channels = Channels });
                Trace?.Complete();
                yield break;
            }
            else if (Event.Type == "error")
            {
                throw new InvalidOperationException("Wyoming synthesis failed.");
            }
        }
        throw new EndOfStreamException("Wyoming connection ended before audio-stop.");
    }
}
