using System.Buffers.Binary;
using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;

namespace Assister.Voice;

public sealed record ToneNote(string Name, double FrequencyHz, int DurationMs, int GapAfterMs,
    string Waveform, double Volume, double AttackMs, double ReleaseMs);
public sealed record ToneDefinition(int Version, string Name, double MasterVolume, int RepeatCount,
    int RepeatGapMs, ToneNote[] Tones);

public sealed class TonePreviewSpeech(ToneCatalog Catalog, string Name) : ITextToSpeechProvider
{
    public IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, CancellationToken Token)
        => Catalog.Audio(Name);
}

// Feedback is separate from a response: it must not close a provider's active voice turn.
public interface ITonePlayback
{
    bool SupportsTonePlayback => true;
    Task PlayToneAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token);
}

public sealed class ToneCatalog(IHostEnvironment Environment, IConfiguration Configuration)
{
    public static readonly string[] Names = ["awake", "confirmed", "done", "goodbye", "error", "intent-match", "ai-think", "ai-thought", "issue"];
    public bool Enabled => Configuration.GetValue("Voice:Tones:Enabled", true);
    public ToneDefinition Read(string Name)
    {
        if (!Names.Contains(Name, StringComparer.Ordinal)) { throw new ArgumentException("Unknown tone."); }
        var Local = Path.Combine(Environment.ContentRootPath, "tones");
        var Source = Path.GetFullPath(Path.Combine(Environment.ContentRootPath, "..", "..", "tones"));
        var Directory = Configuration["Voice:Tones:Path"] ?? (System.IO.Directory.Exists(Local) ? Local
            : System.IO.Directory.Exists(Source) ? Source : Path.Combine(AppContext.BaseDirectory, "tones"));
        var PathName = Path.Combine(Directory, Name + ".json");
        if (new FileInfo(PathName).Length > 65536) { throw new InvalidDataException("Tone definition is too large."); }
        var Definition = JsonSerializer.Deserialize<ToneDefinition>(File.ReadAllText(PathName), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty tone definition.");
        Validate(Definition);
        return Definition;
    }

    public static void Validate(ToneDefinition Definition)
    {
        static bool Range(double Value, double Min, double Max) => double.IsFinite(Value) && Value >= Min && Value <= Max;
        if (Definition.Version != 1 || !Range(Definition.MasterVolume, 0, 1) || Definition.RepeatCount is < 1 or > 10
            || Definition.RepeatGapMs is < 0 or > 10000 || Definition.Tones is not { Length: > 0 and <= 64 })
            throw new InvalidDataException("Invalid tone header.");
        foreach (var Note in Definition.Tones)
            if (Note is null || !Range(Note.FrequencyHz, 20, 20000) || Note.DurationMs is < 1 or > 10000 || Note.GapAfterMs is < 0 or > 10000
                || !Range(Note.Volume, 0, 1) || !Range(Note.AttackMs, 0, 10000) || !Range(Note.ReleaseMs, 0, 10000)
                || Note.Waveform is not ("sine" or "square" or "triangle" or "sawtooth"))
                throw new InvalidDataException("Invalid tone note.");
        var Duration = Definition.Tones.Sum(Note => (long)Note.DurationMs + Note.GapAfterMs) * Definition.RepeatCount
            + (Definition.RepeatCount - 1L) * Definition.RepeatGapMs;
        if (Duration > 15000) { throw new InvalidDataException("Tone exceeds 15 seconds."); }
    }

    public static AudioChunk Render(ToneDefinition Definition)
    {
        Validate(Definition);
        const int Rate = 48000;
        var Duration = Definition.Tones.Sum(Note => Note.DurationMs + Note.GapAfterMs) * Definition.RepeatCount
            + (Definition.RepeatCount - 1) * Definition.RepeatGapMs;
        var Pcm = new byte[Duration * 48 * 2];
        var Offset = 0;
        for (var Repeat = 0; Repeat < Definition.RepeatCount; Repeat++)
        {
            foreach (var Note in Definition.Tones)
            {
                var Count = Note.DurationMs * 48;
                var Attack = Math.Min(Note.DurationMs / 2.0, Note.AttackMs) * 48;
                var Release = Math.Min(Note.DurationMs / 2.0, Note.ReleaseMs) * 48;
                for (var Index = 0; Index < Count; Index++)
                {
                    var Phase = Index * Note.FrequencyHz / Rate;
                    var Cycle = Phase - Math.Floor(Phase);
                    var Sample = Note.Waveform switch
                    {
                        "square" => Cycle < 0.5 ? 1.0 : -1.0,
                        "triangle" => 2 / Math.PI * Math.Asin(Math.Sin(2 * Math.PI * Phase)),
                        "sawtooth" => 2 * Cycle - 1,
                        _ => Math.Sin(2 * Math.PI * Phase)
                    };
                    var Envelope = Math.Min(Attack == 0 ? 1 : Index / Attack, Release == 0 ? 1 : (Count - 1 - Index) / Release);
                    BinaryPrimitives.WriteInt16LittleEndian(Pcm.AsSpan((Offset + Index) * 2),
                        (short)Math.Round(Sample * Math.Clamp(Envelope, 0, 1) * Note.Volume * Definition.MasterVolume * short.MaxValue));
                }
                Offset += Count + Note.GapAfterMs * 48;
            }
            if (Repeat + 1 < Definition.RepeatCount) { Offset += Definition.RepeatGapMs * 48; }
        }
        return new(Pcm, Rate, 2, 1);
    }

    public AudioChunk RenderFor(string Name, AudioChunk Format)
    {
        if (Format.SampleWidth != 2 || Format.SampleRate is < 8000 or > 96000 || Format.Channels is < 1 or > 2)
            throw new InvalidDataException("Unsupported tone output format.");
        var Source = Render(Read(Name));
        var Frames = Source.Pcm.Length / 2;
        var Count = (int)Math.Ceiling(Frames * Format.SampleRate / 48000.0);
        var Pcm = new byte[Count * Format.Channels * 2];
        for (var Index = 0; Index < Count; Index++)
        {
            var Position = Index * 48000.0 / Format.SampleRate;
            var Left = Math.Min((int)Position, Frames - 1);
            var Right = Math.Min(Left + 1, Frames - 1);
            var A = BinaryPrimitives.ReadInt16LittleEndian(Source.Pcm.Span[(Left * 2)..]);
            var B = BinaryPrimitives.ReadInt16LittleEndian(Source.Pcm.Span[(Right * 2)..]);
            var Value = (short)Math.Round(A + (B - A) * (Position - Left));
            for (var Channel = 0; Channel < Format.Channels; Channel++)
                BinaryPrimitives.WriteInt16LittleEndian(Pcm.AsSpan((Index * Format.Channels + Channel) * 2), Value);
        }
        return new(Pcm, Format.SampleRate, 2, Format.Channels);
    }

    public async IAsyncEnumerable<AudioChunk> Audio(string Name)
    {
        yield return Render(Read(Name));
        await Task.CompletedTask;
    }
}

// Async-local scope follows the shared coordinator to satellite playback or rich-client cue events.
public static class VoiceFeedback
{
    private static readonly AsyncLocal<Func<string, CancellationToken, Task>?> Current = new();
    public static Task EmitAsync(string Name, CancellationToken Token) => Current.Value?.Invoke(Name, Token) ?? Task.CompletedTask;
    public static IDisposable Begin(Func<string, CancellationToken, Task> Emit)
    {
        var Previous = Current.Value;
        Current.Value = Emit;
        return new Scope(() => Current.Value = Previous);
    }
    private sealed class Scope(Action Restore) : IDisposable { public void Dispose() => Restore(); }
}
