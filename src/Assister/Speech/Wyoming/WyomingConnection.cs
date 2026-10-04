using System.Net.Sockets;
using System.Text.Json;

namespace Assister.Speech.Wyoming;

public sealed record WyomingEndpoint(string Host, int Port, int TimeoutSeconds = 30);
public sealed record WyomingServiceInfo(JsonElement Data)
{
    public bool Supports(string Service)
    {
        return Data.TryGetProperty(Service, out var Services) &&
            Services.ValueKind == JsonValueKind.Array && Services.GetArrayLength() > 0;
    }
}

public sealed class WyomingConnection : IAsyncDisposable
{
    private readonly TcpClient Client;
    private readonly NetworkStream Stream;
    public WyomingEventReader Reader { get; }
    public WyomingEventWriter Writer { get; }

    private WyomingConnection(TcpClient Client)
    {
        this.Client = Client;
        // NetworkStream supports one reader and one writer concurrently for streaming TTS.
        Stream = Client.GetStream();
        Reader = new(Stream);
        Writer = new(Stream);
    }

    public static async Task<WyomingConnection> ConnectAsync(WyomingEndpoint Endpoint, CancellationToken CancellationToken)
    {
        var Client = new TcpClient { NoDelay = true };
        try
        {
            await Client.ConnectAsync(Endpoint.Host, Endpoint.Port, CancellationToken);
            return new(Client);
        }
        catch
        {
            Client.Dispose();
            throw;
        }
    }

    public async Task<WyomingServiceInfo> DescribeAsync(CancellationToken CancellationToken)
    {
        await Writer.WriteAsync(WyomingEvent.Create("describe"), CancellationToken);
        var Event = await Reader.ReadAsync(CancellationToken);
        if (Event?.Type != "info")
        {
            throw new InvalidDataException("Wyoming endpoint did not return info.");
        }
        return new(Event.Data);
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        Client.Dispose();
    }
}
