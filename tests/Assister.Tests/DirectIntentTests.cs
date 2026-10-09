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
    [InlineData("Turn on DenLite.", "Den Light", "turn_on", null)]
    [InlineData("Set the sync light to 10%", "Sink Light", "set_brightness", 10)]
    [InlineData("Set hallway length to 50%.", "Hallway Lights", "set_brightness", 50)]
    public async Task MisheardNamesRequireConfirmationBeforeExecuting(string Message, string Name, string Action, int? Percent)
    {
        var (_, Fake, Cache) = Create();
        Cache.ApplyEvent(Json(JsonSerializer.Serialize(new { entity_id = "light.recovery", new_state = new {
            entity_id = "light.recovery", state = "off", attributes = new { friendly_name = Name, supported_color_modes = new[] { "brightness" } } } })));
        var Model = new RecoveryModel(JsonSerializer.Serialize(new { target = Name }));
        var Resolver = new HomeAssistantEntityResolver();
        var Coordinator = new RequestCoordinator(new IntentClassifier(), Resolver, new(Fake), Cache,
            NullLogger<RequestCoordinator>.Instance, NameRecovery: new(Model, Resolver));
        var Context = new Assister.Tools.DeviceConversationContext();
        var Suggestion = await Coordinator.ProcessWithHistoryAsync(new(Message), [], default, DeviceContext: Context);
        Assert.Equal("ambiguous", Suggestion.Outcome);
        Assert.Contains(Name, Suggestion.Response);
        Assert.Empty(Fake.Calls);
        Assert.Equal(Action, Context.Pending!.Action);
        Assert.Equal(Percent, Context.Pending.Brightness);
        Assert.Equal("none", Model.Request!.ToolChoice);
        Assert.Null(Model.Request.Tools);
        var Confirmed = await Coordinator.ProcessWithHistoryAsync(new("yes"), [], default, DeviceContext: Context);
        Assert.Equal("succeeded", Confirmed.Outcome);
        Assert.Equal(["light.recovery"], Assert.Single(Fake.Calls).EntityIds);
        Assert.Equal(Percent, Fake.Calls[0].BrightnessPercent);
        Assert.Null(Context.Pending);
        Assert.Equal(1, Model.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"target\":null}")]
    [InlineData("{\"target\":\"Invented Light\"}")]
    [InlineData("{\"target\":\"Desk light\"}")]
    [InlineData("[]")]
    public async Task InvalidOrAmbiguousRecoveryNeverStoresAnAction(string Reply)
    {
        var (_, Fake, Cache) = Create();
        var Resolver = new HomeAssistantEntityResolver();
        var Coordinator = new RequestCoordinator(new IntentClassifier(), Resolver, new(Fake), Cache,
            NullLogger<RequestCoordinator>.Instance, NameRecovery: new(new RecoveryModel(Reply), Resolver));
        var Context = new Assister.Tools.DeviceConversationContext();
        var Result = await Coordinator.ProcessWithHistoryAsync(new("turn on DenLite"), [], default, DeviceContext: Context);
        Assert.Equal("not-found", Result.Outcome);
        Assert.Null(Context.Pending);
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task OfflineDeviceDoesNotInvokeNameRecovery()
    {
        var (_, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"light.kitchen_counter","new_state":{"entity_id":"light.kitchen_counter","state":"unavailable","attributes":{"friendly_name":"Kitchen counter"}}}"""));
        var Model = new RecoveryModel("{}");
        var Resolver = new HomeAssistantEntityResolver();
        var Coordinator = new RequestCoordinator(new IntentClassifier(), Resolver, new(Fake), Cache,
            NullLogger<RequestCoordinator>.Instance, NameRecovery: new(Model, Resolver));
        Assert.Equal("unavailable", (await Coordinator.ProcessAsync(new("turn on kitchen counter"), default)).Outcome);
        Assert.Equal(0, Model.Calls);
        Assert.Empty(Fake.Calls);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("What time is it?")]
    [InlineData("expired")]
    public async Task RejectedUnrelatedOrExpiredSuggestionCannotExecuteLater(string FollowUp)
    {
        var (_, Fake, Cache) = Create();
        var Resolver = new HomeAssistantEntityResolver();
        var Coordinator = new RequestCoordinator(new IntentClassifier(), Resolver, new(Fake), Cache,
            NullLogger<RequestCoordinator>.Instance, NameRecovery: new(new RecoveryModel("{\"target\":\"Kitchen counter\"}"), Resolver));
        var Context = new Assister.Tools.DeviceConversationContext();
        await Coordinator.ProcessWithHistoryAsync(new("turn on kitchen countr"), [], default, DeviceContext: Context);
        Assert.NotNull(Context.Pending);
        if (FollowUp == "expired") { Context.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-6); }
        else { await Coordinator.ProcessWithHistoryAsync(new(FollowUp), [], default, DeviceContext: Context); }
        await Coordinator.ProcessWithHistoryAsync(new("yes"), [], default, DeviceContext: Context);
        Assert.Null(Context.Pending);
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task RecoveryCannotOverrideExplicitArea()
    {
        var (_, Fake, Cache) = Create();
        var Resolver = new HomeAssistantEntityResolver();
        var Recovery = new DeviceNameRecovery(new RecoveryModel("{\"target\":\"Kitchen counter\"}"), Resolver);
        var Result = await Recovery.SuggestAsync(new("turn on countr in office"),
            new(DirectIntentKind.TurnOn, "countr", ExplicitArea: "office"), Cache.Snapshot(), default);
        Assert.Null(Result);
        Assert.Empty(Fake.Calls);
    }

    private sealed class RecoveryModel(string Reply) : ILanguageModel
    {
        public int Calls { get; private set; }
        public LlmRequest? Request { get; private set; }
        public Task<LlmResponse> CompleteAsync(LlmRequest Input, CancellationToken Token)
        { Calls++; Request = Input; return Task.FromResult(new LlmResponse(Reply, [], "stop")); }
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Input, CancellationToken Token) => throw new NotSupportedException();
    }

    [Fact]
    public void FanQueryDoesNotMatchLightsSharingItsHomeAssistantDeviceName()
    {
        var Fan = new HomeAssistantEntity("fan.office_fan", "Office Fan", "office", "Office",
            Json("""{"state":"on","attributes":{"percentage":50,"supported_features":49}}""")) { DeviceName = "Office Fan" };
        var Light = new HomeAssistantEntity("light.office_fan", "Office Yellow Light", "office", "Office",
            Json("""{"state":"off","attributes":{}}""")) { DeviceName = "Office Fan" };
        var Intent = new IntentClassifier().Classify("What is the office fan set to?")!;
        var Result = new HomeAssistantEntityResolver().Resolve(Intent, null, new(false, [Fan, Light], Json("{}")));
        Assert.Equal("fan.office_fan", Assert.Single(Result.Entities).EntityId);
        Assert.Empty(Result.Alternatives);
    }

    [Theory]
    [InlineData("Set the office fan speed to 50%.")]
    [InlineData("Can you set the office fan to 50%?")]
    [InlineData("Set fan.office_fan to 50 percent")]
    [InlineData("Adjust the fan in the office to 50")]
    [InlineData("Turn the office fan on to 50 percent")]
    [InlineData("Switch on the office fan 50%")]
    public async Task FanSpeedCommandsResolveFanInsteadOfItsLights(string Message)
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"fan.office_fan","new_state":{"entity_id":"fan.office_fan","state":"on","attributes":{"friendly_name":"Office Fan","supported_features":1,"percentage":25,"percentage_step":25}}}"""));
        var Result = await Coordinator.ProcessAsync(new(Message), CancellationToken.None);
        Assert.Equal("succeeded", Result.Outcome);
        var Call = Assert.Single(Fake.Calls);
        Assert.Equal(HomeAssistantAction.SetFanSpeed, Call.Action);
        Assert.Equal(50, Call.SpeedPercent);
        Assert.Null(Call.BrightnessPercent);
        Assert.Equal(["fan.office_fan"], Call.EntityIds);
    }

    [Theory]
    [InlineData(101, "invalid-request")]
    [InlineData(-1, "invalid-request")]
    [InlineData(50, "unsupported")]
    public async Task InvalidOrUnsupportedFanSpeedNeverSendsControl(int Speed, string Outcome)
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"fan.office_fan","new_state":{"entity_id":"fan.office_fan","state":"on","attributes":{"friendly_name":"Office Fan","supported_features":0}}}"""));
        var Result = await Coordinator.ProcessAsync(new($"Set the office fan speed to {Speed}%"), CancellationToken.None);
        Assert.Equal(Outcome, Result.Outcome);
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task FanStateIncludesSpeedAndPowerQueriesResolveFans()
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"fan.office_fan","new_state":{"entity_id":"fan.office_fan","state":"on","attributes":{"friendly_name":"Office Fan","supported_features":1,"percentage":50}}}"""));
        var Result = await Coordinator.ProcessAsync(new("What is the office fan set to?"), CancellationToken.None);
        Assert.Contains("50 percent speed", Result.Response);
        Assert.Empty(Fake.Calls);
        await Coordinator.ProcessAsync(new("turn office fan off"), CancellationToken.None);
        Assert.Equal(["fan.office_fan"], Assert.Single(Fake.Calls).EntityIds);
    }

    [Theory]
    [InlineData("What's the status of all the current lights in the house?")]
    [InlineData("What is the state of all lights?")]
    [InlineData("What's the status of both office lights?")]
    [InlineData("What is the state of the lights in the office?")]
    [InlineData("Is every switch off?")]
    [InlineData("What is the status of the desk light and the kitchen light?")]
    [InlineData("What's the status of the light in the whole house?")]
    public async Task GroupStateQuestionsStayOutOfSingleDeviceIntent(string Message)
    {
        var (Coordinator, Fake, _) = Create();
        var Result = await Coordinator.ProcessAsync(new(Message), CancellationToken.None);
        Assert.Equal("unmatched", Result.Outcome);
        Assert.Empty(Fake.Calls);
        var Decision = await new IntentClassifier().MatchAsync(Message, CancellationToken.None);
        Assert.Equal("unmatched", Decision.Status);
    }

    [Theory]
    [InlineData("What's the status of light.living_room_lights?", "light.living_room_lights", null)]
    [InlineData("What is the state of the desk light?", "desk light", null)]
    [InlineData("Is the light in the office on?", "light", "office")]
    public void SingleDeviceStateQuestionsRemainDeterministic(string Message, string Target, string? Area)
    {
        var Intent = new IntentClassifier().Classify(Message);
        Assert.NotNull(Intent);
        Assert.Equal(DirectIntentKind.QueryState, Intent.Kind);
        Assert.Equal(Target, Intent.Target);
        Assert.Equal(Area, Intent.ExplicitArea);
    }

    [Fact]
    public async Task ExactLiveTranscriptControlsTwoNamedLightsWithoutAreaAssignments()
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"light.living_room_lights","new_state":{"entity_id":"light.living_room_lights","state":"on","attributes":{"friendly_name":"Living Room Lights","supported_color_modes":["brightness"]}}}"""));
        Cache.ApplyEvent(Json("""{"entity_id":"light.kitchen_lights","new_state":{"entity_id":"light.kitchen_lights","state":"on","attributes":{"friendly_name":"Kitchen Lights","supported_color_modes":["brightness"]}}}"""));
        var Result = await Coordinator.ProcessAsync(new(" Can you set both the living room light and kitchen light to 100?"), CancellationToken.None);
        Assert.Equal("succeeded", Result.Outcome);
        var Call = Assert.Single(Fake.Calls);
        Assert.Equal(["light.living_room_lights", "light.kitchen_lights"], Call.EntityIds);
        Assert.Equal(100, Call.BrightnessPercent);
    }

    [Fact]
    public async Task ARealNameContainingAndIsNotSplit()
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"light.group","new_state":{"entity_id":"light.group","state":"on","attributes":{"friendly_name":"Dining and kitchen lights","supported_color_modes":["brightness"]}}}"""));
        var Result = await Coordinator.ProcessAsync(new("set dining and kitchen lights to 40 percent"), CancellationToken.None);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal(["light.group"], Assert.Single(Fake.Calls).EntityIds);
    }
    [Theory]
    [InlineData("Can you set both light.desk and kitchen counter to 100?", 100)]
    [InlineData("set reading lamp and kitchen counter to 40 percent", 40)]
    public async Task CoordinatedBrightnessResolvesEveryTargetBeforeOneControl(string Text, int Percent)
    {
        var (Coordinator, Fake, _) = Create();
        var Result = await Coordinator.ProcessAsync(new(Text), CancellationToken.None);
        Assert.Equal("succeeded", Result.Outcome);
        var Call = Assert.Single(Fake.Calls);
        Assert.Equal(Percent, Call.BrightnessPercent);
        Assert.Equal(["light.desk", "light.kitchen_counter"], Call.EntityIds);
        Assert.Equal("direct-intent", Result.HandledBy);
    }

    [Theory]
    [InlineData("set reading lamp and missing light to 40 percent", "not-found")]
    [InlineData("set reading lamp and desk light to 40 percent", "ambiguous")]
    public async Task AnUnresolvedListMemberPreventsAllControls(string Text, string Outcome)
    {
        var (Coordinator, Fake, _) = Create();
        Assert.Equal(Outcome, (await Coordinator.ProcessAsync(new(Text), CancellationToken.None)).Outcome);
        Assert.Empty(Fake.Calls);
    }
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

    [Theory]
    [InlineData("my")]
    [InlineData("our")]
    [InlineData("the")]
    public async Task PossessiveAreaTemperatureUsesConciseRoundedReading(string Prefix)
    {
        var (Coordinator, Fake, Cache) = Create();
        Cache.ApplyEvent(Json("""{"entity_id":"sensor.office_temperature","new_state":{"entity_id":"sensor.office_temperature","state":"79.376","attributes":{"friendly_name":"OfficeTemp LYWSD03MMC/MJWSD05MMC_PVVX-tempc","device_class":"temperature","unit_of_measurement":"°F"}}}"""));
        var Result = await Coordinator.ProcessAsync(new($"What's the temperature in {Prefix} office?"), default);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal("Office temperature is 79.4 degrees Fahrenheit.", Result.Response);
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task TranscriptArtifactOffersRetryWithoutChangingDevice()
    {
        var (Coordinator, Fake, _) = Create();
        var Result = await Coordinator.ProcessAsync(new("Set B, reading lamp to 100."), default);
        Assert.Equal("ambiguous", Result.Outcome);
        Assert.Equal("Did you mean Desk light? Please repeat the command.", Result.Response);
        Assert.Empty(Fake.Calls);
    }

    [Theory]
    [InlineData("Volume 10", "unavailable")]
    [InlineData("set the volume to 50 percent", "unavailable")]
    [InlineData("Volume 11", "invalid-request")]
    [InlineData("Volume -1", "invalid-request")]
    public async Task VolumeDoesNotFallThroughToLanguageModel(string Message, string Outcome)
    {
        var (Coordinator, Fake, _) = Create();
        var Result = await Coordinator.ProcessAsync(new(Message), default);
        Assert.Equal(Outcome, Result.Outcome);
        Assert.Equal("satellite-volume", Result.HandledBy);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolumeHonorsSpeakerCapabilityAndSendsNormalizedLevel(bool Supported)
    {
        var Manager = new Assister.Satellites.SatelliteManager();
        var Speaker = new VolumeSpeaker();
        Manager.Register(Speaker);
        Manager.Update("test", State => State with { Capabilities = new() { VolumeControl = Supported } });
        var (Coordinator, _, _) = Create(Manager);
        var Result = await Coordinator.ProcessAsync(new("Volume 10"), default);
        Assert.Equal(Supported ? "succeeded" : "unsupported", Result.Outcome);
        Assert.Equal("satellite-volume", Result.HandledBy);
        if (Supported) { Assert.Equal(new Assister.Satellites.SatelliteEvent("set-volume", "1"), Assert.Single(Speaker.Events)); }
        else { Assert.Empty(Speaker.Events); Assert.Contains("volume buttons", Result.Response); }
    }

    private sealed class VolumeSpeaker : Assister.Satellites.ISatelliteConnection
    {
        public string SatelliteId => "test";
        public string Name => "Test";
        public string? Area => null;
        public List<Assister.Satellites.SatelliteEvent> Events { get; } = [];
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) => throw new NotSupportedException();
        public Task SendEventAsync(Assister.Satellites.SatelliteEvent Event, CancellationToken Token) { Events.Add(Event); return Task.CompletedTask; }
    }

    private static (RequestCoordinator, FakeHomeAssistant, HomeAssistantStateCache) Create(Assister.Satellites.SatelliteManager? Satellites = null)
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
        return (new(new IntentClassifier(), new HomeAssistantEntityResolver(), new(Fake), Cache, NullLogger<RequestCoordinator>.Instance, Satellites: Satellites), Fake, Cache);
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
