using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Assister.Contracts;
using Assister.Conversations;
using Assister.Diagnostics;
using Assister.Intents;
using Assister.Llm;
using Assister.Modules.HomeAssistant;
using Assister.Persistence;
using Assister.Satellites;
using Assister.Speech.Wyoming;
using Assister.Tools;
using Assister.Voice;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Runs updated modules against real dependencies, without deploying the application or taking voice ownership.
internal static class LiveModuleProbe
{
    public static async Task<int> RunAsync(bool StreamingOnly = false)
    {
        var Config = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Diagnostics:PersistHistory"] = "false" }).Build();
        var Cache = new HomeAssistantStateCache();
        using var Home = new HomeAssistantClient(Config, Cache, () => new HomeAssistantConnection(), NullLogger<HomeAssistantClient>.Instance);
        using var Lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var Token = Lifetime.Token;
        var Failures = 0;
        var DirectoryName = Path.Combine(Path.GetTempPath(), "assister-module-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryName);
        try
        {
            await Home.StartAsync(Token);
            using (var Ready = CancellationTokenSource.CreateLinkedTokenSource(Token))
            {
                Ready.CancelAfter(TimeSpan.FromSeconds(30));
                while (Cache.Snapshot().IsStale) { await Task.Delay(100, Ready.Token); }
            }
            await using var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(DirectoryName, "probe.db")).Options);
            await Database.Database.MigrateAsync(Token);
            var Store = new LocalStore(Database);
            using var Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var Runs = new RunStore(Config);
            var Registry = new ToolRegistry(new[] { "ha_search", "ha_get_state", "ha_get_history" }
                .Select(Name => (IAssisterTool)new HomeAssistantTool(Name, Cache, new NoControls(), Http, Config)).Append(new WeatherTool(Cache, Http, Config)));
            var Intents = new IntentStore(Database, IntentActionRegistry.Default);
            await Intents.InitializeAsync(Token);
            var Coordinator = new ConversationCoordinator(Database,
                new RequestCoordinator(new IntentEngine(Intents, new(), IntentActionRegistry.Default), new HomeAssistantEntityResolver(), new(new NoControls()), Cache,
                    NullLogger<RequestCoordinator>.Instance, new(new OpenAiCompatibleLanguageModel(Http, Config), Registry, new(Registry, Store), Config), Diagnostics: Runs), new(), Runs);
            var Satellite = "module-probe-" + Guid.NewGuid().ToString("N");
            Guid? Conversation = null;
            foreach (var Message in StreamingOnly ? Array.Empty<string>() : new[] { "what is the temperature in the office", "Has the office been warmer than the living room this afternoon?", "What is the weather this weekend?", "What about Sunday night?" })
            {
                using var Run = RunTracing.BeginRun(Runs, "probe", Satellite, Conversation: Conversation, Text: Message);
                var Result = await Coordinator.ProcessAsync(new(Message, Satellite, ConversationId: Conversation), Token);
                Run.Complete(Result.Outcome);
                var RequiredTool = Message.StartsWith("Has the office", StringComparison.Ordinal) ? "ha_get_history"
                    : Message.Contains("weather", StringComparison.OrdinalIgnoreCase) || Message.Contains("Sunday", StringComparison.Ordinal) ? "weather_forecast" : null;
                var Evidence = RequiredTool is null || Runs.Get(Result.RunId)!.Steps.Any(Step => Step.Kind == "ToolCall" && Step.Name == RequiredTool
                    && Step.Status == "succeeded" && Step.Output is { } Output
                    && (Output.ValueKind == JsonValueKind.Array && Output.GetArrayLength() >= 2 && Output.EnumerateArray().All(Series => Series.GetProperty("numeric_samples").GetInt32() > 0)
                        || Output.ValueKind == JsonValueKind.Object && Output.TryGetProperty("forecast", out var Forecast) && Forecast.GetArrayLength() > 0));
                var Accepted = Result.Outcome == "succeeded" && Evidence && Result.ConversationId is not null
                    && (Conversation is null || Result.ConversationId == Conversation);
                if (!Accepted) { Failures++; }
                Conversation = Result.ConversationId;
                Console.WriteLine(JsonSerializer.Serialize(new { check = Message, accepted = Accepted, requiredTool = RequiredTool, toolEvidence = Evidence,
                    Result.Outcome, Result.DurationMilliseconds, Result.RunId, response = Result.Response[..Math.Min(500, Result.Response.Length)] }));
                if (!Accepted)
                    Console.WriteLine(JsonSerializer.Serialize(new { check = "Tool diagnostics", steps = Runs.Get(Result.RunId)!.Steps.Where(Step => Step.Kind == "ToolCall")
                        .Select(Step => new { Step.Name, Step.Status, arguments = Step.Input,
                            failureCategory = Step.Metadata is { } Metadata && Metadata.TryGetProperty("failureCategory", out var Category) ? Category.GetString() : null,
                            error = Step.Output is { ValueKind: JsonValueKind.Object } Output && Output.TryGetProperty("error", out var Error) ? Error.GetString() : null }) }));
            }
            try
            {
                var Endpoint = new WyomingEndpoint(Config["TextToSpeech:Host"]!, Config.GetValue("TextToSpeech:Port", 10200));
                await using var Connection = await WyomingConnection.ConnectAsync(Endpoint, Token);
                var Info = await Connection.DescribeAsync(Token);
                var Native = Info.Data.GetProperty("tts")[0].TryGetProperty("supports_synthesize_streaming", out var Flag) && Flag.ValueKind == JsonValueKind.True;
                var Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var Clock = Stopwatch.StartNew();
                double? FirstAudio = null;
                var Chunks = new List<AudioChunk>();
                var Tts = new WyomingTextToSpeechProvider(Endpoint);
                await foreach (var Chunk in Tts.SynthesizeStreamAsync(Text(Token), new(Config["TextToSpeech:Voice"]), Token))
                {
                    FirstAudio ??= Clock.Elapsed.TotalMilliseconds;
                    Chunks.Add(Chunk);
                    if (Chunks.Sum(Item => Item.Pcm.Length) > 4 * 1024 * 1024) { throw new InvalidDataException(); }
                    Release.TrySetResult();
                }
                var Wave = await VoiceAudioStore.Wave48kAsync(Audio(), Token);
                Console.WriteLine(JsonSerializer.Serialize(new { check = "Streaming synthesis", accepted = true, nativeStreaming = Native,
                    timeToFirstAudioMilliseconds = FirstAudio, totalMilliseconds = Clock.Elapsed.TotalMilliseconds, wavBytes = Wave.Length, physicalPlayback = false }));
                async IAsyncEnumerable<string> Text([EnumeratorCancellation] CancellationToken InputToken)
                {
                    yield return "This is a test of the local voice assistant.";
                    // Prove audio arrives before text input ends on either negotiated path.
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), InputToken);
                    yield return "The second sentence completes the streaming test.";
                }
                async IAsyncEnumerable<AudioChunk> Audio() { foreach (var Chunk in Chunks) { yield return Chunk; } await Task.CompletedTask; }
            }
            catch (Exception Error) when (Error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
            {
                Failures++;
                Console.WriteLine(JsonSerializer.Serialize(new { check = "Streaming synthesis", accepted = false, failureCategory = Error.GetType().Name }));
            }
            try
            {
                using var StreamingLifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
                var StreamingToken = StreamingLifetime.Token;
                var Sentences = Channel.CreateBounded<string>(8);
                var Buffer = new SentenceBuffer();
                var Clock = Stopwatch.StartNew();
                double? FirstText = null;
                double? FirstAudio = null;
                double? GenerationComplete = null;
                var Producer = ProduceAsync();
                long Bytes = 0;
                try
                {
                    var Tts = new WyomingTextToSpeechProvider(new(Config["TextToSpeech:Host"]!, Config.GetValue("TextToSpeech:Port", 10200)));
                    await foreach (var Chunk in Tts.SynthesizeStreamAsync(Sentences.Reader.ReadAllAsync(StreamingToken), new(Config["TextToSpeech:Voice"]), StreamingToken))
                    { FirstAudio ??= Clock.Elapsed.TotalMilliseconds; Bytes += Chunk.Pcm.Length; }
                    var Result = await Producer;
                    var Accepted = Result.Outcome == "succeeded" && Bytes > 0;
                    if (!Accepted) { Failures++; }
                    Console.WriteLine(JsonSerializer.Serialize(new { check = "LLM to streaming TTS", accepted = Accepted,
                        timeToFirstTextMilliseconds = FirstText, timeToFirstAudioMilliseconds = FirstAudio,
                        generationCompletedMilliseconds = GenerationComplete, totalMilliseconds = Clock.Elapsed.TotalMilliseconds,
                        pcmBytes = Bytes, physicalPlayback = false }));
                }
                finally
                {
                    StreamingLifetime.Cancel();
                    try { await Producer; } catch (Exception) { }
                }
                async Task<RequestResult> ProduceAsync()
                {
                    try
                    {
                        var Result = await Coordinator.ProcessStreamingAsync(new("Explain why ice floats in twelve short sentences.", Satellite, NewConversation: true),
                            async (Delta, DeltaToken) =>
                            {
                                FirstText ??= Clock.Elapsed.TotalMilliseconds;
                                foreach (var Sentence in Buffer.Append(Delta)) { await Sentences.Writer.WriteAsync(VoiceFormatter.Format(Sentence), DeltaToken); }
                            }, StreamingToken);
                        GenerationComplete = Clock.Elapsed.TotalMilliseconds;
                        foreach (var Sentence in Buffer.Append("", Final: true)) { await Sentences.Writer.WriteAsync(VoiceFormatter.Format(Sentence), StreamingToken); }
                        Sentences.Writer.TryComplete();
                        return Result;
                    }
                    catch (Exception Error) { Sentences.Writer.TryComplete(Error); throw; }
                }
            }
            catch (Exception Error) when (Error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
            {
                Failures++;
                Console.WriteLine(JsonSerializer.Serialize(new { check = "LLM to streaming TTS", accepted = false, failureCategory = Error.GetType().Name }));
            }
        }
        finally
        {
            await Home.StopAsync(CancellationToken.None);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // The path is a new probe-owned directory, never the production data directory.
            Directory.Delete(DirectoryName, recursive: true);
        }
        return Failures == 0 ? 0 : 1;
    }
    private sealed class NoControls : IHomeAssistantClient
    {
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken Token) => throw new InvalidOperationException("Read-only probe cannot control devices.");
    }
}
