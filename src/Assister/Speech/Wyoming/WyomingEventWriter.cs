using System.Text.Json;

namespace Assister.Speech.Wyoming;

public sealed class WyomingEventWriter(Stream Stream)
{
    public async Task WriteAsync(WyomingEvent Event, CancellationToken CancellationToken)
    {
        var Data = JsonSerializer.SerializeToUtf8Bytes(Event.Data);
        if (Data.Length > WyomingEventReader.MaxDataBytes || Event.Payload.Length > WyomingEventReader.MaxPayloadBytes)
        {
            throw new InvalidDataException("Wyoming event exceeds limit.");
        }
        var Header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = Event.Type,
            data_length = Data.Length,
            payload_length = Event.Payload.Length
        });
        await Stream.WriteAsync(Header, CancellationToken);
        await Stream.WriteAsync(new byte[] { 10 }, CancellationToken);
        await Stream.WriteAsync(Data, CancellationToken);
        await Stream.WriteAsync(Event.Payload, CancellationToken);
        await Stream.FlushAsync(CancellationToken);
    }
}
