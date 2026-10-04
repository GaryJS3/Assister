using Assister.Diagnostics;
using Assister.Contracts;
using System.Text;

namespace Assister.Speech.Wyoming;

public sealed class WyomingSpeechToTextProvider(WyomingEndpoint Endpoint) : ISpeechToTextProvider
{
    public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio,
        SpeechToTextOptions Options, CancellationToken CancellationToken)
    {
        using var Trace = RunTracing.CurrentKind == "SpeechToText" ? null : RunTracing.Start("SpeechToText", "Speech to text", "Invoke the Wyoming speech recognition provider.");
        Trace?.Input(new { languageRequested = Options.Language });
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        Timeout.CancelAfter(TimeSpan.FromSeconds(Endpoint.TimeoutSeconds));
        var Token = Timeout.Token;
        await using var Connection = await WyomingConnection.ConnectAsync(Endpoint, Token);
        var Info = await Connection.DescribeAsync(Token);
        if (!Info.Supports("asr"))
        {
            throw new InvalidOperationException("Wyoming endpoint does not advertise speech recognition.");
        }
        await Connection.Writer.WriteAsync(WyomingEvent.Create("transcribe", new { language = Options.Language }), Token);
        AudioChunk? Format = null;
        await foreach (var Chunk in Audio.WithCancellation(Token))
        {
            if (Chunk.SampleRate <= 0 || Chunk.SampleWidth is < 1 or > 4 || Chunk.Channels <= 0 ||
                Chunk.Pcm.Length % (Chunk.SampleWidth * Chunk.Channels) != 0)
            {
                throw new InvalidDataException("Invalid PCM audio.");
            }
            if (Format is null)
            {
                Format = Chunk;
                await Connection.Writer.WriteAsync(WyomingEvent.Create("audio-start",
                    new { rate = Chunk.SampleRate, width = Chunk.SampleWidth, channels = Chunk.Channels }), Token);
            }
            else if (Format.SampleRate != Chunk.SampleRate || Format.SampleWidth != Chunk.SampleWidth || Format.Channels != Chunk.Channels)
            {
                throw new InvalidDataException("PCM format changed during transcription.");
            }
            await Connection.Writer.WriteAsync(WyomingEvent.Create("audio-chunk",
                new { rate = Chunk.SampleRate, width = Chunk.SampleWidth, channels = Chunk.Channels }, Chunk.Pcm), Token);
        }
        if (Format is null)
        {
            throw new InvalidDataException("No audio received.");
        }
        await Connection.Writer.WriteAsync(WyomingEvent.Create("audio-stop"), Token);
        var Partial = new StringBuilder();
        while (await Connection.Reader.ReadAsync(Token) is { } Event)
        {
            if (Event.Type == "transcript")
            {
                Trace?.Output(new { transcript = Event.Data.GetProperty("text").GetString(), returnedLanguage = Event.Data.TryGetProperty("language", out var ReturnedLanguage) ? ReturnedLanguage.GetString() : Options.Language });
                Trace?.Complete();
                return new(Event.Data.GetProperty("text").GetString() ?? "",
                    Event.Data.TryGetProperty("language", out var Language) ? Language.GetString() : Options.Language);
            }
            if (Event.Type == "transcript-chunk")
            {
                Partial.Append(Event.Data.GetProperty("text").GetString());
            }
            if (Event.Type == "transcript-stop")
            {
                Trace?.Output(new { transcript = Partial.ToString(), returnedLanguage = Options.Language });
                Trace?.Complete();
                return new(Partial.ToString(), Options.Language);
            }
            if (Event.Type == "error")
            {
                throw new InvalidOperationException("Wyoming transcription failed.");
            }
        }
        throw new EndOfStreamException("Wyoming connection ended before transcript.");
    }
}
