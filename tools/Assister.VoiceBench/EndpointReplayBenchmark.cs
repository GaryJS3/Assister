using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Voice;
using Microsoft.Extensions.Configuration;

internal static class EndpointReplayBenchmark
{
    public static async Task RunAsync(string[] Args)
    {
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Voice:Endpointing:ModelPath"] = Args[1] }).Build();
        using var Classifier = new SmartTurnClassifier(Config);
        var Cold = Stopwatch.StartNew(); await Classifier.ScoreAsync(ReadOnlyMemory<short>.Empty, default);
        var ColdMs = Cold.Elapsed.TotalMilliseconds;
        var Clips = JsonSerializer.Deserialize<EndpointBenchmark.Clip[]>(await File.ReadAllTextAsync(Args[2]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var Rows = new List<object>(); var Index = 0;
        foreach (var Clip in Clips)
        {
            var Pcm = await File.ReadAllBytesAsync(Clip.Path);
            foreach (var Adaptive in Index++ % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var Clock = Stopwatch.StartNew();
                var Source = new Replay(Pcm, Clock);
                var Cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var Stream = Adaptive ? AdaptiveEndpointing.CaptureAsync(Source, Classifier, new(), false, Deadline.Token)
                    : VoiceActivityDetector.UntilSilenceAsync(Source, .004, Deadline.Token);
                long Bytes = 0;
                await foreach (var Chunk in Stream.WithCancellation(Deadline.Token)) Bytes += Chunk.Pcm.Length;
                Rows.Add(new { Clip.Id, Clip.Complete, adaptive = Adaptive, sourceAudioMs = Pcm.Length / 32.0,
                    deliveredAudioMs = Bytes / 32.0, finalVoicedMs = Source.FinalVoicedMs,
                    audioEndpointDelayMs = Bytes / 32.0 - Source.FinalVoicedMs,
                    measuredPauseToEndpointMs = Clock.Elapsed.TotalMilliseconds - Source.LastSpeechWallMs,
                    endedBeforeLastEnergyFrame = Bytes / 32.0 + 20 < Source.FinalVoicedMs,
                    endpointReason = Source.EndpointReason,
                    incompleteClassifiedComplete = !Clip.Complete && Source.EndpointReason == "semantic-complete",
                    wallMs = Clock.Elapsed.TotalMilliseconds, cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - Cpu).TotalMilliseconds });
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { measurement = "20 ms paced recorded audio through actual C# capture policies; excludes device, endpoint transport, STT and playback", coldModelInitializationMs = ColdMs, results = Rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class Replay(byte[] Pcm, Stopwatch Clock) : ISatelliteConnection
    {
        public string SatelliteId => "endpoint-replay";
        public string Name => "Endpoint replay";
        public string? Area => null;
        public double LastSpeechWallMs { get; private set; }
        public string? EndpointReason { get; private set; }
        public double FinalVoicedMs { get; } = FindFinalVoice(Pcm);
        private static double FindFinalVoice(byte[] Pcm)
        {
            double Last = 0;
            for (var Offset = 0; Offset + 640 <= Pcm.Length; Offset += 640)
            {
                if (Voiced(Pcm.AsSpan(Offset, 640))) Last = (Offset + 640) / 32.0;
            }
            return Last;
        }
        public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([EnumeratorCancellation] CancellationToken Token)
        {
            for (var Offset = 0; Offset < Pcm.Length + 96000; Offset += 640)
            {
                var Remaining = (Offset + 640) / 32.0 - Clock.Elapsed.TotalMilliseconds;
                if (Remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(Remaining), Token);
                var Frame = new byte[640];
                if (Offset < Pcm.Length) Pcm.AsSpan(Offset, Math.Min(640, Pcm.Length - Offset)).CopyTo(Frame);
                if (Voiced(Frame)) LastSpeechWallMs = Clock.Elapsed.TotalMilliseconds;
                yield return new(Frame, 16000, 2, 1);
            }
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { EndpointReason = Event.Text; return Task.CompletedTask; }
        public Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) => throw new NotSupportedException();
        private static bool Voiced(ReadOnlySpan<byte> Pcm)
        {
            double Mean = 0, Sum = 0;
            var Samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(Pcm);
            foreach (var Sample in Samples) Mean += Sample / 32768.0;
            Mean /= Samples.Length;
            foreach (var Sample in Samples) { var Delta = Sample / 32768.0 - Mean; Sum += Delta * Delta; }
            return Math.Sqrt(Sum / Samples.Length) >= .004;
        }
    }
}
