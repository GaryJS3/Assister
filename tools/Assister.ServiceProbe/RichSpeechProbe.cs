using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Assister.Contracts;

static class RichSpeechProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<int> RunAsync(string[] Args)
    {
        string? Argument(string Key) { var Index = Array.IndexOf(Args, Key); return Index >= 0 && Index + 1 < Args.Length ? Args[Index + 1] : null; }
        var Url = Argument("--url") ?? "http://127.0.0.1:8080/";
        var Samples = int.Parse(Argument("--samples") ?? "3");
        if (Samples is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(Samples));
        var Credential = Environment.GetEnvironmentVariable("ASSISTER_RICH_PROBE_TOKEN")
            ?? Environment.GetEnvironmentVariable("RichClients__Clients__web__Token");
        if (string.IsNullOrWhiteSpace(Credential)) throw new InvalidOperationException("A configured rich-client credential is required.");
        using var Http = new HttpClient { BaseAddress = new Uri(Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120) };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
        var Measurements = new List<Measurement>();
        var Modes = Args.Contains("--buffered-only") ? new[] { false } : new[] { false, true };
        foreach (var Scenario in new[]
        {
            (Name: "short-knowledge", Prompt: "What causes the Moon's phases? Answer in one short sentence."),
            (Name: "long-knowledge", Prompt: "What causes the Moon's phases? Explain in four short sentences.")
        })
        for (var Sample = 0; Sample < Samples; Sample++)
        foreach (var Streaming in Sample % 2 == 0 ? Modes : Modes.Reverse())
        {
            using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var Conversation = await Post<ClientConversation>("api/client/conversations", null, Deadline.Token);
            var Clock = Stopwatch.StartNew();
            var Item = await Post<InteractionSnapshot>($"api/client/conversations/{Conversation.Id}/interactions",
                new SubmitInteraction(Scenario.Prompt, Guid.NewGuid().ToString(), Speak: true, StreamSpeech: Streaming), Deadline.Token);
            var Timings = new Dictionary<string, double>();
            var Counts = new Dictionary<string, int>();
            var Segments = 0; long SegmentPcmBytes = 0; int? CompletePcmBytes = null;
            long? FirstSegmentSequence = null, ResponseSequence = null;
            Guid? RunId = null; string Outcome = "missing-terminal"; bool StreamCompleted = false;
            using var Socket = new ClientWebSocket();
            Socket.Options.SetRequestHeader("Authorization", "Bearer " + Credential);
            // Keep the server side open until this observer has drained audio downloads and the terminal event.
            var StreamUrl = new UriBuilder(new Uri(Http.BaseAddress, $"api/client/interactions/{Item.Id}/stream?followPlayback=true")) { Scheme = Http.BaseAddress.Scheme == "https" ? "wss" : "ws" };
            await Socket.ConnectAsync(StreamUrl.Uri, Deadline.Token);
            while (Socket.State == WebSocketState.Open)
            {
                var Event = await Receive(Socket, Deadline.Token);
                if (Event is null) break;
                if (!Event.Value.TryGetProperty("sequence", out _)) continue;
                var Type = Event.Value.GetProperty("type").GetString()!;
                var Data = Event.Value.GetProperty("data");
                Counts[Type] = Counts.GetValueOrDefault(Type) + 1;
                Timings.TryAdd(Type, Clock.Elapsed.TotalMilliseconds);
                if (Type == "interaction.started") RunId = Data.GetProperty("runId").GetGuid();
                if (Type == "response.completed") ResponseSequence = Event.Value.GetProperty("sequence").GetInt64();
                if (Type == "tts.segment")
                {
                    var Segment = Data.Deserialize<SpeechSegment>(Json)!;
                    if (Segment.Index != Segments++) throw new InvalidDataException("Out-of-order speech segment.");
                    FirstSegmentSequence ??= Event.Value.GetProperty("sequence").GetInt64();
                    var Bytes = await Http.GetByteArrayAsync(Segment.Url, Deadline.Token);
                    SegmentPcmBytes += ValidateWave(Bytes);
                    Timings.TryAdd("playableAudioDownloaded", Clock.Elapsed.TotalMilliseconds);
                }
                if (Type == "tts.audio")
                {
                    var Bytes = await Http.GetByteArrayAsync(Data.GetProperty("url").GetString()!, Deadline.Token);
                    CompletePcmBytes = ValidateWave(Bytes);
                    Timings.TryAdd("playableAudioDownloaded", Clock.Elapsed.TotalMilliseconds);
                }
                if (Type == "tts.completed" && Data.TryGetProperty("streaming", out var Stream) && Stream.GetBoolean())
                    StreamCompleted = Data.GetProperty("segmentCount").GetInt32() == Segments;
                if (Type is "interaction.completed" or "interaction.failed" or "interaction.cancelled") { Outcome = Type; break; }
            }
            var Valid = Outcome == "interaction.completed" && CompletePcmBytes is > 0 && !Counts.ContainsKey("tts.failed")
                && (!Streaming || (Segments > 0 && StreamCompleted && SegmentPcmBytes == CompletePcmBytes));
            var Row = new Measurement(Scenario.Name, Streaming ? "streaming" : "buffered", Sample, Item.Id, RunId, Outcome, Valid,
                Timings, Segments, SegmentPcmBytes, CompletePcmBytes,
                FirstSegmentSequence is { } First && ResponseSequence is { } Last && First < Last);
            Measurements.Add(Row);
            Console.Error.WriteLine($"{Row.Scenario}/{Row.Mode} #{Sample + 1}: {Outcome}, valid={Valid}, first playable={Timings.GetValueOrDefault("playableAudioDownloaded"):F0}ms, segments={Segments}");
        }
        double? Percentile(IEnumerable<Measurement> Rows, double P)
        {
            var Values = Rows.Where(Row => Row.Valid).Select(Row => Row.TimingsMs["playableAudioDownloaded"]).Order().ToArray();
            return Values.Length > 0 ? Math.Round(Values[(int)Math.Ceiling(P * Values.Length) - 1], 2) : null;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow, endpoint = Http.BaseAddress,
            scope = "Live read-only rich-client text+TTS, actual configured model/provider and authenticated WAV downloads. No audio played, device commands, or fabricated playback acknowledgements. Client monotonic elapsed time from submission; network/download included, capture/STT/tones/browser/hardware excluded. Fresh conversations; inference/cache warmth uncontrolled; nearest-rank percentiles.",
            samplesPerCell = Samples,
            summary = Measurements.GroupBy(Row => new { Row.Scenario, Row.Mode }).Select(Group => new
            {
                Group.Key.Scenario, Group.Key.Mode, count = Group.Count(), passed = Group.Count(Row => Row.Valid),
                firstPlayableMedianMs = Percentile(Group, .5), firstPlayableP95Ms = Percentile(Group, .95),
                beforeResponseCompletion = Group.Count(Row => Row.SegmentBeforeResponseCompletion)
            }), measurements = Measurements
        }, Json));
        return Measurements.All(Row => Row.Valid) ? 0 : 1;

        async Task<T> Post<T>(string Path, object? Body, CancellationToken Token)
        { using var Response = await Http.PostAsJsonAsync(Path, Body, Token); Response.EnsureSuccessStatusCode(); return (await Response.Content.ReadFromJsonAsync<T>(Token))!; }
    }
    private static int ValidateWave(byte[] Bytes)
    {
        if (Bytes.Length < 44 || !Bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !Bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            || !Bytes.AsSpan(36, 4).SequenceEqual("data"u8)) throw new InvalidDataException("Invalid WAV response.");
        var Size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(40, 4));
        if (Size <= 0 || Bytes.Length != 44L + Size + (Size & 1)) throw new InvalidDataException("Incomplete WAV response.");
        return Size;
    }
    private static async Task<JsonElement?> Receive(ClientWebSocket Socket, CancellationToken Token)
    {
        using var Output = new MemoryStream(); var Buffer = new byte[8192];
        while (true)
        {
            var Result = await Socket.ReceiveAsync(Buffer, Token);
            if (Result.MessageType == WebSocketMessageType.Close) return null;
            if (Output.Length + Result.Count > 1024 * 1024) throw new InvalidDataException("Oversized event.");
            Output.Write(Buffer, 0, Result.Count);
            if (Result.EndOfMessage) return JsonSerializer.Deserialize<JsonElement>(Output.ToArray());
        }
    }
    private sealed record Measurement(string Scenario, string Mode, int Sample, Guid InteractionId, Guid? RunId,
        string Outcome, bool Valid, Dictionary<string, double> TimingsMs, int Segments, long SegmentPcmBytes,
        int? CompletePcmBytes, bool SegmentBeforeResponseCompletion);
}
