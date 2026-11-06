using Assister.Satellites;
using Assister.Modules.Reminders;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class ReminderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReminderDefaultsToAcknowledgementIncludingBroadcasts(bool Broadcast)
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        await Store.ExecuteAsync("INSERT INTO Reminders VALUES('test','Stretch',$p0,'America/New_York',20,0,NULL,0,'active')", default, Broadcast ? "all" : "kitchen");
        await Store.ExecuteAsync("INSERT INTO Satellites VALUES('kitchen','Kitchen',NULL,'test','',1,'','', '')", default);
        var Clock = new TestClock();
        var Manager = new Assister.Satellites.SatelliteManager(Clock: Clock);
        var Speaker = new Satellite();
        Manager.Register(Speaker);
        Manager.Update("kitchen", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Scheduler = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        await Scheduler.TickAsync(Store, new Speech(), Clock.GetUtcNow(), default);
        Assert.Contains("kitchen", Manager.PendingAnnouncementIds);
        Clock.Advance(599);
        await Manager.RepeatAnnouncementAsync("kitchen", new Speech(), default);
        Assert.Equal(1, Speaker.Deliveries);
        Clock.Advance(1);
        await Manager.RepeatAnnouncementAsync("kitchen", new Speech(), default);
        Assert.Equal(2, Speaker.Deliveries);
        Assert.True(await Manager.AcknowledgeAsync("kitchen", "acknowledged", default));
        Clock.Advance(600);
        await Manager.RepeatAnnouncementAsync("kitchen", new Speech(), default);
        Assert.Equal(2, Speaker.Deliveries);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int Seconds) => Now = Now.AddSeconds(Seconds);
    }
    [Fact]
    public async Task DeliveredBroadcastsDoNotConsumeThePendingDeliveryBatch()
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        await Store.ExecuteAsync("INSERT INTO Satellites VALUES('kitchen','Kitchen',NULL,'test','',1,'','', '')", default);
        for (var Index = 0; Index < 100; Index++)
        {
            var Id = $"delivered-{Index}";
            await Store.ExecuteAsync("INSERT INTO Reminders VALUES($p0,'Done','all','America/New_York',20,0,NULL,0,'completed')", default, Id);
            await Store.ExecuteAsync("INSERT INTO ReminderBroadcastDeliveries VALUES($p0,0,'kitchen',1)", default, Id);
        }
        await Store.ExecuteAsync("INSERT INTO Reminders VALUES('pending','Stretch','all','America/New_York',20,0,NULL,1,'active')", default);
        var Manager = new SatelliteManager();
        var Kitchen = new Satellite();
        Manager.Register(Kitchen);
        Manager.Update("kitchen", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Scheduler = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        Assert.Equal(1, Kitchen.Deliveries);
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        Assert.Contains("kitchen", Manager.PendingAnnouncementIds);
    }

    [Fact]
    public async Task BroadcastRetriesOfflineDestinationWithoutRepeatingSuccessfulPlayback()
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        await Store.ExecuteAsync("INSERT INTO Reminders VALUES('broadcast','Stretch','all','America/New_York',20,0,NULL,0,'active')", default);
        foreach (var Id in new[] { "kitchen", "office" })
            await Store.ExecuteAsync("INSERT INTO Satellites VALUES($p0,$p0,NULL,'test','',1,'','', '')", default, Id);
        var Manager = new Assister.Satellites.SatelliteManager();
        var Kitchen = new Satellite();
        Manager.Register(Kitchen);
        Manager.Update("kitchen", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Scheduler = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        Assert.Equal(1, Kitchen.Deliveries);
        Assert.NotEqual("0", Assert.Single(await Store.QueryAsync("SELECT NextDueAt FROM Reminders", default))[0]);
        var Office = new Satellite("office");
        Manager.Register(Office);
        Manager.Update("office", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        Assert.Equal(1, Kitchen.Deliveries);
        Assert.Equal(1, Office.Deliveries);
        Assert.NotEqual("0", Assert.Single(await Store.QueryAsync("SELECT NextDueAt FROM Reminders", default))[0]);
    }
    [Fact]
    public async Task OfflineReminderSurvivesRestartThenDeliversAndAdvancesOnce()
    {
        var PathName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            await using (var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=" + PathName).Options))
            {
                await Database.Database.MigrateAsync();
                var Store = new LocalStore(Database);
                await new ReminderIntentHandler(Store, new ConfigurationBuilder().Build()).TryHandleAsync(new("remind me to stretch every night at 8", SatelliteId: "kitchen"), default);
                await Store.ExecuteAsync("UPDATE Reminders SET NextDueAt=0", default);
            }
            await using var Reopened = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=" + PathName).Options);
            var Persisted = new LocalStore(Reopened);
            var Manager = new Assister.Satellites.SatelliteManager();
            var Scheduler = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
            await Scheduler.TickAsync(Persisted, new Speech(), DateTimeOffset.UtcNow, default);
            Assert.Equal("0", Assert.Single(await Persisted.QueryAsync("SELECT NextDueAt FROM Reminders", default))[0]);
            var Speaker = new Satellite();
            Manager.Register(Speaker);
            Manager.Update("kitchen", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
            await Scheduler.TickAsync(Persisted, new Speech(), DateTimeOffset.UtcNow, default);
            await Scheduler.TickAsync(Persisted, new Speech(), DateTimeOffset.UtcNow, default);
            Assert.Equal(1, Speaker.Deliveries);
            Assert.True(long.Parse(Assert.Single(await Persisted.QueryAsync("SELECT NextDueAt FROM Reminders", default))[0]) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(PathName); }
    }

    [Fact]
    public async Task LegacyStuckBroadcastAdvancesWithoutReplayingAlreadyDeliveredSpeakers()
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        await Store.ExecuteAsync("INSERT INTO Reminders VALUES('broadcast','Vitamins','all','America/New_York',21,0,NULL,0,'active')", default);
        foreach (var Id in new[] { "kitchen", "offline" })
        {
            await Store.ExecuteAsync("INSERT INTO Satellites VALUES($p0,$p0,NULL,'test','',1,'','', '')", default, Id);
            await Store.ExecuteAsync("INSERT INTO ReminderBroadcastDeliveries VALUES('broadcast',0,$p0,$p1)", default, Id, Id == "kitchen" ? 1 : 0);
        }
        var Manager = new SatelliteManager();
        var Kitchen = new Satellite();
        Manager.Register(Kitchen);
        Manager.Update("kitchen", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Scheduler = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        var Now = DateTimeOffset.Parse("2026-10-31T12:00:00-04:00");
        // Use a future-independent assertion: whichever is later, real time or the supplied tick.
        await Scheduler.TickAsync(Store, new Speech(), Now, default);
        Assert.True(long.Parse(Assert.Single(await Store.QueryAsync("SELECT NextDueAt FROM Reminders", default))[0]) > Now.ToUnixTimeSeconds());
        Assert.Equal(0, Kitchen.Deliveries);
        Assert.Equal("offline", Assert.Single(await Store.QueryAsync("SELECT SatelliteId FROM ReminderBroadcastDeliveries WHERE Delivered=0", default))[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BroadcastDismissalClearsPeersAndOfflineRetryAcrossSchedulerRestart(bool Clear)
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        await Store.ExecuteAsync("INSERT INTO Reminders VALUES('broadcast','Vitamins','all','America/New_York',21,0,NULL,0,'active')", default);
        var Manager = new SatelliteManager();
        foreach (var Id in new[] { "kitchen", "office", "offline" })
        {
            await Store.ExecuteAsync("INSERT INTO Satellites VALUES($p0,$p0,NULL,'test','',1,'','', '')", default, Id);
            if (Id == "offline") continue;
            Manager.Register(new Satellite(Id));
            Manager.Update(Id, State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        }
        var Scheduler = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        Assert.Equal(2, Manager.PendingAnnouncementIds.Length);
        if (Clear) Assert.True(await Manager.StopAsync("office", default));
        else Assert.True(await Manager.AcknowledgeAsync("office", "acknowledged", default));
        Assert.Empty(Manager.PendingAnnouncementIds);
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        Assert.Empty(await Store.QueryAsync("SELECT SatelliteId FROM ReminderBroadcastDeliveries WHERE Delivered=0", default));
        var Offline = new Satellite("offline");
        Manager.Register(Offline);
        Manager.Update("offline", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Restarted = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        await Restarted.TickAsync(Store, new Speech(), DateTimeOffset.UtcNow, default);
        Assert.Equal(0, Offline.Deliveries);
    }

    [Fact]
    public async Task UnacknowledgedOccurrenceDoesNotBlockNextNightAndOfflineRetriesSurviveRestart()
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        var Now = DateTimeOffset.UtcNow;
        await Store.ExecuteAsync("INSERT INTO Reminders VALUES('broadcast','Vitamins','all','America/New_York',21,0,NULL,0,'active')", default);
        foreach (var Id in new[] { "kitchen", "offline" })
            await Store.ExecuteAsync("INSERT INTO Satellites VALUES($p0,$p0,NULL,'test','',1,'','', '')", default, Id);
        var Manager = new SatelliteManager();
        var Kitchen = new Satellite();
        Manager.Register(Kitchen);
        Manager.Update("kitchen", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Scheduler = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        await Scheduler.TickAsync(Store, new Speech(), Now, default);
        var Next = long.Parse(Assert.Single(await Store.QueryAsync("SELECT NextDueAt FROM Reminders", default))[0]);
        Assert.True(Next > Now.ToUnixTimeSeconds());
        Assert.Contains("kitchen", Manager.PendingAnnouncementIds);
        await Scheduler.TickAsync(Store, new Speech(), DateTimeOffset.FromUnixTimeSeconds(Next), default);
        Assert.Equal(2, Kitchen.Deliveries);
        Assert.Single(Manager.PendingAnnouncementIds);
        Assert.Contains(Manager.Events("kitchen"), Event => Event.Type == "Broadcast occurrence superseded or cancelled");
        var Offline = new Satellite("offline");
        Manager.Register(Offline);
        Manager.Update("offline", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Restarted = new ReminderScheduler(null!, Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReminderScheduler>.Instance);
        await Restarted.TickAsync(Store, new Speech(), DateTimeOffset.FromUnixTimeSeconds(Next), default);
        Assert.Equal(1, Offline.Deliveries);
        Assert.Equal(2, Kitchen.Deliveries);
    }

    private sealed class Speech : Assister.Contracts.ITextToSpeechProvider
    {
        public async IAsyncEnumerable<Assister.Contracts.AudioChunk> SynthesizeAsync(string Text, Assister.Contracts.TextToSpeechOptions Options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        {
            await Task.CompletedTask;
            yield return new(new byte[2], 16000, 2, 1);
        }
    }

    private sealed class Satellite(string Id = "kitchen") : Assister.Satellites.ISatelliteConnection
    {
        public string SatelliteId => Id;
        public string Name => "Kitchen";
        public string? Area => null;
        public int Deliveries { get; private set; }
        public IAsyncEnumerable<Assister.Contracts.AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public Task SendEventAsync(Assister.Satellites.SatelliteEvent Event, CancellationToken Token) => Task.CompletedTask;
        public async Task SendAudioAsync(IAsyncEnumerable<Assister.Contracts.AudioChunk> Audio, CancellationToken Token)
        {
            await foreach (var Chunk in Audio.WithCancellation(Token)) { }
            Deliveries++;
        }
    }

    [Theory]
    [InlineData("Remind me to take my vitamin every night at 8", 20, "")]
    [InlineData("Remind me to take out the trash Sunday night at 6", 18, "0")]
    [InlineData("Remind me to stretch every day at 8 AM", 8, "")]
    public async Task CreatesPersistentLocalRecurrence(string Text, int Hour, string Weekday)
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        var Handler = new ReminderIntentHandler(Store, new ConfigurationBuilder().Build());
        Assert.Contains("I'll remind", await Handler.TryHandleAsync(new(Text, SatelliteId: "kitchen"), default));
        var Row = Assert.Single(await Store.QueryAsync("SELECT Hour,Weekday,TimeZone,SatelliteId FROM Reminders", default));
        Assert.Equal(Hour.ToString(), Row[0]);
        Assert.Equal(Weekday, Row[1]);
        Assert.Equal("America/New_York", Row[2]);
        Assert.Equal("kitchen", Row[3]);
    }

    [Fact]
    public async Task AmbiguousTimeDoesNotCreateReminderAndCancellationIsScoped()
    {
        await using var Database = await OpenAsync();
        var Store = new LocalStore(Database);
        var Handler = new ReminderIntentHandler(Store, new ConfigurationBuilder().Build());
        Assert.Contains("AM or PM", await Handler.TryHandleAsync(new("remind me to stretch daily at 8", SatelliteId: "kitchen"), default));
        Assert.Empty(await Store.QueryAsync("SELECT Id FROM Reminders", default));
        await Handler.TryHandleAsync(new("remind me to stretch every night at 8", SatelliteId: "kitchen"), default);
        Assert.Contains("no matching", await Handler.TryHandleAsync(new("cancel reminder stretch", SatelliteId: "bedroom"), default));
        Assert.Contains("stretch", await Handler.TryHandleAsync(new("list my reminders", SatelliteId: "kitchen"), default));
        Assert.Contains("Cancelled", await Handler.TryHandleAsync(new("cancel reminder stretch", SatelliteId: "kitchen"), default));
        Assert.Equal("cancelled", Assert.Single(await Store.QueryAsync("SELECT Status FROM Reminders", default))[0]);
    }

    [Theory]
    [InlineData("2026-03-07T14:00:00Z", 20, 0, "2026-03-08T01:00:00Z")]
    [InlineData("2026-03-08T01:00:00Z", 20, 0, "2026-03-09T00:00:00Z")]
    [InlineData("2026-03-08T05:00:00Z", 2, 30, "2026-03-08T07:00:00Z")]
    [InlineData("2026-11-01T04:00:00Z", 1, 30, "2026-11-01T06:30:00Z")]
    public void RecurrenceHandlesDst(string After, int Hour, int Minute, string Expected)
    {
        var Actual = new ReminderSchedule(Hour, Minute, null).Next(DateTimeOffset.Parse(After), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal(DateTimeOffset.Parse(Expected), Actual);
    }

    [Fact]
    public void WeeklyScheduleAdvancesPastCurrentOccurrence()
    {
        var Schedule = new ReminderSchedule(18, 0, DayOfWeek.Sunday);
        var Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T22:00:00Z"), Schedule.Next(DateTimeOffset.Parse("2026-10-11T22:00:00Z"), Zone));
    }

    private static async Task<AssisterDbContext> OpenAsync()
    {
        var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=:memory:").Options);
        await Database.Database.OpenConnectionAsync();
        await Database.Database.MigrateAsync();
        return Database;
    }
}
