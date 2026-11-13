using System.Text;
using Assister.Speech.Wyoming;

namespace Assister.Tests;

public sealed class WyomingFramingTests
{
    [Fact]
    public async Task CoalescedEventsUseBufferedReadsAndPreserveEveryFrame()
    {
        var Bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"type\":\"audio-stop\"}\n", 100)));
        using var Stream = new CountingStream(Bytes);
        var Reader = new WyomingEventReader(Stream);
        for (var Index = 0; Index < 100; Index++) Assert.Equal("audio-stop", (await Reader.ReadAsync(default))!.Type);
        Assert.Null(await Reader.ReadAsync(default));
        Assert.InRange(Stream.Reads, 1, 2);
    }

    [Fact]
    public async Task PayloadAcrossBufferBoundariesPreservesTheFollowingHeader()
    {
        var Payload = new byte[20000];
        new Random(42).NextBytes(Payload);
        using var Stream = new MemoryStream();
        var Writer = new WyomingEventWriter(Stream);
        await Writer.WriteAsync(WyomingEvent.Create("audio-chunk", Payload: Payload), default);
        await Writer.WriteAsync(WyomingEvent.Create("audio-stop"), default);
        Stream.Position = 0;
        var Reader = new WyomingEventReader(Stream);
        Assert.Equal(Payload, (await Reader.ReadAsync(default))!.Payload.ToArray());
        Assert.Equal("audio-stop", (await Reader.ReadAsync(default))!.Type);
        Assert.Null(await Reader.ReadAsync(default));
    }

    [Fact]
    public async Task FragmentedDataMergesHeaderAndPreservesPayloadAndNextEvent()
    {
        var Bytes = Encoding.UTF8.GetBytes("{\"type\":\"audio-chunk\",\"data\":{\"rate\":16000},\"data_length\":11,\"payload_length\":4}\n{\"width\":2}\u0001\u0002\u0003\u0004{\"type\":\"audio-stop\"}\n");
        using var Stream = new FragmentedStream(Bytes);
        var Reader = new WyomingEventReader(Stream);
        var First = await Reader.ReadAsync(default);
        Assert.Equal("audio-chunk", First!.Type);
        Assert.Equal(16000, First.Data.GetProperty("rate").GetInt32());
        Assert.Equal(2, First.Data.GetProperty("width").GetInt32());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, First.Payload.ToArray());
        Assert.Equal("audio-stop", (await Reader.ReadAsync(default))!.Type);
        Assert.Null(await Reader.ReadAsync(default));
    }

    [Theory]
    [InlineData("{\"type\":\"audio\",\"payload_length\":-1}\n")]
    [InlineData("{\"type\":\"audio\",\"data_length\":1048577}\n")]
    [InlineData("{}\n")]
    public async Task RejectsInvalidOrUnboundedHeaders(string Header)
    {
        using var Stream = new MemoryStream(Encoding.UTF8.GetBytes(Header));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WyomingEventReader(Stream).ReadAsync(default));
    }

    [Fact]
    public async Task TruncatedPayloadFailsClearly()
    {
        using var Stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"audio\",\"payload_length\":3}\nX"));
        await Assert.ThrowsAsync<EndOfStreamException>(() => new WyomingEventReader(Stream).ReadAsync(default));
    }

    private sealed class FragmentedStream(byte[] Bytes) : MemoryStream(Bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> Buffer, CancellationToken CancellationToken = default)
        {
            return base.ReadAsync(Buffer[..Math.Min(2, Buffer.Length)], CancellationToken);
        }
    }

    private sealed class CountingStream(byte[] Bytes) : Stream
    {
        private readonly MemoryStream Inner = new(Bytes);
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] Buffer, int Offset, int Count) { Reads++; return Inner.Read(Buffer, Offset, Count); }
        public override ValueTask<int> ReadAsync(Memory<byte> Buffer, CancellationToken Token = default)
        { Reads++; return Inner.ReadAsync(Buffer, Token); }
        public override Task<int> ReadAsync(byte[] Buffer, int Offset, int Count, CancellationToken Token)
            => ReadAsync(Buffer.AsMemory(Offset, Count), Token).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long Offset, SeekOrigin Origin) => throw new NotSupportedException();
        public override void SetLength(long Value) => throw new NotSupportedException();
        public override void Write(byte[] Buffer, int Offset, int Count) => throw new NotSupportedException();
        protected override void Dispose(bool Disposing) { if (Disposing) Inner.Dispose(); base.Dispose(Disposing); }
    }
}
