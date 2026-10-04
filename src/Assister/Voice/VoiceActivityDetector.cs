using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Satellites;

namespace Assister.Voice;

public static class VoiceActivityDetector
{
    // Conservative energy VAD for the initial bridge. Keep the threshold configurable for the real microphone.
    public static async IAsyncEnumerable<AudioChunk> UntilSilenceAsync(ISatelliteConnection Satellite, double Threshold,
        [EnumeratorCancellation] CancellationToken Token)
    {
        var Spoken = false;
        var Silence = 0.0;
        var Duration = 0.0;
        await foreach (var Chunk in Satellite.ReceiveAudioAsync(Token).WithCancellation(Token))
        {
            if (Chunk.SampleWidth != 2 || Chunk.Channels != 1) { throw new InvalidDataException(); }
            var Energy = EnergyOf(Chunk.Pcm);
            var Seconds = Chunk.Pcm.Length / (double)(Chunk.SampleRate * 2);
            Duration += Seconds;
            if (Energy >= Threshold) { Spoken = true; Silence = 0; }
            else { Silence += Seconds; }
            yield return Chunk;
            if (Spoken && Silence >= 0.9 || !Spoken && Duration >= 5 || Duration >= 20)
            {
                await Satellite.SendEventAsync(new("end-of-speech"), Token);
                yield break;
            }
        }
    }
    private static double EnergyOf(ReadOnlyMemory<byte> Pcm)
    {
        double Sum = 0;
        for (var Index = 0; Index + 1 < Pcm.Length; Index += 2)
        {
            var Value = BinaryPrimitives.ReadInt16LittleEndian(Pcm.Span.Slice(Index, 2)) / 32768.0;
            Sum += Value * Value;
        }
        return Math.Sqrt(Sum / Math.Max(1, Pcm.Length / 2));
    }
}
