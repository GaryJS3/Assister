using System.Text.Json;

namespace Assister.Speech.Wyoming;

public sealed record WyomingEvent(string Type, JsonElement Data, ReadOnlyMemory<byte> Payload)
{
    public static WyomingEvent Create(string Type, object? Data = null, ReadOnlyMemory<byte> Payload = default)
    {
        return new(Type, JsonSerializer.SerializeToElement(Data ?? new { }), Payload);
    }
}
