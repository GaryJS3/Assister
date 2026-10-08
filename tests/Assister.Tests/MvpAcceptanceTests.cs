using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Assister.Contracts;
using Assister.Conversations;
using Assister.Intents;
using Assister.Llm;
using Assister.Modules.HomeAssistant;
using Assister.Modules.Timers;
using Assister.Persistence;
using Assister.Satellites;
using Assister.Tools;
using Assister.Voice;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class MvpAcceptanceTests
{
    [Fact]
    public async Task VoicePowerBrightnessAndTemperatureWorkWithModelOffline()
    {
        await using var App = await Fixture.CreateAsync();
        foreach (var Message in new[] { "turn the kitchen light off", "set the kitchen lights to 50 percent", "what is the temperature in the office" })
        {
            var Result = await App.VoiceAsync(Message);
            Assert.Equal("succeeded", Result.Outcome);
            Assert.Equal("direct-intent", Result.Request!.HandledBy);
            Assert.NotEmpty(App.Satellite.Spoken);
        }
        Assert.Equal(2, App.Actions.Calls.Count);
        Assert.Equal(HomeAssistantAction.TurnOff, App.Actions.Calls[0].Action);
        Assert.Equal(50, App.Actions.Calls[1].BrightnessPercent);
        Assert.Empty(App.Model.Requests);
        Assert.Equal(0, App.Manager.ActiveSessionCount);
    }

    [Fact]
    public async Task ComparativeHistoryUsesBoundedSummariesAndFollowUpOmitsToolResults()
    {
        await using var App = await Fixture.CreateAsync();
        var End = DateTimeOffset.UtcNow;
        App.Model.Responses.Enqueue(Call("search", "ha_search", new { query = "temperature", domains = new[] { "sensor" } }));
        App.Model.Responses.Enqueue(Call("history", "ha_get_history", new { entity_ids = new[] { "sensor.office", "sensor.living_room" }, start = End.AddHours(-3).ToString("O"), end = End.ToString("O") }));
        App.Model.Responses.Enqueue(new("The office averaged 76 degrees; the living room averaged 72 across the recorded samples.", [], "stop"));
        var Result = await App.VoiceAsync("Has the office been warmer than the living room this afternoon?");
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal("language-model", Result.Request!.HandledBy);
        Assert.Contains("api/history/period/", Assert.Single(App.HttpHandler.Paths));
        var ToolResult = Assert.Single(App.Model.Requests[2].Messages, Message => Message.ToolCallId == "history").Content!;
        Assert.Contains("sample_mean", ToolResult);
        Assert.Contains("not time-weighted", ToolResult);
        Assert.DoesNotContain("raw-history-marker", ToolResult);
        Assert.True(ToolResult.Length < 2000);
        App.Model.Responses.Enqueue(new("The comparison was based on state-change samples.", [], "stop"));
        var FollowUp = await App.VoiceAsync("What were those averages based on?");
        Assert.Equal(Result.Session.ConversationId, FollowUp.Session.ConversationId);
        Assert.DoesNotContain(App.Model.Requests.Last().Messages, Message => Message.Role == "tool");
        Assert.DoesNotContain(App.Model.Requests.Last().Messages, Message => Message.Content?.Contains("numeric_samples") == true);
        var Turns = await App.Database.ConversationTurns.ToListAsync();
        Assert.Equal(2, Turns.Count);
        Assert.All(Turns, Turn => Assert.DoesNotContain("raw-history-marker", Turn.AssistantText));
    }

    [Fact]
    public async Task WeatherFollowUpUsesSameConversationAndRealForecastTool()
    {
        await using var App = await Fixture.CreateAsync();
        App.Model.Responses.Enqueue(Call("daily", "weather_forecast", new { type = "daily" }));
        App.Model.Responses.Enqueue(new("The forecast is sunny, around 75 degrees Fahrenheit.", [], "stop"));
        var First = await App.VoiceAsync("What is the weather this weekend?");
        App.Model.Responses.Enqueue(Call("prior", "chat_history", new { limit = 2 }));
        App.Model.Responses.Enqueue(Call("night", "weather_forecast", new { type = "twice_daily" }));
        App.Model.Responses.Enqueue(new("Sunday night is forecast to be clear, around 60 degrees Fahrenheit.", [], "stop"));
        var Next = await App.VoiceAsync("What about Sunday night?");
        Assert.Equal("succeeded", First.Outcome);
        Assert.Equal("succeeded", Next.Outcome);
        Assert.Equal(First.Session.ConversationId, Next.Session.ConversationId);
        Assert.Equal(2, App.HttpHandler.ForecastTypes.Count);
        Assert.Equal(["daily", "twice_daily"], App.HttpHandler.ForecastTypes);
        Assert.DoesNotContain(App.Model.Requests[2].Messages, Message => Message.Content == "What is the weather this weekend?");
        Assert.Contains(App.Model.Requests[2].Tools!, Tool => Tool.Function.Name == "chat_history");
        Assert.Contains(App.Model.Requests[3].Tools!, Tool => Tool.Function.Name == "weather_forecast");
        Assert.Contains(App.Model.Requests[4].Messages, Message => Message.Role == "tool" && Message.Content!.Contains("is_daytime"));
        Assert.Empty(App.Actions.Calls);
    }

    [Fact]
    public async Task ConversationRestartAndSatelliteSeparationPreserveBoundedContext()
    {
        var PathName = Path.Combine(Path.GetTempPath(), "assister-mvp-" + Guid.NewGuid() + ".db");
        try
        {
            Guid Conversation;
            await using (var App = await Fixture.CreateAsync(PathName))
            {
                var Result = await App.Coordinator.ProcessAsync(new("what is the temperature in the office", "first"), CancellationToken.None);
                Conversation = Result.ConversationId!.Value;
                for (var Index = 0; Index < 16; Index++)
                {
                    App.Database.ConversationTurns.Add(new() { ConversationId = Conversation, UserText = $"topic {Index} " + new string('a', 900), AssistantText = new string('b', 1500), Outcome = "succeeded" });
                }
                await App.Database.SaveChangesAsync();
            }
            await using (var App = await Fixture.CreateAsync(PathName))
            {
                App.Model.Responses.Enqueue(new("I retained the recent conversation.", [], "stop"));
                var Result = await App.Coordinator.ProcessAsync(new("continue our discussion", "first"), CancellationToken.None);
                Assert.Equal(Conversation, Result.ConversationId);
                var History = App.Model.Requests.Single().Messages.Skip(1).SkipLast(1).ToArray();
                Assert.Empty(History);
                Assert.DoesNotContain(History, Message => Message.Role == "tool");
                var Rejected = await App.Coordinator.ProcessAsync(new("continue", "second", ConversationId: Conversation), CancellationToken.None);
                Assert.Equal("invalid-request", Rejected.Outcome);
                var Other = await App.Coordinator.ProcessAsync(new("what is the temperature in the office", "second"), CancellationToken.None);
                Assert.NotEqual(Conversation, Other.ConversationId);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(PathName); }
    }

    private static LlmResponse Call(string Id, string Name, object Arguments) => new(null, [new(Id, new(Name, JsonSerializer.Serialize(Arguments)))], "tool_calls");

    private sealed class Fixture : IAsyncDisposable
    {
        public required AssisterDbContext Database { get; init; }
        public ConversationCoordinator Coordinator { get; private set; } = null!;
        public Model Model { get; } = new();
        public Actions Actions { get; } = new();
        public Handler HttpHandler { get; } = new();
        public HttpClient Http { get; private set; } = null!;
        public Satellite Satellite { get; } = new();
        public SatelliteManager Manager { get; } = new();
        public static async Task<Fixture> CreateAsync(string? FileName = null)
        {
            var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=" + (FileName ?? ":memory:")).Options);
            await Database.Database.OpenConnectionAsync();
            await Database.Database.MigrateAsync();
            var App = new Fixture { Database = Database };
            App.Http = new(App.HttpHandler);
            var Cache = new HomeAssistantStateCache();
            Cache.Load(JsonSerializer.Deserialize<JsonElement>("""
                [{"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen Light","supported_color_modes":["brightness"]}},
                 {"entity_id":"sensor.office","state":"76","attributes":{"friendly_name":"Office Temperature","device_class":"temperature","unit_of_measurement":"°F"}},
                 {"entity_id":"sensor.living_room","state":"72","attributes":{"friendly_name":"Living Room Temperature","device_class":"temperature","unit_of_measurement":"°F"}},
                 {"entity_id":"weather.home","state":"sunny","attributes":{"friendly_name":"Home Weather","temperature_unit":"°F"}}]
                """), JsonSerializer.Deserialize<JsonElement>("{}"), JsonSerializer.Deserialize<JsonElement>("""
                [{"entity_id":"light.kitchen","area_id":"kitchen"},{"entity_id":"sensor.office","area_id":"office"},{"entity_id":"sensor.living_room","area_id":"living"}]
                """), JsonSerializer.Deserialize<JsonElement>("[]"), JsonSerializer.Deserialize<JsonElement>("""
                [{"area_id":"kitchen","name":"Kitchen"},{"area_id":"office","name":"Office"},{"area_id":"living","name":"Living Room"}]
                """));
            Cache.SetStale(false);
            var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["HomeAssistant:Url"] = "http://home.test/", ["HomeAssistant:Token"] = "test", ["Weather:EntityId"] = "  ", ["Voice:StreamingEnabled"] = "false" }).Build();
            var Store = new LocalStore(Database);
            var Tools = new[] { "ha_search", "ha_get_state", "ha_control", "ha_get_history" }
                .Select(Name => (IAssisterTool)new HomeAssistantTool(Name, Cache, App.Actions, App.Http, Config)).Append(new WeatherTool(Cache, App.Http, Config)).Append(new ChatHistoryTool(Store)).ToArray();
            var Registry = new ToolRegistry(Tools);
            var Intents = new IntentStore(Database, IntentActionRegistry.Default);
            await Intents.InitializeAsync(CancellationToken.None);
            var Requests = new RequestCoordinator(new IntentEngine(Intents, new(), IntentActionRegistry.Default), new HomeAssistantEntityResolver(),
                new(App.Actions), Cache, NullLogger<RequestCoordinator>.Instance, new(App.Model, Registry, new(Registry, Store), Config), new(Store));
            App.Coordinator = new(Database, Requests, new());
            App.Manager.Register(App.Satellite);
            return App;
        }
        public async Task<VoiceResult> VoiceAsync(string Message)
            => await new VoicePipeline(new Stt(Message), new Tts(Satellite.Spoken), Coordinator, Manager, new ConfigurationBuilder().Build())
                .RunAsync(Satellite, null, CancellationToken.None);
        public async ValueTask DisposeAsync() { Http.Dispose(); await Database.DisposeAsync(); }
    }
    private sealed class Model : ILanguageModel
    {
        public Queue<LlmResponse> Responses { get; } = new();
        public List<LlmRequest> Requests { get; } = [];
        public Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken Token)
        {
            Requests.Add(Request with { Messages = Request.Messages.ToArray() });
            if (Responses.Count == 0) { throw new IOException("Model is offline."); }
            return Task.FromResult(Responses.Dequeue());
        }
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request, CancellationToken Token) => throw new NotSupportedException();
    }
    private sealed class Actions : IHomeAssistantClient
    {
        public List<HomeAssistantControl> Calls { get; } = [];
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken Token) { Calls.Add(Control); return Task.CompletedTask; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> ForecastTypes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Paths.Add(Request.RequestUri!.PathAndQuery);
            string Body;
            if (Request.Method == HttpMethod.Get)
            {
                var Series = new[] { "sensor.office", "sensor.living_room" }.Select((Id, Index) => Enumerable.Range(0, 1500)
                    .Select(_ => new { entity_id = Id, state = Index == 0 ? "76" : "72", raw = "raw-history-marker" }).ToArray()).ToArray();
                Body = JsonSerializer.Serialize(Series);
            }
            else
            {
                var Arguments = JsonSerializer.Deserialize<JsonElement>(await Request.Content!.ReadAsStringAsync(Token));
                var Type = Arguments.GetProperty("type").GetString()!;
                ForecastTypes.Add(Type);
                Body = JsonSerializer.Serialize(new { service_response = new Dictionary<string, object> { ["weather.home"] = new
                    { forecast = new[] { new { datetime = "2026-10-04T23:00:00-04:00", condition = "clear", temperature = Type == "daily" ? 75 : 60, is_daytime = Type == "daily" } } } } });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Body) };
        }
    }
    private sealed class Satellite : ISatelliteConnection
    {
        public string SatelliteId => "kitchen";
        public string Name => "Kitchen";
        public string? Area => "Kitchen";
        public List<string> Spoken { get; } = [];
        public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[2], 16000, 2, 1); }
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token)
        { await foreach (var _ in Audio.WithCancellation(Token)) { } }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) => Task.CompletedTask;
    }
    private sealed class Stt(string Message) : ISpeechToTextProvider
    {
        public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
        { await foreach (var _ in Audio.WithCancellation(Token)) { } return new(Message, "en"); }
    }
    private sealed class Tts(List<string> Spoken) : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken Token)
        { Spoken.Add(Text); await Task.Yield(); yield return new(new byte[2], 16000, 2, 1); }
    }
}
