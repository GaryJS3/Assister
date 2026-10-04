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
        if (Registration.SatelliteId != Configuration["EspHome:SatelliteId"] || Registration.Name.Length > 128 || Registration.Area.Length > 128)
        { throw new RpcException(new(StatusCode.PermissionDenied, "Satellite is not configured.")); }
        var Outgoing = Channel.CreateBounded<BridgeFrame>(64);
        var Connection = new BridgeConnection(Registration, Outgoing.Writer, Audio, Configuration);
        if (!Manager.Register(Connection)) { throw new RpcException(new(StatusCode.AlreadyExists, "Satellite already connected.")); }
        var Sender = SendAsync();
        CancellationTokenSource? SessionCancellation = null;
        Task? Session = null;
        string? TransportSession = null;
        try
        {
            await Outgoing.Writer.WriteAsync(new() { Type = "registered", SatelliteId = Connection.SatelliteId }, Lifetime.Token);
            while (await Requests.MoveNext(Lifetime.Token))
            {
                var Frame = Requests.Current;
                if (Frame.Pcm.Length > 65536) { throw new RpcException(new(StatusCode.ResourceExhausted, "Audio chunk too large.")); }
                if (Frame.Type == "start")
                {
                    if (Session is { IsCompleted: false })
                    {
                        SessionCancellation?.Cancel();
                        Connection.StopAudio();
                        await Session;
                    }
                    SessionCancellation?.Dispose();
                    SessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                    TransportSession = Frame.SessionId;
                    Connection.TransportSession = Frame.SessionId;
                    Connection.StartAudio();
                    Session = RunAsync(Frame.ConversationId, SessionCancellation.Token);
                }
                else if (Frame.SessionId == TransportSession)
                {
                    if (Frame.Type == "audio")
                    {
                        if (Frame.SampleRate != 16000 || Frame.SampleWidth != 2 || Frame.Channels != 1 || Frame.Pcm.Length % 2 != 0)
                        { throw new RpcException(new(StatusCode.InvalidArgument, "Expected 16kHz mono S16 PCM.")); }
                        Connection.WriteAudio(new(Frame.Pcm.ToByteArray(), 16000, 2, 1));
                    }
                    else if (Frame.Type == "stop") { Connection.StopAudio(); }
                    else if (Frame.Type == "cancel") { SessionCancellation?.Cancel(); Connection.StopAudio(); }
                }
                if (Frame.Type == "playback-finished") { Connection.PlaybackFinished(Frame.Text == "succeeded"); }
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
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
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
        public void PlaybackFinished(bool Succeeded) { Playback?.TrySetResult(Succeeded); }
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
            using var Delivery = RunTracing.Start("Playback", "Satellite playback", "Publish audio-ready and wait for the satellite's explicit playback acknowledgement.");
            Delivery.Metadata(new { audioReadyAt = DateTimeOffset.UtcNow });
            await Outgoing.WriteAsync(new() { Type = "audio-ready", Url = Base.TrimEnd('/') + "/api/voice/audio/" + Id + (UseFlac ? ".flac" : ".wav"),
                Text = Announcement ? "announcement" : "", SessionId = Announcement ? "" : TransportSession }, CancellationToken);
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
            if (Event.Type == "timer-expired") { Announcement = true; }
            await Outgoing.WriteAsync(new() { Type = Event.Type, Text = Event.Text ?? "", SessionId = TransportSession,
                VoiceSessionId = Event.SessionId?.ToString() ?? "",
                ConversationId = Event.ConversationId?.ToString() ?? "" }, CancellationToken);
        }
    }
}
