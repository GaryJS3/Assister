using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Modules.Timers;
using Assister.Persistence;
using Assister.Satellites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class TimerDeliveryTests
{
    [Theory]
    [InlineData("How much left on the timer?")]
    [InlineData("How much time is left on my timer?")]
    [InlineData("How long does the timer have left?")]
    [InlineData("How long left on the tea timer?")]
    public async Task RemainingTimeReadsPersistedTimersWithoutChangingThem(string Text)
    {
        await using var Database = await OpenAsync(":memory:");
        var Store = new LocalStore(Database);
        var Handler = new TimerIntentHandler(Store);
        await Handler.TryHandleAsync(new("set a tea timer for 30 minutes", SatelliteId: "bedroom"), CancellationToken.None);
        var Before = Assert.Single(await Store.QueryAsync("SELECT Id,DueAt,Status FROM Timers", CancellationToken.None));
        Assert.True(TimerIntentHandler.Recognizes(Text));
        Assert.Contains("left", await Handler.TryHandleAsync(new(Text, SatelliteId: "bedroom"), CancellationToken.None));
        Assert.Equal("There is no matching active timer.", await Handler.TryHandleAsync(new(Text, SatelliteId: "kitchen"), CancellationToken.None));
        Assert.Equal(Before, Assert.Single(await Store.QueryAsync("SELECT Id,DueAt,Status FROM Timers", CancellationToken.None)));
        await Handler.TryHandleAsync(new("set a tea timer for 30 minutes", SatelliteId: "bedroom"), CancellationToken.None);
        var Multiple = await Handler.TryHandleAsync(new(Text, SatelliteId: "bedroom"), CancellationToken.None);
        Assert.Contains("(1)", Multiple);
        Assert.Contains("(2)", Multiple);
        await Store.ExecuteAsync("UPDATE Timers SET DueAt=0,Status='notification-pending'", CancellationToken.None);
        Assert.Contains("waiting to be announced", await Handler.TryHandleAsync(new(Text, SatelliteId: "bedroom"), CancellationToken.None));
    }

    [Theory]
    [InlineData(" Set a 30 minute timer.", "timer", 1800)]
    [InlineData("start a 30-minute tea timer", "tea", 1800)]
    [InlineData("set a tea timer for 30 minutes", "tea", 1800)]
    [InlineData("set a 2 hour timer", "timer", 7200)]
    [InlineData("set a 10 second timer", "timer", 10)]
    public async Task TimerWordOrdersPersistTheRequestedDurationAndSatellite(string Text, string Name, long Seconds)
    {
        await using var Database = await OpenAsync(":memory:");
        var Store = new LocalStore(Database);
        Assert.True(TimerIntentHandler.Recognizes(Text));
        var Response = await new TimerIntentHandler(Store).TryHandleAsync(new(Text, SatelliteId: "bedroom"), CancellationToken.None);
        Assert.StartsWith("Started your", Response);
        var Row = Assert.Single(await Store.QueryAsync("SELECT Name,SatelliteId,DueAt-CreatedAt FROM Timers", CancellationToken.None));
        Assert.Equal(Name, Row[0]);
        Assert.Equal("bedroom", Row[1]);
        Assert.Equal(Seconds.ToString(), Row[2]);
    }

    [Fact]
    public async Task SlowPlaybackOnOneSatelliteDoesNotDelayAnother()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var Database = await OpenAsync(":memory:");
        var Store = new LocalStore(Database);
        await InsertAsync(Store, "a-slow", 0);
        await InsertAsync(Store, "b-ready", 1);
        var Manager = new SatelliteManager();
        var Slow = new Satellite("a-slow", Holds: true);
        var Ready = new Satellite("b-ready");
        Register(Manager, Slow);
        Register(Manager, Ready);
        var Work = Service(Manager).TickAsync(Store, new Tts(), Timeout.Token);
        await Ready.Completed.Task.WaitAsync(Timeout.Token);
        Assert.Equal(1, Ready.Deliveries);
        Assert.False(Work.IsCompleted);
        Slow.Release.TrySetResult();
        await Work;
        Assert.Equal(2, (await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='completed'", Timeout.Token)).Count);
    }
    [Fact]
    public async Task OfflineBacklogAndFailedSatelliteDoNotStarveConnectedTimers()
    {
        await using var Database = await OpenAsync(":memory:");
        var Store = new LocalStore(Database);
        for (var Index = 0; Index < 25; Index++) { await InsertAsync(Store, "offline", Index); }
        await InsertAsync(Store, "a-failing", 30);
        await InsertAsync(Store, "b-ready", 31);
        var Manager = new SatelliteManager();
        var Failed = new Satellite("a-failing", Fails: true);
        var Ready = new Satellite("b-ready");
        Register(Manager, Failed);
        Register(Manager, Ready);
        await Service(Manager).TickAsync(Store, new Tts(), CancellationToken.None);
        Assert.Equal(1, Ready.Deliveries);
        Assert.Single(await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='completed'", CancellationToken.None));
        Assert.Equal(26, (await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='notification-pending'", CancellationToken.None)).Count);
        Assert.Equal(0, Manager.ActiveSessionCount);
    }

    [Fact]
    public async Task RestartDefersBusyTimerThenDeliversOnceAndPreservesCancellation()
    {
        var FileName = Path.Combine(Path.GetTempPath(), "assister-timers-" + Guid.NewGuid() + ".db");
        try
        {
            await using (var Database = await OpenAsync(FileName))
            {
                var Store = new LocalStore(Database);
                await InsertAsync(Store, "bedroom", 0);
                await InsertAsync(Store, "bedroom", 1);
                await Store.ExecuteAsync("UPDATE Timers SET Status='cancelled' WHERE Name='timer1'", CancellationToken.None);
            }
            await using (var Database = await OpenAsync(FileName))
            {
                var Store = new LocalStore(Database);
                var Manager = new SatelliteManager();
                var Satellite = new Satellite("bedroom");
                Register(Manager, Satellite);
                var Busy = Guid.NewGuid();
                Assert.True(Manager.BeginSession("bedroom", Busy));
                await Service(Manager).TickAsync(Store, new Tts(), CancellationToken.None);
                Assert.Equal(0, Satellite.Deliveries);
                Manager.EndSession("bedroom", Busy);
                await Service(Manager).TickAsync(Store, new Tts(), CancellationToken.None);
                await Service(Manager).TickAsync(Store, new Tts(), CancellationToken.None);
                Assert.Equal(1, Satellite.Deliveries);
                Assert.Single(await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='completed'", CancellationToken.None));
                Assert.Single(await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='cancelled'", CancellationToken.None));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(FileName); }
    }

    [Fact]
    public async Task ExplicitlyStoppedExpiryIsConsumedAndNextTimerRecovers()
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var Database = await OpenAsync(":memory:");
        var Store = new LocalStore(Database);
        await InsertAsync(Store, "bedroom", 0);
        var Manager = new SatelliteManager();
        var Satellite = new Satellite("bedroom");
        Register(Manager, Satellite);
        var Speech = new Tts(Blocks: true);
        var Work = Service(Manager).TickAsync(Store, Speech, Timeout.Token);
        await Speech.Started.Task.WaitAsync(Timeout.Token);
        Assert.True(await Manager.StopAsync("bedroom", Timeout.Token));
        await Work;
        Assert.Single(await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='completed'", Timeout.Token));
        await Service(Manager).TickAsync(Store, new Tts(), Timeout.Token);
        Assert.Equal(0, Satellite.Deliveries);
        await InsertAsync(Store, "bedroom", 1);
        await Service(Manager).TickAsync(Store, new Tts(), Timeout.Token);
        Assert.Equal(1, Satellite.Deliveries);
        Assert.Equal(0, Manager.ActiveSessionCount);
    }

    private static TimerExpiryService Service(SatelliteManager Manager) => new(null!, Manager, NullLogger<TimerExpiryService>.Instance);
    private static async Task<AssisterDbContext> OpenAsync(string FileName)
    {
        var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=" + FileName).Options);
        await Database.Database.OpenConnectionAsync();
        await Database.Database.MigrateAsync();
        return Database;
    }
    private static Task<int> InsertAsync(LocalStore Store, string Satellite, int Index) => Store.ExecuteAsync(
        "INSERT INTO Timers VALUES($p0,$p1,$p2,NULL,NULL,$p3,$p4,'active')", CancellationToken.None,
        Guid.NewGuid().ToString(), "timer" + Index, Satellite, DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
    private static void Register(SatelliteManager Manager, Satellite Satellite)
    {
        Manager.Register(Satellite);
        Manager.Update(Satellite.SatelliteId, State => State with { Capabilities = new() { AnnouncementPlayback = true } });
    }
    private sealed class Tts(bool Blocks = false) : ITextToSpeechProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken Token)
        {
            Started.TrySetResult();
            if (Blocks) { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, Token); }
            yield return new(new byte[2], 16000, 2, 1);
        }
    }
    private sealed class Satellite(string Id, bool Fails = false, bool Holds = false) : ISatelliteConnection
    {
        public string SatelliteId => Id;
        public string Name => Id;
        public string? Area => null;
        public int Deliveries { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token)
        {
            await foreach (var _ in Audio.WithCancellation(Token)) { }
            if (Holds) { await Release.Task.WaitAsync(Token); }
            if (Fails) { throw new IOException(); }
            Deliveries++;
            Completed.TrySetResult();
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) => Task.CompletedTask;
    }
}
