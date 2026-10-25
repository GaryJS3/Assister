using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Diagnostics;

namespace Assister.Speech.Wyoming;

// Prepare recognition during a quiet tail; only the input owner's end-of-capture commits a result.
public sealed class OverlappingSpeechToTextProvider(ISpeechToTextProvider Inner,
    double QuietThreshold = .003, int QuietMilliseconds = 800) : IStreamingSpeechToTextProvider
{
    public Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
        => TranscribeStreamingAsync(Audio, Options, (_, _) => Task.CompletedTask, Token);

    public async Task<TranscriptionResult> TranscribeStreamingAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options,
        Func<string, CancellationToken, Task> OnPartial, CancellationToken Token)
    {
        if (!double.IsFinite(QuietThreshold) || QuietThreshold is <= 0 or > .02 || QuietMilliseconds is < 160 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(QuietThreshold));
        using var Preparation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var Trace = RunTracing.Start("SpeechToText", "STT overlap", "Prepare the unchanged recognizer during quiet input; discard preparation if speech resumes. Never commit before capture ends.");
        var Clock = Stopwatch.StartNew();
        var Chunks = new List<AudioChunk>();
        AudioChunk? Format = null;
        Task<TranscriptionResult>? Prepared = null;
        var Invalidated = false; var Used = false; var Completed = false;
        double Sum = 0, AudioMs = 0, CapturedAt = 0;
        int Samples = 0, VoicedMs = 0, QuietMs = 0, Bytes = 0;
        double? PreparedAt = null;
        try
        {
            await using var Input = Audio.GetAsyncEnumerator(Token);
            while (await Input.MoveNextAsync())
            {
                var Chunk = Input.Current;
                Token.ThrowIfCancellationRequested();
                if (Chunk.SampleRate <= 0 || Chunk.SampleWidth is < 1 or > 4 || Chunk.Channels <= 0
                    || Chunk.Pcm.Length % ((long)Chunk.SampleWidth * Chunk.Channels) != 0)
                    throw new InvalidDataException("Invalid recognition audio.");
                if (Format is null && (Chunk.SampleRate != 16000 || Chunk.SampleWidth != 2 || Chunk.Channels != 1))
                {
                    // Other PCM formats keep the original streaming provider and its validation.
                    var Passthrough = Inner is IStreamingSpeechToTextProvider Original
                        ? await Original.TranscribeStreamingAsync(Forward(Chunk, Input, Token), Options, OnPartial, Token)
                        : await Inner.TranscribeAsync(Forward(Chunk, Input, Token), Options, Token);
                    Completed = true;
                    return Passthrough;
                }
                if (Chunk.Pcm.Length > 2 * 1024 * 1024 - Bytes) throw new InvalidDataException("Excessive recognition audio.");
                Format ??= Chunk;
                if (Format.SampleRate != Chunk.SampleRate || Format.SampleWidth != Chunk.SampleWidth || Format.Channels != Chunk.Channels)
                    throw new InvalidDataException("Recognition audio format changed.");
                Bytes += Chunk.Pcm.Length;
                AudioMs += Chunk.Pcm.Length * 1000.0 / (Chunk.SampleRate * Chunk.SampleWidth * Chunk.Channels);
                if (AudioMs > 30000) throw new InvalidDataException("Recognition audio exceeds thirty seconds.");
                Chunks.Add(Chunk with { Pcm = Chunk.Pcm.ToArray() });
                if (Chunk.SampleRate != 16000 || Chunk.SampleWidth != 2 || Chunk.Channels != 1) continue;
                for (var Offset = 0; Offset < Chunk.Pcm.Length; Offset += 2)
                {
                    var Value = BinaryPrimitives.ReadInt16LittleEndian(Chunk.Pcm.Span.Slice(Offset, 2)) / 32768.0;
                    Sum += Value * Value;
                    if (++Samples != 320) continue;
                    if (Math.Sqrt(Sum / Samples) >= QuietThreshold)
                    {
                        // Require clear speech to arm preparation; remain sensitive to soft resumed speech.
                        if (Math.Sqrt(Sum / Samples) >= Math.Max(.015, QuietThreshold)) VoicedMs += 20;
                        QuietMs = 0;
                        if (Prepared is not null && !Invalidated) { Invalidated = true; Preparation.Cancel(); }
                    }
                    else QuietMs += 20;
                    Samples = 0; Sum = 0;
                }
                // A buffered upload should not launch speculative work: no real-time pause can be hidden there.
                if (Prepared is null && VoicedMs >= 100 && QuietMs >= QuietMilliseconds
                    && Clock.Elapsed.TotalMilliseconds >= AudioMs - 250)
                {
                    PreparedAt = Clock.Elapsed.TotalMilliseconds;
                    Prepared = Inner.TranscribeAsync(Replay(Chunks.ToArray(), Preparation.Token), Options, Preparation.Token);
                }
            }
            if (Prepared is not null && Samples > 0 && Math.Sqrt(Sum / Samples) >= QuietThreshold)
            { Invalidated = true; Preparation.Cancel(); }
            CapturedAt = Clock.Elapsed.TotalMilliseconds;
            Token.ThrowIfCancellationRequested();
            if (Prepared is not null && !Invalidated)
            {
                try
                {
                    var Result = await Prepared;
                    Token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(Result.Text)) { Used = true; Completed = true; return Result; }
                }
                catch (Exception Error) when (!Token.IsCancellationRequested
                    && Error is IOException or InvalidOperationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
                { Trace.Detail("preparationFailure", Error.GetType().Name); }
            }
            var Final = Inner is IStreamingSpeechToTextProvider Streaming
                ? await Streaming.TranscribeStreamingAsync(Replay(Chunks, Token), Options, OnPartial, Token)
                : await Inner.TranscribeAsync(Replay(Chunks, Token), Options, Token);
            Completed = true;
            return Final;
        }
        finally
        {
            Preparation.Cancel();
            if (Prepared is not null)
            {
                try { await Prepared; }
                catch (Exception Error) when (Error is IOException or InvalidOperationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
            }
            Trace.Metadata(new { preparedAtMilliseconds = PreparedAt, captureCompletedMilliseconds = CapturedAt,
                postCaptureMilliseconds = CapturedAt > 0 ? Clock.Elapsed.TotalMilliseconds - CapturedAt : (double?)null,
                usedPreparedTranscript = Used, invalidatedByResumedSpeech = Invalidated, audioMilliseconds = AudioMs,
                quietThreshold = QuietThreshold, armingThreshold = Math.Max(.015, QuietThreshold), quietMilliseconds = QuietMilliseconds });
            if (Completed) Trace.Complete();
        }
    }

    private static async IAsyncEnumerable<AudioChunk> Replay(IEnumerable<AudioChunk> Chunks,
        [EnumeratorCancellation] CancellationToken Token)
    {
        foreach (var Chunk in Chunks) { Token.ThrowIfCancellationRequested(); yield return Chunk; }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<AudioChunk> Forward(AudioChunk First, IAsyncEnumerator<AudioChunk> Input,
        [EnumeratorCancellation] CancellationToken Token)
    {
        Token.ThrowIfCancellationRequested(); yield return First;
        while (await Input.MoveNextAsync()) { Token.ThrowIfCancellationRequested(); yield return Input.Current; }
    }
}
