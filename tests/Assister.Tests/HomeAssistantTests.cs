using System.Text.Json;
using Assister.Modules.HomeAssistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class HomeAssistantTests
{
    private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);

    [Fact]
    public void CacheJoinsAreasAndRetainsStaleStatesWithUpdatesAndRemoval()
    {
        var Cache = new HomeAssistantStateCache();
        Cache.Load(Json("""[{"entity_id":"light.office","state":"off","attributes":{"friendly_name":"Desk"}}]"""), Json("{}"),
            Json("""[{"entity_id":"light.office","device_id":"desk","area_id":"override"}]"""),
            Json("""[{"id":"desk","area_id":"office"}]"""), Json("""[{"area_id":"override","name":"Study"}]"""));
        Cache.SetStale(false);
        Assert.Equal("Study", Assert.Single(Cache.Snapshot().Entities).AreaName);
        Cache.ApplyEvent(Json("""{"entity_id":"light.office","new_state":{"entity_id":"light.office","state":"on","attributes":{}}}"""));
        Cache.SetStale(true);
        Assert.True(Cache.Snapshot().IsStale);
        Assert.Equal("on", Assert.Single(Cache.Snapshot().Entities).State.GetProperty("state").GetString());
        Cache.ApplyEvent(Json("""{"entity_id":"light.office","new_state":null}"""));
        Assert.Empty(Cache.Snapshot().Entities);
    }

    [Fact]
    public async Task InitializationAuthenticatesAndReplaysEventsDuringSnapshotLoading()
    {
        var Cache = new HomeAssistantStateCache();
        var Fake = new FakeConnection();
        Fake.Messages.Enqueue(Json("""{"type":"auth_required"}"""));
        Fake.Messages.Enqueue(Json("""{"type":"auth_ok"}"""));
        Fake.Messages.Enqueue(Reply(1, "null"));
        Fake.Messages.Enqueue(Reply(2, """[{"entity_id":"light.desk","state":"off","attributes":{}}]"""));
        Fake.Messages.Enqueue(Json("""{"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"light.desk","new_state":{"entity_id":"light.desk","state":"on","attributes":{}}}}}"""));
        Fake.Messages.Enqueue(Reply(3, "{}"));
        for (var Id = 4; Id <= 6; Id++)
        {
            Fake.Messages.Enqueue(Reply(Id, "[]"));
        }
        for (var Id = 7; Id <= 9; Id++) { Fake.Messages.Enqueue(Reply(Id, "null")); }
        var Client = Create(Cache, Fake);
        await Client.InitializeAsync(Fake, "secret", CancellationToken.None);
        Assert.Equal("auth", Fake.Sent[0].GetProperty("type").GetString());
        Assert.Equal("subscribe_events", Fake.Sent[1].GetProperty("type").GetString());
        Assert.Equal("on", Assert.Single(Cache.Snapshot().Entities).State.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AuthenticationRejectionDoesNotLoadCache()
    {
        var Cache = new HomeAssistantStateCache();
        var Fake = new FakeConnection();
        Fake.Messages.Enqueue(Json("""{"type":"auth_required"}"""));
        Fake.Messages.Enqueue(Json("""{"type":"auth_invalid","message":"secret"}"""));
        var Error = await Assert.ThrowsAsync<InvalidDataException>(() => Create(Cache, Fake).InitializeAsync(Fake, "secret", CancellationToken.None));
        Assert.DoesNotContain("secret", Error.Message);
        Assert.True(Cache.Snapshot().IsStale);
    }

    [Fact]
    public async Task DisconnectMarksCacheStaleAndReconnectReloadsIt()
    {
        var Cache = new HomeAssistantStateCache();
        FakeConnection Ready(string State)
        {
            var Fake = new FakeConnection();
            Fake.Messages.Enqueue(Json("""{"type":"auth_required"}"""));
            Fake.Messages.Enqueue(Json("""{"type":"auth_ok"}"""));
            Fake.Messages.Enqueue(Reply(1, "null"));
            Fake.Messages.Enqueue(Reply(2, JsonSerializer.Serialize(new[] { new { entity_id = "light.desk", state = State, attributes = new { } } })));
            Fake.Messages.Enqueue(Reply(3, "{}"));
            for (var Id = 4; Id <= 6; Id++)
            {
                Fake.Messages.Enqueue(Reply(Id, "[]"));
            }
            for (var Id = 7; Id <= 9; Id++) { Fake.Messages.Enqueue(Reply(Id, "null")); }
            for (var Id = 10; Id <= 12; Id++) { Fake.Messages.Enqueue(Reply(Id, "[]")); }
            return Fake;
        }
        var First = Ready("off");
        var Second = Ready("on");
        Second.BlockWhenEmpty = true;
        var Attempts = 0;
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant:Url"] = "http://localhost:8123/",
            ["HomeAssistant:Token"] = "secret"
        }).Build();
        using var Client = new HomeAssistantClient(Configuration, Cache, () => Interlocked.Increment(ref Attempts) == 1 ? First : Second, NullLogger<HomeAssistantClient>.Instance);
        await Client.StartAsync(CancellationToken.None);
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (Client.Status != "Unavailable")
            {
                await Task.Delay(10, Deadline.Token);
            }
            Assert.True(Cache.Snapshot().IsStale);
            Assert.Single(Cache.Snapshot().Entities);
            while (Client.Status != "Connected")
            {
                await Task.Delay(10, Deadline.Token);
            }
            Assert.False(Cache.Snapshot().IsStale);
            Assert.Equal("on", Assert.Single(Cache.Snapshot().Entities).State.GetProperty("state").GetString());
        }
        finally { await Client.StopAsync(CancellationToken.None); }
        Assert.True(Cache.Snapshot().IsStale);
    }
    [Theory]
    [InlineData("entity_registry_updated")]
    [InlineData("device_registry_updated")]
    [InlineData("area_registry_updated")]
    public async Task RegistryChangesRefreshAreasWithoutLosingStateEvents(string EventType)
    {
        var Cache = new HomeAssistantStateCache();
        var Fake = new FakeConnection { BlockWhenEmpty = true };
        Fake.Messages.Enqueue(Json("""{"type":"auth_required"}"""));
        Fake.Messages.Enqueue(Json("""{"type":"auth_ok"}"""));
        Fake.Messages.Enqueue(Reply(1, "null"));
        Fake.Messages.Enqueue(Reply(2, """[{"entity_id":"light.desk","state":"off","attributes":{}}]"""));
        Fake.Messages.Enqueue(Reply(3, "{}"));
        for (var Id = 4; Id <= 6; Id++) { Fake.Messages.Enqueue(Reply(Id, "[]")); }
        for (var Id = 7; Id <= 9; Id++) { Fake.Messages.Enqueue(Reply(Id, "null")); }
        void Registries(int Id, string AreaName)
        {
            Fake.Messages.Enqueue(Reply(Id, """[{"entity_id":"light.desk","device_id":"desk"}]"""));
            Fake.Messages.Enqueue(Reply(Id + 1, """[{"id":"desk","area_id":"study"}]"""));
            Fake.Messages.Enqueue(Reply(Id + 2, JsonSerializer.Serialize(new[] { new { area_id = "study", name = AreaName } })));
        }
        Registries(10, "Office");
        Fake.Messages.Enqueue(JsonSerializer.SerializeToElement(new { type = "event", @event = new { event_type = EventType, data = new { } } }));
        Fake.Messages.Enqueue(Json("""{"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"light.desk","new_state":{"entity_id":"light.desk","state":"on","attributes":{}}}}}"""));
        // An edit while refresh replies are being received requires another refresh.
        Fake.Messages.Enqueue(JsonSerializer.SerializeToElement(new { type = "event", @event = new { event_type = EventType, data = new { } } }));
        Registries(13, "Study");
        Registries(16, "Living Room");
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant:Url"] = "http://localhost:8123/",
            ["HomeAssistant:Token"] = "secret"
        }).Build();
        using var Client = new HomeAssistantClient(Configuration, Cache, () => Fake, NullLogger<HomeAssistantClient>.Instance);
        await Client.StartAsync(CancellationToken.None);
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (Cache.Snapshot().Entities.SingleOrDefault()?.AreaName != "Living Room")
            {
                await Task.Delay(10, Deadline.Token);
            }
            Assert.False(Cache.Snapshot().IsStale);
            Assert.Equal("on", Assert.Single(Cache.Snapshot().Entities).State.GetProperty("state").GetString());
            Assert.Equal(1, Cache.Status().StateEventCount);
            Assert.Equal(new[] { "state_changed", "entity_registry_updated", "device_registry_updated", "area_registry_updated" },
                Fake.Sent.Where(Message => Message.TryGetProperty("event_type", out _)).Select(Message => Message.GetProperty("event_type").GetString()));
        }
        finally { await Client.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualAndScheduledRebuildReplaceEntireSnapshot(bool Scheduled)
    {
        var Fake = new FakeConnection { BlockWhenEmpty = true };
        var Cache = new HomeAssistantStateCache();
        var Clock = new RefreshClock();
        Fake.Messages.Enqueue(Json("""{"type":"auth_required"}"""));
        Fake.Messages.Enqueue(Json("""{"type":"auth_ok"}"""));
        Fake.Messages.Enqueue(Reply(1, "null"));
        Fake.Messages.Enqueue(Reply(2, """[{"entity_id":"light.old","state":"off","attributes":{}}]"""));
        Fake.Messages.Enqueue(Reply(3, "{}"));
        for (var Id = 4; Id <= 6; Id++) { Fake.Messages.Enqueue(Reply(Id, "[]")); }
        for (var Id = 7; Id <= 9; Id++) { Fake.Messages.Enqueue(Reply(Id, "null")); }
        for (var Id = 10; Id <= 12; Id++) { Fake.Messages.Enqueue(Reply(Id, "[]")); }
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant:Url"] = "http://localhost:8123/",
            ["HomeAssistant:Token"] = "secret"
        }).Build();
        using var Client = new HomeAssistantClient(Configuration, Cache, () => Fake, NullLogger<HomeAssistantClient>.Instance, Clock);
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Client.StartAsync(Deadline.Token);
        try
        {
            while (Client.Status != "Connected" || Clock.Timer is null) { await Task.Delay(10, Deadline.Token); }
            Assert.Equal(TimeSpan.FromMinutes(15), Clock.DueTime);
            Task<bool>? Completion = null;
            if (Scheduled) { Clock.Timer!.Fire(); }
            else { Completion = Client.RequestCacheRebuildAsync(Deadline.Token); }
            Fake.Messages.Enqueue(Reply(13, """[{"entity_id":"light.new","state":"on","attributes":{"friendly_name":"New name"}}]"""));
            Fake.Messages.Enqueue(Reply(14, """{"light":{"new_service":{}}}"""));
            Fake.Messages.Enqueue(Reply(15, """[{"entity_id":"light.new","area_id":"new","aliases":["New alias"]}]"""));
            Fake.Messages.Enqueue(Reply(16, "[]"));
            Fake.Messages.Enqueue(Reply(17, """[{"area_id":"new","name":"New area"}]"""));
            if (Completion is not null) { Assert.True(await Completion); }
            while (Cache.Snapshot().Entities.SingleOrDefault()?.EntityId != "light.new" || Cache.Status().IsStale)
            {
                await Task.Delay(10, Deadline.Token);
            }
            var Entity = Assert.Single(Cache.Snapshot().Entities);
            Assert.Equal("New name", Entity.Name);
            Assert.Equal("New area", Entity.AreaName);
            Assert.Equal("New alias", Assert.Single(Entity.Aliases));
            Assert.True(Cache.Snapshot().Services.GetProperty("light").TryGetProperty("new_service", out _));
        }
        finally { await Client.StopAsync(CancellationToken.None); }
        Assert.False(await Client.RequestCacheRebuildAsync(Deadline.Token));
    }

    private sealed class RefreshClock : TimeProvider
    {
        public RefreshTimer? Timer { get; private set; }
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback Callback, object? State, TimeSpan DueTime, TimeSpan Period)
        {
            this.DueTime = DueTime;
            return Timer = new RefreshTimer(Callback, State);
        }
    }

    private sealed class RefreshTimer(TimerCallback Callback, object? State) : ITimer
    {
        public void Fire() => Callback(State);
        public bool Change(TimeSpan DueTime, TimeSpan Period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static HomeAssistantClient Create(HomeAssistantStateCache Cache, FakeConnection Fake) => new(new ConfigurationBuilder().Build(), Cache, () => Fake, NullLogger<HomeAssistantClient>.Instance);
    private static JsonElement Reply(int Id, string Result) => Json($$"""{"id":{{Id}},"type":"result","success":true,"result":{{Result}}}""");
    private sealed class FakeConnection : IHomeAssistantConnection
    {
        public System.Collections.Concurrent.ConcurrentQueue<JsonElement> Messages { get; } = new();
        public List<JsonElement> Sent { get; } = new();
        public Task ConnectAsync(Uri Url, CancellationToken Token) => Task.CompletedTask;
        public Task SendAsync(object Message, CancellationToken Token)
        {
            Sent.Add(JsonSerializer.SerializeToElement(Message));
            return Task.CompletedTask;
        }
        public bool BlockWhenEmpty
        {
            get; set;
        }
        public async Task<JsonElement> ReceiveAsync(CancellationToken Token)
        {
            while (Messages.IsEmpty && BlockWhenEmpty)
            {
                await Task.Delay(10, Token);
            }
            if (Messages.TryDequeue(out var Message)) { return Message; }
            throw new InvalidOperationException("No queued HA message.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}


