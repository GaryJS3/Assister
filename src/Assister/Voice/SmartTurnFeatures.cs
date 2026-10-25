using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Assister.Voice;

// WhisperFeatureExtractor(chunk_length=8, do_normalize=True), after Smart Turn's
// left-padding/truncation. 16 kHz, periodic Hann 400, hop 160, 80 Slaney mel bins.
public static class SmartTurnFeatures
{
    private static readonly double[] Window = Enumerable.Range(0, 400).Select(I => .5 - .5 * Math.Cos(2 * Math.PI * I / 400)).ToArray();
    private static readonly (int Bin, double Weight)[][] Filters = MelFilters();
    public static float[] Extract(ReadOnlySpan<short> Pcm, CancellationToken Token = default)
    {
        const int Length = 128000;
        var Wave = new double[Length];
        var Count = Math.Min(Pcm.Length, Length);
        for (var I = 0; I < Count; I++) Wave[Length - Count + I] = Pcm[Pcm.Length - Count + I] / 32768.0;
        var Mean = Wave.Sum() / Length;
        var Variance = Wave.Sum(X => (X - Mean) * (X - Mean)) / Length;
        var Scale = Math.Sqrt(Variance + 1e-7);
        for (var I = 0; I < Length; I++) Wave[I] = (Wave[I] - Mean) / Scale;
        var Features = new float[80 * 800];
        var Spectrum = new Complex[400];
        var Power = new double[201];
        var Prefix = new float[80];
        var PrefixReady = false;
        var Maximum = double.NegativeInfinity;
        for (var Frame = 0; Frame < 800; Frame++)
        {
            Token.ThrowIfCancellationRequested();
            var InPrefix = Frame * 160 + 199 < Length - Count;
            if (InPrefix && PrefixReady)
            {
                for (var Mel = 0; Mel < 80; Mel++) Features[Mel * 800 + Frame] = Prefix[Mel];
                continue;
            }
            for (var I = 0; I < 400; I++)
            {
                var Index = Frame * 160 + I - 200;
                if (Index < 0) Index = -Index;
                if (Index >= Length) Index = 2 * Length - 2 - Index;
                Spectrum[I] = new(Wave[Index] * Window[I], 0);
            }
            Fourier.Forward(Spectrum, FourierOptions.NoScaling);
            for (var I = 0; I <= 200; I++) Power[I] = Spectrum[I].Real * Spectrum[I].Real + Spectrum[I].Imaginary * Spectrum[I].Imaginary;
            for (var Mel = 0; Mel < 80; Mel++)
            {
                double Energy = 0;
                foreach (var (Bin, Weight) in Filters[Mel]) Energy += Power[Bin] * Weight;
                var Log = Math.Log10(Math.Max(1e-10, Energy));
                Features[Mel * 800 + Frame] = (float)Log;
                if (InPrefix) Prefix[Mel] = (float)Log;
                Maximum = Math.Max(Maximum, Log);
            }
            if (InPrefix) PrefixReady = true;
        }
        for (var I = 0; I < Features.Length; I++) Features[I] = (float)((Math.Max(Features[I], Maximum - 8) + 4) / 4);
        return Features;
    }
    private static (int, double)[][] MelFilters()
    {
        static double Mel(double Hz) => Hz < 1000 ? Hz / (200.0 / 3) : 15 + Math.Log(Hz / 1000) / (Math.Log(6.4) / 27);
        static double Hz(double Mel) => Mel < 15 ? Mel * (200.0 / 3) : 1000 * Math.Exp((Mel - 15) * Math.Log(6.4) / 27);
        var Edges = Enumerable.Range(0, 82).Select(I => Hz(Mel(8000) * I / 81)).ToArray();
        return Enumerable.Range(0, 80).Select(M => Enumerable.Range(0, 201).Select(B =>
            (Bin: B, Weight: Math.Max(0, Math.Min((B * 40.0 - Edges[M]) / (Edges[M + 1] - Edges[M]),
                (Edges[M + 2] - B * 40.0) / (Edges[M + 2] - Edges[M + 1]))) * 2 / (Edges[M + 2] - Edges[M])))
            .Where(P => P.Weight > 0).ToArray()).ToArray();
    }
}
