using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.HomeAssistant;
using Assister.Tools;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class HomeAssistantToolTests
{
    [Fact]
    public async Task FullNameSearchExcludesIncidentalLightMatchesAndAllowsSingularControl()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""
            [
            {"entity_id":"light.living_room_lights","state":"off","attributes":{"friendly_name":"Living Room Lights","supported_color_modes":["brightness"]}},
            {"entity_id":"light.bedroom","state":"on","attributes":{"friendly_name":"Bedroom Light","supported_color_modes":["brightness"]}}
            ]
            """), Json("{}"), Json("[]"), Json("[]"), Json("[]"));
        Cache.SetStale(false);
        var Actions = new RecordingActions();
        using var Http = new HttpClient();
        var Config = new ConfigurationBuilder().Build();
        var Context = new ToolExecutionContext(new("Dim living room light to 40 percent"), []);
        var Search = new HomeAssistantTool("ha_search", Cache, Actions, Http, Config);
        var Result = Json(await Search.ExecuteAsync(Json("""{"query":"living room light","domains":["light"]}"""), Context, CancellationToken.None));
        Assert.Equal("light.living_room_lights", Assert.Single(Result.EnumerateArray()).GetProperty("entity_id").GetString());
        Assert.True(Result[0].GetProperty("supports_brightness").GetBoolean());
        var Control = new HomeAssistantTool("ha_control", Cache, Actions, Http, Config);
        Assert.Contains("completed", await Control.ExecuteAsync(Json("""{"entity_id":"light.living_room_lights","action":"set_brightness","brightness_pct":40}"""), Context, CancellationToken.None));
        Assert.Equal(40, Assert.Single(Actions.Calls).BrightnessPercent);

        Context = new(new("Dim the light"), []);
        Result = Json(await Search.ExecuteAsync(Json("""{"query":"light","domains":["light"]}"""), Context, CancellationToken.None));
        Assert.Equal(2, Result.GetArrayLength());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Control.ExecuteAsync(
            Json("""{"entity_id":"light.living_room_lights","action":"set_brightness","brightness_pct":40}"""), Context, CancellationToken.None));
        Assert.Single(Actions.Calls);
        Assert.Empty(Json(await Search.ExecuteAsync(Json("""{"query":"missing"}"""), Context, CancellationToken.None)).EnumerateArray());
    }

    private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);
    private sealed class RecordingActions : IHomeAssistantClient
    {
        public List<HomeAssistantControl> Calls { get; } = [];
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken CancellationToken)
        {
            Calls.Add(Control);
            return Task.CompletedTask;
        }
    }
}
