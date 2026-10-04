using Assister.Contracts;
using Assister.Persistence;
using Assister.Satellites;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Assister.Tests;

public sealed class SatelliteManagerTests
{
    [Fact]
    public void ReconnectRefreshesOwnershipAndLateDisconnectCannotRemoveNewConnection()
    {
        var Manager = new SatelliteManager();
        var First = new FakeSatellite();
        Assert.True(Manager.Register(First));
        Assert.False(Manager.Register(new FakeSatellite()));
        Manager.Update("test", State => State with { VoiceOwnership = VoiceOwnership.OwnedByAssister, Capabilities = new() { MultiChannelMicrophone = true } });
        var Session = Guid.NewGuid();
        Assert.True(Manager.BeginSession("test", Session));
        Assert.False(Manager.BeginSession("test", Guid.NewGuid()));
        Manager.Disconnect(First);
        Assert.Equal(0, Manager.ActiveSessionCount);
        Assert.Equal(VoiceSessionState.Disconnected, Manager.State("test").Activity);
        Assert.NotNull(Manager.State("test").LastSeen);
        var Second = new FakeSatellite();
        Assert.True(Manager.Register(Second));
        Assert.Equal(VoiceOwnership.Unknown, Manager.State("test").VoiceOwnership);
        Manager.Update("test", State => State with { Capabilities = new() { Microphone = true } });
        Assert.False(Manager.State("test").Capabilities.MultiChannelMicrophone);
        Manager.Disconnect(First);
        Assert.Equal(1, Manager.Count);
        var Next = Guid.NewGuid();
        Assert.True(Manager.BeginSession("test", Next));
        Manager.Stage("test", Next, VoiceSessionState.CapturingAudio);
        Manager.Stage("test", Session, VoiceSessionState.PlaybackFailed);
        Manager.EndSession("test", Session);
        Assert.Equal(VoiceSessionState.CapturingAudio, Manager.State("test").Activity);
        Assert.Equal(1, Manager.ActiveSessionCount);
    }

    [Fact]
    public void WakeWordsRejectUnavailableDuplicatesAndExcessModels()
    {
        var Config = new VoiceConfiguration([new("nabu", "Okay Nabu"), new("jarvis", "Hey Jarvis")], ["nabu"], 1);
        Assert.Null(SatelliteConfiguration.Validate(Config, ["jarvis"]));
        Assert.NotNull(SatelliteConfiguration.Validate(Config, ["alexa"]));
        Assert.NotNull(SatelliteConfiguration.Validate(Config, ["nabu", "jarvis"]));
        Assert.NotNull(SatelliteConfiguration.Validate(Config with { MaxActiveWakeWords = 2 }, ["nabu", "nabu"]));
    }

    [Fact]
    public async Task DesiredWakeWordsPersistAndFirmwareDriftCannotSubstituteAnotherModel()
    {
        await using var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=:memory:").Options);
        await Database.Database.OpenConnectionAsync();
        await Database.Database.MigrateAsync();
        Database.Satellites.Add(new() { Id = "test", Configuration = JsonSerializer.Serialize(new DesiredVoiceConfiguration(["jarvis"])) });
        await Database.SaveChangesAsync();
        Database.ChangeTracker.Clear();
        var Manager = new SatelliteManager(); var Connection = new FakeSatellite();
        Manager.Register(Connection);
        Manager.Update("test", State => State with { VoiceOwnership = VoiceOwnership.OwnedByAssister,
            VoiceConfiguration = new([new("nabu", "Okay Nabu")], ["nabu"], 1) });
        var Configuration = new SatelliteConfiguration(Database, Manager);
        await Configuration.ReconcileAsync("test", Connection, CancellationToken.None, true);
        Assert.Contains("no longer available", Manager.State("test").ConfigurationDrift);
        Assert.Empty(Connection.Events);
        Manager.Update("test", State => State with { VoiceConfiguration = new([new("jarvis", "Hey Jarvis")], ["nabu"], 1) });
        await Configuration.ReconcileAsync("test", Connection, CancellationToken.None, true);
        Assert.Single(Connection.Events);
        await Configuration.ReconcileAsync("test", Connection, CancellationToken.None);
        Assert.Single(Connection.Events); // A configuration mismatch cannot create a command loop.
        Manager.Update("test", State => State with { VoiceConfiguration = State.VoiceConfiguration with { ActiveWakeWords = ["jarvis"] } });
        await Configuration.ReconcileAsync("test", Connection, CancellationToken.None);
        Assert.Null(Manager.State("test").ConfigurationDrift);
    }

    [Fact]
    public async Task UnsupportedAndBusyAnnouncementsDoNotSendCommandsAndFailureReleasesSession()
    {
        var Manager = new SatelliteManager(); var Connection = new FakeSatellite(); Manager.Register(Connection);
        Assert.False(await Manager.AnnounceAsync("test", "hello", new Tts(), CancellationToken.None));
        Assert.Empty(Connection.Events);
        Manager.Update("test", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        var Session = Guid.NewGuid(); Manager.BeginSession("test", Session);
        Assert.False(await Manager.AnnounceAsync("test", "hello", new Tts(), CancellationToken.None));
        Manager.EndSession("test", Session);
        Connection.FailPlayback = true;
        await Assert.ThrowsAsync<IOException>(() => Manager.AnnounceAsync("test", "hello", new Tts(), CancellationToken.None));
        Assert.Equal(0, Manager.ActiveSessionCount);
        Connection.FailPlayback = false;
        Assert.True(await Manager.AnnounceAsync("test", "hello", new Tts(), CancellationToken.None));
        Assert.Equal("announcement", Connection.Events.Last().Type);
    }

    [Fact]
    public void EventHistoryIsBoundedAndAudioObjectsHaveOpaqueIdsAndSizeLimits()
    {
        var Manager = new SatelliteManager();
        for (var Index = 0; Index < 150; Index++) Manager.Record("test", Index.ToString());
        Assert.Equal(100, Manager.Events("test").Length);
        Assert.Equal("50", Manager.Events("test")[0].Type);
        using var Audio = new VoiceAudioStore();
        var First = Audio.Add([1]); var Second = Audio.Add([2]);
        Assert.NotEqual(First, Second);
        Assert.Null(Audio.Get(Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => Audio.Add(new byte[8 * 1024 * 1024 + 1]));
    }

    private sealed class Tts : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[2], 16000, 2, 1); }
    }
    private sealed class FakeSatellite : ISatelliteConnection
    {
        public string SatelliteId => "test";
        public string Name => "Test";
        public string? Area => null;
        public bool FailPlayback { get; set; }
        public List<SatelliteEvent> Events { get; } = [];
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token)
        { await foreach (var _ in Audio.WithCancellation(Token)) { } if (FailPlayback) throw new IOException(); }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { Events.Add(Event); return Task.CompletedTask; }
    }
}
