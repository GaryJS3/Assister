using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assister.Contracts;
using Assister.Speech.Wyoming;

internal static class SttBenchmark
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public sealed record Clip(string Name, string Path, string Reference);
    public sealed record Settings(string WyomingHost, int WyomingPort, string HttpBaseUrl, string[] Models, Clip[] Clips,
        bool? VadFilter = null, bool Overlap = false, int OverlapQuietMilliseconds = 800, double OverlapQuietThreshold = .003);
    private sealed record Mode(string Name, Func<IAsyncEnumerable<AudioChunk>, CancellationToken, Task<TranscriptionResult>> Run);
    private sealed record Measurement(string Clip, string Mode, int Sample, double AudioMs, double PostCaptureMs, double TotalMs, string Transcript, double ReferenceWordErrorRate);

    public static async Task RunAsync(string[] Args)
    {
        var Config = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Args[1]), Json)!;
        var Samples = Args.Length > 2 ? int.Parse(Args[2]) : 3;
        if (Samples is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(Samples));
        var Paced = Args.Contains("--paced");
        using var Http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var Wyoming = new WyomingSpeechToTextProvider(new(Config.WyomingHost, Config.WyomingPort, 120));
        var Modes = new List<Mode> { new("wyoming-large", (Audio, Token) => Wyoming.TranscribeAsync(Audio, new("en"), Token)) };
        if (Config.Overlap)
        {
            var Overlap = new OverlappingSpeechToTextProvider(Wyoming, Config.OverlapQuietThreshold, Config.OverlapQuietMilliseconds);
            Modes.Add(new("wyoming-large-overlap", (Audio, Token) => Overlap.TranscribeAsync(Audio, new("en"), Token)));
        }
        foreach (var Model in Config.Models)
            Modes.Add(new(Model + (Config.VadFilter is false ? "/vad-off" : ""), async (Audio, Token) =>
            {
                using var Pcm = new MemoryStream();
                await foreach (var Chunk in Audio.WithCancellation(Token)) Pcm.Write(Chunk.Pcm.Span);
                using var Form = new MultipartFormDataContent();
                var File = new ByteArrayContent(Wave(Pcm.ToArray()));
                File.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                Form.Add(File, "file", "speech.wav"); Form.Add(new StringContent(Model), "model");
                Form.Add(new StringContent("en"), "language"); Form.Add(new StringContent("json"), "response_format");
                if (Config.VadFilter is { } Vad) Form.Add(new StringContent(Vad ? "true" : "false"), "vad_filter");
                using var Response = await Http.PostAsync(Config.HttpBaseUrl.TrimEnd('/') + "/audio/transcriptions", Form, Token);
                Response.EnsureSuccessStatusCode();
                var Result = await Response.Content.ReadFromJsonAsync<JsonElement>(Token);
                return new(Result.GetProperty("text").GetString() ?? "", "en");
            }));
        var Rows = new List<Measurement>();
        for (var Sample = -1; Sample < Samples; Sample++)
        for (var ClipIndex = 0; ClipIndex < Config.Clips.Length; ClipIndex++)
        {
            var Clip = Config.Clips[ClipIndex];
            var Bytes = await File.ReadAllBytesAsync(Clip.Path);
            var Pcm = ReadPcm(Bytes);
            for (var Offset = 0; Offset < Modes.Count; Offset++)
            {
                var Mode = Modes[(Math.Max(Sample, 0) + ClipIndex + Offset) % Modes.Count];
                using var Deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var Clock = Stopwatch.StartNew(); double? Ended = null;
                async IAsyncEnumerable<AudioChunk> Audio([EnumeratorCancellation] CancellationToken Token)
                {
                    for (var Position = 0; Position < Pcm.Length; Position += 3200)
                    {
                        var Size = Math.Min(3200, Pcm.Length - Position);
                        if (Paced) await Task.Delay(TimeSpan.FromSeconds(Size / 32000.0), Token);
                        yield return new(Pcm.AsMemory(Position, Size), 16000, 2, 1);
                    }
                    Ended = Clock.Elapsed.TotalMilliseconds;
                }
                var Result = await Mode.Run(Audio(Deadline.Token), Deadline.Token);
                if (Sample >= 0) Rows.Add(new(Clip.Name, Mode.Name, Sample, Pcm.Length / 32.0,
                    Clock.Elapsed.TotalMilliseconds - Ended!.Value, Clock.Elapsed.TotalMilliseconds, Result.Text, WordErrorRate(Clip.Reference, Result.Text)));
                Console.Error.WriteLine($"{Clip.Name} / {Mode.Name} / {(Sample < 0 ? "warmup" : Sample)} complete");
            }
        }
        double Percentile(IEnumerable<double> Values, double P) { var Sorted = Values.Order().ToArray(); return Sorted[(int)Math.Ceiling(Sorted.Length * P) - 1]; }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow, samplesPerClipMode = Samples, paced = Paced,
            overlapQuietMilliseconds = Config.OverlapQuietMilliseconds, overlapQuietThreshold = Config.OverlapQuietThreshold,
            scope = "Retained PCM replay; STT only, no routing, device actions or playback. First pass per clip/mode discarded; rotating order. Reference is original transcript, not independent human ground truth.",
            summary = Rows.GroupBy(Row => Row.Mode).Select(Group => new { mode = Group.Key, count = Group.Count(),
                postCaptureMedianMs = Percentile(Group.Select(Row => Row.PostCaptureMs), .5), postCaptureP95Ms = Percentile(Group.Select(Row => Row.PostCaptureMs), .95),
                exactReferenceMatches = Group.Count(Row => Row.ReferenceWordErrorRate == 0), meanReferenceWordErrorRate = Group.Average(Row => Row.ReferenceWordErrorRate) }),
            measurements = Rows
        }, Json));
    }

    internal static byte[] ReadPcm(byte[] Wave)
    {
        using var Reader = new BinaryReader(new MemoryStream(Wave));
        if (Encoding.ASCII.GetString(Reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not RIFF");
        Reader.ReadInt32(); if (Encoding.ASCII.GetString(Reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not WAV");
        var ValidFormat = false;
        while (Reader.BaseStream.Position + 8 <= Wave.Length)
        {
            var Name = Encoding.ASCII.GetString(Reader.ReadBytes(4)); var Size = Reader.ReadInt32();
            if (Size < 0 || Reader.BaseStream.Position + Size > Wave.Length) throw new InvalidDataException("Invalid WAV chunk");
            var Start = Reader.BaseStream.Position;
            if (Name == "fmt ")
                ValidFormat = Size >= 16 && Reader.ReadInt16() == 1 && Reader.ReadInt16() == 1 && Reader.ReadInt32() == 16000
                    && Reader.ReadInt32() == 32000 && Reader.ReadInt16() == 2 && Reader.ReadInt16() == 16;
            if (Name == "data") return ValidFormat && Size % 2 == 0 ? Reader.ReadBytes(Size) : throw new InvalidDataException("Requires mono 16 kHz PCM16");
            Reader.BaseStream.Position = Start + Size + (Size & 1);
        }
        throw new InvalidDataException("No PCM");
    }

    internal static byte[] Wave(byte[] Pcm)
    {
        using var Output = new MemoryStream(); using var Writer = new BinaryWriter(Output);
        Writer.Write("RIFF"u8); Writer.Write(Pcm.Length + 36); Writer.Write("WAVEfmt "u8); Writer.Write(16);
        Writer.Write((short)1); Writer.Write((short)1); Writer.Write(16000); Writer.Write(32000); Writer.Write((short)2); Writer.Write((short)16);
        Writer.Write("data"u8); Writer.Write(Pcm.Length); Writer.Write(Pcm); return Output.ToArray();
    }

    private static double WordErrorRate(string Reference, string Actual)
    {
        string[] Words(string Text) => Regex.Matches(Text.Replace("%", " percent ").ToLowerInvariant(), "[a-z0-9]+")
            .Select(Match => Match.Value switch { "ten" => "10", "fifteen" => "15", _ => Match.Value }).ToArray();
        var Expected = Words(Reference); var Received = Words(Actual);
        var Distance = new int[Expected.Length + 1, Received.Length + 1];
        for (var I = 0; I <= Expected.Length; I++) Distance[I, 0] = I;
        for (var J = 0; J <= Received.Length; J++) Distance[0, J] = J;
        for (var I = 1; I <= Expected.Length; I++) for (var J = 1; J <= Received.Length; J++)
            Distance[I, J] = Math.Min(Distance[I - 1, J] + 1, Math.Min(Distance[I, J - 1] + 1, Distance[I - 1, J - 1] + (Expected[I - 1] == Received[J - 1] ? 0 : 1)));
        return Distance[Expected.Length, Received.Length] / (double)Math.Max(1, Expected.Length);
    }
}
