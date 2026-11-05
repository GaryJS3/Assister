using Assister.Contracts;
using Assister.Interactions;
using Assister.Satellites;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class MixedDeviceGroupTests
{
    [Theory]
    [InlineData("announcement", "notify")]
    [InlineData("alarm", "alarm")]
    [InlineData("timer", "alarm")]
    public async Task MixedGroupsScheduleRetryAndPersistIndependentDelivery(string Kind, string Importance)
    {
        using var Fixture = new GroupFixture();
        var Clock = new TestClock();
        var Manager = new SatelliteManager(Clock: Clock);
        var Speaker = new Speaker("kitchen"); var Offline = new Speaker("bedroom"); var Stranger = new Speaker("stranger");
        Register(Manager, Speaker); Register(Manager, Stranger);
        Guid Id;
        using (var Notices = new NotificationStore(Fixture.Config, Clock, Fixture.Signals))
        {
            Notices.SetSatelliteGroups("kitchen", "home", [" Upstairs ", "upstairs"]);
            Notices.SetSatelliteGroups("bedroom", "home", ["Upstairs"]);
            Notices.SetSatelliteGroups("stranger", "other", ["Upstairs"]);
            Assert.Single(Notices.SatelliteGroups().First(Item => Item.SatelliteId == "kitchen").Groups);
            var Item = Notices.Create("home", new(Kind, "Ready", "mixed", Clock.Now.AddMinutes(1), Importance: Importance, TargetGroup: "upstairs"));
            Id = Item.Id;
            var Service = ServiceFor(Notices, Manager);
            await Service.TickAsync(new Speech(), default);
            Assert.Equal(0, Speaker.Deliveries);
            Assert.Empty(Notices.List("home", "windows", true));
            Clock.Now = Clock.Now.AddMinutes(1);
            Assert.Equal(Id, Assert.Single(Notices.List("home", "windows", true)).Id);
            Assert.Equal(Id, Assert.Single(Notices.List("home", "android", true)).Id);
            Assert.Empty(Notices.List("other", "stranger", true));
            var Busy = Guid.NewGuid(); Manager.BeginSession("kitchen", Busy);
            await Service.TickAsync(new Speech(), default);
            Assert.Equal(0, Speaker.Deliveries);
            Manager.EndSession("kitchen", Busy);
            await Service.TickAsync(new Speech(), default);
            Assert.Equal(1, Speaker.Deliveries);
            Assert.Equal(0, Offline.Deliveries);
            Assert.Equal(0, Stranger.Deliveries);
            Assert.Empty(Notices.PendingForSatellite("kitchen"));
            Assert.Equal(Id, Assert.Single(Notices.PendingForSatellite("bedroom")).Id);
            if (Importance == "alarm") Assert.True(await Manager.AcknowledgeAsync("kitchen", "stop", default));
        }
        using var Reopened = new NotificationStore(Fixture.Config, Clock, Fixture.Signals);
        var Recovered = ServiceFor(Reopened, Manager);
        Register(Manager, Offline);
        await Recovered.TickAsync(new Speech(), default);
        Assert.Equal(1, Speaker.Deliveries);
        Assert.Equal(1, Offline.Deliveries);
        Assert.Empty(Reopened.PendingForSatellite("bedroom"));
        Assert.Single(Reopened.List("home", "android", true)); // Satellite acknowledgement never suppresses client delivery.
    }

    [Fact]
    public async Task FailureCancellationAndExpiryDoNotStopUnrelatedAlarms()
    {
        using var Fixture = new GroupFixture(); var Clock = new TestClock();
        using var Notices = new NotificationStore(Fixture.Config, Clock, Fixture.Signals);
        Notices.SetSatelliteGroups("kitchen", "home", ["Upstairs"]);
        var Speaker = new Speaker("kitchen") { Fail = true }; var Manager = new SatelliteManager(Clock: Clock);
        Register(Manager, Speaker); var Service = ServiceFor(Notices, Manager);
        var Notice = Notices.Create("home", new("alarm", "Wake up", "wake", Importance: "alarm", TargetGroup: "Upstairs"));
        await Service.TickAsync(new Speech(), default);
        Assert.Single(Notices.PendingForSatellite("kitchen"));
        Assert.Empty(Manager.PendingAnnouncementIds);
        Speaker.Fail = false;
        await Service.TickAsync(new Speech(), default);
        Assert.Contains("kitchen", Manager.PendingAnnouncementIds);
        Assert.False(Notices.Cancel("other", "stranger", Notice.Id));
        var Voice = Guid.NewGuid(); Manager.BeginSession("kitchen", Voice);
        Assert.True(Notices.Cancel("home", "sender-outside-group", Notice.Id));
        await Service.TickAsync(new Speech(), default);
        Assert.Empty(Manager.PendingAnnouncementIds);
        Assert.True(Manager.IsBusy("kitchen"));
        Manager.EndSession("kitchen", Voice);
        // Cancelling an older group notice must not remove an unrelated alarm on the same satellite.
        var Second = Notices.Create("home", new("alarm", "Second", "second", Importance: "alarm", TargetGroup: "Upstairs"));
        await Service.TickAsync(new Speech(), default);
        await Manager.AcknowledgeAsync("kitchen", "stop", default);
        await Manager.AnnounceAsync("kitchen", "Unrelated", new Speech(), default, Importance: AnnouncementImportance.Alarm);
        Notices.Cancel("home", "sender-outside-group", Second.Id);
        await Service.TickAsync(new Speech(), default);
        Assert.Contains("kitchen", Manager.PendingAnnouncementIds);
        await Manager.AcknowledgeAsync("kitchen", "stop", default);
        var Expiring = Notices.Create("home", new("alarm", "Expire", "expiry", ExpiresAt: Clock.Now.AddSeconds(5), Importance: "alarm", TargetGroup: "Upstairs"));
        await Service.TickAsync(new Speech(), default);
        Clock.Now = Clock.Now.AddSeconds(5);
        await Service.TickAsync(new Speech(), default);
        Assert.Empty(Manager.PendingAnnouncementIds);
        Assert.Empty(Notices.PendingForSatellite("kitchen"));
    }

    [Fact]
    public async Task RemovingMembershipStopsOnlyItsOutstandingGroupAlarm()
    {
        using var Fixture = new GroupFixture();
        using var Notices = new NotificationStore(Fixture.Config, Signals: Fixture.Signals);
        Notices.SetSatelliteGroups("kitchen", "home", ["Upstairs"]);
        var Manager = new SatelliteManager(); Register(Manager, new("kitchen"));
        Notices.Create("home", new("alarm", "Ready", "ready", Importance: "alarm", TargetGroup: "Upstairs"));
        var Service = ServiceFor(Notices, Manager); await Service.TickAsync(new Speech(), default);
        Assert.Contains("kitchen", Manager.PendingAnnouncementIds);
        Notices.SetSatelliteGroups("kitchen", "home", []);
        await Service.TickAsync(new Speech(), default);
        Assert.Empty(Manager.PendingAnnouncementIds);
        Assert.Empty(Notices.ResolvedSatelliteNotifications());
    }

    private static SatelliteNotificationService ServiceFor(NotificationStore Notices, SatelliteManager Manager) =>
        new(Notices, Manager, null!, NullLogger<SatelliteNotificationService>.Instance);
    [Fact]
    public async Task SatelliteOriginRequiresMembershipAndRejectsAmbiguousOwners()
    {
        using var Fixture = new GroupFixture();
        using var Notices = new NotificationStore(Fixture.Config, Signals: Fixture.Signals);
        Notices.SetSatelliteGroups("kitchen", "home", ["Upstairs"]);
        var Bridge = Fixture.Bridge(Notices);
        var Handler = new AnnouncementHandler(new(), new Speech(), Rich: Bridge);
        var Result = await Handler.TryHandleAsync(new("announce to group Upstairs: Hello", "kitchen"), new(), default);
        Assert.Contains("Queued announcement", Result);
        Assert.Single(Notices.List("home", "windows", true));
        Assert.False(Bridge.PublishGroup("unassigned", "Upstairs", "alarm", "Unauthorized", "bad", "alarm"));
        Notices.SetSatelliteGroups("kitchen", "other", ["Upstairs"]);
        Assert.Null(Bridge.GroupOwner("kitchen", "Upstairs"));
        Assert.False(Bridge.PublishGroup("kitchen", "Upstairs", "alarm", "Ambiguous", "bad2", "alarm"));
        Assert.Single(Notices.List("home", "windows", true));
    }
    private static void Register(SatelliteManager Manager, Speaker Speaker)
    {
        Manager.Register(Speaker);
        Manager.Update(Speaker.SatelliteId, State => State with { Capabilities = new() { AnnouncementPlayback = true } });
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Speech : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[2], 16000, 2, 1); }
    }
    private sealed class Speaker(string Id) : ISatelliteConnection
    {
        public string SatelliteId => Id;
        public string Name => Id;
        public string? Area => null;
        public bool Fail;
        public int Deliveries;
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) => Task.CompletedTask;
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token)
        {
            if (Fail) throw new IOException("Unavailable");
            await foreach (var _ in Audio.WithCancellation(Token)) { }
            Deliveries++;
        }
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
    }
    private sealed class GroupFixture : IDisposable
    {
        public IConfiguration Config { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "mixed-groups-" + Guid.NewGuid()),
            ["RichClients:Clients:windows:Owner"] = "home", ["RichClients:Clients:android:Owner"] = "home"
        }).Build();
        private readonly ServiceProvider Services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        private readonly InteractionStore Interactions;
        public ClientSignals Signals { get; }
        public RichNotificationBridge Bridge(NotificationStore Notices) => new(Notices, Interactions, Config, Signals);
        public GroupFixture()
        {
            Interactions = new(Config);
            Signals = new(Config, Interactions, new(Config, Services.GetRequiredService<IDataProtectionProvider>()));
            Signals.Register("windows", "home", new("windows", "Desktop", []));
            Signals.Register("android", "home", new("android", "Phone", []));
            Signals.Register("stranger", "stranger", new("android", "Stranger", []));
            Signals.SetGroups("windows", ["Upstairs"]); Signals.SetGroups("android", ["Upstairs"]); Signals.SetGroups("stranger", ["Upstairs"]);
        }
        public void Dispose() { Signals.Dispose(); Interactions.Dispose(); Services.Dispose(); }
    }
}
