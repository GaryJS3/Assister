using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Assister.Voice;
using Microsoft.Extensions.Configuration;

internal static class EndpointBenchmark
{
    public sealed record Clip(string Id, string Path, bool Complete, string Language, string Dataset, string? PcmBase64 = null);
    public static async Task RunAsync(string[] Args)
    {
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Voice:Endpointing:ModelPath"] = Args[1] }).Build();
        using var Classifier = new SmartTurnClassifier(Config);
        var Cold = Stopwatch.StartNew(); await Classifier.ScoreAsync(ReadOnlyMemory<short>.Empty, default);
        var ColdMs = Cold.Elapsed.TotalMilliseconds;
        var Manifest = Args[2] == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(Args[2]);
        if (Manifest.Length > 50 * 1024 * 1024) throw new InvalidDataException("Evaluation manifest is too large.");
        var Clips = JsonSerializer.Deserialize<Clip[]>(Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (Clips.Length is < 1 or > 200) throw new InvalidDataException("Invalid evaluation sample count.");
        var Rows = new List<object>();
        foreach (var Clip in Clips)
        {
            var Bytes = Clip.PcmBase64 is { } Encoded ? Convert.FromBase64String(Encoded) : await File.ReadAllBytesAsync(Clip.Path);
            var Pcm = MemoryMarshal.Cast<byte, short>(Bytes.AsSpan()).ToArray();
            if (Args.Contains("--pause")) Pcm = [.. Pcm, .. new short[4800]];
            var Cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var Clock = Stopwatch.StartNew();
            var Score = await Classifier.ScoreAsync(Pcm, default);
            Rows.Add(new { Clip.Id, Clip.Complete, Clip.Language, Clip.Dataset, audioSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Bytes)),
                audioMs = Pcm.Length / 16.0, Score.Probability,
                Score.FeatureMilliseconds, Score.InferenceMilliseconds, totalMs = Clock.Elapsed.TotalMilliseconds,
                cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - Cpu).TotalMilliseconds });
            if (Rows.Count == 1 && Args.Length > 3 && Args[3] != "--pause")
            {
                var Features = SmartTurnFeatures.Extract(Pcm);
                await File.WriteAllBytesAsync(Args[3], MemoryMarshal.AsBytes(Features.AsSpan()).ToArray());
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { model = Path.GetFileName(Args[1]), sampleCount = Rows.Count,
            appendedQuietMs = Args.Contains("--pause") ? 300 : 0, coldModelInitializationMs = ColdMs, results = Rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
