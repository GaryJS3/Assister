using System.Text;
using Assister.Speech.Wyoming;

namespace Assister.Tests;

public sealed class WyomingFramingTests
{
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
}
