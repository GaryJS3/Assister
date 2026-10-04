using System.Diagnostics;
using System.Text.Json;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Llm;
using Assister.Satellites;
using Assister.Tools;
using Assister.Voice;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task VoiceActivationsUnderOneAmbientActivityRemainIndependent()
    {
        using var Store = new RunStore();
        using var Parent = new Activity("long-lived-grpc").Start();
        var Pipeline = new VoicePipeline(new Stt(), new Tts(), new Coordinator(), new SatelliteManager(), new ConfigurationBuilder().Build(), Store);
        for (var Index = 0; Index < 3; Index++)
            await Pipeline.RunAsync(new Satellite(), null, CancellationToken.None);
        var Runs = Store.Snapshot();
        Assert.Equal(3, Runs.Count);
        Assert.Equal(3, Runs.Select(Run => Run.RunId).Distinct().Count());
        Assert.Equal(3, Runs.Select(Run => Run.VoiceSessionId).Distinct().Count());
        Assert.Single(Runs.Select(Run => Run.ActivityTraceId).Distinct());
        foreach (var Summary in Runs)
        {
            var Run = Store.Get(Summary.RunId)!;
            Assert.Equal("voice", Run.Source);
            Assert.Equal("What is the temperature?", Run.UserText);
            Assert.Equal("74°F", Run.RawResponse);
            Assert.Equal("74 degrees Fahrenheit", Run.SpokenResponse);
            var Recognition = Assert.Single(Run.Steps, Step => Step.Kind == "SpeechToText");
            Assert.Equal("What is the temperature?", Recognition.Output!.Value.GetProperty("transcript").GetString());
            Assert.True(Recognition.Metadata!.Value.GetProperty("postAudioLatencyMilliseconds").GetDouble() >= 0);
            var Audio = Assert.Single(Run.Steps, Step => Step.Name == "Microphone audio");
            Assert.Equal(3200, Audio.Output!.Value.GetProperty("pcmByteCount").GetInt64());
            Assert.Equal(100, Audio.Output.Value.GetProperty("audioDurationMilliseconds").GetDouble());
            var Synthesis = Assert.Single(Run.Steps, Step => Step.Kind == "TextToSpeech");
            Assert.Equal("74 degrees Fahrenheit", Synthesis.Input!.Value.GetProperty("spokenText").GetString());
            Assert.Contains(Run.Steps, Step => Step.Kind == "Playback" && Step.Status == "succeeded");
            Assert.DoesNotContain("pcm\":", JsonSerializer.Serialize(Run, RunStore.Json));
        }
    }

    [Fact]
    public async Task ToolSelectionRoundsArgumentsAndResultsAreStructuredAndNested()
    {
        using var Store = new RunStore();
        var Config = new ConfigurationBuilder().Build();
        var Registry = new ToolRegistry([new SearchTool()]);
        using var Run = RunTracing.BeginRun(Store, "debug", Text: "Was the office hot?");
        var Result = await new ToolLoop(new Model(), Registry, new ToolBroker(Registry), Config)
            .RespondAsync(new("Was the office hot?"), [], CancellationToken.None, RunTracing.RunId);
        RunTracing.Response(Result, Result, "succeeded", "language-model", null);
        Run.Complete("succeeded");
        var Trace = Store.Get(RunTracing.RunId)!;
        var Selection = Assert.Single(Trace.Steps, Step => Step.Kind == "ToolSelection");
        Assert.Equal("ha_search", Selection.Metadata!.Value.GetProperty("selectedTools")[0].GetString());
        var Rounds = Trace.Steps.Where(Step => Step.Kind == "LanguageModel").ToArray();
        Assert.Equal(2, Rounds.Length);
        Assert.Equal("LLM Round 1", Rounds[0].Name);
        Assert.Equal("The office is warm.", Rounds[1].Output!.Value.GetProperty("assistantContent").GetString());
        Assert.Equal("system", Rounds[0].Input!.Value.GetProperty("messages")[0].GetProperty("role").GetString());
        var Tool = Assert.Single(Trace.Steps, Step => Step.Kind == "ToolCall");
        Assert.Equal(Rounds[0].Id, Tool.ParentId);
        Assert.Equal("office", Tool.Input!.Value.GetProperty("query").GetString());
        Assert.Equal(74, Tool.Output!.Value.GetProperty("temperature").GetInt32());
        Assert.False(Tool.OutputTruncated);
    }

    [Fact]
    public void SecretsAreRedactedInAllFieldsAndPersistedPayloadsAreBounded()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assister-diagnostics", Guid.NewGuid().ToString());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Assister:DataPath"] = Path, ["HomeAssistant:Token"] = "ha-private-secret",
            ["LanguageModel:ApiKey"] = "llm-private-secret", ["SatelliteBridge:Token"] = "satellite-private-secret", ["Diagnostics:MaxRuns"] = "2"
        }).Build();
        Guid LastId;
        using (var Store = new RunStore(Config))
        {
            for (var Index = 0; Index < 3; Index++)
            {
                using var Run = RunTracing.BeginRun(Store, "debug", Text: "ha-private-secret");
                using var Step = RunTracing.Start("ToolCall", "tool", "Bearer satellite-private-secret");
                Step.Input(new { authorization = "other-secret", password = "password-secret", content = "llm-private-secret" });
                Step.Output(new { nested = new { token = "nested-secret", text = new string('x', 50000) } });
                Step.Metadata(new { value = "ha-private-secret", apiKey = "unknown-api-key" });
                Step.Complete(); RunTracing.Response("llm-private-secret", "satellite-private-secret", "succeeded", "test", null);
                Run.Complete("succeeded");
            }
            Assert.Equal(2, Store.Snapshot().Count);
            LastId = Store.Snapshot()[0].RunId;
        }
        using var Reopened = new RunStore(Config);
        Assert.Equal(2, Reopened.Snapshot().Count);
        var Persisted = Reopened.Get(LastId)!;
        var Serialized = JsonSerializer.Serialize(Persisted, RunStore.Json);
        foreach (var Secret in new[] { "ha-private-secret", "llm-private-secret", "satellite-private-secret", "other-secret", "password-secret", "nested-secret", "unknown-api-key" })
            Assert.DoesNotContain(Secret, Serialized);
        Assert.True(Persisted.Steps.Single().OutputTruncated);
        Assert.True(Serialized.Length < 32768);
    }

    [Fact]
    public void DisabledPayloadCaptureKeepsSemanticMetadata()
    {
        using var Store = new RunStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Diagnostics:CapturePayloads"] = "false", ["Diagnostics:PersistHistory"] = "false" }).Build());
        using var Run = RunTracing.BeginRun(Store, "debug");
        using var Step = RunTracing.Start("LanguageModel", "LLM Round 1", "Selected home tools.");
        Step.Input(new { secretPrompt = "private content" }); Step.Metadata(new { toolsOffered = new[] { "ha_search" } });
        var Data = Store.Get(RunTracing.RunId)!;
        var Serialized = JsonSerializer.Serialize(Data, RunStore.Json);
        Assert.DoesNotContain("private content", Serialized);
        Assert.Contains("ha_search", Serialized);
    }

    private sealed class Stt : ISpeechToTextProvider
    {
        public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
        { await foreach (var _ in Audio.WithCancellation(Token)) { } return new("What is the temperature?", "en"); }
    }
    private sealed class Tts : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[3200], 16000, 2, 1); }
    }
    private sealed class Coordinator : IRequestCoordinator
    {
        public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken Token)
            => Task.FromResult(new RequestResult("74°F", Guid.NewGuid(), "direct-intent", RunTracing.RunId, "succeeded", [], 1, 1));
    }
    private sealed class Satellite : ISatelliteConnection
    {
        public string SatelliteId => "office"; public string Name => "Office"; public string? Area => "Office";
        public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[3200], 16000, 2, 1); }
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) { await foreach (var _ in Audio.WithCancellation(Token)) { } }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) => Task.CompletedTask;
    }
    private sealed class Model : ILanguageModel
    {
        private int Round;
        public Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken Token) => Task.FromResult(++Round == 1
            ? new LlmResponse(null, [new("call1", new("ha_search", "{\"query\":\"office\"}"))], "tool_calls") : new("The office is warm.", [], "stop"));
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request, CancellationToken Token) => throw new NotSupportedException();
    }
    private sealed class SearchTool : IAssisterTool
    {
        public bool StateChanging => false;
        public LlmTool Definition => new(new("ha_search", "Search", JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""")));
        public Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken Token) => Task.FromResult("{\"temperature\":74}");
    }
}
