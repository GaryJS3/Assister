using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Satellites;
using Assister.Voice;
using Microsoft.Extensions.Configuration;

// Offline orchestration benchmark: actual VoicePipeline, deterministic simulated providers.
// Does not connect to Home Assistant, inference servers or physical audio devices.
if (args.FirstOrDefault() == "--stt-analyze")
{
    var Config = System.Text.Json.JsonSerializer.Deserialize<SttBenchmark.Settings>(await File.ReadAllTextAsync(args[1]), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    foreach (var Clip in Config.Clips)
    {
        var Pcm = SttBenchmark.ReadPcm(await File.ReadAllBytesAsync(Clip.Path));
        var Energy = Enumerable.Range(0, Pcm.Length / 640).Select(Index =>
        {
            double Sum = 0;
            for (var Offset = Index * 640; Offset < Index * 640 + 640; Offset += 2)
            {
                var Sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(Pcm.AsSpan(Offset)) / 32768.0;
                Sum += Sample * Sample;
            }
            return Math.Sqrt(Sum / 320);
        }).ToArray();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Clip.Name, durationMs = Pcm.Length / 32.0,
            peakRms = Energy.Max(), trailing = new[] { .003, .005, .01, .015 }.Select(Threshold => new { Threshold, quietMs = Energy.Reverse().TakeWhile(Rms => Rms < Threshold).Count() * 20 }),
            preparations = new[] { 240, 400, 600, 800 }.Select(Window =>
            {
                int Voiced = 0, Quiet = 0; int? Started = null, Invalidated = null;
                for (var Index = 0; Index < Energy.Length; Index++)
                {
                    if (Energy[Index] >= .003) { Voiced += 20; Quiet = 0; if (Started is not null) Invalidated ??= Index * 20; }
                    else Quiet += 20;
                    if (Started is null && Voiced >= 100 && Quiet >= Window) Started = (Index + 1) * 20;
                }
                return new { Window, Started, Invalidated };
            }) }));
    }
    return;
}
if (args.FirstOrDefault() == "--stt")
{
    await SttBenchmark.RunAsync(args);
    return;
}
if (args.FirstOrDefault() == "--browser-harness")
{
    using var Listener = new System.Net.HttpListener();
    Listener.Prefixes.Add("http://localhost:8559/"); Listener.Start();
    Console.WriteLine("Browser audio harness: http://localhost:8559/ (synthetic audio only)");
    while (true)
    {
        var Request = await Listener.GetContextAsync();
        var Script = Request.Request.Url!.AbsolutePath == "/speech-player.js";
        var File = Script ? "src/Assister/wwwroot/speech-player.js" : "tests/browser/speech-playback.html";
        var Bytes = await System.IO.File.ReadAllBytesAsync(File);
        Request.Response.ContentType = Script ? "text/javascript" : "text/html";
        await Request.Response.OutputStream.WriteAsync(Bytes); Request.Response.Close();
    }
}
if (args.FirstOrDefault() == "--rich")
{
    await RichSpeechBenchmark.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 20);
    return;
}
if (args.FirstOrDefault() == "--live")
{
    await LiveBaseline.RunAsync(args);
    return;
}
var Samples = args.Length > 0 ? int.Parse(args[0]) : 20;
if (Samples is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(Samples));
var Rows = new List<Measurement>();
var Scenarios = new[]
{
    new Scenario("direct-control", ["The test light is on."], 15, 0, false),
    new Scenario("short-knowledge", ["The Moon orbits Earth.", "One orbit takes about a month."], 100, 0, false),
    new Scenario("tool-backed", ["The test temperature is 21 degrees.", "That is the latest reading."], 100, 120, true),
    new Scenario("long-reply", ["Here is the first useful sentence.", "Here is the second useful sentence.", "Here is the third useful sentence.", "Here is the last useful sentence."], 100, 0, false)
};
using var Deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
// Warm each path once; rotate mode order each measured repetition to limit order bias.
foreach (var Scenario in Scenarios)
{
    for (var Repetition = -1; Repetition < Samples; Repetition++)
    foreach (var Mode in (Repetition % 2 == 0 ? new[] { 2, 1, 0 } : new[] { 0, 1, 2 }))
    {
        var Clock = Stopwatch.StartNew();
        var Coordinator = new SimulatedCoordinator(Scenario);
        var Satellite = new SimulatedSatellite(Clock, Mode == 2);
        var Manager = new SatelliteManager();
        Manager.Register(Satellite);
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voice:StreamingEnabled"] = (Mode != 0).ToString()
        }).Build();
        using var Runs = new RunStore();
        var Result = await new VoicePipeline(new SimulatedStt(), new SimulatedTts(), Coordinator, Manager, Config, Runs)
            .RunAsync(Satellite, null, Deadline.Token);
        var Total = Clock.Elapsed.TotalMilliseconds;
        if (Result.Outcome != "succeeded" || Satellite.FirstUsefulAudio is null || Manager.ActiveSessionCount != 0)
            throw new InvalidOperationException("Benchmark pipeline failed.");
        var Run = Runs.Get(Runs.Snapshot().Single().RunId)!;
        var Metrics = Run.Steps.Single(Step => Step.Name == "Voice latency").Metadata!.Value.GetProperty("milestones");
        var CaptureEnd = Metrics.GetProperty("captureCompleted").GetDouble();
        if (Repetition >= 0) Rows.Add(new(Scenario.Name, new[] { "buffered", "overlap-buffered-transport", "overlap-streaming-transport" }[Mode],
            Repetition, Satellite.FirstUsefulAudio.Value - Satellite.CaptureCompleted, Total - Satellite.CaptureCompleted,
            Metrics.GetProperty("firstSynthesizedPcm").GetDouble() - CaptureEnd));
    }
}
var Summary = Rows.GroupBy(Row => new { Row.Scenario, Row.Mode }).Select(Group => new
{
    Group.Key.Scenario, Group.Key.Mode, count = Group.Count(),
    firstUsefulAudioMedianMs = Percentile(Group.Select(Row => Row.FirstUsefulAudioMs), .5),
    firstUsefulAudioP95Ms = Percentile(Group.Select(Row => Row.FirstUsefulAudioMs), .95),
    completionMedianMs = Percentile(Group.Select(Row => Row.CompletionMs), .5),
    completionP95Ms = Percentile(Group.Select(Row => Row.CompletionMs), .95),
    firstPcmMedianMs = Percentile(Group.Select(Row => Row.FirstPcmMs), .5)
}).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    measuredAt = DateTimeOffset.UtcNow, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, processorCount = Environment.ProcessorCount,
    scope = "Offline real pipeline; simulated providers/transport playback, warm process, tones disabled; no physical playback or inference speed claim.",
    configuration = new { samplesPerCell = Samples, warmupsPerCell = 1, sttFinalizationMs = 20, ttsSentenceMs = 35, pcmChunksPerSentence = 2,
        simulatedPlaybackTailMs = 10, scenarios = Scenarios, percentile = "nearest rank", origin = "server capture completion" },
    summary = Summary, measurements = Rows
}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

static double Percentile(IEnumerable<double> Values, double P)
{
    var Sorted = Values.Order().ToArray();
    return Math.Round(Sorted[Math.Max(0, (int)Math.Ceiling(P * Sorted.Length) - 1)], 2);
}
record Scenario(string Name, string[] Sentences, int GenerationPerSentenceMs, int ToolMs, bool HoldToolRound);
record Measurement(string Scenario, string Mode, int Repetition, double FirstUsefulAudioMs, double CompletionMs, double FirstPcmMs);
sealed class SimulatedStt : ISpeechToTextProvider
{
    public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
    {
        await foreach (var _ in Audio.WithCancellation(Token)) { }
        await Task.Delay(20, Token);
        return new("offline benchmark request", "en");
    }
}
sealed class SimulatedCoordinator(Scenario Scenario) : IStreamingRequestCoordinator
{
    public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken Token) => Generate(null, Token);
    public Task<RequestResult> ProcessStreamingAsync(UserRequest Request, Func<string, CancellationToken, Task> OnText, CancellationToken Token)
        => Generate(OnText, Token);
    private async Task<RequestResult> Generate(Func<string, CancellationToken, Task>? OnText, CancellationToken Token)
    {
        await Task.Delay(Scenario.ToolMs, Token);
        foreach (var Sentence in Scenario.Sentences)
        {
            await Task.Delay(Scenario.GenerationPerSentenceMs, Token);
            if (!Scenario.HoldToolRound && OnText is not null) await OnText(Sentence + " ", Token);
        }
        if (Scenario.HoldToolRound && OnText is not null) await OnText(string.Join(" ", Scenario.Sentences) + " ", Token);
        return new(string.Join(" ", Scenario.Sentences), Guid.NewGuid(), "language-model", Guid.NewGuid(), "succeeded", [], null, 0);
    }
}
sealed class SimulatedTts : ITextToSpeechProvider
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken Token)
    {
        // Same per-sentence work whether called with one sentence or an entire buffered reply.
        var Count = Math.Max(1, Text.Count(Character => Character == '.'));
        for (var Sentence = 0; Sentence < Count; Sentence++)
        {
            await Task.Delay(35, Token);
            yield return new(new byte[320], 16000, 2, 1);
            await Task.Yield();
            yield return new(new byte[320], 16000, 2, 1);
        }
    }
}
sealed class SimulatedSatellite(Stopwatch Clock, bool Streaming) : ISatelliteConnection, IStreamingAudioPlayback
{
    public string SatelliteId => "offline-benchmark";
    public string Name => "Offline benchmark";
    public string? Area => null;
    public bool SupportsStreamingPlayback => Streaming;
    public double? FirstUsefulAudio { get; private set; }
    public double CaptureCompleted { get; private set; }
    public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([EnumeratorCancellation] CancellationToken Token)
    {
        await Task.Yield();
        Token.ThrowIfCancellationRequested();
        yield return new(new byte[3200], 16000, 2, 1);
        CaptureCompleted = Clock.Elapsed.TotalMilliseconds;
    }
    public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token)
    {
        var HasAudio = false;
        await foreach (var Chunk in Audio.WithCancellation(Token))
        {
            HasAudio |= !Chunk.Pcm.IsEmpty;
            if (Streaming && HasAudio) FirstUsefulAudio ??= Clock.Elapsed.TotalMilliseconds;
        }
        if (HasAudio) FirstUsefulAudio ??= Clock.Elapsed.TotalMilliseconds;
        await Task.Delay(10, Token);
    }
    public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) => Task.CompletedTask;
}
