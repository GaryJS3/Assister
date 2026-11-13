using System.Threading.Channels;
using Assister.Contracts;
using Assister.Satellites;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class AnnouncementQueueTests
{
    [Fact]
    public async Task BusyDestinationDoesNotBlockOtherDestinations()
    {
        await using var Fixture = new QueueFixture();
        var Busy = Fixture.Add("busy");
        var Ready = Fixture.Add("ready");
        var Session = Guid.NewGuid();
        Assert.True(Fixture.Manager.BeginSession("busy", Session));
        Assert.True(Fixture.Queue.Enqueue("busy", "First"));
        Assert.True(Fixture.Queue.Enqueue("ready", "Second"));
        await Fixture.Queue.StartAsync(default);
        Assert.Equal("Second", await Ready.NextAsync());
        Assert.False(Busy.Started.Reader.TryRead(out _));
        Fixture.Manager.EndSession("busy", Session);
        Assert.Equal("First", await Busy.NextAsync());
    }

    [Fact]
    public async Task SlowPlaybackPreservesDestinationOrderWithoutBlockingPeers()
    {
        await using var Fixture = new QueueFixture();
        var Slow = Fixture.Add("slow", Block: true);
        var Ready = Fixture.Add("ready");
        Assert.True(Fixture.Queue.Enqueue("slow", "First"));
        Assert.True(Fixture.Queue.Enqueue("slow", "Second"));
        Assert.True(Fixture.Queue.Enqueue("ready", "Independent"));
        await Fixture.Queue.StartAsync(default);
        Assert.Equal("First", await Slow.NextAsync());
        Assert.Equal("Independent", await Ready.NextAsync());
        Assert.False(Slow.Started.Reader.TryRead(out _));
        Slow.Release.TrySetResult();
        Assert.Equal("Second", await Slow.NextAsync());
    }

    [Fact]
    public async Task CapacityIncludesActiveAndDeferredDeliveries()
    {
        await using var Fixture = new QueueFixture();
        var Slow = Fixture.Add("slow", Block: true);
        for (var Index = 0; Index < 100; Index++) Assert.True(Fixture.Queue.Enqueue("slow", Index.ToString()));
        Assert.False(Fixture.Queue.Enqueue("slow", "Overflow"));
        await Fixture.Queue.StartAsync(default);
        Assert.Equal("0", await Slow.NextAsync());
        Assert.False(Fixture.Queue.Enqueue("slow", "Still full"));
        await Fixture.Queue.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Fixture.Manager.IsBusy("slow"));
        Assert.False(Fixture.Queue.Enqueue("slow", "Stopped"));
    }

    [Fact]
    public async Task AtMostFourDestinationsPlayConcurrently()
    {
        await using var Fixture = new QueueFixture();
        var Speakers = Enumerable.Range(0, 6).Select(Index => Fixture.Add(Index.ToString(), Block: true)).ToArray();
        foreach (var Speaker in Speakers) Assert.True(Fixture.Queue.Enqueue(Speaker.SatelliteId, "Hello"));
        await Fixture.Queue.StartAsync(default);
        foreach (var Speaker in Speakers.Take(4)) Assert.Equal("Hello", await Speaker.NextAsync());
        foreach (var Speaker in Speakers.Skip(4)) Assert.False(Speaker.Started.Reader.TryRead(out _));
        foreach (var Speaker in Speakers.Take(4)) Speaker.Release.TrySetResult();
        foreach (var Speaker in Speakers.Skip(4)) Assert.Equal("Hello", await Speaker.NextAsync());
    }

    [Fact]
    public async Task ExpiredBusyWorkReleasesCapacityAndIsNotPlayed()
    {
        var Clock = new TestClock();
        await using var Fixture = new QueueFixture(Clock);
        var Busy = Fixture.Add("busy");
        var Ready = Fixture.Add("ready");
        Assert.True(Fixture.Manager.BeginSession("busy", Guid.NewGuid()));
        Assert.True(Fixture.Queue.Enqueue("busy", "Expired"));
        Clock.Advance();
        Assert.True(Fixture.Queue.Enqueue("ready", "Current"));
        await Fixture.Queue.StartAsync(default);
        Assert.Equal("Current", await Ready.NextAsync());
        Assert.Contains(Fixture.Manager.Events("busy"), Item => Item.Type == "Queued announcement unavailable");
        Assert.False(Busy.Started.Reader.TryRead(out _));
        Assert.True(Fixture.Queue.Enqueue("ready", "More"));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance() => Now = Now.AddMinutes(3);
    }

    private sealed class QueueFixture : IAsyncDisposable
    {
        private readonly ServiceProvider Services = new ServiceCollection().AddTransient<ITextToSpeechProvider, Speech>().BuildServiceProvider();
        public SatelliteManager Manager { get; } = new();
        public AnnouncementQueue Queue { get; }
        public QueueFixture(TimeProvider? Clock = null)
            => Queue = new(Manager, Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AnnouncementQueue>.Instance, Clock);
        public Speaker Add(string Id, bool Block = false)
        {
            var Speaker = new Speaker(Id);
            if (!Block) Speaker.Release.TrySetResult();
            Manager.Register(Speaker);
            Manager.Update(Id, State => State with { CurrentVolume = .35, Capabilities = new() { AnnouncementPlayback = true, VolumeControl = true } });
            return Speaker;
        }
        public async ValueTask DisposeAsync()
        {
            await Queue.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Queue.Dispose();
            await Services.DisposeAsync();
        }
    }

    private sealed class Speech : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        {
            await Task.Yield();
            Token.ThrowIfCancellationRequested();
            yield return new(new byte[2], 16000, 2, 1);
        }
    }

    private sealed class Speaker(string Id) : ISatelliteConnection
    {
        public string SatelliteId => Id;
        public string Name => Id;
        public string? Area => null;
        public Channel<string> Started { get; } = Channel.CreateUnbounded<string>();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> NextAsync() => Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Chunks, CancellationToken Token)
        {
            await foreach (var Chunk in Chunks.WithCancellation(Token)) { }
            await Release.Task.WaitAsync(Token);
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token)
        {
            if (Event.Type == "announcement") Started.Writer.TryWrite(Event.Text!);
            return Task.CompletedTask;
        }
    }
}
