using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.HomeAssistant;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assister.IntegrationTests;

public sealed class TextRequestTests
{
    [Fact]
    public async Task TextEndpointExecutesDirectControlsAndRejectsInvalidRequests()
    {
        using var Parent = new Activity("shared-http-parent").Start();
        var Directory = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        await using var Factory = new TestApplication(Directory);
        using var Http = Factory.CreateClient();
        var Response = await Http.PostAsJsonAsync("/api/test/message", new UserRequest("turn the light off", Area: "office"));
        Assert.Equal(HttpStatusCode.OK, Response.StatusCode);
        var Result = await Response.Content.ReadFromJsonAsync<RequestResult>();
        Assert.Equal("succeeded", Result!.Outcome);
        Assert.Equal("direct-intent", Result.HandledBy);
        Assert.Equal(["light.desk"], Result.EntityIds);
        Assert.Single(Factory.Fake.Calls);
        Assert.NotNull(Result.ConversationId);
        var FollowUp = await Http.PostAsJsonAsync("/api/test/message", new UserRequest("what is the state of light.desk"));
        Assert.Equal(Result.ConversationId, (await FollowUp.Content.ReadFromJsonAsync<RequestResult>())!.ConversationId);
        var OtherSatellite = await Http.PostAsJsonAsync("/api/test/message", new UserRequest("what is the state of light.desk", "other"));
        Assert.NotEqual(Result.ConversationId, (await OtherSatellite.Content.ReadFromJsonAsync<RequestResult>())!.ConversationId);
        var CrossSatellite = await Http.PostAsJsonAsync("/api/test/message", new UserRequest("hello", "other", ConversationId: Result.ConversationId));
        Assert.Equal(HttpStatusCode.BadRequest, CrossSatellite.StatusCode);

        Response = await Http.PostAsJsonAsync("/api/test/message", new UserRequest("set the light to 150 percent", Area: "office"));
        Assert.Equal(HttpStatusCode.BadRequest, Response.StatusCode);
        Response = await Http.PostAsJsonAsync("/api/test/message", new UserRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, Response.StatusCode);
        Response = await Http.PostAsJsonAsync("/api/test/message", new UserRequest("turn the kitchen light off", Area: "office"));
        Assert.Equal("not-found", (await Response.Content.ReadFromJsonAsync<RequestResult>())!.Outcome);
        Assert.Single(Factory.Fake.Calls);
        var Runs = JsonDocument.Parse(await Http.GetStringAsync("/api/diagnostics/runs"));
        Assert.Equal(7, Runs.RootElement.GetArrayLength());
        Assert.Equal(7, Runs.RootElement.EnumerateArray().Select(Run => Run.GetProperty("runId").GetString()).Distinct().Count());
        var Run = JsonDocument.Parse(await Http.GetStringAsync("/api/diagnostics/runs/" + Result.RunId)).RootElement;
        Assert.Equal("debug", Run.GetProperty("source").GetString());
        Assert.Equal(Result.Response, Run.GetProperty("rawResponse").GetString());
        Assert.Equal(Result.SpokenResponse, Run.GetProperty("spokenResponse").GetString());
        Assert.Equal(5, Run.GetProperty("steps").GetArrayLength());
        Assert.All(Run.GetProperty("steps").EnumerateArray(), Step =>
        {
            Assert.NotEqual("running", Step.GetProperty("status").GetString());
            Assert.True(Step.GetProperty("durationMilliseconds").GetDouble() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(Step.GetProperty("summary").GetString()));
        });
        var Classification = Run.GetProperty("steps").EnumerateArray().Single(Step => Step.GetProperty("kind").GetString() == "IntentClassification");
        Assert.Equal("TurnPower", Classification.GetProperty("output").GetProperty("rule").GetString());
        Assert.Equal("light", Classification.GetProperty("output").GetProperty("target").GetString());
        var Resolution = Run.GetProperty("steps").EnumerateArray().Single(Step => Step.GetProperty("kind").GetString() == "EntityResolution");
        Assert.Equal("light.desk", Resolution.GetProperty("output").GetProperty("selected")[0].GetProperty("entityId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/api/diagnostics/runs/" + Run.GetProperty("runId").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync("/api/diagnostics/runs/missing")).StatusCode);
    }

    [Fact]
    public async Task DebugLlmUsesToolsAndFollowUpsKeepConversationUntilExplicitReset()
    {
        var Directory = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        await using var Factory = new TestApplication(Directory, true);
        using var Http = Factory.CreateClient();
        var First = await (await Http.PostAsJsonAsync("/api/test/message", new UserRequest("How hot did the office get this week?", "debug-ui-test", "office", NewConversation: true)))
            .Content.ReadFromJsonAsync<RequestResult>();
        Assert.Equal("language-model", First!.HandledBy);
        Assert.Equal("succeeded", First.Outcome);
        var Run = JsonDocument.Parse(await Http.GetStringAsync("/api/diagnostics/runs/" + First.RunId)).RootElement;
        var Steps = Run.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("unmatched", Steps.Single(Step => Step.GetProperty("kind").GetString() == "IntentClassification").GetProperty("status").GetString());
        Assert.Contains(Steps, Step => Step.GetProperty("kind").GetString() == "ToolSelection");
        var Rounds = Steps.Where(Step => Step.GetProperty("kind").GetString() == "LanguageModel").ToArray();
        Assert.Equal(2, Rounds.Length);
        var Tool = Steps.Single(Step => Step.GetProperty("name").GetString() == "ha_search");
        Assert.Equal(Rounds[0].GetProperty("id").GetString(), Tool.GetProperty("parentId").GetString());
        Assert.Equal("office", Tool.GetProperty("input").GetProperty("query").GetString());
        Assert.Equal(JsonValueKind.Array, Tool.GetProperty("output").ValueKind);
        Assert.DoesNotContain(Steps, Step => Step.GetProperty("kind").GetString() is "SpeechToText" or "TextToSpeech" or "Playback");
        var FollowUp = await (await Http.PostAsJsonAsync("/api/test/message", new UserRequest("And now?", "debug-ui-test", "office", First.ConversationId)))
            .Content.ReadFromJsonAsync<RequestResult>();
        Assert.Equal(First.ConversationId, FollowUp!.ConversationId);
        Assert.NotEqual(First.RunId, FollowUp.RunId);
        var Reset = await (await Http.PostAsJsonAsync("/api/test/message", new UserRequest("Hello", "debug-ui-test", NewConversation: true)))
            .Content.ReadFromJsonAsync<RequestResult>();
        Assert.NotEqual(First.ConversationId, Reset!.ConversationId);
    }

    private sealed class TestApplication(string Directory, bool FakeLlm = false) : WebApplicationFactory<Program>
    {
        public FakeHomeAssistant Fake { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Configuration) => Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = Directory,
                ["HomeAssistant:Url"] = "",
                ["LanguageModel:BaseUrl"] = "http://127.0.0.1:1/v1"
            }));
            Builder.ConfigureServices(Services =>
            {
                var Cache = new HomeAssistantStateCache();
                Cache.Load(JsonSerializer.Deserialize<JsonElement>("""[{"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk"}}]"""),
                    JsonSerializer.Deserialize<JsonElement>("{}"),
                    JsonSerializer.Deserialize<JsonElement>("""[{"entity_id":"light.desk","area_id":"office"}]"""),
                    JsonSerializer.Deserialize<JsonElement>("[]"), JsonSerializer.Deserialize<JsonElement>("""[{"area_id":"office","name":"Office"}]"""));
                Cache.SetStale(false);
                Services.RemoveAll<HomeAssistantStateCache>();
                Services.AddSingleton(Cache);
                Services.RemoveAll<IHomeAssistantClient>();
                Services.AddSingleton<IHomeAssistantClient>(Fake);
                if (FakeLlm)
                {
                    Services.RemoveAll<ILanguageModel>();
                    Services.AddSingleton<ILanguageModel>(new DebugModel());
                }
            });
        }
    }

    private sealed class DebugModel : ILanguageModel
    {
        private int Calls;
        public Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken Token)
            => Task.FromResult(++Calls == 1 ? new LlmResponse(null, [new("debug-search", new("ha_search", "{\"query\":\"office\",\"limit\":3}"))], "tool_calls")
                : new LlmResponse("The office is warm.", [], "stop"));
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request, CancellationToken Token) => throw new NotSupportedException();
    }

    private sealed class FakeHomeAssistant : IHomeAssistantClient
    {
        public List<HomeAssistantControl> Calls { get; } = [];
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken CancellationToken)
        {
            Calls.Add(Control);
            return Task.CompletedTask;
        }
    }
}
