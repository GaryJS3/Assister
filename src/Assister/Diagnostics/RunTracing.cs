using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Assister.Contracts;

namespace Assister.Diagnostics;

public sealed record DiagnosticStep(Guid Id, Guid? ParentId, int Sequence, string Kind, string Name,
    string Status, DateTimeOffset StartedAt, double DurationMilliseconds, string Summary,
    JsonElement? Input, JsonElement? Output, JsonElement? Metadata, bool InputTruncated, bool OutputTruncated);
public sealed record DiagnosticRun(Guid RunId, string Source, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    double DurationMilliseconds, string Outcome, string? SatelliteId, string? Area, Guid? VoiceSessionId,
    Guid? ConversationId, string? ActivityTraceId, string? UserText, string? RawResponse, string? SpokenResponse,
    string? HandledBy, IReadOnlyList<DiagnosticStep> Steps)
{
    public bool HasFailures { get; init; }
}

// Every diagnostic write passes through this sanitizer, including summary fields and persisted data.
public sealed class DiagnosticSanitizer(IConfiguration? Configuration = null)
{
    public bool CapturePayloads { get; } = Configuration?.GetValue("Diagnostics:CapturePayloads", true) ?? true;
    private readonly string[] Secrets = Configuration?.AsEnumerable().Where(Item => Sensitive(Item.Key) && !string.IsNullOrEmpty(Item.Value))
        .Select(Item => Item.Value!).Distinct().OrderByDescending(Value => Value.Length).ToArray() ?? [];
    private static bool Sensitive(string Key) => Regex.IsMatch(Key, "authorization|api.?key|password|secret|token(?!s)|connection.?string|encryption.?key", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public string Text(string? Value, int Limit = 4096)
    {
        var Text = Value ?? "";
        foreach (var Secret in Secrets) { Text = Text.Replace(Secret, "[redacted]", StringComparison.Ordinal); }
        Text = Regex.Replace(Text, @"(?i)\bBearer\s+[^\s""<>]+", "Bearer [redacted]");
        Text = Regex.Replace(Text, @"(?i)\b(?:api[_-]?key|password|access[_-]?token|authorization)\s*[:=]\s*[^\s,;]+", "[redacted credential]");
        return Text.Length > Limit ? Text[..Limit] + " [truncated]" : Text;
    }
    // Keep credential context and possible configured-secret suffixes until a later fragment confirms the boundary.
    public int StreamPrefixLength(string Value)
    {
        var Hold = Math.Max(32, Secrets.Select(Secret => Secret.Length).DefaultIfEmpty(0).Max());
        var Cut = Math.Min(4096, Value.Length - Hold);
        if (Cut <= 0)
            return 0;
        Cut = Value.LastIndexOfAny([' ', '\t', '\r', '\n'], Cut - 1) + 1;
        foreach (Match Match in Regex.Matches(Value, @"(?i)\bBearer\s+[^\s""<>]+|\b(?:api[_-]?key|password|access[_-]?token|authorization)\s*[:=]\s*[^\s,;]+"))
            if (Match.Index < Cut && Match.Index + Match.Length >= Cut)
                Cut = Match.Index;
        foreach (var Secret in Secrets)
        {
            var Start = Value.IndexOf(Secret, StringComparison.Ordinal);
            while (Start >= 0 && Start < Cut)
            {
                if (Start + Secret.Length > Cut)
                {
                    Cut = Start;
                    break;
                }
                Start = Value.IndexOf(Secret, Start + Secret.Length, StringComparison.Ordinal);
            }
        }
        return Cut;
    }
    public (JsonElement? Value, bool Truncated) Payload(object? Value, bool Detailed = true, int Budget = 32768)
    {
        if (Value is null) { return (null, false); }
        if (Detailed && !CapturePayloads) { return (JsonSerializer.SerializeToElement(new { omitted = "Payload capture disabled" }), false); }
        var Truncated = false;
        JsonNode? Clean(JsonElement Item, int Depth)
        {
            if (Depth > 10) { Truncated = true; return JsonValue.Create("[depth limit]"); }
            switch (Item.ValueKind)
            {
                case JsonValueKind.Object:
                    var Object = new JsonObject();
                    foreach (var Property in Item.EnumerateObject().Take(64))
                    {
                        Object[Text(Property.Name, 128)] = Sensitive(Property.Name) ? JsonValue.Create("[redacted]") : Clean(Property.Value, Depth + 1);
                    }
                    if (Item.EnumerateObject().Count() > 64) { Truncated = true; }
                    return Object;
                case JsonValueKind.Array:
                    var Array = new JsonArray();
                    foreach (var Element in Item.EnumerateArray().Take(64)) { Array.Add(Clean(Element, Depth + 1)); }
                    if (Item.GetArrayLength() > 64) { Truncated = true; }
                    return Array;
                case JsonValueKind.String:
                    var Original = Item.GetString();
                    if (Original?.Length > 4096) { Truncated = true; }
                    return JsonValue.Create(Text(Original));
                default: return JsonNode.Parse(Item.GetRawText());
            }
        }
        try
        {
            var Element = JsonSerializer.SerializeToElement(Value, RunStore.Json);
            var Cleaned = Clean(Element, 0);
            var Bytes = JsonSerializer.SerializeToUtf8Bytes(Cleaned);
            // Preserve structured payloads and message order while shortening large content fields.
            // Keep the first and last entries when an array must be shortened (system/current user messages).
            for (var Pass = 0; Bytes.Length > Budget && Pass < 256; Pass++)
            {
                var Strings = new List<JsonValue>();
                var Arrays = new List<JsonArray>();
                void Visit(JsonNode? Node)
                {
                    if (Node is JsonObject Object) { foreach (var Pair in Object) { Visit(Pair.Value); } }
                    else if (Node is JsonArray Array) { Arrays.Add(Array); foreach (var Item in Array) { Visit(Item); } }
                    else if (Node is JsonValue String && String.TryGetValue<string>(out var Text) && Text.Length > 128) { Strings.Add(String); }
                }
                Visit(Cleaned);
                var Largest = Strings.OrderByDescending(Node => Node.GetValue<string>().Length).FirstOrDefault();
                if (Largest is not null)
                {
                    var Text = Largest.GetValue<string>();
                    Largest.ReplaceWith(JsonValue.Create(Text[..Math.Max(64, Text.Length / 2)] + " [truncated]"));
                }
                else if (Arrays.OrderByDescending(Array => Array.Count).FirstOrDefault(Array => Array.Count > 2) is { } Array)
                { Array.RemoveAt(Array.Count / 2); }
                else { break; }
                Truncated = true;
                Bytes = JsonSerializer.SerializeToUtf8Bytes(Cleaned);
            }
            if (Bytes.Length > Budget)
                return (JsonSerializer.SerializeToElement(new { truncated = true, reason = "Diagnostics payload budget exceeded" }), true);
            return (JsonSerializer.SerializeToElement(Cleaned), Truncated);
        }
        catch (Exception Error) when (Error is JsonException or NotSupportedException or ArgumentException)
        { return (JsonSerializer.SerializeToElement(new { omitted = "Unsupported diagnostic payload" }), true); }
    }
    public static object ParseJson(string Value)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(Value, new JsonSerializerOptions { MaxDepth = 16 }); }
        catch (JsonException) { return new { invalidJson = true }; }
    }
}

public static class RunTracing
{
    public static readonly ActivitySource Source = new("Assister.Runs");
    private static readonly AsyncLocal<RunScope?> Ambient = new();
    private static readonly AsyncLocal<TraceStep?> Parent = new();
    public static Guid RunId => Ambient.Value?.Run.RunId ?? Guid.Empty;
    public static string? CurrentKind => Parent.Value?.Kind;
    public static RunScope BeginRun(RunStore Store, string Source, string? Satellite = null, string? Area = null,
        Guid? VoiceSession = null, Guid? Conversation = null, string? Text = null)
    {
        var Scope = new RunScope(Store, Source, Satellite, Area, VoiceSession, Conversation, Text, Ambient.Value, Parent.Value);
        Ambient.Value = Scope;
        Parent.Value = null;
        return Scope;
    }
    public static RunScope? EnsureRun(RunStore? Store, string Source, string? Satellite, string? Area, Guid? Conversation, string? Text)
        => Ambient.Value is null ? BeginRun(Store ?? new RunStore(), Source, Satellite, Area, null, Conversation, Text) : null;
    public static TraceStep Start(string Kind, string Name, string Summary)
    {
        var Step = new TraceStep(Ambient.Value, Parent.Value, Kind, Name, Summary);
        Parent.Value = Step;
        return Step;
    }
    public static TraceStep Start(string Name, string Summary) => Start(Name switch
    {
        "LLM" => "LanguageModel", "STT" => "SpeechToText", "TTS" => "TextToSpeech",
        "Tool broker" or "Tool · Home Assistant" => "ToolCall", "Intent execution" => "IntentExecution",
        "Intent classification" => "IntentClassification", "Entity resolution" => "EntityResolution", _ => "Input"
    }, Name, Summary);
    public static void Response(string Raw, string Spoken, string Outcome, string HandledBy, Guid? Conversation)
    {
        var Scope = Ambient.Value;
        if (Scope is null) { return; }
        lock (Scope.Store.Gate)
        {
            var Clean = Scope.Store.Sanitizer;
            Scope.Run = Scope.Run with { RawResponse = Clean.Text(Raw), SpokenResponse = Clean.Text(Spoken),
                Outcome = Clean.Text(Outcome, 128), HandledBy = Clean.Text(HandledBy, 128), ConversationId = Conversation };
        }
    }
    public static void Transcript(string Text)
    {
        if (Ambient.Value is { } Scope) { lock (Scope.Store.Gate) { Scope.Run = Scope.Run with { UserText = Scope.Store.Sanitizer.Text(Text) }; } }
    }
    public sealed class RunScope : IDisposable
    {
        internal readonly RunStore Store;
        internal DiagnosticRun Run;
        internal readonly List<TraceStep> Steps = [];
        internal int PayloadBytes;
        internal int SemanticBytes;
        private readonly RunScope? Previous;
        private readonly TraceStep? PreviousParent;
        private bool Disposed;
        internal RunScope(RunStore Store, string Source, string? Satellite, string? Area, Guid? VoiceSession,
            Guid? Conversation, string? Text, RunScope? Previous, TraceStep? PreviousParent)
        {
            this.Store = Store; this.Previous = Previous; this.PreviousParent = PreviousParent;
            var Clean = Store.Sanitizer;
            Run = new(Guid.NewGuid(), Clean.Text(Source, 32), DateTimeOffset.UtcNow, null, 0, "running", Clean.Text(Satellite, 128),
                Clean.Text(Area, 128), VoiceSession, Conversation, Activity.Current?.TraceId.ToString(), Clean.Text(Text), null, null, null, []);
            Store.Register(this);
        }
        public void Complete(string Outcome) { lock (Store.Gate) { Run = Run with { Outcome = Store.Sanitizer.Text(Outcome, 128) }; } }
        internal DiagnosticRun Snapshot(bool IncludeSteps) => Run with
        {
            DurationMilliseconds = ((Run.FinishedAt ?? DateTimeOffset.UtcNow) - Run.StartedAt).TotalMilliseconds,
            HasFailures = Steps.Any(Step => Step.Snapshot().Status is "failed" or "rejected" or "unavailable" or "interrupted-or-failed"),
            Steps = IncludeSteps ? Steps.Select(Step => Step.Snapshot()).ToArray() : []
        };
        public void Dispose()
        {
            if (Disposed) { return; }
            Disposed = true;
            lock (Store.Gate)
            {
                Run = Run with { FinishedAt = DateTimeOffset.UtcNow, Outcome = Run.Outcome == "running" ? "interrupted-or-failed" : Run.Outcome };
                Store.Finish(this);
            }
            Ambient.Value = Previous; Parent.Value = PreviousParent;
        }
    }
    public sealed class TraceStep : IDisposable
    {
        private readonly RunScope? Scope;
        private readonly TraceStep? Previous;
        private readonly Activity? Activity;
        private DiagnosticStep Step;
        private DateTimeOffset? Finished;
        private bool FeedbackFinished;
        public string Kind => Step.Kind;
        public Guid Id => Step.Id;
        internal TraceStep(RunScope? Scope, TraceStep? Previous, string Kind, string Name, string Summary)
        {
            this.Scope = Scope; this.Previous = Previous;
            var Clean = Scope?.Store.Sanitizer ?? new DiagnosticSanitizer();
            Activity = Source.StartActivity(Name);
            Activity?.SetTag("assister.run_id", RunId.ToString());
            Step = new(Guid.NewGuid(), Previous?.Id, 0, Clean.Text(Kind, 64), Clean.Text(Name, 128), "running", DateTimeOffset.UtcNow, 0,
                Clean.Text(Summary, 1024), null, null, null, false, false);
            if (Scope is not null) { lock (Scope.Store.Gate) { Step = Step with { Sequence = Scope.Steps.Count + 1 }; if (Scope.Steps.Count < 128) { Scope.Steps.Add(this); } } }
            InteractionFeedback.Emit("step.started", new { stepId = Step.Id, parentStepId = Step.ParentId, kind = Step.Kind, label = Step.Name, summary = Step.Summary });
        }
        private void Change(Action Action) { if (Scope is not null) { lock (Scope.Store.Gate) { Action(); } } }
        private (JsonElement? Value, bool Truncated) Bound(object? Value, bool Detailed)
        {
            if (Scope is null) { return (null, false); }
            var Budget = Detailed ? Math.Max(0, Math.Min(32768, 131072 - Scope.PayloadBytes))
                : Math.Max(0, Math.Min(4096, 32768 - Scope.SemanticBytes));
            var Result = Scope.Store.Sanitizer.Payload(Value, Detailed, Budget);
            var Bytes = Result.Value is { } Element ? System.Text.Encoding.UTF8.GetByteCount(Element.GetRawText()) : 0;
            if (Detailed) { Scope.PayloadBytes += Bytes; } else { Scope.SemanticBytes += Bytes; }
            return Result;
        }
        public void Input(object? Value, bool Detailed = true) => Change(() => { var Data = Bound(Value, Detailed); Step = Step with { Input = Data.Value, InputTruncated = Data.Truncated }; PublishUpdated(); });
        public void Output(object? Value, bool Detailed = true) => Change(() => { var Data = Bound(Value, Detailed); Step = Step with { Output = Data.Value, OutputTruncated = Data.Truncated }; PublishUpdated(); });
        private void PublishUpdated() => InteractionFeedback.Emit("step.updated", new
        {
            stepId = Step.Id, parentStepId = Step.ParentId, kind = Step.Kind,
            input = Step.Input, output = Step.Output, inputTruncated = Step.InputTruncated, outputTruncated = Step.OutputTruncated
        });
        internal (JsonElement? Value, bool Truncated) ModelTextPayload(string Text)
        {
            if (Scope is null) return new DiagnosticSanitizer().Payload(Text);
            lock (Scope.Store.Gate) return Bound(Text, true);
        }
        public ModelTextScope Thinking(int ModelRound, CancellationToken Token = default)
            => new(this, Scope?.Store.Sanitizer ?? new DiagnosticSanitizer(), ModelRound, Token);
        public ModelTextScope ModelOutput(int ModelRound, CancellationToken Token = default)
            => new(this, Scope?.Store.Sanitizer ?? new DiagnosticSanitizer(), ModelRound, Token, "model.output");
        public void Metadata(object? Value) => Change(() => { Step = Step with { Metadata = Bound(Value, false).Value }; });
        public void Detail(string Key, object? Value) => Change(() =>
        {
            var Data = Step.Metadata is { } Element ? JsonNode.Parse(Element.GetRawText())!.AsObject() : new JsonObject();
            var Safe = Bound(new Dictionary<string, object?> { [Key] = Value }, false);
            if (Safe.Value is { } SafeElement && SafeElement.TryGetProperty(Key, out var Item)) { Data[Key] = JsonNode.Parse(Item.GetRawText()); }
            Step = Step with { Metadata = JsonSerializer.SerializeToElement(Data) };
        });
        public void Complete(string Status = "succeeded") => Change(() => Step = Step with { Status = Scope!.Store.Sanitizer.Text(Status, 128) });
        // Finish model timing before child tools while keeping this round as their semantic parent.
        public void Finish(string Status = "succeeded") { Complete(Status); Change(() => Finished ??= DateTimeOffset.UtcNow); PublishFinished(); }
        private void PublishFinished()
        {
            if (FeedbackFinished) return;
            FeedbackFinished = true;
            InteractionFeedback.Emit(Step.Status is "failed" or "rejected" or "unavailable" or "interrupted-or-failed" ? "step.failed" : "step.completed",
                new { stepId = Step.Id, parentStepId = Step.ParentId, kind = Step.Kind, label = Step.Name, status = Step.Status, durationMs = Snapshot().DurationMilliseconds });
        }
        internal DiagnosticStep Snapshot() => Step with { DurationMilliseconds = ((Finished ?? DateTimeOffset.UtcNow) - Step.StartedAt).TotalMilliseconds };
        public void Dispose()
        {
            Change(() => { Finished ??= DateTimeOffset.UtcNow; if (Step.Status == "running") { Step = Step with { Status = "interrupted-or-failed" }; } });
            PublishFinished();
            Activity?.Dispose(); Parent.Value = Previous;
        }
    }
}

public sealed class RunStore : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal readonly object Gate = new();
    internal readonly DiagnosticSanitizer Sanitizer;
    private readonly Dictionary<Guid, RunTracing.RunScope> Active = [];
    private readonly Dictionary<Guid, DiagnosticRun> Completed = [];
    private readonly SqliteConnection? Database;
    private readonly int MaximumRuns;
    private readonly int RetentionDays;
    private readonly ILogger<RunStore>? Logger;
    public RunStore(IConfiguration? Configuration = null, ILogger<RunStore>? Logger = null)
    {
        this.Logger = Logger;
        Sanitizer = new(Configuration);
        MaximumRuns = Math.Clamp(Configuration?.GetValue("Diagnostics:MaxRuns", 1000) ?? 1000, 1, 1000);
        RetentionDays = Math.Clamp(Configuration?.GetValue("Diagnostics:RetentionDays", 7) ?? 7, 1, 30);
        if (Configuration is null || !Configuration.GetValue("Diagnostics:PersistHistory", true)) { return; }
        var Path = System.IO.Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(Path);
        Database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(Path, "diagnostics.db") }.ToString());
        Database.Open();
        using var Command = Database.CreateCommand();
        Command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS DiagnosticRuns (RunId TEXT PRIMARY KEY, StartedAt INTEGER NOT NULL, Summary TEXT NOT NULL, Payload TEXT NOT NULL);";
        Command.ExecuteNonQuery();
        Prune();
    }
    internal void Register(RunTracing.RunScope Scope) { lock (Gate) { if (Active.Count < 64) { Active[Scope.Run.RunId] = Scope; } } }
    internal void Finish(RunTracing.RunScope Scope)
    {
        if (!Active.Remove(Scope.Run.RunId)) { return; }
        var Run = Scope.Snapshot(true);
        Completed[Run.RunId] = Run;
        while (Completed.Count > Math.Min(MaximumRuns, 100)) { Completed.Remove(Completed.Keys.First()); }
        if (Database is null) { return; }
        try
        {
            using var Command = Database.CreateCommand();
            Command.CommandText = "INSERT OR REPLACE INTO DiagnosticRuns VALUES($id,$time,$summary,$payload)";
            Command.Parameters.AddWithValue("$id", Run.RunId.ToString());
            Command.Parameters.AddWithValue("$time", Run.StartedAt.ToUnixTimeSeconds());
            Command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(Run with { Steps = [] }, Json));
            Command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(Run, Json));
            Command.ExecuteNonQuery(); Prune();
        }
        catch (SqliteException) { Logger?.LogWarning("Diagnostic history could not be persisted."); }
    }
    private void Prune()
    {
        if (Database is null) { return; }
        using var Command = Database.CreateCommand();
        Command.CommandText = "DELETE FROM DiagnosticRuns WHERE StartedAt < $cutoff OR RunId NOT IN (SELECT RunId FROM DiagnosticRuns ORDER BY StartedAt DESC, rowid DESC LIMIT $max)";
        Command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeSeconds());
        Command.Parameters.AddWithValue("$max", MaximumRuns); Command.ExecuteNonQuery();
    }
    public IReadOnlyList<DiagnosticRun> Snapshot()
    {
        lock (Gate)
        {
            var Runs = Completed.Values.Where(Run => Run.StartedAt >= DateTimeOffset.UtcNow.AddDays(-RetentionDays)).Select(Run => Run with { Steps = Array.Empty<DiagnosticStep>() }).ToDictionary(Run => Run.RunId);
            if (Database is not null)
            {
                using var Command = Database.CreateCommand();
                Command.CommandText = "SELECT Summary FROM DiagnosticRuns WHERE StartedAt >= $cutoff ORDER BY StartedAt DESC LIMIT $max";
                Command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeSeconds());
                Command.Parameters.AddWithValue("$max", MaximumRuns);
                using var Reader = Command.ExecuteReader();
                while (Reader.Read()) { var Run = JsonSerializer.Deserialize<DiagnosticRun>(Reader.GetString(0), Json)!; Runs[Run.RunId] = Run; }
            }
            foreach (var Scope in Active.Values) { Runs[Scope.Run.RunId] = Scope.Snapshot(false); }
            return Runs.Values.OrderByDescending(Run => Run.StartedAt).Take(MaximumRuns).ToArray();
        }
    }
    public DiagnosticRun? Get(Guid Id)
    {
        lock (Gate)
        {
            if (Active.TryGetValue(Id, out var Scope)) { return Scope.Snapshot(true); }
            if (Completed.TryGetValue(Id, out var Run) && Run.StartedAt >= DateTimeOffset.UtcNow.AddDays(-RetentionDays)) { return Run; }
            if (Database is null) { return null; }
            using var Command = Database.CreateCommand();
            Command.CommandText = "SELECT Payload FROM DiagnosticRuns WHERE RunId=$id AND StartedAt >= $cutoff";
            Command.Parameters.AddWithValue("$id", Id.ToString());
            Command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeSeconds());
            return Command.ExecuteScalar() is string Payload ? JsonSerializer.Deserialize<DiagnosticRun>(Payload, Json) : null;
        }
    }
    public void Dispose() => Database?.Dispose();
}
