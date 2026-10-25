using System.Buffers.Binary;

namespace Assister.Voice;

public sealed record AdaptiveTurnOptions(double EnergyThreshold = .004, int MinimumSpeechMs = 120,
    int AnalysisQuietMs = 300, int FallbackQuietMs = 900, int IncompleteQuietMs = 1600,
    double CompleteProbability = .8, double IncompleteProbability = .2, int InferenceTimeoutMs = 400)
{
    public static AdaptiveTurnOptions FromConfiguration(IConfiguration Config) => new(
        EnergyThreshold: Math.Clamp(Config.GetValue("Voice:Endpointing:EnergyThreshold", .004), .001, .1),
        AnalysisQuietMs: Math.Clamp(Config.GetValue("Voice:Endpointing:AnalysisQuietMs", 300), 200, 700),
        FallbackQuietMs: 900,
        IncompleteQuietMs: Math.Clamp(Config.GetValue("Voice:Endpointing:IncompleteQuietMs", 1600), 1000, 2000),
        CompleteProbability: Math.Clamp(Config.GetValue("Voice:Endpointing:CompleteProbability", .8), .5, .99),
        IncompleteProbability: Math.Clamp(Config.GetValue("Voice:Endpointing:IncompleteProbability", .2), .01, .49),
        InferenceTimeoutMs: Math.Clamp(Config.GetValue("Voice:Endpointing:InferenceTimeoutMs", 400), 100, 700));
}

// Audio-time state, independent of transport chunk sizes, model execution and wall clocks.
public sealed class AdaptiveTurnState(AdaptiveTurnOptions Options)
{
    private readonly short[] Ring = new short[128000];
    private readonly short[] Frame = new short[320];
    private int RingHead, RingCount, FrameCount, VoicedMs;
    private int CandidateMs, LongestCandidateMs, LastCandidateMs;
    private double NoiseFloor = double.PositiveInfinity, Peak;
    private bool ConfirmedSpeech;
    private long ScoredRevision = -1;
    private double? Probability;
    public long SpeechRevision { get; private set; }
    public long AudioBytes { get; private set; }
    public int DurationMs { get; private set; }
    public int QuietMs { get; private set; }
    public double? CompletionProbability => Probability;
    public bool SpeechSeen => ConfirmedSpeech;
    public bool ShouldScore => SpeechSeen && QuietMs >= Options.AnalysisQuietMs && ScoredRevision != SpeechRevision;
    public void Observe(ReadOnlySpan<byte> Pcm)
    {
        if (Pcm.Length % 2 != 0) throw new InvalidDataException("PCM must contain complete S16 samples.");
        AudioBytes += Pcm.Length;
        for (var I = 0; I < Pcm.Length; I += 2)
        {
            var Sample = BinaryPrimitives.ReadInt16LittleEndian(Pcm[I..]);
            Ring[RingHead] = Sample; RingHead = (RingHead + 1) % Ring.Length; RingCount = Math.Min(Ring.Length, RingCount + 1);
            Frame[FrameCount++] = Sample;
            if (FrameCount != Frame.Length) continue;
            FrameCount = 0; DurationMs += 20;
            double Mean = 0, Sum = 0;
            foreach (var Value in Frame) Mean += Value / 32768.0;
            Mean /= Frame.Length;
            foreach (var Value in Frame) { var Delta = Value / 32768.0 - Mean; Sum += Delta * Delta; }
            var Energy = Math.Sqrt(Sum / Frame.Length);
            NoiseFloor = Math.Min(NoiseFloor, Energy); Peak = Math.Max(Peak, Energy);
            var Threshold = Math.Max(Options.EnergyThreshold, NoiseFloor * 2.5);
            if (Energy >= Options.EnergyThreshold)
            { CandidateMs += 20; LongestCandidateMs = Math.Max(LongestCandidateMs, CandidateMs); LastCandidateMs = DurationMs; }
            else CandidateMs = 0;
            if (Energy >= Threshold)
            {
                VoicedMs += 20; QuietMs = 0; SpeechRevision++; Probability = null;
                if (DurationMs >= 250 && VoicedMs >= Options.MinimumSpeechMs) ConfirmedSpeech = true;
            }
            else if (!ConfirmedSpeech && DurationMs >= 250 && LongestCandidateMs >= Options.MinimumSpeechMs && Peak >= Threshold)
            {
                // As in the conservative VAD, speech captured during calibration remains
                // usable once a later quiet frame establishes the actual noise floor.
                ConfirmedSpeech = true; QuietMs = DurationMs - LastCandidateMs; SpeechRevision++;
            }
            else { QuietMs += 20; if (!SpeechSeen) VoicedMs = 0; }
        }
    }
    public (long Revision, short[] Pcm) BeginScore()
    {
        ScoredRevision = SpeechRevision;
        var Pcm = new short[RingCount];
        var Start = (RingHead - RingCount + Ring.Length) % Ring.Length;
        for (var I = 0; I < Pcm.Length; I++) Pcm[I] = Ring[(Start + I) % Ring.Length];
        return (SpeechRevision, Pcm);
    }
    public bool Apply(long Revision, double? Score)
    {
        if (Revision != SpeechRevision) return false;
        Probability = Score is >= 0 and <= 1 && double.IsFinite(Score.Value) ? Score : null;
        return true;
    }
    public string? Decision => DurationMs >= 20000 ? "duration-limit"
        : !SpeechSeen ? DurationMs >= 5000 ? "no-speech" : null
        : QuietMs < Options.AnalysisQuietMs ? null
        : Probability >= Options.CompleteProbability ? "semantic-complete"
        : QuietMs >= (Probability <= Options.IncompleteProbability ? Options.IncompleteQuietMs : Options.FallbackQuietMs)
            ? Probability <= Options.IncompleteProbability ? "incomplete-timeout" : "silence-fallback" : null;
}
