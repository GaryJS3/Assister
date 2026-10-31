using System.Text.Json;
using Assister.Contracts;
using Assister.Intents;
using Assister.Modules.HomeAssistant;
using Assister.Tools;

namespace Assister.Tests;

public sealed class BermudaTrackerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T16:00:00Z");
    private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);
    private static HomeAssistantEntity Entity(string Id, string Role, string State, string Device = "keys", string Name = "Gary's keys",
        string? Updated = "2026-10-31T15:58:00Z") => new(Id, Name + " " + Role, "static", "Wrong static area",
        Json(JsonSerializer.Serialize(new { state = State, last_changed = "2026-10-31T15:55:00Z", last_updated = Updated,
            attributes = new { last_seen = "2026-10-31T15:59:00Z" } })))
        { Platform = "bermuda", DeviceId = Device, DeviceName = Name, OriginalName = Role };
    private static HomeAssistantSnapshot Snapshot(params HomeAssistantEntity[] Entities) => new(false, Entities, Json("{}"));

    [Theory]
    [InlineData("Where are Gary's keys?", "gary's keys")]
    [InlineData("Where is Meg’s phone?", "meg’s phone")]
    [InlineData("Please find Gary's keys", "gary's keys")]
    [InlineData("Where's Meg's phone right now?", "meg's phone")]
    public void NativeIntentAndCatalogRecognizeLocationQuestions(string Message, string Target)
    {
        var Decision = IntentMatching.Match(Message, IntentCatalog.BuiltIns, new());
        Assert.Equal("builtin-tracker", Decision.Match!.Definition.Id);
        Assert.Equal(DirectIntentKind.QueryTracker, Decision.Match.Intent!.Kind);
        Assert.Equal(Target, Decision.Match.Intent.Target);
    }

    [Fact]
    public void GeneralLocationQuestionsRemainAvailableToTheModel()
    {
        Assert.Null(new IntentClassifier().Classify("Where is Bermuda?"));
        Assert.Null(new IntentClassifier().Classify("Where is Paris?"));
    }

    [Fact]
    public void LocationUsesBermudaStatesAndPairsByRegistryDeviceId()
    {
        var Area = Entity("sensor.renamed", "Area", "Kitchen");
        var Last = Entity("sensor.other_renamed", "Area Last Seen", "Office", Updated: "2026-10-31T15:59:00Z");
        var Result = BermudaTracker.Read(Snapshot(Area, Last, Entity("sensor.meg", "Area Last Seen", "Bedroom", "phone", "Meg's phone")), Area, Now);
        Assert.Equal("Kitchen", Result.CurrentArea);
        Assert.Null(Result.LastSeenArea);
        Assert.DoesNotContain("Last seen area", Result.Response);
        Assert.DoesNotContain("Office", Result.Response);
        Assert.True(Result.CurrentlySeen);
        Assert.Equal(120, Result.LastUpdatedSecondsAgo);
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T15:58:00Z"), Result.LastUpdatedAt);
        Assert.Contains("Last updated about 2 minutes ago", Result.Response);
        Assert.DoesNotContain("Wrong static area", Result.Response);
    }

    [Fact]
    public void LostAreaReportsLastSeenLocationsSensorUpdateTime()
    {
        var Area = Entity("sensor.keys_area", "Area", "unknown", Updated: "2026-10-31T15:59:00Z");
        var Result = BermudaTracker.Read(Snapshot(Area, Entity("sensor.keys_last", "Area Last Seen", "Kitchen", Updated: "2026-10-31T15:50:00Z")), Area, Now);
        Assert.False(Result.CurrentlySeen);
        Assert.Null(Result.CurrentArea);
        Assert.Equal("Kitchen", Result.LastSeenArea);
        Assert.Equal(600, Result.LastUpdatedSecondsAgo);
        Assert.Contains("no longer being seen", Result.Response);
        Assert.Contains("Last updated about 10 minutes ago", Result.Response);
        Assert.DoesNotContain("detection", Result.Response);
    }

    [Fact]
    public void UnavailableSensorDoesNotClaimDeviceHasLeft()
    {
        var Area = Entity("sensor.keys", "Area", "unavailable", Updated: "2026-11-01T00:00:00Z");
        var Result = BermudaTracker.Read(Snapshot(Area), Area, Now);
        Assert.Contains("sensor is unavailable", Result.Response);
        Assert.Null(Result.LastUpdatedAt);
        Assert.Null(Result.LastUpdatedSecondsAgo);
        Assert.Null(Result.LastSeenArea);
    }

    [Fact]
    public void TrackerResolutionNormalizesPossessivesAndPreservesAmbiguityAcrossRooms()
    {
        var First = Entity("sensor.one", "Area", "Kitchen") with { Aliases = ["Spare keys"] };
        var Resolver = new HomeAssistantEntityResolver();
        var Intent = new IntentMatch(DirectIntentKind.QueryTracker, "Gary’s keys");
        Assert.Equal(First.EntityId, Assert.Single(Resolver.Resolve(Intent, null, Snapshot(First)).Entities).EntityId);
        Assert.Single(BermudaTracker.Find(Snapshot(First), "spare keys"));
        Assert.Empty(BermudaTracker.Find(Snapshot(First with { Platform = "other" }), "Gary's keys"));
        var Second = First with { EntityId = "sensor.two", DeviceId = "other", AreaName = "Bedroom" };
        var Result = Resolver.Resolve(Intent, "Wrong static area", Snapshot(First, Second));
        Assert.Empty(Result.Entities);
        Assert.Equal(2, Result.Alternatives.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("2026-10-31T15:58:00")]
    [InlineData("2026-11-01T00:00:00Z")]
    public void InvalidUpdateTimeDoesNotFallBackToDetectionOrStateChangeTime(string? Updated)
    {
        var Area = Entity("sensor.keys", "Area", "Kitchen", Updated: Updated);
        var Result = BermudaTracker.Read(Snapshot(Area), Area, Now);
        Assert.Null(Result.LastUpdatedAt);
        Assert.Null(Result.LastUpdatedSecondsAgo);
        Assert.Contains("last update time is unavailable", Result.Response);
        Assert.DoesNotContain("detected", Result.Response);
    }

    [Fact]
    public async Task ToolAndDirectIntentShareLocationAndStaleCacheBehavior()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""[{"entity_id":"sensor.keys","state":"unknown","attributes":{}},{"entity_id":"sensor.last","state":"Kitchen","attributes":{}}]"""), Json("{}"),
            Json("""[{"entity_id":"sensor.keys","platform":"bermuda","device_id":"keys","original_name":"Area"},{"entity_id":"sensor.last","platform":"bermuda","device_id":"keys","original_name":"Area Last Seen"}]"""),
            Json("""[{"id":"keys","name":"Gary's keys"}]"""), Json("[]"));
        Cache.SetStale(false);
        var Tool = new BermudaTrackerTool(Cache);
        Assert.False(Tool.StateChanging);
        var Arguments = Json("""{"query":"Gary's keys"}""");
        ToolBroker.Validate(Arguments, Tool.Definition.Function.Parameters);
        var Result = Json(await Tool.ExecuteAsync(Arguments, new(new UserRequest("where are Gary's keys"), []), default));
        var Handler = new DirectIntentHandler(null!, Cache);
        var Intent = new IntentMatch(DirectIntentKind.QueryTracker, "Gary's keys");
        var Direct = await Handler.ExecuteAsync(Intent, new HomeAssistantEntityResolver().Resolve(Intent, null, Cache.Snapshot()), default);
        Assert.Equal("succeeded", Direct.Outcome);
        Assert.Equal(Result.GetProperty("Response").GetString(), Direct.Response);
        Assert.Contains("Kitchen", Direct.Response);
        Cache.SetStale(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tool.ExecuteAsync(Arguments, new(new UserRequest("find keys"), []), default));
    }
}
