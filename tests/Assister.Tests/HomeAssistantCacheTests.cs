using System.Text.Json;
using Assister.Modules.HomeAssistant;

namespace Assister.Tests;

public sealed class HomeAssistantCacheTests
{
    private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);

    [Fact]
    public void StateEventsReuseUnchangedEntitiesAndKeepEarlierSnapshotsStable()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""[{"entity_id":"light.one","state":"off","attributes":{}},{"entity_id":"light.two","state":"off","attributes":{}}]"""),
            Json("{}"), Json("[]"), Json("[]"), Json("[]"));
        var Before = Cache.Snapshot();
        Assert.Same(Before.Entities, Cache.Snapshot().Entities);
        Cache.ApplyEvent(Json("""{"entity_id":"light.one","new_state":{"entity_id":"light.one","state":"on","attributes":{}}}"""));
        var After = Cache.Snapshot();
        Assert.Equal("off", Before.Entities[0].State.GetProperty("state").GetString());
        Assert.Equal("on", After.Entities[0].State.GetProperty("state").GetString());
        Assert.Same(Before.Entities[1], After.Entities[1]);
        Assert.Same(After.Entities, Cache.Snapshot().Entities);
        Cache.SetStale(false);
        Assert.False(Cache.Snapshot().IsStale);
        Assert.True(Before.IsStale);
        Assert.Same(After.Entities, Cache.Snapshot().Entities);
        Cache.ApplyEvent(Json("""{"entity_id":"light.two","new_state":null}"""));
        Assert.Single(Cache.Snapshot().Entities);
        Assert.Equal(2, After.Entities.Count);
        Assert.Equal(2, Cache.Status().StateEventCount);
    }

    [Fact]
    public void RegistryRefreshAndLaterEventsUseOwnedMetadataWithoutMutatingOlderViews()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""[{"entity_id":"light.one","state":"off","attributes":{"friendly_name":"Original"}}]"""),
            Json("{}"), Json("[]"), Json("[]"), Json("[]"));
        var Before = Cache.Snapshot();
        using (var Entities = JsonDocument.Parse("""[{"entity_id":"light.one","name":"Desk","device_id":"device","aliases":["Lamp"]}]"""))
        using (var Devices = JsonDocument.Parse("""[{"id":"device","area_id":"office","name_by_user":"Desk device"}]"""))
        using (var Areas = JsonDocument.Parse("""[{"area_id":"office","name":"Office"}]"""))
            Cache.UpdateRegistries(Entities.RootElement, Devices.RootElement, Areas.RootElement);

        Cache.ApplyEvent(Json("""{"entity_id":"light.one","new_state":{"entity_id":"light.one","state":"on","attributes":{}}}"""));
        var After = Cache.Snapshot();
        var Entity = Assert.Single(After.Entities);
        Assert.Equal("Desk", Entity.Name);
        Assert.Equal("Office", Entity.AreaName);
        Assert.Equal("Desk device", Entity.DeviceName);
        Assert.Equal("Lamp", Assert.Single(Entity.Aliases));
        Assert.Equal("Original", Assert.Single(Before.Entities).Name);
        Assert.Throws<NotSupportedException>(() => ((IList<HomeAssistantEntity>)After.Entities)[0] = Before.Entities[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)Entity.Aliases)[0] = "Changed");
    }

    [Fact]
    public async Task ConcurrentReadersSeeWholeStableSnapshotsDuringUpdates()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("[]"), Json("{}"), Json("[]"), Json("[]"), Json("[]"));
        var Update = Json("""{"entity_id":"light.one","new_state":{"entity_id":"light.one","state":"on","attributes":{}}}""");
        var Remove = Json("""{"entity_id":"light.one","new_state":null}""");
        var Writer = Task.Run(() =>
        {
            for (var Index = 0; Index < 500; Index++)
            {
                Cache.ApplyEvent(Update);
                Cache.ApplyEvent(Remove);
            }
        });
        var Readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var Index = 0; Index < 500; Index++)
            {
                var View = Cache.Snapshot();
                var Count = View.Entities.Count;
                foreach (var Entity in View.Entities) Assert.Equal("on", Entity.State.GetProperty("state").GetString());
                Assert.Equal(Count, View.Entities.Count);
            }
        }));
        await Task.WhenAll(Readers.Append(Writer));
        Assert.Empty(Cache.Snapshot().Entities);
        Assert.Equal(1000, Cache.Status().StateEventCount);
    }
}
