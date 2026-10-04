using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.HomeAssistant;
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
        var Run = Runs.RootElement.EnumerateArray().Single(Run => Run.GetProperty("steps")[0].GetProperty("details").GetProperty("traceId").GetString() == Result.TraceId.ToString());
        Assert.Equal(4, Run.GetProperty("steps").GetArrayLength());
        Assert.All(Run.GetProperty("steps").EnumerateArray(), Step =>
        {
            Assert.False(Step.GetProperty("active").GetBoolean());
            Assert.True(Step.GetProperty("durationMilliseconds").GetDouble() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(Step.GetProperty("details").GetProperty("reason").GetString()));
        });
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/api/diagnostics/runs/" + Run.GetProperty("id").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync("/api/diagnostics/runs/missing")).StatusCode);
    }

    private sealed class TestApplication(string Directory) : WebApplicationFactory<Program>
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
            });
        }
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
