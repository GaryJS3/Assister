using Assister.Contracts;
using Assister.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class RunAudioTests : IDisposable
{
    private readonly string DataPath = Path.Combine(Path.GetTempPath(), "assister-audio-" + Guid.NewGuid());
    private readonly TestClock Clock = new();
    private RunAudioStore Open(params (string Key, string Value)[] Settings)
    {
        var Values = Settings.ToDictionary(Item => Item.Key, Item => (string?)Item.Value);
        Values["Assister:DataPath"] = DataPath;
        return new(new ConfigurationBuilder().AddInMemoryCollection(Values).Build(), NullLogger<RunAudioStore>.Instance, Clock);
    }
    private static async IAsyncEnumerable<AudioChunk> Chunks(byte[] Bytes, bool Fail = false)
    {
        yield return new(Bytes, 16000, 2, 1);
        await Task.Yield();
        if (Fail) throw new IOException("test stream ended early");
    }
    private static async Task Drain(IAsyncEnumerable<AudioChunk> Source)
    { await foreach (var _ in Source) { } }

    [Fact]
    public async Task BothRecordingsPersistAsWaveAndExpireIndependentlyOfTraceMetadata()
    {
        var Id = Guid.NewGuid();
        using (var Store = Open())
        {
            await Drain(Store.ObserveAsync(Id, "input", Chunks([1, 2]), default));
            await Drain(Store.ObserveAsync(Id, "output", Chunks([3, 4]), default));
            Assert.Equal(new byte[] { 1, 2 }, Store.Get(Id, "input")![44..]);
            Assert.Equal(new byte[] { 3, 4 }, Store.Get(Id, "output")![44..]);
            Assert.Equal("available", Store.Info(Id, "input").State);
            Assert.False(Store.Info(Id, "input").Partial);
        }
        using var Reopened = Open();
        Assert.True(Reopened.Get(Id, "output")!.AsSpan().StartsWith("RIFF"u8));
        Clock.Now += TimeSpan.FromHours(25);
        Assert.Null(Reopened.Get(Id, "input"));
        Assert.Equal("expired", Reopened.Info(Id, "output").State);
        Reopened.Prune();
        Assert.Equal(0, Reopened.Info(Id, "output").Size);
    }

    [Fact]
    public async Task FailedStreamsRetainPlayablePrefixAndPreserveTheOriginalFailure()
    {
        using var Store = Open();
        var Id = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => Drain(Store.ObserveAsync(Id, "input", Chunks([1, 2], Fail: true), default)));
        Assert.True(Store.Info(Id, "input").Partial);
        Assert.Equal(new byte[] { 1, 2 }, Store.Get(Id, "input")![44..]);
    }

    [Fact]
    public async Task DisabledAndOversizedCaptureNeverInterruptTheSource()
    {
        var Id = Guid.NewGuid();
        using (var Store = Open(("Diagnostics:CaptureAudio", "false")))
        {
            await Drain(Store.ObserveAsync(Id, "input", Chunks([1, 2]), default));
            Assert.Equal("disabled", Store.Info(Id, "input").State);
            Assert.Null(Store.Get(Id, "input"));
        }
        using var Enabled = Open();
        await Drain(Enabled.ObserveAsync(Id, "output", Chunks(new byte[RunAudioStore.MaximumPcmBytes + 2]), default));
        Assert.Equal("too-large", Enabled.Info(Id, "output").State);
        Assert.Null(Enabled.Get(Id, "output"));
    }

    [Fact]
    public async Task StorageBudgetEvictsOldestAudioAndKeepsRecentRecordings()
    {
        using var Store = Open(("Diagnostics:MaxAudioMiB", "16"));
        var Ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var Id in Ids)
        {
            await Drain(Store.ObserveAsync(Id, "input", Chunks(new byte[6 * 1024 * 1024]), default));
            Clock.Now += TimeSpan.FromSeconds(1);
        }
        Assert.Equal("expired", Store.Info(Ids[0], "input").State);
        Assert.Null(Store.Get(Ids[0], "input"));
        Assert.NotNull(Store.Get(Ids[1], "input"));
        Assert.NotNull(Store.Get(Ids[2], "input"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PcmWaveHeadersDescribeFormatAndPadOddDataLengths(int Width)
    {
        var Wave = RunAudioStore.Wave(new byte[Width], 22050, Width, 1);
        Assert.Equal(Width * 8, BitConverter.ToInt16(Wave, 34));
        Assert.Equal(22050, BitConverter.ToInt32(Wave, 24));
        Assert.Equal(Width, BitConverter.ToInt32(Wave, 40));
        Assert.Equal(44 + Width + (Width & 1), Wave.Length);
        Assert.Equal(Wave.Length - 8, BitConverter.ToInt32(Wave, 4));
    }

    public void Dispose() { if (Directory.Exists(DataPath)) Directory.Delete(DataPath, recursive: true); }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
