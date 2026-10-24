using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

static class LiveBaseline
{
    public static async Task RunAsync(string[] Args)
    {
        if (Args.Length < 2) throw new ArgumentException("Usage: --live http://server:port [samples=10]");
        var Count = Args.Length > 2 ? int.Parse(Args[2]) : 10;
        if (Count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Count));
        using var Client = new HttpClient { BaseAddress = new Uri(Args[1]), Timeout = TimeSpan.FromSeconds(120) };
        var Rows = new List<LiveMeasurement>();
        var Settings = await Client.GetFromJsonAsync<JsonElement>("/api/settings/ai-server");
        var Device = "latency-benchmark-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var Scenario in new[]
        {
            (Name: "short-knowledge", Text: "What causes the Moon's phases? Answer in one short sentence."),
            (Name: "long-reply", Text: "Explain how rain forms in five short sentences."),
            (Name: "tool-readonly", Text: "What is the current temperature in the office? Only read the temperature; do not change any devices.")
        })
        {
            var Durations = new List<double>();
            for (var Index = 0; Index < Count; Index++)
            {
                var Clock = Stopwatch.StartNew();
                using var Response = await Client.PostAsJsonAsync("/api/test/message", new
                {
                    message = Scenario.Text, satelliteId = Device, newConversation = true
                });
                Response.EnsureSuccessStatusCode();
                var Result = await Response.Content.ReadFromJsonAsync<JsonElement>();
                var Elapsed = Clock.Elapsed.TotalMilliseconds;
                var Outcome = Result.GetProperty("outcome").GetString();
                if (Outcome == "succeeded") Durations.Add(Elapsed);
                Rows.Add(new(Scenario.Name, Index, Outcome, Elapsed, Result.GetProperty("handledBy").GetString(),
                    Result.TryGetProperty("runId", out var Run) ? Run.GetString() : null,
                    Result.TryGetProperty("durationMilliseconds", out var Duration) ? Duration.GetDouble() : null));
            }
            var Sorted = Durations.Order().ToArray();
            Console.Error.WriteLine($"{Scenario.Name}: {Sorted.Length}/{Count} succeeded; median {(Sorted.Length == 0 ? double.NaN : Sorted[(Sorted.Length - 1) / 2]):F1} ms");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow, endpoint = Client.BaseAddress!.GetLeftPart(UriPartial.Authority),
            scope = "Live text routing baseline; new conversation each sample; no STT, TTS or physical playback. Read-only questions only. Failures excluded from latency summaries.",
            configuration = new { samplesPerScenario = Count, conversation = "fresh", processWarmth = "uncontrolled",
                model = Settings.GetProperty("model").GetString(), maxTokens = Settings.GetProperty("maxTokens").GetInt32(),
                temperature = Settings.GetProperty("temperature").GetDouble(), enableThinking = Settings.GetProperty("enableThinking") },
            summary = Rows.GroupBy(Row => Row.Scenario).Select(Group =>
            {
                var Sorted = Group.Where(Row => Row.Outcome == "succeeded").Select(Row => Row.ElapsedMs).Order().ToArray();
                return new { scenario = Group.Key, count = Group.Count(), succeeded = Sorted.Length,
                    medianMs = Sorted.Length > 0 ? (double?)Math.Round(Sorted[(Sorted.Length - 1) / 2], 2) : null,
                    p95Ms = Sorted.Length > 0 ? (double?)Math.Round(Sorted[(int)Math.Ceiling(.95 * Sorted.Length) - 1], 2) : null };
            }),
            measurements = Rows
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
    private sealed record LiveMeasurement(string Scenario, int Sample, string? Outcome, double ElapsedMs, string? HandledBy, string? RunId, double? ServerDurationMs);
}
