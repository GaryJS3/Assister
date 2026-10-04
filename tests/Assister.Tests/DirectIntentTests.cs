using System.Text.Json;
using Assister.Contracts;
using Assister.Intents;
using Assister.Modules.HomeAssistant;
using Assister.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class DirectIntentTests
{
    [Theory]
    [InlineData("Turn the kitchen light off.", DirectIntentKind.TurnOff, "kitchen light")]
    [InlineData("please turn on the office switch", DirectIntentKind.TurnOn, "office switch")]
    [InlineData("set the kitchen lights to 50 percent", DirectIntentKind.SetBrightness, "kitchen lights")]
    [InlineData(" Set living room light to 100.", DirectIntentKind.SetBrightness, "living room light")]
    [InlineData("Turn living room lights to 50%", DirectIntentKind.SetBrightness, "living room lights")]
    [InlineData("what is the temperature in the office?", DirectIntentKind.QueryTemperature, "temperature")]
    [InlineData("what's the status of light.desk", DirectIntentKind.QueryState, "light.desk")]
    public void ClassifierExtractsIntentAndSlots(string Text, DirectIntentKind Kind, string Target)
    {
        var Match = new IntentClassifier().Classify(Text);
        Assert.NotNull(Match);
        Assert.Equal(Kind, Match.Kind);
        Assert.Equal(Target, Match.Target);
    }

    [Fact]
    public void BareThermostatNumberIsNotBrightness()
    {
        Assert.Null(new IntentClassifier().Classify("set thermostat to 70"));
    }

    [Fact]
    public async Task NamedLivingRoomGroupDoesNotNeedAnAreaAssignment()
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"light.living_room_lights","new_state":{"entity_id":"light.living_room_lights","state":"off","attributes":{"friendly_name":"Living Room Lights","supported_color_modes":["brightness"]}}}"""));
        Cache.ApplyEvent(Json("""{"entity_id":"sensor.living_room_temperature","new_state":{"entity_id":"sensor.living_room_temperature","state":"74","attributes":{}}}"""));
        // Include the real room metadata without assigning the light group to it.
        var Snapshot = Cache.Snapshot();
        var Entities = Snapshot.Entities.Select(Entity => Entity.EntityId == "sensor.living_room_temperature"
            ? Entity with { AreaId = "living_room", AreaName = "Living Room" } : Entity).ToArray();
        var Resolution = new HomeAssistantEntityResolver().Resolve(new(DirectIntentKind.SetBrightness, "living room light", 100), "office", new(false, Entities, Snapshot.Services));
        Assert.Equal(["light.living_room_lights"], Resolution.Entities.Select(Entity => Entity.EntityId));
        var Result = await Coordinator.ProcessAsync(new("Set living room light to 100."), CancellationToken.None);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal(100, Assert.Single(Fake.Calls).BrightnessPercent);
    }

    [Fact]
    public async Task AreaPowerAndPluralBrightnessUseTheSameCoordinatorWithoutLlm()
    {
        var (Coordinator, Fake, _) = Create();
        var Power = await Coordinator.ProcessAsync(new("turn the light off", Area: "office"), CancellationToken.None);
        Assert.Equal("succeeded", Power.Outcome);
        Assert.Equal(["light.desk"], Power.EntityIds);
        var Brightness = await Coordinator.ProcessAsync(new("set the kitchen lights to 50 percent"), CancellationToken.None);
        Assert.Equal("succeeded", Brightness.Outcome);
        Assert.Equal(2, Fake.Calls.Count);
        Assert.Equal(50, Fake.Calls[1].BrightnessPercent);
        Assert.Equal(2, Fake.Calls[1].EntityIds.Count);
        Assert.Equal("direct-intent", Brightness.HandledBy);
        Assert.NotEqual(Guid.Empty, Brightness.TraceId);
        Assert.Null(Brightness.ConversationId);
    }

    [Theory]
    [InlineData("turn the kitchen light off", null, "ambiguous")]
    [InlineData("turn the light off", null, "ambiguous")]
    [InlineData("turn the light off", "garage", "not-found")]
    [InlineData("turn the light in the garage off", "office", "not-found")]
    [InlineData("turn the desk off and the kitchen light off", "office", "not-found")]
    [InlineData("set the kitchen lights to 101 percent", null, "invalid-request")]
    [InlineData("set the kitchen lights to -1 percent", null, "invalid-request")]
    [InlineData("unlock the door", null, "unmatched")]
    public async Task UncertainOrInvalidCommandsNeverExecute(string Message, string? Area, string Outcome)
    {
        var (Coordinator, Fake, _) = Create();
        var Result = await Coordinator.ProcessAsync(new(Message, Area: Area), CancellationToken.None);
        Assert.Equal(Outcome, Result.Outcome);
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task TemperatureUsesCachedStateAndStaleCacheBlocksCommands()
    {
        var (Coordinator, Fake, Cache) = Create();
        var Temperature = await Coordinator.ProcessAsync(new("What is the temperature in the office?"), CancellationToken.None);
        Assert.Equal("Office temperature is 74 degrees Fahrenheit.", Temperature.Response);
        Assert.Empty(Fake.Calls);
        Cache.SetStale(true);
        var Result = await Coordinator.ProcessAsync(new("turn the desk light off"), CancellationToken.None);
        Assert.Equal("unavailable", Result.Outcome);
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task AliasesResolveButPartialNamesAndDuplicateNamesRequireClarification()
    {
        var (Coordinator, Fake, _) = Create();
        Assert.Equal("succeeded", (await Coordinator.ProcessAsync(new("turn the reading lamp off"), CancellationToken.None)).Outcome);
        Assert.Equal("ambiguous", (await Coordinator.ProcessAsync(new("turn the desk light off"), CancellationToken.None)).Outcome);
        Assert.Equal("ambiguous", (await Coordinator.ProcessAsync(new("turn the kitchen off"), CancellationToken.None)).Outcome);
        Assert.Equal("succeeded", (await Coordinator.ProcessAsync(new("turn light.desk off"), CancellationToken.None)).Outcome);
        Assert.Equal(2, Fake.Calls.Count);
    }

    [Fact]
    public async Task UnsupportedBrightnessUnavailableEntitiesAndTransportFailuresAreBounded()
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"light.desk","new_state":{"entity_id":"light.desk","state":"off","attributes":{"friendly_name":"Desk light","supported_color_modes":["onoff"]}}}"""));
        Assert.Equal("unsupported", (await Coordinator.ProcessAsync(new("set light.desk to 50 percent"), CancellationToken.None)).Outcome);
        Cache.ApplyEvent(Json("""{"entity_id":"light.desk","new_state":{"entity_id":"light.desk","state":"unavailable","attributes":{}}}"""));
        Assert.Equal("unavailable", (await Coordinator.ProcessAsync(new("turn light.desk off"), CancellationToken.None)).Outcome);
        Assert.Empty(Fake.Calls);
        Fake.Failure = new HttpRequestException("secret remote response");
        var Result = await Coordinator.ProcessAsync(new("turn light.kitchen_ceiling off"), CancellationToken.None);
        Assert.Equal("failed", Result.Outcome);
        Assert.DoesNotContain("secret", Result.Response);
        Assert.Single(Fake.Calls);
    }

    private static (RequestCoordinator, FakeHomeAssistant, HomeAssistantStateCache) Create()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""
            [
              {"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk light","supported_color_modes":["brightness"]}},
              {"entity_id":"light.kitchen_ceiling","state":"off","attributes":{"friendly_name":"Desk light","supported_color_modes":["brightness"]}},
              {"entity_id":"light.kitchen_counter","state":"on","attributes":{"friendly_name":"Kitchen counter","supported_color_modes":["color_temp"]}},
              {"entity_id":"sensor.office_temperature","state":"74","attributes":{"friendly_name":"Office temperature","device_class":"temperature","unit_of_measurement":"°F"}}
            ]
            """), Json("{}"), Json("""
            [
              {"entity_id":"light.desk","device_id":"desk","aliases":["reading lamp"]},
              {"entity_id":"light.kitchen_ceiling","area_id":"kitchen"},
              {"entity_id":"light.kitchen_counter","area_id":"kitchen"},
              {"entity_id":"sensor.office_temperature","area_id":"office"}
            ]
            """), Json("""[{"id":"desk","area_id":"office"}]"""),
            Json("""[{"area_id":"office","name":"Office"},{"area_id":"kitchen","name":"Kitchen"}]"""));
        Cache.SetStale(false);
        var Fake = new FakeHomeAssistant();
        return (new(new IntentClassifier(), new HomeAssistantEntityResolver(), new(Fake), Cache, NullLogger<RequestCoordinator>.Instance), Fake, Cache);
    }

    private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);
    private sealed class FakeHomeAssistant : IHomeAssistantClient
    {
        public List<HomeAssistantControl> Calls { get; } = [];
        public Exception? Failure
        {
            get; set;
        }
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken CancellationToken)
        {
            Calls.Add(Control);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
