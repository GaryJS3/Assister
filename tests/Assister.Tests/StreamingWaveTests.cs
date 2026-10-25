using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Satellites;

namespace Assister.Tests;

public sealed class StreamingWaveTests
{
    [Fact]
    public async Task HttpConsumptionRetainsProducerDiagnosticContext()
    {
        var Context = new AsyncLocal<string?> { Value = "voice-run" };
        async IAsyncEnumerable<AudioChunk> Source()
        {
            Assert.Equal("voice-run", Context.Value);
            await Task.Yield();
            Assert.Equal("voice-run", Context.Value);
            yield return new(new byte[8], 48000, 2, 1);
        }
        using var Store = new VoiceAudioStore();
        var Stream = Store.GetStream(Store.AddStream(Source(), default))!;
        Context.Value = "http-request";
        Assert.True(await Stream.WriteAsync(System.IO.Stream.Null, default));
        Assert.Equal("http-request", Context.Value);
    }

    [Theory]
    [InlineData(8000, 1)] [InlineData(16000, 2)] [InlineData(22050, 1)]
    [InlineData(44100, 2)] [InlineData(48000, 1)] [InlineData(96000, 2)]
    public async Task ArbitraryChunkBoundariesMatchBufferedResampling(int Rate, int Channels)
    {
        var Pcm = new byte[137 * Channels * 2];
        new Random(17).NextBytes(Pcm);
        async IAsyncEnumerable<AudioChunk> Source(bool Split)
        {
            for (var Frame = 0; Frame < 137;)
            {
                var Count = Math.Min(Split ? 7 : 137, 137 - Frame);
                yield return new(Pcm.AsMemory(Frame * Channels * 2, Count * Channels * 2), Rate, 2, Channels);
                Frame += Count;
            }
            await Task.CompletedTask;
        }
        var Buffered = await VoiceAudioStore.Wave48kAsync(Source(false), default);
        using var Streamed = new MemoryStream();
        await foreach (var Bytes in StreamingWave.Encode48kAsync(Source(true), default)) Streamed.Write(Bytes.Span);
        var Result = Streamed.ToArray();
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(Result.AsSpan(4)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(Result.AsSpan(40)));
        Assert.Equal(Buffered[44..], Result[44..]);
    }

    [Fact]
    public async Task StreamWritesPcmBeforeSynthesisFinishesAndAllowsOnlyOneConsumer()
    {
        using var Store = new VoiceAudioStore();
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AudioChunk> Source([EnumeratorCancellation] CancellationToken Token = default)
        {
            yield return new(new byte[960], 48000, 2, 1);
            Reached.SetResult();
            await Release.Task.WaitAsync(Token);
            yield return new(new byte[960], 48000, 2, 1);
        }
        var Id = Store.AddStream(Source(), Deadline.Token);
        var Audio = Store.GetStream(Id)!;
        using var Output = new MemoryStream();
        var Write = Audio.WriteAsync(Output, Deadline.Token);
        await Reached.Task.WaitAsync(Deadline.Token);
        Assert.True(Output.Length > 44);
        Assert.False(Audio.Completion.IsCompleted);
        Assert.False(await Audio.WriteAsync(Stream.Null, Deadline.Token));
        Release.SetResult();
        Assert.True(await Write);
        await Audio.Completion;
        Store.Remove(Id);
        Assert.Null(Store.GetStream(Id));
    }

    [Fact]
    public async Task DisconnectCancelsSynthesisAndFailsCompletion()
    {
        using var Store = new VoiceAudioStore();
        using var Abort = new CancellationTokenSource();
        var Reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Disposed = false;
        async IAsyncEnumerable<AudioChunk> Source([EnumeratorCancellation] CancellationToken Token = default)
        {
            try
            {
                yield return new(new byte[960], 48000, 2, 1);
                Reading.SetResult();
                await Task.Delay(Timeout.Infinite, Token);
            }
            finally { Disposed = true; }
        }
        var Id = Store.AddStream(Source(), default);
        var Audio = Store.GetStream(Id)!;
        var Write = Audio.WriteAsync(Stream.Null, Abort.Token);
        await Reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Audio.Completion);
        Assert.True(Disposed);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task InvalidOrEmptyAudioCannotCompleteSuccessfully(bool Empty)
    {
        async IAsyncEnumerable<AudioChunk> Source()
        { if (!Empty) yield return new(new byte[3], 22050, 2, 1); await Task.CompletedTask; }
        using var Store = new VoiceAudioStore();
        var Audio = Store.GetStream(Store.AddStream(Source(), default))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => Audio.WriteAsync(Stream.Null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => Audio.Completion);
    }
}
