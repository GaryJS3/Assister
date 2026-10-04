using System.Collections.Concurrent;
using Assister.Contracts;

namespace Assister.Satellites;

public sealed class VoiceAudioStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, (byte[] Data, DateTimeOffset Expires)> Audio = new();
    private readonly object Gate = new();
    private readonly Timer Cleanup;
    public VoiceAudioStore() => Cleanup = new Timer(_ => RemoveExpired(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    private void RemoveExpired()
    {
        foreach (var Item in Audio.Where(Item => Item.Value.Expires <= DateTimeOffset.UtcNow)) { Audio.TryRemove(Item.Key, out _); }
    }
    public Guid Add(byte[] Wave)
    {
        if (Wave.Length is < 1 or > 8 * 1024 * 1024) { throw new InvalidDataException("Audio object size is invalid."); }
        lock (Gate)
        {
            RemoveExpired();
            if (Audio.Count >= 16) { throw new InvalidOperationException("Audio storage is full."); }
            var Id = Guid.NewGuid();
            Audio[Id] = (Wave, DateTimeOffset.UtcNow.AddMinutes(2));
            return Id;
        }
    }
    public byte[]? Get(Guid Id) => Audio.TryGetValue(Id, out var Item) && Item.Expires > DateTimeOffset.UtcNow ? Item.Data : null;
    public void Remove(Guid Id) => Audio.TryRemove(Id, out _);
    public void Dispose() => Cleanup.Dispose();

    public static async Task<byte[]> WaveAsync(IAsyncEnumerable<AudioChunk> Chunks, CancellationToken Token)
    {
        AudioChunk? Format = null;
        using var Pcm = new MemoryStream();
        await foreach (var Chunk in Chunks.WithCancellation(Token))
        {
            Format ??= Chunk;
            if (Chunk.SampleWidth != 2 || Chunk.SampleRate is < 8000 or > 96000 || Chunk.Channels is < 1 or > 2
                || Chunk.SampleRate != Format.SampleRate || Chunk.Channels != Format.Channels || Chunk.Pcm.Length % (2 * Chunk.Channels) != 0
                || Pcm.Length + Chunk.Pcm.Length > 4 * 1024 * 1024) { throw new InvalidDataException(); }
            Pcm.Write(Chunk.Pcm.Span);
        }
        if (Format is null || Pcm.Length == 0) { throw new InvalidDataException(); }
        using var Wave = new MemoryStream();
        using var Writer = new BinaryWriter(Wave);
        Writer.Write("RIFF"u8); Writer.Write((int)Pcm.Length + 36); Writer.Write("WAVEfmt "u8); Writer.Write(16);
        Writer.Write((short)1); Writer.Write((short)Format.Channels); Writer.Write(Format.SampleRate);
        Writer.Write(Format.SampleRate * Format.Channels * 2); Writer.Write((short)(Format.Channels * 2)); Writer.Write((short)16);
        Writer.Write("data"u8); Writer.Write((int)Pcm.Length); Writer.Write(Pcm.ToArray());
        return Wave.ToArray();
    }

    public static async Task<byte[]> Wave48kAsync(IAsyncEnumerable<AudioChunk> Chunks, CancellationToken Token)
    {
        var Source = await WaveAsync(Chunks, Token);
        var Channels = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(Source.AsSpan(22));
        var Rate = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Source.AsSpan(24));
        if (Rate == 48000 && Channels == 1) { return Source; }
        var Frames = (Source.Length - 44) / (Channels * 2);
        var Count = checked((int)Math.Ceiling(Frames * 48000.0 / Rate));
        if (Count > (8 * 1024 * 1024 - 44) / 2) { throw new InvalidDataException("Response audio is too large."); }
        var Pcm = new byte[Count * 2];
        double Sample(int Index)
        {
            double Sum = 0;
            for (var Channel = 0; Channel < Channels; Channel++)
                Sum += System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(Source.AsSpan(44 + (Index * Channels + Channel) * 2));
            return Sum / Channels;
        }
        for (var Index = 0; Index < Count; Index++)
        {
            if (Index % 4096 == 0) { Token.ThrowIfCancellationRequested(); }
            var Position = Index * (double)Rate / 48000.0;
            var Left = Math.Min((int)Position, Frames - 1);
            var Right = Math.Min(Left + 1, Frames - 1);
            var Value = Sample(Left) + (Sample(Right) - Sample(Left)) * (Position - Left);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(Pcm.AsSpan(Index * 2), (short)Math.Clamp(Math.Round(Value), short.MinValue, short.MaxValue));
        }
        return await WaveAsync(Output(), Token);
        async IAsyncEnumerable<AudioChunk> Output() { yield return new(Pcm, 48000, 2, 1); await Task.CompletedTask; }
    }
}
