using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.HomeAssistant;
using Assister.Tools;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class HomeAssistantToolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryStatisticsAndPagesCoverFullRange(bool Timestamps)
    {
        var Body = """[[{"entity_id":"sensor.temp","state":"unavailable","last_changed":"2026-01-01T01:00:00Z"},{"state":"10","last_changed":"2026-01-01T02:00:00Z"},{"state":"30","last_changed":"2026-01-01T03:00:00Z"},{"state":"30","last_changed":"2026-01-01T04:00:00Z"},{"state":"20","last_changed":"2026-01-01T05:00:00Z"}]]""";
        var Result = await History(Body, new { entity_ids = new[] { "sensor.temp" }, start = "2026-01-01T00:00:00Z", end = "2026-01-02T00:00:00Z",
            include_timestamps = Timestamps, top_count = 2, bottom_count = 1, include_samples = true, sample_offset = 1, sample_limit = 2 });
        var Stats = Result[0];
        Assert.Equal(4, Stats.GetProperty("numeric_samples").GetInt32());
        Assert.Equal(90, Stats.GetProperty("sum").GetDouble());
        Assert.Equal(22.5, Stats.GetProperty("sample_mean").GetDouble());
        Assert.Equal(25, Stats.GetProperty("median").GetDouble());
        Assert.Equal(10, Stats.GetProperty("minimum").GetDouble());
        Assert.Equal(30, Stats.GetProperty("maximum").GetDouble());
        Assert.Equal(3, Stats.GetProperty("next_sample_offset").GetInt32());
        Assert.Equal(2, Stats.GetProperty("samples").GetArrayLength());
        Assert.Equal(Timestamps, Stats.TryGetProperty("maximum_at", out var Peak));
        if (Timestamps) { Assert.Equal(DateTimeOffset.Parse("2026-01-01T03:00:00Z"), Peak.GetDateTimeOffset()); }
        else { Assert.Equal(30, Stats.GetProperty("top")[0].GetDouble()); }
    }

    [Fact]
    public async Task HistoryAcceptsMoreThanOneMiBAndReportsConfiguredLimit()
    {
        var Body = "[[{\"entity_id\":\"sensor.temp\",\"state\":\"10\",\"padding\":\"" + new string('x', 1100000) + "\"}]]";
        var Arguments = new { entity_ids = new[] { "sensor.temp" }, start = "2026-01-01T00:00:00Z", end = "2026-01-02T00:00:00Z" };
        Assert.Equal(1, (await History(Body, Arguments))[0].GetProperty("numeric_samples").GetInt32());
        Assert.Equal("history_response_too_large", (await History(Body, Arguments, 1)).GetProperty("code").GetString());
        var Empty = (await History("[]", Arguments))[0];
        Assert.Equal(0, Empty.GetProperty("numeric_samples").GetInt32());
        Assert.Equal(JsonValueKind.Null, Empty.GetProperty("maximum").ValueKind);
    }

    private static async Task<JsonElement> History(string Body, object Arguments, int MiB = 16)
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""[{"entity_id":"sensor.temp","state":"74","attributes":{"unit_of_measurement":"°F"}}]"""), Json("{}"), Json("[]"), Json("[]"), Json("[]"));
        Cache.SetStale(false);
        using var Http = new HttpClient(new HistoryHandler(Body));
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["HomeAssistant:Url"] = "http://ha/", ["HomeAssistant:HistoryMaxResponseMiB"] = MiB.ToString() }).Build();
        var Tool = new HomeAssistantTool("ha_get_history", Cache, new RecordingActions(), Http, Configuration);
        var Registry = new ToolRegistry([Tool]);
        return Json(await new ToolBroker(Registry).ExecuteAsync(new("history", new("ha_get_history", JsonSerializer.Serialize(Arguments))),
            Registry.All.Keys.ToHashSet(), new(new("office history"), ["sensor.temp"]), CancellationToken.None));
    }

    private sealed class HistoryHandler(string Body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body) });
    }

    [Fact]
    public async Task FutureHistoryRangeReturnsCorrectableErrorWithoutContactingHa()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""[{"entity_id":"sensor.temp","state":"74","attributes":{"friendly_name":"Temp","device_class":"temperature"}}]"""), Json("{}"), Json("[]"), Json("[]"), Json("[]"));
        Cache.SetStale(false);
        using var Http = new HttpClient();
        var Tool = new HomeAssistantTool("ha_get_history", Cache, new RecordingActions(), Http, new ConfigurationBuilder().Build());
        var Context = new ToolExecutionContext(new("office temperature history"), new HashSet<string> { "sensor.temp" });
        var Registry = new ToolRegistry([Tool]);
        var Result = Json(await new ToolBroker(Registry).ExecuteAsync(new("history", new("ha_get_history", JsonSerializer.Serialize(new
            { entity_ids = new[] { "sensor.temp" }, start = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), end = DateTimeOffset.UtcNow.AddHours(4).ToString("O") }))),
            Registry.All.Keys.ToHashSet(), Context, CancellationToken.None));
        Assert.Contains("future", Result.GetProperty("error").GetString());
        Assert.True(Result.TryGetProperty("current_utc", out _));
    }
    [Fact]
    public async Task TemperatureSearchUsesDeviceClassForAbbreviatedSensorNames()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""[{"entity_id":"sensor.office_tempc","state":"74","attributes":{"friendly_name":"OfficeTemp-tempc","device_class":"temperature","unit_of_measurement":"°F"}}]"""),
            Json("{}"), Json("""[{"entity_id":"sensor.office_tempc","area_id":"office"}]"""), Json("[]"), Json("""[{"area_id":"office","name":"Office"}]"""));
        Cache.SetStale(false);
        using var Http = new HttpClient();
        var Context = new ToolExecutionContext(new("Was the office warmer?"), []);
        var Search = new HomeAssistantTool("ha_search", Cache, new RecordingActions(), Http, new ConfigurationBuilder().Build());
        var Result = Json(await Search.ExecuteAsync(Json("""{"query":"temperature","area":"office","domains":["sensor"]}"""), Context, CancellationToken.None));
        Assert.Equal("sensor.office_tempc", Assert.Single(Result.EnumerateArray()).GetProperty("entity_id").GetString());
        Assert.True(Result[0].GetProperty("is_temperature").GetBoolean());
        Assert.Contains("sensor.office_tempc", Context.ObservedEntities);
    }
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
        Assert.Contains("ambiguous_targets", await Control.ExecuteAsync(
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
