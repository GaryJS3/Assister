using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assister.Speech.Wyoming;

// One reader owns a buffered stream for the lifetime of the connection.
// ReadExactlyAsync handles fragmented TCP frames; remaining bytes stay buffered.
public sealed class WyomingEventReader(Stream Stream)
{
    public const int MaxHeaderBytes = 65536;
    public const int MaxDataBytes = 1048576;
    public const int MaxPayloadBytes = 4194304;

    public async Task<WyomingEvent?> ReadAsync(CancellationToken CancellationToken)
    {
        using var Header = new MemoryStream();
        var Byte = new byte[1];
        while (true)
        {
            var Count = await Stream.ReadAsync(Byte, CancellationToken);
            if (Count == 0)
            {
                if (Header.Length == 0)
                {
                    return null;
                }
                throw new EndOfStreamException("Incomplete Wyoming header.");
            }
            if (Byte[0] == 10)
            {
                break;
            }
            if (Header.Length >= MaxHeaderBytes)
            {
                throw new InvalidDataException("Wyoming header exceeds limit.");
            }
            Header.WriteByte(Byte[0]);
        }

        var Root = JsonNode.Parse(Header.ToArray())?.AsObject()
            ?? throw new InvalidDataException("Missing Wyoming header.");
        var Type = Root["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(Type))
        {
            throw new InvalidDataException("Missing Wyoming event type.");
        }
        var DataLength = ReadLength(Root, "data_length", MaxDataBytes);
        var PayloadLength = ReadLength(Root, "payload_length", MaxPayloadBytes);
        var Data = Root["data"]?.AsObject() ?? new JsonObject();
        if (DataLength > 0)
        {
            var Bytes = new byte[DataLength];
            await Stream.ReadExactlyAsync(Bytes, CancellationToken);
            var Extra = JsonNode.Parse(Bytes)?.AsObject()
                ?? throw new InvalidDataException("Invalid Wyoming data.");
            foreach (var Pair in Extra)
            {
                Data[Pair.Key] = Pair.Value?.DeepClone();
            }
        }
        var Payload = new byte[PayloadLength];
        await Stream.ReadExactlyAsync(Payload, CancellationToken);
        return new WyomingEvent(Type, JsonSerializer.SerializeToElement(Data), Payload);
    }

    private static int ReadLength(JsonObject Header, string Name, int Maximum)
    {
        var Length = Header[Name]?.GetValue<int>() ?? 0;
        if (Length < 0 || Length > Maximum)
        {
            throw new InvalidDataException($"Invalid Wyoming {Name}.");
        }
        return Length;
    }
}
