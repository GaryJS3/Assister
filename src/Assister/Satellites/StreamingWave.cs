using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Assister.Contracts;

namespace Assister.Satellites;

// A single WAV stream, with continuous interpolation across provider chunk boundaries.
public static class StreamingWave
{
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> Encode48kAsync(IAsyncEnumerable<AudioChunk> Chunks,
        [EnumeratorCancellation] CancellationToken Token)
    {
        AudioChunk? Format = null;
        long InputBytes = 0, Frames = 0, OutputIndex = 0;
        double Previous = 0;
        await foreach (var Chunk in Chunks.WithCancellation(Token))
        {
            Format ??= Chunk;
            InputBytes += Chunk.Pcm.Length;
            if (Chunk.SampleWidth != 2 || Chunk.SampleRate is < 8000 or > 96000 || Chunk.Channels is < 1 or > 2
                || Chunk.SampleRate != Format.SampleRate || Chunk.Channels != Format.Channels
                || Chunk.Pcm.Length % (2 * Chunk.Channels) != 0 || InputBytes > 4 * 1024 * 1024)
                throw new InvalidDataException("Invalid streaming PCM format or size.");
            if (Chunk.Pcm.IsEmpty) continue;
            if (Frames == 0)
            {
                using var Header = new MemoryStream();
                using var Writer = new BinaryWriter(Header);
                Writer.Write("RIFF"u8); Writer.Write(uint.MaxValue); Writer.Write("WAVEfmt "u8); Writer.Write(16);
                Writer.Write((short)1); Writer.Write((short)1); Writer.Write(48000); Writer.Write(96000);
                Writer.Write((short)2); Writer.Write((short)16); Writer.Write("data"u8); Writer.Write(uint.MaxValue);
                yield return Header.ToArray();
            }
            // Bound each HTTP write even if a provider returns a large chunk.
            using var Output = new MemoryStream(8192);
            for (var Offset = 0; Offset < Chunk.Pcm.Length; Offset += Chunk.Channels * 2)
            {
                if (Offset % 4096 == 0) Token.ThrowIfCancellationRequested();
                double Current = 0;
                for (var Channel = 0; Channel < Chunk.Channels; Channel++)
                    Current += BinaryPrimitives.ReadInt16LittleEndian(Chunk.Pcm.Span.Slice(Offset + Channel * 2));
                Current /= Chunk.Channels;
                if (Frames == 0) Previous = Current;
                while (OutputIndex * (double)Chunk.SampleRate / 48000 <= Frames)
                {
                    var Position = OutputIndex * (double)Chunk.SampleRate / 48000;
                    var Value = Frames == 0 ? Current : Previous + (Current - Previous) * (Position - (Frames - 1));
                    var Sample = (short)Math.Clamp(Math.Round(Value), short.MinValue, short.MaxValue);
                    Output.WriteByte((byte)Sample); Output.WriteByte((byte)(Sample >> 8));
                    if (++OutputIndex > (8 * 1024 * 1024 - 44) / 2) throw new InvalidDataException("Response audio is too large.");
                    if (Output.Length >= 8192) { yield return Output.ToArray(); Output.SetLength(0); }
                }
                Previous = Current;
                Frames++;
            }
            if (Output.Length > 0) yield return Output.ToArray();
        }
        if (Frames == 0 || Format is null) throw new InvalidDataException("Empty streaming audio.");
        // Repeat the final sample for the fractional tail, exactly as the buffered resampler does.
        using var Tail = new MemoryStream();
        while (OutputIndex < (long)Math.Ceiling(Frames * 48000.0 / Format.SampleRate))
        {
            Token.ThrowIfCancellationRequested();
            var Sample = (short)Math.Clamp(Math.Round(Previous), short.MinValue, short.MaxValue);
            Tail.WriteByte((byte)Sample); Tail.WriteByte((byte)(Sample >> 8));
            if (++OutputIndex > (8 * 1024 * 1024 - 44) / 2) throw new InvalidDataException("Response audio is too large.");
        }
        if (Tail.Length > 0) yield return Tail.ToArray();
    }
}
