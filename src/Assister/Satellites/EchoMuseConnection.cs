using System.Text.Json;
using System.Threading.Channels;
using Assister.Contracts;
using Assister.Diagnostics;

namespace Assister.Satellites;

public interface IProviderEndpointing { bool OwnsEndpointing { get; } }

// One logical endpoint on a multiplexed controller link. Each activation has its own audio queue.
public sealed class EchoMuseConnection(string DeviceId, string Id, string Label, string? Room,
    Func<object, CancellationToken, Task> Send, VoiceAudioStore Audio, IConfiguration Configuration,
    SatelliteManager Manager) : ISatelliteConnection
{
    private VoiceAudioStore Store => Audio;
    private SatelliteManager StateManager => Manager;
    private string Device => DeviceId;
    private Task WriteAsync(object Message, CancellationToken Token) => Send(Message, Token);
    private readonly object Gate = new();
    private Playback? Announcement;
    private Turn? Voice;
    public string SatelliteId => Id;
    public string Name => Label;
    public string? Area => Room;
    public string ControllerDeviceId => DeviceId;
    public Turn? Start(string SessionId, string? WakeWord, CancellationToken Token)
    {
        lock (Gate)
        {
            if (Voice is not null || Announcement is not null || Manager.IsBusy(SatelliteId)) { return null; }
            return Voice = new(this, SessionId, WakeWord, Token);
        }
    }
    public void Release(Turn Turn) { lock (Gate) { if (ReferenceEquals(Voice, Turn)) { Voice = null; } } }
    public bool Handle(JsonElement Message)
    {
        if (Text(Message, "deviceId") != DeviceId) { return false; }
        lock (Gate)
        {
            if (Text(Message, "sessionId") is { } Session && Voice?.SessionId == Session) { return Voice.Handle(Message); }
            if (Text(Message, "requestId") is { } Request && Announcement?.Id == Request)
            {
                var Kind = Text(Message, "type");
                if (Kind == "play_started")
                { Manager.Update(Id, State => State with { CurrentPlaybackState = "Playing announcement" }); Manager.Record(Id, "Announcement playback started", TraceId: Announcement.Trace); return true; }
                if (Kind is "play_finished" or "play_failed")
                {
                    Manager.Update(Id, State => State with { CurrentPlaybackState = "Assister output idle" });
                    return Announcement.Done.TrySetResult(Kind == "play_finished" && Text(Message, "reason") != "cancelled");
                }
            }
        }
        return false;
    }
    public void Disconnect()
    {
        lock (Gate) { Voice?.Cancel(); Announcement?.Done.TrySetException(new IOException("Controller disconnected.")); }
    }
    public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new InvalidOperationException("An activation is required.");
    public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Chunks, CancellationToken Token)
    {
        var Wave = await VoiceAudioStore.Wave48kAsync(Chunks, Token);
        var AudioId = Audio.Add(Wave);
        var Operation = new Playback(Guid.NewGuid().ToString(), RunTracing.RunId == Guid.Empty ? null : RunTracing.RunId);
        lock (Gate)
        {
            if (Announcement is not null || Voice is not null) { Audio.Remove(AudioId); throw new IOException("Satellite is busy."); }
            Announcement = Operation;
        }
        try
        {
            await Send(new { type = "play", requestId = Operation.Id, deviceId = DeviceId, audioUrl = Url(AudioId), kind = "announcement" }, Token);
            if (!await Operation.Done.Task.WaitAsync(TimeSpan.FromSeconds(110), Token)) { throw new IOException("Announcement playback failed."); }
        }
        catch
        {
            await TryStopAsync(new { type = "stop", requestId = Operation.Id, deviceId = DeviceId });
            throw;
        }
        finally { lock (Gate) { if (ReferenceEquals(Announcement, Operation)) { Announcement = null; } } Audio.Remove(AudioId); }
    }
    public async Task SendEventAsync(SatelliteEvent Event, CancellationToken Token)
    {
        if (Event.Type != "stop-playback") { return; }
        object? Command;
        lock (Gate)
        {
            Command = Announcement is { } Current ? new { type = "stop", requestId = Current.Id, deviceId = DeviceId } : null;
            if (Command is null && Voice is { } Active)
            { Command = new { type = "turn_cancel", sessionId = Active.SessionId, deviceId = DeviceId }; Active.Cancel(); }
        }
        if (Command is not null) { await Send(Command, Token); }
    }
    private async Task TryStopAsync(object Command)
    {
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await Send(Command, Timeout.Token); } catch (Exception Error) when (Error is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
    }
    private string Url(Guid AudioId)
    {
        var Base = Configuration["Assister:PublicUrl"];
        if (!Uri.TryCreate(Base, UriKind.Absolute, out var Address) || Address.Scheme is not ("http" or "https") || Address.UserInfo.Length > 0)
            throw new InvalidOperationException("Device-reachable Assister public URL is required.");
        return Base!.TrimEnd('/') + "/api/voice/audio/" + AudioId + ".wav";
    }
    public static string? Text(JsonElement Message, string Key) => Message.TryGetProperty(Key, out var Value) && Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
    private sealed record Playback(string Id, Guid? Trace)
    { public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }

    public sealed class Turn : ISatelliteConnection, IVoiceActivationContext, IProviderEndpointing, IDisposable
    {
        private readonly EchoMuseConnection Owner;
        private readonly Channel<AudioChunk> Input = Channel.CreateBounded<AudioChunk>(64);
        private readonly TaskCompletionSource<string> End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> Playback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource Lifetime;
        private long Bytes;
        private string Response = "";
        private Guid? RunId;
        private Guid? VoiceSessionId;
        private bool PlaybackStarted;
        public Turn(EchoMuseConnection Owner, string SessionId, string? WakeWord, CancellationToken Token)
        {
            this.Owner = Owner; this.SessionId = SessionId;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Lifetime.CancelAfter(TimeSpan.FromSeconds(120));
            Activation = new(SessionId, WakeWord, DateTimeOffset.UtcNow);
            _ = InputDeadlineAsync();
        }
        public string SessionId { get; }
        public string SatelliteId => Owner.SatelliteId;
        public string Name => Owner.Name;
        public string? Area => Owner.Area;
        public VoiceActivation Activation { get; }
        public bool OwnsEndpointing => true;
        public CancellationToken Token => Lifetime.Token;
        public bool NoReply { get; private set; }
        public bool ControllerEnded { get; private set; }
        public Task<string> InputEnded => End.Task;
        public bool Handle(JsonElement Message)
        {
            switch (Text(Message, "type"))
            {
                case "audio":
                    if (End.Task.IsCompleted || Lifetime.IsCancellationRequested) { return false; }
                    var Data = Text(Message, "data");
                    if (Data is null || Data.Length > 87384) { Cancel(); return false; }
                    byte[] Pcm;
                    try { Pcm = Convert.FromBase64String(Data); } catch (FormatException) { Cancel(); return false; }
                    Bytes += Pcm.Length;
                    if (Pcm.Length is < 2 or > 65536 || Pcm.Length % 2 != 0 || Bytes > 640000 || !Input.Writer.TryWrite(new(Pcm, 16000, 2, 1)))
                    { Cancel(); return false; }
                    return true;
                case "audio_end":
                    var Reason = Text(Message, "reason") ?? "unknown";
                    if (!End.TrySetResult(Reason)) { return false; }
                    Owner.StateManager.Record(SatelliteId, "Audio input ended", Reason, VoiceSessionId, RunId);
                    if (Reason != "speech_end") { NoReply = true; Cancel(); }
                    else { Input.Writer.TryComplete(); }
                    return true;
                case "turn_cancel": ControllerEnded = true; Cancel(); return true;
                case "turn_finished": ControllerEnded = true; return true;
                case "play_started":
                    if (!PlaybackStarted)
                    {
                        PlaybackStarted = true;
                        if (VoiceSessionId is { } Session) Owner.StateManager.Stage(SatelliteId, Session, VoiceSessionState.PlayingResponse);
                        Owner.StateManager.Update(SatelliteId, State => State with { CurrentPlaybackState = "Playing voice response" });
                        Owner.StateManager.Record(SatelliteId, "Playback started", SessionId: VoiceSessionId, TraceId: RunId);
                    }
                    return true;
                case "play_finished":
                case "play_failed":
                    var Success = Text(Message, "type") == "play_finished";
                    if (!Success) { ControllerEnded = true; }
                    if (!Playback.TrySetResult(Success)) { return false; }
                    Owner.StateManager.Update(SatelliteId, State => State with { CurrentPlaybackState = "Assister output idle" });
                    Owner.StateManager.Record(SatelliteId, Success ? "Playback finished" : "Playback failed", SessionId: VoiceSessionId, TraceId: RunId);
                    return true;
                default: return false;
            }
        }
        public void Cancel() { Lifetime.Cancel(); Input.Writer.TryComplete(); }
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => Input.Reader.ReadAllAsync(Token);
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Chunks, CancellationToken Token)
        {
            if (await End.Task.WaitAsync(Token) != "speech_end") { throw new OperationCanceledException(Token); }
            var Wave = await VoiceAudioStore.Wave48kAsync(Chunks, Token);
            var AudioId = Owner.Store.Add(Wave);
            RunId = RunTracing.RunId;
            try
            {
                using var Delivery = RunTracing.Start("Playback", "Satellite playback", "Serve opaque WAV audio and wait for the matching controller playback acknowledgment.");
                Delivery.Metadata(new { audioReadyAt = DateTimeOffset.UtcNow, transportSessionId = SessionId, controllerDeviceId = Owner.Device });
                await Owner.WriteAsync(new { type = "turn_response", sessionId = SessionId, deviceId = Owner.Device, audioUrl = Owner.Url(AudioId), text = Response }, Token);
                if (!await Playback.Task.WaitAsync(Token)) { throw new IOException("Satellite playback failed."); }
                Delivery.Output(new { playbackAcknowledgement = "succeeded", acknowledgedAt = DateTimeOffset.UtcNow });
                Delivery.Complete();
            }
            finally { Owner.Store.Remove(AudioId); }
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token)
        {
            VoiceSessionId ??= Event.SessionId;
            RunId = RunTracing.RunId;
            if (Event.Type == "response") { Response = Event.Text ?? ""; }
            return Task.CompletedTask;
        }
        public Task SendErrorAsync(CancellationToken Token) => Owner.WriteAsync(new { type = "turn_error", sessionId = SessionId, deviceId = Owner.Device, message = "Assister could not complete this request." }, Token);
        public Task SendCancelAsync(CancellationToken Token) => Owner.WriteAsync(new { type = "turn_cancel", sessionId = SessionId, deviceId = Owner.Device }, Token);
        private async Task InputDeadlineAsync()
        {
            try { await End.Task.WaitAsync(TimeSpan.FromSeconds(20), Lifetime.Token); }
            catch (TimeoutException) { try { Cancel(); } catch (ObjectDisposedException) { } }
            catch (OperationCanceledException) { }
        }
        public void Dispose() { Lifetime.Cancel(); Lifetime.Dispose(); }
    }
}
