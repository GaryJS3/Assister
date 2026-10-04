using System.Collections.Concurrent;
using Assister.Contracts;
using Assister.Diagnostics;

namespace Assister.Satellites;

public sealed record SatelliteEvent(string Type, string? Text = null, Guid? SessionId = null, Guid? ConversationId = null);
public sealed record VoiceActivation(string TransportSessionId, string? WakeWord, DateTimeOffset ReceivedAt);
public interface IVoiceActivationContext
{
    VoiceActivation? Activation { get; }
}
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
    private readonly DiagnosticSanitizer Sanitizer;
    public SatelliteManager(IConfiguration? Configuration = null) => Sanitizer = new(Configuration);
    private readonly ConcurrentDictionary<string, ISatelliteConnection> Connections = new();
    private readonly ConcurrentDictionary<string, Guid> Sessions = new();
    private readonly ConcurrentDictionary<string, SatelliteRuntimeState> Runtime = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SatelliteHistoryEvent>> History = new();
    public int Count => Connections.Count;
    public int ActiveSessionCount => Sessions.Count;
    public bool Register(ISatelliteConnection Connection)
    {
        if (!Connections.TryAdd(Connection.SatelliteId, Connection)) { return false; }
        Update(Connection.SatelliteId, State => State with { ConnectionState = "Online", ConnectedAt = DateTimeOffset.UtcNow,
            VoiceOwnership = VoiceOwnership.Unknown, LastError = null, Activity = VoiceSessionState.Idle,
            CurrentVoiceSessionId = null, Capabilities = new(), VoiceConfiguration = new([], [], 0), CurrentPlaybackState = "Unknown" });
        Record(Connection.SatelliteId, "Connected");
        return true;
    }
    public void Disconnect(ISatelliteConnection Connection)
    {
        if (!((ICollection<KeyValuePair<string, ISatelliteConnection>>)Connections).Remove(new(Connection.SatelliteId, Connection))) { return; }
        Sessions.TryRemove(Connection.SatelliteId, out _);
        Update(Connection.SatelliteId, State => State with { ConnectionState = "Offline", VoiceOwnership = VoiceOwnership.Unknown,
            CurrentVoiceSessionId = null, Activity = VoiceSessionState.Disconnected, CurrentPlaybackState = "Unknown" }, false);
        Record(Connection.SatelliteId, "Disconnected");
    }
    public bool BeginSession(string Satellite, Guid Session) => Sessions.TryAdd(Satellite, Session);
    public void EndSession(string Satellite, Guid Session)
    {
        if (((ICollection<KeyValuePair<string, Guid>>)Sessions).Remove(new(Satellite, Session)))
            Update(Satellite, State => State with { CurrentVoiceSessionId = null }, false);
    }
    public bool TryGet(string Satellite, out ISatelliteConnection? Connection) => Connections.TryGetValue(Satellite, out Connection);
    public SatelliteRuntimeState State(string Id) => Runtime.GetValueOrDefault(Id) ?? new();
    public void Update(string Id, Func<SatelliteRuntimeState, SatelliteRuntimeState> Change, bool Seen = true)
    {
        Runtime.AddOrUpdate(Id, _ => Apply(new()), (_, State) => Apply(State));
        SatelliteRuntimeState Apply(SatelliteRuntimeState State) => Seen ? Change(State) with { LastSeen = DateTimeOffset.UtcNow } : Change(State);
    }
    public void Record(string Id, string Type, string? Detail = null, Guid? SessionId = null, Guid? TraceId = null)
    {
        var Events = History.GetOrAdd(Id, _ => new());
        Events.Enqueue(new(DateTimeOffset.UtcNow, Sanitizer.Text(Type, 128), Detail is null ? null : Sanitizer.Text(Detail, 512),
            SessionId, TraceId ?? (RunTracing.RunId == Guid.Empty ? null : RunTracing.RunId)));
        while (Events.Count > 100) { Events.TryDequeue(out _); }
    }
    public SatelliteHistoryEvent[] Events(string Id) => History.TryGetValue(Id, out var Events) ? Events.ToArray() : [];
    public void Stage(string Id, Guid Session, VoiceSessionState Stage, Guid? Conversation = null)
    {
        if (!Sessions.TryGetValue(Id, out var Current) || Current != Session) { return; }
        Update(Id, State => State with { CurrentVoiceSessionId = Session, Activity = Stage, ConversationId = Conversation ?? State.ConversationId });
        Record(Id, Stage.ToString(), SessionId: Session);
    }
    public async Task<bool> AnnounceAsync(string Id, string Text, ITextToSpeechProvider Tts, CancellationToken Token)
    {
        if (!State(Id).Capabilities.AnnouncementPlayback || !TryGet(Id, out var Connection)) { return false; }
        var Session = Guid.NewGuid();
        if (!BeginSession(Id, Session)) { return false; }
        try
        {
            using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await Connection!.SendEventAsync(new("announcement", Text, Session), Timeout.Token);
            await Connection.SendAudioAsync(Tts.SynthesizeAsync(Text, new(), Timeout.Token), Timeout.Token);
            Record(Id, "Announcement completed", SessionId: Session);
            return true;
        }
        catch (Exception)
        {
            Record(Id, "Announcement failed", SessionId: Session);
            throw;
        }
        finally { EndSession(Id, Session); }
    }
}
