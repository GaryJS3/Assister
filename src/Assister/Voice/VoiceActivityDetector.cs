using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Diagnostics;

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
        var Floor = double.PositiveInfinity;
        var Peak = 0.0;
        using var Trace = RunTracing.Start("Speech endpointing", "End microphone capture after speech followed by silence, using DC-corrected energy and the observed noise floor.");
        await foreach (var Chunk in Satellite.ReceiveAudioAsync(Token).WithCancellation(Token))
        {
            if (Chunk.SampleWidth != 2 || Chunk.Channels != 1) { throw new InvalidDataException(); }
            var Energy = EnergyOf(Chunk.Pcm);
            var Seconds = Chunk.Pcm.Length / (double)(Chunk.SampleRate * 2);
            Duration += Seconds;
            Floor = Math.Min(Floor, Energy);
            Peak = Math.Max(Peak, Energy);
            var EffectiveThreshold = Math.Max(Threshold, Floor * 2.5);
            // Keep the initial audio as pre-roll while estimating background energy. A peak
            // already captured during calibration can establish speech once the floor falls.
            if (Duration >= 0.25 && Peak >= EffectiveThreshold) { Spoken = true; }
            if (Duration < 0.25 || Energy >= EffectiveThreshold) { Silence = 0; }
            else { Silence += Seconds; }
            yield return Chunk;
            if (Spoken && Silence >= 0.9 || !Spoken && Duration >= 5 || Duration >= 20)
            {
                Trace.Detail("audioSeconds", Duration);
                Trace.Detail("noiseRms", Floor);
                Trace.Detail("peakRms", Peak);
                Trace.Detail("threshold", EffectiveThreshold);
                Trace.Complete(Duration >= 20 ? "duration-limit" : Spoken ? "silence" : "no-speech");
                await Satellite.SendEventAsync(new("end-of-speech"), Token);
                yield break;
            }
        }
    }
    private static double EnergyOf(ReadOnlyMemory<byte> Pcm)
    {
        double Sum = 0;
        double Mean = 0;
        var Samples = Math.Max(1, Pcm.Length / 2);
        for (var Index = 0; Index + 1 < Pcm.Length; Index += 2)
        { Mean += BinaryPrimitives.ReadInt16LittleEndian(Pcm.Span.Slice(Index, 2)) / 32768.0; }
        Mean /= Samples;
        for (var Index = 0; Index + 1 < Pcm.Length; Index += 2)
        {
            var Value = BinaryPrimitives.ReadInt16LittleEndian(Pcm.Span.Slice(Index, 2)) / 32768.0 - Mean;
            Sum += Value * Value;
        }
        return Math.Sqrt(Sum / Samples);
    }
}
