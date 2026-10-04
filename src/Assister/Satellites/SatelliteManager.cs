using System.Collections.Concurrent;
using Assister.Contracts;

namespace Assister.Satellites;

public sealed record SatelliteEvent(string Type, string? Text = null, Guid? SessionId = null, Guid? ConversationId = null);
public interface ISatelliteConnection
{
    string SatelliteId { get; }
    string Name { get; }
    string? Area { get; }
    IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken CancellationToken);
    Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken CancellationToken);
    Task SendEventAsync(SatelliteEvent Event, CancellationToken CancellationToken);
}

public sealed class SatelliteManager
{
    private readonly ConcurrentDictionary<string, ISatelliteConnection> Connections = new();
    private readonly ConcurrentDictionary<string, Guid> Sessions = new();
    public int Count => Connections.Count;
    public int ActiveSessionCount => Sessions.Count;
    public bool Register(ISatelliteConnection Connection) => Connections.TryAdd(Connection.SatelliteId, Connection);
    public void Disconnect(ISatelliteConnection Connection) => ((ICollection<KeyValuePair<string, ISatelliteConnection>>)Connections).Remove(new(Connection.SatelliteId, Connection));
    public bool BeginSession(string Satellite, Guid Session) => Sessions.TryAdd(Satellite, Session);
    public void EndSession(string Satellite, Guid Session) => ((ICollection<KeyValuePair<string, Guid>>)Sessions).Remove(new(Satellite, Session));
    public bool TryGet(string Satellite, out ISatelliteConnection? Connection) => Connections.TryGetValue(Satellite, out Connection);
}
