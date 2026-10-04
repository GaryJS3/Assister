using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assister.Contracts;
using Assister.Intents;
using Assister.Modules.HomeAssistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assister.IntegrationTests;

public sealed class IntentWorkbenchTests
{
    [Fact]
    public async Task InspectorNeverExecutesAndPlansAreRevalidatedBeforeExplicitExecution()
    {
        await using var Factory = new TestApplication();
        using var Http = Factory.CreateClient();
        var Definition = new IntentDefinition("movie-lighting", "Movie lighting", "SetBrightness", true,
            ["movie lighting"], "light.desk", 20, Response: "Movie lighting is ready.");
        Definition = await SaveAsync(Http, Definition);
        var Preview = await InspectAsync(Http, "movie lighting");
        Assert.Equal("matched", Preview.MatchStatus);
        Assert.Equal("ready", Preview.Outcome);
        Assert.Equal(["light.desk"], Preview.EntityIds);
        Assert.True(Preview.StateChanging);
        Assert.Empty(Factory.Actions.Calls);
        var Tests = await (await Http.PostAsJsonAsync("/api/intents/tests", new { })).Content.ReadFromJsonAsync<IntentTestResult[]>();
        Assert.All(Tests!, Test => Assert.True(Test.Passed));
        Assert.Empty(Factory.Actions.Calls);
        Definition = await SaveAsync(Http, Definition with { Brightness = 30 });
        var Stale = await Http.PostAsJsonAsync("/api/intents/execute", new IntentExecuteRequest("movie lighting", null, Preview.Fingerprint));
        Assert.Equal(HttpStatusCode.Conflict, Stale.StatusCode);
        Assert.Empty(Factory.Actions.Calls);
        Preview = await InspectAsync(Http, "movie lighting");
        var Execute = await Http.PostAsJsonAsync("/api/intents/execute", new IntentExecuteRequest("movie lighting", null, Preview.Fingerprint));
        var Result = JsonDocument.Parse(await Execute.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("succeeded", Result.GetProperty("outcome").GetString());
        Assert.Equal("Movie lighting is ready.", Result.GetProperty("response").GetString());
        Assert.Equal(30, Assert.Single(Factory.Actions.Calls).BrightnessPercent);

        Factory.Actions.Fail = true;
        Execute = await Http.PostAsJsonAsync("/api/intents/execute", new IntentExecuteRequest("movie lighting", null, Preview.Fingerprint));
        Result = JsonDocument.Parse(await Execute.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("failed", Result.GetProperty("outcome").GetString());
        Assert.DoesNotContain("Movie lighting is ready", Result.GetProperty("response").GetString());
        var Unmatched = await InspectAsync(Http, "some unknown request");
        Assert.False(Unmatched.CanExecute);
        Assert.Equal(HttpStatusCode.Conflict, (await Http.PostAsJsonAsync("/api/intents/execute", new IntentExecuteRequest("some unknown request", null, Unmatched.Fingerprint))).StatusCode);
        Assert.Equal(2, Factory.Actions.Calls.Count);
    }

    [Fact]
    public async Task CustomCrudPersistsAndTheNormalCoordinatorUsesTheSameEngine()
    {
        var Directory = Path.Combine(Path.GetTempPath(), "assister-intents", Guid.NewGuid().ToString());
        await using (var Factory = new TestApplication(Directory))
        {
            using var Http = Factory.CreateClient();
            var Definition = await SaveAsync(Http, new("hello-rule", "Greeting", "Reply", true, ["hello assister"], Response: "Hello from a saved intent."));
            var Normal = await (await Http.PostAsJsonAsync("/api/test/message", new UserRequest("hello assister"))).Content.ReadFromJsonAsync<RequestResult>();
            Assert.Equal("direct-intent", Normal!.HandledBy);
            Assert.Equal("Hello from a saved intent.", Normal.Response);
            var Example = new IntentExample("hello-test", "Greeting regression", "hello assister", null, Definition.Id);
            Assert.Equal(HttpStatusCode.OK, (await Http.PutAsJsonAsync("/api/intents/examples/hello-test", Example)).StatusCode);
            var Conflict = await Http.PutAsJsonAsync("/api/intents/hello-rule", Definition with { Version = 0 });
            Assert.Equal(HttpStatusCode.Conflict, Conflict.StatusCode);
            var BuiltIn = (await Http.GetFromJsonAsync<IntentDefinition[]>("/api/intents"))!.Single(Row => Row.Handler == "TurnOff");
            BuiltIn = await SaveAsync(Http, BuiltIn with { Enabled = false });
            Assert.Equal("unmatched", (await InspectAsync(Http, "turn desk light off")).MatchStatus);
            Assert.Equal(HttpStatusCode.BadRequest, (await Http.DeleteAsync($"/api/intents/{BuiltIn.Id}?version={BuiltIn.Version}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Http.PutAsJsonAsync($"/api/intents/{BuiltIn.Id}", BuiltIn with { Handler = "Reply" })).StatusCode);
            Assert.Empty(Factory.Actions.Calls);
        }
        await using (var Factory = new TestApplication(Directory))
        {
            using var Http = Factory.CreateClient();
            var Definition = (await Http.GetFromJsonAsync<IntentDefinition[]>("/api/intents"))!.Single(Row => Row.Id == "hello-rule");
            Assert.Equal("matched", (await InspectAsync(Http, "hello assister")).MatchStatus);
            Assert.Equal("unmatched", (await InspectAsync(Http, "turn desk light off")).MatchStatus);
            var Tests = await (await Http.PostAsJsonAsync("/api/intents/tests", new { })).Content.ReadFromJsonAsync<IntentTestResult[]>();
            Assert.Contains(Tests!, Test => Test.Example.Id == "hello-test" && Test.Passed);
            Assert.Contains(Tests!, Test => Test.Example.Id == "example-power" && !Test.Passed);
            Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"/api/intents/{Definition.Id}?version={Definition.Version}")).StatusCode);
            Tests = await (await Http.PostAsJsonAsync("/api/intents/tests", new { })).Content.ReadFromJsonAsync<IntentTestResult[]>();
            Assert.Contains(Tests!, Test => Test.Example.Id == "hello-test" && !Test.Passed);
        }
    }

    [Fact]
    public async Task DraftsAndConflictsCannotExecuteAndUnknownHandlersAreRejected()
    {
        await using var Factory = new TestApplication();
        using var Http = Factory.CreateClient();
        var Draft = new IntentDefinition("draft-greeting", "Draft", "Reply", true, ["hi assister"], Response: "Hi.");
        var Preview = await (await Http.PostAsJsonAsync("/api/intents/inspect", new IntentInspectRequest("hi assister", Draft: Draft))).Content.ReadFromJsonAsync<IntentPreview>();
        Assert.Equal("matched", Preview!.MatchStatus);
        Assert.False(Preview.CanExecute);
        Assert.DoesNotContain((await Http.GetFromJsonAsync<IntentDefinition[]>("/api/intents"))!, Row => Row.Id == Draft.Id);
        await SaveAsync(Http, Draft);
        await SaveAsync(Http, Draft with { Id = "conflicting-greeting" });
        Assert.Equal("ambiguous", (await InspectAsync(Http, "hi assister")).MatchStatus);
        var Normal = await (await Http.PostAsJsonAsync("/api/test/message", new UserRequest("hi assister"))).Content.ReadFromJsonAsync<RequestResult>();
        Assert.Equal("ambiguous", Normal!.Outcome);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PutAsJsonAsync("/api/intents/unsafe-handler", Draft with { Id = "unsafe-handler", Handler = "shell" })).StatusCode);
        Assert.Equal("timer-route", (await InspectAsync(Http, "set a timer for 5 minutes")).MatchStatus);
        Assert.Empty(Factory.Actions.Calls);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/intents.html")).StatusCode);
    }

    private static async Task<IntentDefinition> SaveAsync(HttpClient Http, IntentDefinition Definition)
    {
        var Response = await Http.PutAsJsonAsync($"/api/intents/{Definition.Id}", Definition);
        Response.EnsureSuccessStatusCode();
        return (await Response.Content.ReadFromJsonAsync<IntentDefinition>())!;
    }
    private static async Task<IntentPreview> InspectAsync(HttpClient Http, string Text) =>
        (await (await Http.PostAsJsonAsync("/api/intents/inspect", new IntentInspectRequest(Text))).Content.ReadFromJsonAsync<IntentPreview>())!;

    private sealed class TestApplication(string? DataPath = null) : WebApplicationFactory<Program>
    {
        public RecordingActions Actions { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = DataPath ?? Path.Combine(Path.GetTempPath(), "assister-intents", Guid.NewGuid().ToString()),
                ["HomeAssistant:Url"] = "", ["LanguageModel:BaseUrl"] = "http://127.0.0.1:1/v1"
            }));
            Builder.ConfigureServices(Services =>
            {
                var Cache = new HomeAssistantStateCache();
                Cache.Load(JsonSerializer.Deserialize<JsonElement>("""[{"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk light","brightness":255,"supported_color_modes":["brightness"]}}]"""),
                    JsonSerializer.Deserialize<JsonElement>("{}"), JsonSerializer.Deserialize<JsonElement>("[]"), JsonSerializer.Deserialize<JsonElement>("[]"), JsonSerializer.Deserialize<JsonElement>("[]"));
                Cache.SetStale(false);
                Services.RemoveAll<HomeAssistantStateCache>(); Services.AddSingleton(Cache);
                Services.RemoveAll<IHomeAssistantClient>(); Services.AddSingleton<IHomeAssistantClient>(Actions);
            });
        }
    }
    private sealed class RecordingActions : IHomeAssistantClient
    {
        public List<HomeAssistantControl> Calls { get; } = [];
        public bool Fail { get; set; }
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken Token)
        {
            Calls.Add(Control);
            return Fail ? Task.FromException(new InvalidOperationException("not confirmed")) : Task.CompletedTask;
        }
    }
}
