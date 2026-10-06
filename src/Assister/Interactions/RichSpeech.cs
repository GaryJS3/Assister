using Assister.Contracts;

namespace Assister.Interactions;

public sealed class RichSpeech(ISpeechToTextProvider Stt, ITextToSpeechProvider Tts, InteractionStore Store, IConfiguration Configuration)
{
    public async Task<string> TranscribeAsync(Guid Id, byte[] Bytes, CancellationToken Token)
    {
        Store.Append(Id, "stt.started", new { encoding = "pcm_s16le", sampleRate = 16000, channels = 1 }, "transcribing");
        try
        {
            async IAsyncEnumerable<AudioChunk> Audio()
            {
                for (var Offset = 0; Offset < Bytes.Length; Offset += 3200)
                {
                    Token.ThrowIfCancellationRequested();
                    yield return new(Bytes.AsMemory(Offset, Math.Min(3200, Bytes.Length - Offset)), 16000, 2, 1);
                    await Task.Yield();
                }
            }
            var Result = Stt is IStreamingSpeechToTextProvider Streaming
                ? await Streaming.TranscribeStreamingAsync(Audio(), new(), (Text, Cancellation) =>
                {
                    Cancellation.ThrowIfCancellationRequested();
                    Store.Append(Id, "stt.partial", new { text = Text[..Math.Min(1000, Text.Length)] });
                    return Task.CompletedTask;
                }, Token)
                : await Stt.TranscribeAsync(Audio(), new(), Token);
            Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(Result.Text) || Result.Text.Length > 1000) throw new InvalidDataException("Invalid transcript.");
            Store.SaveTranscript(Id, Result.Text);
            return Result.Text;
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Store.Append(Id, "stt.failed", new ProtocolError("stt_failed", "Speech could not be transcribed.", true));
            throw new RichSpeechException("stt_failed");
        }
    }
    public async Task SynthesizeAsync(Guid Id, string Text, CancellationToken Token)
    {
        Store.Append(Id, "tts.started", new { });
        try
        {
            using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Deadline.CancelAfter(TimeSpan.FromSeconds(60));
            using var Pcm = new MemoryStream();
            AudioChunk? Format = null;
            await foreach (var Chunk in Tts.SynthesizeAsync(Text, new(Configuration["TextToSpeech:Voice"]), Deadline.Token).WithCancellation(Deadline.Token))
            {
                if (Chunk.SampleRate is < 8000 or > 96000 || Chunk.SampleWidth is < 1 or > 4 || Chunk.Channels is < 1 or > 2
                    || Chunk.Pcm.Length % (Chunk.SampleWidth * Chunk.Channels) != 0 || Pcm.Length + Chunk.Pcm.Length > 8 * 1024 * 1024)
                    throw new InvalidDataException("Invalid speech audio.");
                Format ??= Chunk;
                if (Format.SampleRate != Chunk.SampleRate || Format.SampleWidth != Chunk.SampleWidth || Format.Channels != Chunk.Channels)
                    throw new InvalidDataException("Speech format changed.");
                Pcm.Write(Chunk.Pcm.Span);
            }
            if (Format is null || Pcm.Length == 0) throw new InvalidDataException("No speech audio.");
            Token.ThrowIfCancellationRequested();
            using var Wave = new MemoryStream();
            using (var Writer = new BinaryWriter(Wave, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                Writer.Write("RIFF"u8); Writer.Write((int)Pcm.Length + 36 + (int)(Pcm.Length & 1)); Writer.Write("WAVEfmt "u8); Writer.Write(16);
                Writer.Write((short)1); Writer.Write((short)Format.Channels); Writer.Write(Format.SampleRate);
                Writer.Write(Format.SampleRate * Format.Channels * Format.SampleWidth); Writer.Write((short)(Format.Channels * Format.SampleWidth));
                Writer.Write((short)(Format.SampleWidth * 8)); Writer.Write("data"u8); Writer.Write((int)Pcm.Length); Writer.Write(Pcm.ToArray());
                if ((Pcm.Length & 1) != 0) Writer.Write((byte)0);
            }
            Store.SaveAudio(Id, Wave.ToArray());
            Store.Append(Id, "tts.completed", new { });
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
        catch (Exception)
        { Store.Append(Id, "tts.failed", new ProtocolError("tts_failed", "Speech generation failed. The text answer is still available.", true)); }
    }
}
public sealed class RichSpeechException(string Code) : Exception(Code);
