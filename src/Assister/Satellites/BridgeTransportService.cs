using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Assister.Contracts;
using Assister.Satellites.Protocol;
using Assister.Voice;
using Assister.Diagnostics;
using Grpc.Core;

namespace Assister.Satellites;

public sealed class BridgeTransportService(SatelliteManager Manager, IServiceScopeFactory Scopes, VoiceAudioStore Audio,
    IConfiguration Configuration, ILogger<BridgeTransportService> Logger) : SatelliteTransport.SatelliteTransportBase
{
    public override async Task Connect(IAsyncStreamReader<BridgeFrame> Requests, IServerStreamWriter<BridgeFrame> Responses, ServerCallContext Context)
    {
        var Secret = Configuration["SatelliteBridge:Token"];
        var Supplied = Context.RequestHeaders.FirstOrDefault(Header => Header.Key == "authorization")?.Value;
        if (string.IsNullOrWhiteSpace(Secret) || Supplied is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes("Bearer " + Secret), Encoding.UTF8.GetBytes(Supplied)))
        { throw new RpcException(new(StatusCode.Unauthenticated, "Bridge authentication required.")); }
        using var Lifetime = CancellationTokenSource.CreateLinkedTokenSource(Context.CancellationToken);
        if (!await Requests.MoveNext(Lifetime.Token) || Requests.Current.Type != "register") { throw new RpcException(new(StatusCode.InvalidArgument, "Register first.")); }
        var Registration = Requests.Current;
        if (Registration.SatelliteId.Length is < 1 or > 128 || Registration.Name.Length > 128 || Registration.Area.Length > 128)
        { throw new RpcException(new(StatusCode.PermissionDenied, "Satellite is not configured.")); }
        await using (var Scope = Scopes.CreateAsyncScope())
        {
            var Database = Scope.ServiceProvider.GetRequiredService<Assister.Persistence.AssisterDbContext>();
            if (await Database.Satellites.FindAsync([Registration.SatelliteId], Lifetime.Token) is not { Enabled: true, ProviderType: "ESPHome" } Device)
                throw new RpcException(new(StatusCode.PermissionDenied, "Satellite is disabled or unknown."));
            // Identity and room policy come from Assister, never from provider registration.
            Registration.Name = Device.Name;
            Registration.Area = Device.AreaId ?? "";
        }
        var Outgoing = Channel.CreateBounded<BridgeFrame>(64);
        var Connection = new BridgeConnection(Registration, Outgoing.Writer, Audio, Configuration);
        if (!Manager.Register(Connection)) { throw new RpcException(new(StatusCode.AlreadyExists, "Satellite already connected.")); }
        var Sender = SendAsync();
        CancellationTokenSource? SessionCancellation = null;
        Task? Session = null;
        string? TransportSession = null;
        var SeenSessions = new HashSet<string>(StringComparer.Ordinal);
        var RecentSessions = new Queue<string>();
        try
        {
            await Outgoing.Writer.WriteAsync(new() { Type = "registered", SatelliteId = Connection.SatelliteId }, Lifetime.Token);
            while (await Requests.MoveNext(Lifetime.Token))
            {
                var Frame = Requests.Current;
                if (Frame.Pcm.Length > 65536) { throw new RpcException(new(StatusCode.ResourceExhausted, "Audio chunk too large.")); }
                if (Frame.Type == "heartbeat") { Manager.Update(Connection.SatelliteId, State => State); continue; }
                if (Frame.Type == "device-log")
                {
                    Manager.Record(Connection.SatelliteId, "ESPHome log", new DiagnosticSanitizer(Configuration).Text(Frame.Text, 512));
                    continue;
                }
                if (Frame.Type is "metadata" or "configuration" or "ownership" or "media-state" or "device-error")
                {
                    ApplyState(Frame);
                    if (Frame.Type is "configuration" or "ownership")
                    {
                        await using var Scope = Scopes.CreateAsyncScope();
                        await Scope.ServiceProvider.GetRequiredService<SatelliteConfiguration>().ReconcileAsync(Connection.SatelliteId, Connection, Lifetime.Token,
                            Frame.Type == "ownership" && Frame.Ownership == "OwnedByAssister");
                    }
                    continue;
                }
                if (Frame.Type == "start")
                {
                    if (string.IsNullOrWhiteSpace(Frame.SessionId) || Frame.SessionId.Length > 128 || SeenSessions.Contains(Frame.SessionId)) { continue; }
                    if (Session is { IsCompleted: false })
                    {
                        Manager.Record(Connection.SatelliteId, "Duplicate session rejected");
                        continue;
                    }
                    SessionCancellation?.Dispose();
                    SeenSessions.Add(Frame.SessionId);
                    RecentSessions.Enqueue(Frame.SessionId);
                    if (RecentSessions.Count > 64) { SeenSessions.Remove(RecentSessions.Dequeue()); }
                    SessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                    TransportSession = Frame.SessionId;
                    Connection.TransportSession = Frame.SessionId;
                    Connection.StartAudio();
                    Manager.Update(Connection.SatelliteId, State => State with { VoiceOwnership = VoiceOwnership.OwnedByAssister, WakeWord = Frame.WakeWord });
                    Manager.Record(Connection.SatelliteId, "Voice session requested", Frame.WakeWord.Length <= 128 ? Frame.WakeWord : null);
                    Session = RunAsync(Frame.ConversationId, SessionCancellation.Token);
                }
                else if (Frame.SessionId == TransportSession)
                {
                    if (Frame.Type == "audio")
                    {
                        if (Frame.SampleRate != 16000 || Frame.SampleWidth != 2 || Frame.Channels != 1 || Frame.Pcm.Length % 2 != 0)
                        { throw new RpcException(new(StatusCode.InvalidArgument, "Expected 16kHz mono S16 PCM.")); }
                        Connection.WriteAudio(new(Frame.Pcm.ToByteArray(), 16000, 2, 1));
                        if (Frame.SourceChannels is 1 or 2 && Manager.State(Connection.SatelliteId).MicrophoneSourceChannels != Frame.SourceChannels)
                            Manager.Update(Connection.SatelliteId, State => State with { MicrophoneSourceChannels = Frame.SourceChannels });
                    }
                    else if (Frame.Type == "stop") { Connection.StopAudio(); }
                    else if (Frame.Type == "cancel") { SessionCancellation?.Cancel(); Connection.StopAudio(); }
                }
                if (Frame.Type == "playback-started" && Connection.IsCurrentPlayback(Frame.PlaybackId, Frame.SessionId))
                {
                    var Current = Manager.State(Connection.SatelliteId).CurrentVoiceSessionId;
                    if (Current is { } Id) { Manager.Stage(Connection.SatelliteId, Id, VoiceSessionState.PlayingResponse); }
                }
                if (Frame.Type == "playback-finished") { Connection.PlaybackFinished(Frame.PlaybackId, Frame.SessionId, Frame.Text == "succeeded"); }
            }
        }
        finally
        {
            Lifetime.Cancel();
            SessionCancellation?.Cancel();
            Connection.StopAudio();
            Outgoing.Writer.TryComplete();
            if (Session is not null) { try { await Session; } catch (OperationCanceledException) { } }
            try { await Sender; } catch (OperationCanceledException) { } catch (RpcException) { }
            SessionCancellation?.Dispose();
            Manager.Disconnect(Connection);
        }
        void ApplyState(BridgeFrame Frame)
        {
            Manager.Update(Connection.SatelliteId, State =>
            {
                if (Frame.Type == "ownership" && Enum.TryParse<VoiceOwnership>(Frame.Ownership, out var Ownership))
                    return State with { VoiceOwnership = Ownership, LastError = Ownership == VoiceOwnership.Conflict
                        ? "Voice assistant channel is unavailable. Another ESPHome API client appears to own it. Home Assistant Assist Satellite may still be active for this device." : null };
                if (Frame.Configuration is { } Config)
                    return State with { VoiceConfiguration = new(Config.AvailableWakeWords.Take(32).Select(Word => new WakeWord(Word.Id, Word.Name)).ToArray(),
                        Config.ActiveWakeWords.Take(32).ToArray(), Config.MaxActiveWakeWords),
                        Capabilities = State.Capabilities with { WakeWord = Config.AvailableWakeWords.Count > 0, WakeWordConfiguration = true } };
                if (Frame.Type == "media-state") return State with { CurrentPlaybackState = Frame.Text, CurrentVolume = Frame.Volume, MuteState = Frame.Muted };
                if (Frame.Type == "device-error") return State with { LastError = "Device operation failed." };
                if (Frame.Device is { } Device && Frame.Capabilities is { } Cap)
                    return State with { DeviceName = Device.DeviceName, Model = Device.Model, FirmwareVersion = Device.FirmwareVersion,
                        ESPHomeVersion = Device.EsphomeVersion, ApiVersion = Device.ApiVersion,
                        Capabilities = new() { Microphone = Cap.Microphone, MultiChannelMicrophone = Cap.MultiChannelMicrophone,
                            VoiceAssistant = Cap.VoiceAssistant, ApiAudio = Cap.ApiAudio, WakeWord = Cap.WakeWord,
                            WakeWordConfiguration = Cap.WakeWordConfiguration, Speaker = Cap.Speaker, MediaPlayer = Cap.MediaPlayer,
                            MediaPlayback = Cap.MediaPlayback, AnnouncementPlayback = Cap.AnnouncementPlayback,
                            VolumeControl = Cap.VolumeControl, MuteControl = Cap.MuteControl, Timers = Cap.Timers, StartConversation = Cap.StartConversation } };
                return State;
            });
            Manager.Record(Connection.SatelliteId, Frame.Type);
        }
        async Task SendAsync()
        {
            await foreach (var Frame in Outgoing.Reader.ReadAllAsync(Lifetime.Token)) { await Responses.WriteAsync(Frame, Lifetime.Token); }
        }
        async Task RunAsync(string Conversation, CancellationToken Token)
        {
            try
            {
                await using var Scope = Scopes.CreateAsyncScope();
                var Pipeline = Scope.ServiceProvider.GetRequiredService<VoicePipeline>();
                var Result = await Pipeline.RunAsync(Connection, Guid.TryParse(Conversation, out var Id) ? Id : null, Token);
                await Outgoing.Writer.WriteAsync(new() { Type = "session-result", Text = Result.Outcome,
                    SessionId = Connection.TransportSession, ConversationId = Result.Session.ConversationId?.ToString() ?? "" }, Token);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                if (!Lifetime.IsCancellationRequested)
                    await Outgoing.Writer.WriteAsync(new() { Type = "session-result", Text = "cancelled", SessionId = Connection.TransportSession }, Lifetime.Token);
            }
            catch (Exception Error)
            {
                Logger.LogWarning("Satellite session failed ({FailureType}).", Error.GetType().Name);
                await Outgoing.Writer.WriteAsync(new() { Type = "session-result", Text = "failed" }, Lifetime.Token);
            }
        }
    }

    private sealed class BridgeConnection(BridgeFrame Registration, ChannelWriter<BridgeFrame> Outgoing, VoiceAudioStore Audio,
        IConfiguration Configuration) : ISatelliteConnection
    {
        private Channel<AudioChunk>? Incoming;
        private bool Receiving;
        private bool Announcement;
        private TaskCompletionSource<bool>? Playback;
        private string PlaybackId = "";
        private string PlaybackSession = "";
        public bool IsCurrentPlayback(string Id, string Session) => Playback is not null && Session == PlaybackSession && Id == PlaybackId;
        public string SatelliteId => Registration.SatelliteId;
        public string TransportSession { get; set; } = "";
        public string Name => Registration.Name;
        public string? Area => Registration.Area;
        public void StartAudio() { Incoming = Channel.CreateBounded<AudioChunk>(128); Receiving = true; }
        public void WriteAudio(AudioChunk Chunk)
        {
            if (Receiving && Incoming is not null && !Incoming.Writer.TryWrite(Chunk))
            { Incoming.Writer.TryComplete(new IOException("Microphone buffer overflow.")); Receiving = false; }
        }
        public void StopAudio() { Receiving = false; Incoming?.Writer.TryComplete(); }
        public void PlaybackFinished(string Id, string Session, bool Succeeded)
        {
            if (Session == PlaybackSession && (Id == PlaybackId || (Id.Length == 0 && !Announcement))) { Playback?.TrySetResult(Succeeded); }
        }
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken CancellationToken) => Incoming?.Reader.ReadAllAsync(CancellationToken)
            ?? throw new InvalidOperationException();
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Chunks, CancellationToken CancellationToken)
        {
            var Wave = await VoiceAudioStore.WaveAsync(Chunks, CancellationToken);
            var UseFlac = Configuration.GetValue("SatelliteBridge:UseFlac", true);
            byte[] Data;
            using (var Encoding = RunTracing.Start("Playback", "Audio encoding", "Encode synthesized PCM for satellite delivery."))
            {
                Data = UseFlac ? await VoiceAudioEncoder.FlacAsync(Wave, CancellationToken) : Wave;
                Encoding.Output(new { encoding = UseFlac ? "FLAC" : "WAV", byteCount = Data.Length,
                    sampleRate = UseFlac ? 48000 : (int?)null, channels = UseFlac ? 1 : (int?)null });
                Encoding.Complete();
            }
            var Id = Audio.Add(Data);
            var Base = Configuration["Assister:PublicUrl"] ?? throw new InvalidOperationException("Public audio URL is required.");
            Playback = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaybackId = Guid.NewGuid().ToString();
            PlaybackSession = Announcement ? "" : TransportSession;
            using var Delivery = RunTracing.Start("Playback", "Satellite playback", "Publish audio-ready and wait for the satellite's explicit playback acknowledgement.");
            Delivery.Metadata(new { audioReadyAt = DateTimeOffset.UtcNow });
            await Outgoing.WriteAsync(new() { Type = "audio-ready", Url = Base.TrimEnd('/') + "/api/voice/audio/" + Id + (UseFlac ? ".flac" : ".wav"),
                Text = Announcement ? "announcement" : "", SessionId = PlaybackSession, PlaybackId = PlaybackId, TraceId = RunTracing.RunId.ToString() }, CancellationToken);
            if (Playback is not null)
            {
                try
                {
                    using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
                    Timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    if (!await Playback.Task.WaitAsync(Timeout.Token)) { throw new IOException("Playback failed."); }
                    Delivery.Output(new { playbackAcknowledgement = "succeeded", acknowledgedAt = DateTimeOffset.UtcNow });
                    Delivery.Complete();
                }
                finally { Playback = null; Announcement = false; }
            }
        }
        public async Task SendEventAsync(SatelliteEvent Event, CancellationToken CancellationToken)
        {
            if (Event.Type == "end-of-speech") { StopAudio(); }
            if (Event.Type is "timer-expired" or "announcement") { Announcement = true; }
            await Outgoing.WriteAsync(new() { Type = Event.Type, Text = Event.Text ?? "", SessionId = TransportSession,
                VoiceSessionId = Event.SessionId?.ToString() ?? "",
                TraceId = RunTracing.RunId.ToString(),
                ConversationId = Event.ConversationId?.ToString() ?? "" }, CancellationToken);
        }
    }
}
