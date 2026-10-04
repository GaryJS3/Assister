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
    private static HomeAssistantClient Create(HomeAssistantStateCache Cache, FakeConnection Fake) => new(new ConfigurationBuilder().Build(), Cache, () => Fake, NullLogger<HomeAssistantClient>.Instance);
    private static JsonElement Reply(int Id, string Result) => Json($$"""{"id":{{Id}},"type":"result","success":true,"result":{{Result}}}""");
    private sealed class FakeConnection : IHomeAssistantConnection
    {
        public Queue<JsonElement> Messages { get; } = new();
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
            if (Messages.Count == 0 && BlockWhenEmpty)
            {
                await Task.Delay(Timeout.Infinite, Token);
            }
            return Messages.Dequeue();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}


