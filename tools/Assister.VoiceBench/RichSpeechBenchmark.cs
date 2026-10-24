using System.Diagnostics;
using System.Text.Json;
using Assister.Contracts;
using Assister.Interactions;
using Microsoft.Extensions.Configuration;

static class RichSpeechBenchmark
{
    public static async Task RunAsync(int Count)
    {
        if (Count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(Count));
        var Rows = new List<Row>();
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-rich-benchmark-" + Guid.NewGuid()) }).Build();
        using var Store = new InteractionStore(Configuration);
        using var Deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        foreach (var Scenario in new[]
        {
            new Scenario("direct-control", ["The test light is on."], 15, 0, false),
            new Scenario("short-knowledge", ["The Moon orbits Earth.", "One orbit takes about a month."], 100, 0, false),
            new Scenario("tool-backed", ["The test temperature is 21 degrees.", "That is the latest reading."], 100, 120, true),
            new Scenario("long-reply", ["Here is the first useful sentence.", "Here is the second useful sentence.", "Here is the third useful sentence.", "Here is the last useful sentence."], 100, 0, false)
        })
        for (var Sample = -1; Sample < Count; Sample++)
        foreach (var Streaming in Sample % 2 == 0 ? new[] { true, false } : new[] { false, true })
        {
            var Conversation = Store.CreateConversation("benchmark");
            var Item = Store.Submit(Conversation.Id, "benchmark", new("offline benchmark", Guid.NewGuid().ToString(), Speak: true, StreamSpeech: Streaming));
            var Clock = Stopwatch.StartNew();
            var Work = GenerateAsync();
            double? First = null;
            while (!Work.IsCompleted)
            {
                if (First is null && Store.Events(Item.Id, 0).Any(Event => Event.Type == (Streaming ? "tts.segment" : "tts.audio"))) First = Clock.Elapsed.TotalMilliseconds;
                await Task.Delay(1, Deadline.Token);
            }
            await Work;
            First ??= Clock.Elapsed.TotalMilliseconds;
            var Total = Clock.Elapsed.TotalMilliseconds;
            var Events = Store.Events(Item.Id, 0);
            if (Events.Any(Event => Event.Type == "tts.failed") || !Events.Any(Event => Event.Type == "tts.audio")) throw new InvalidOperationException("Synthesis failed.");
            Store.Append(Item.Id, "interaction.completed", new { }, "completed");
            if (Sample >= 0) Rows.Add(new(Scenario.Name, Streaming ? "streaming" : "buffered", Sample, First.Value, Total));

            async Task GenerateAsync()
            {
                await using var Output = Streaming ? new RichSpeechOutput(Item.Id, new SimulatedTts(), Store, Configuration, Deadline.Token) : null;
                await Task.Delay(Scenario.ToolMs, Deadline.Token);
                foreach (var Sentence in Scenario.Sentences)
                {
                    await Task.Delay(Scenario.GenerationPerSentenceMs, Deadline.Token);
                    if (!Scenario.HoldToolRound && Output is not null) await Output.AppendAsync(Sentence + " ");
                }
                if (Output is not null)
                {
                    if (Scenario.HoldToolRound) await Output.AppendAsync(string.Join(" ", Scenario.Sentences) + " ");
                    await Output.CompleteAsync();
                }
                else await new RichSpeech(new SimulatedStt(), new SimulatedTts(), Store, Configuration)
                    .SynthesizeAsync(Item.Id, string.Join(" ", Scenario.Sentences), Deadline.Token);
            }
        }
        double Percentile(IEnumerable<double> Values, double P)
        { var Sorted = Values.Order().ToArray(); return Math.Round(Sorted[(int)Math.Ceiling(Sorted.Length * P) - 1], 2); }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow,
            scope = "Actual RichSpeech versus RichSpeechOutput and SQLite segment/audio storage; simulated generation/TTS; no network, browser decoding or physical playback. Monotonic wall time from generation start, first audio availability observed by 1ms polling (scheduler granularity included).",
            configuration = new { samplesPerCell = Count, warmupsPerCell = 1, generationPerSentenceMs = 100, ttsPerSentenceMs = 35, toolMs = 120, streamingQueueCapacity = 8, maximumPcmBytes = 8388608, maximumSegments = 1024, modeOrder = "alternating" },
            summary = Rows.GroupBy(Row => new { Row.Scenario, Row.Mode }).Select(Group => new
            {
                Group.Key.Scenario, Group.Key.Mode, count = Group.Count(),
                firstAudioMedianMs = Percentile(Group.Select(Row => Row.FirstAudioMs), .5),
                firstAudioP95Ms = Percentile(Group.Select(Row => Row.FirstAudioMs), .95),
                completionMedianMs = Percentile(Group.Select(Row => Row.CompletionMs), .5),
                completionP95Ms = Percentile(Group.Select(Row => Row.CompletionMs), .95)
            }), measurements = Rows
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
    private sealed record Row(string Scenario, string Mode, int Sample, double FirstAudioMs, double CompletionMs);
}
