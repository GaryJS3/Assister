using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Assister.Persistence;
using Assister.Voice;
using Microsoft.EntityFrameworkCore;

namespace Assister.Satellites;

// A single authenticated, continuously read controller socket multiplexes all registered devices.
public sealed class EchoMuseProvider(IConfiguration Configuration, IServiceScopeFactory Scopes,
    IHttpClientFactory Clients, SatelliteManager Manager, VoiceAudioStore Audio,
    ILogger<EchoMuseProvider> Logger) : BackgroundService
{
    private readonly HashSet<(string Device, string Session)> Seen = [];
    private readonly Queue<(string Device, string Session)> Recent = new();
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        if (!Configuration.GetValue("EchoMuse:Enabled", false)) { return; }
        var Delay = 5;
        while (!StoppingToken.IsCancellationRequested)
        {
            try { await ConnectAsync(StoppingToken); Delay = 5; }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch (OwnershipConflict)
            {
                Delay = 60;
                await using var Scope = Scopes.CreateAsyncScope();
                var Database = Scope.ServiceProvider.GetRequiredService<AssisterDbContext>();
                foreach (var Id in await Database.Satellites.Where(Device => Device.ProviderType == "EchoMuse").Select(Device => Device.Id).ToArrayAsync(StoppingToken))
                {
                    Manager.Update(Id, State => State with { VoiceOwnership = VoiceOwnership.Conflict, LastError = "Another external backend owns the EchoMuse controller connection." }, false);
                    Manager.Record(Id, "Voice ownership conflict", "Another external backend owns the EchoMuse controller connection.");
                }
                Logger.LogWarning("Another external backend owns EchoMuse; retrying in 60s.");
            }
            catch (Exception Error)
            {
                // Remote bodies, tokens and exception messages must never enter logs.
                Logger.LogWarning("EchoMuse link unavailable ({FailureType}); retrying in {DelaySeconds}s.", Error.GetType().Name, Delay);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Delay), StoppingToken); }
            catch (OperationCanceledException) { return; }
            Delay = Math.Min(Delay * 2, 60);
        }
    }
    private async Task ConnectAsync(CancellationToken StoppingToken)
    {
        using var Lifetime = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
        var Base = Configuration["EchoMuse:ControllerUrl"];
        if (!Uri.TryCreate(Base, UriKind.Absolute, out var Address) || Address.Scheme is not ("http" or "https") || Address.UserInfo.Length > 0)
            throw new InvalidOperationException("EchoMuse controller URL is invalid.");
        var Password = Configuration["EchoMuse:Password"];
        if (string.IsNullOrWhiteSpace(Password)) { throw new InvalidOperationException("EchoMuse credential is required."); }
        using var Client = Clients.CreateClient("echomuse");
        Client.BaseAddress = new Uri(Base!.TrimEnd('/') + "/");
        Client.Timeout = TimeSpan.FromSeconds(10);
        using var Login = await Client.PostAsJsonAsync("api/auth/login", new { username = Configuration["EchoMuse:Username"] ?? "admin", password = Password }, Lifetime.Token);
        Login.EnsureSuccessStatusCode();
        var Auth = await Login.Content.ReadFromJsonAsync<JsonElement>(Lifetime.Token);
        var Token = EchoMuseConnection.Text(Auth, "token");
        if (string.IsNullOrWhiteSpace(Token)) { throw new IOException("Controller session unavailable."); }
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var Socket = new ClientWebSocket();
        Socket.Options.CollectHttpResponseDetails = true;
        Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        Socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        Socket.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        var Target = new UriBuilder(new Uri(Client.BaseAddress, "api/voice")) { Scheme = Address.Scheme == "https" ? "wss" : "ws" };
        var Devices = new Dictionary<string, EchoMuseConnection>(StringComparer.Ordinal);
        var FeedbackPlayback = false;
        var VolumeControl = false;
        var Tasks = new HashSet<Task>();
        using var SendGate = new SemaphoreSlim(1);
        async Task Send(object Message, CancellationToken CancellationToken)
        {
            using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, Lifetime.Token);
            Timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var Bytes = JsonSerializer.SerializeToUtf8Bytes(Message);
            if (Bytes.Length > 16384) { throw new IOException("Controller message is too large."); }
            await SendGate.WaitAsync(Timeout.Token);
            try { await Socket.SendAsync(Bytes.AsMemory(), WebSocketMessageType.Text, true, Timeout.Token); }
            finally { SendGate.Release(); }
        }
        Task? Inventory = null;
        try
        {
            // Import approved inventory before claiming voice, so a first-connect conflict
            // remains visible on persistent satellite records without marking them online.
            await RefreshAsync(false);
            try { await Socket.ConnectAsync(Target.Uri, Lifetime.Token); }
            catch (WebSocketException) when (Socket.HttpStatusCode == HttpStatusCode.Conflict)
            { throw new OwnershipConflict(); }
            using (var HelloTimeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token))
            {
                HelloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var Hello = await ReadAsync(Socket, HelloTimeout.Token);
                if (EchoMuseConnection.Text(Hello.RootElement, "type") != "hello" ||
                    !Hello.RootElement.TryGetProperty("protocolVersion", out var Version) || Version.GetInt32() != 1 ||
                    EchoMuseConnection.Text(Hello.RootElement, "voiceBackend") != "external")
                    throw new IOException("Unsupported controller voice protocol or backend.");
                VolumeControl = True(Hello.RootElement, "volumeControl");
                FeedbackPlayback = Hello.RootElement.TryGetProperty("feedbackPlayback", out var Feedback) && Feedback.ValueKind == JsonValueKind.True;
            }
            await RefreshAsync();
            Inventory = PollAsync();
            Logger.LogInformation("EchoMuse external voice link connected.");
            while (Socket.State == WebSocketState.Open)
            {
                using var Document = await ReadAsync(Socket, Lifetime.Token);
                var Message = Document.RootElement;
                var Kind = EchoMuseConnection.Text(Message, "type");
                var DeviceId = EchoMuseConnection.Text(Message, "deviceId");
                EchoMuseConnection? Device;
                lock (Devices) { Device = DeviceId is null ? null : Devices.GetValueOrDefault(DeviceId); }
                if (Device is null) { continue; }
                if (Kind != "turn_start") { Device.Handle(Message); continue; }
                var Session = EchoMuseConnection.Text(Message, "sessionId");
                if (Session is null || Session.Length is < 1 or > 128 || DeviceId!.Length > 128 || !Seen.Add((DeviceId, Session))) { continue; }
                Recent.Enqueue((DeviceId, Session));
                if (Recent.Count > 256) { Seen.Remove(Recent.Dequeue()); }
                if (!ValidAudio(Message))
                { await Send(new { type = "turn_cancel", sessionId = Session, deviceId = DeviceId }, Lifetime.Token); continue; }
                bool Full; lock (Tasks) { Full = Tasks.Count(Task => !Task.IsCompleted) >= 32; }
                if (Full) { await Send(new { type = "turn_cancel", sessionId = Session, deviceId = DeviceId }, Lifetime.Token); continue; }
                var WakeWord = EchoMuseConnection.Text(Message, "wakeWord");
                WakeWord = WakeWord is null ? null : new Diagnostics.DiagnosticSanitizer(Configuration).Text(WakeWord, 128);
                var Turn = Device.Start(Session, WakeWord, Lifetime.Token);
                if (Turn is null)
                { await Send(new { type = "turn_cancel", sessionId = Session, deviceId = DeviceId }, Lifetime.Token); continue; }
                Manager.Update(Device.SatelliteId, State => State with { WakeWord = WakeWord ?? State.WakeWord });
                var Work = ProcessAsync(Device, Turn);
                lock (Tasks) { Tasks.RemoveWhere(Task => Task.IsCompleted); Tasks.Add(Work); }
            }
        }
        finally
        {
            Lifetime.Cancel();
            Socket.Abort();
            if (Inventory is not null) { try { await Inventory; } catch (OperationCanceledException) { } catch (HttpRequestException) { } }
            EchoMuseConnection[] Registered; lock (Devices) { Registered = Devices.Values.ToArray(); }
            foreach (var Device in Registered) { Device.Disconnect(); }
            Task[] Pending; lock (Tasks) { Pending = Tasks.ToArray(); }
            await Task.WhenAll(Pending);
            foreach (var Device in Registered) { Manager.Disconnect(Device); }
        }
        async Task ProcessAsync(EchoMuseConnection Device, EchoMuseConnection.Turn Turn)
        {
            try
            {
                await using var Scope = Scopes.CreateAsyncScope();
                var Result = await Scope.ServiceProvider.GetRequiredService<VoicePipeline>().RunAsync(Turn, null, Turn.Token);
                if (Result.Outcome == "cancelled") { return; }
                if (Result.Request is null || Result.Outcome is "stt-failed" or "tts-failed" or "processing-failed" or "playback-failed")
                {
                    if (await Turn.InputEnded.WaitAsync(Turn.Token) == "speech_end" && !Turn.NoReply && !Turn.ControllerEnded)
                        await Turn.SendErrorAsync(Turn.Token);
                }
            }
            catch (OperationCanceledException) when (Turn.Token.IsCancellationRequested)
            {
                if (!Lifetime.IsCancellationRequested && !Turn.NoReply && !Turn.ControllerEnded)
                {
                    using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await Turn.SendCancelAsync(Timeout.Token); } catch (Exception) { }
                }
            }
            catch (Exception Error)
            {
                Logger.LogWarning("EchoMuse request failed ({FailureType}).", Error.GetType().Name);
                try
                {
                    if (await Turn.InputEnded.WaitAsync(Turn.Token) == "speech_end" && !Turn.NoReply && !Turn.ControllerEnded)
                        await Turn.SendErrorAsync(Turn.Token);
                }
                catch (Exception) { }
            }
            finally { Device.Release(Turn); Turn.Dispose(); }
        }
        async Task PollAsync()
        {
            try
            {
                while (true) { await Task.Delay(TimeSpan.FromSeconds(15), Lifetime.Token); await RefreshAsync(); }
            }
            catch (Exception Error) when (!Lifetime.IsCancellationRequested)
            { Logger.LogWarning("EchoMuse inventory unavailable ({FailureType}).", Error.GetType().Name); Lifetime.Cancel(); Socket.Abort(); }
        }
        async Task RefreshAsync(bool Owned = true)
        {
            using var Response = await Client.GetAsync("api/devices", HttpCompletionOption.ResponseHeadersRead, Lifetime.Token);
            Response.EnsureSuccessStatusCode();
            using var Stream = await Response.Content.ReadAsStreamAsync(Lifetime.Token);
            var Bytes = new byte[1024 * 1024 + 1]; var Count = 0;
            while (Count < Bytes.Length) { var Read = await Stream.ReadAsync(Bytes.AsMemory(Count), Lifetime.Token); if (Read == 0) break; Count += Read; }
            if (Count == Bytes.Length) { throw new IOException("Controller inventory too large."); }
            using var List = JsonDocument.Parse(Bytes.AsMemory(0, Count));
            if (List.RootElement.ValueKind != JsonValueKind.Array || List.RootElement.GetArrayLength() > 128) { throw new IOException("Controller inventory invalid."); }
            var Online = new HashSet<string>();
            foreach (var Item in List.RootElement.EnumerateArray())
            {
                var DeviceId = EchoMuseConnection.Text(Item, "device_id");
                if (DeviceId is null || DeviceId.Length > 100 || !DeviceId.All(Character => char.IsAsciiLetterOrDigit(Character) || Character is '-' or '_') || !True(Item, "approved")) { continue; }
                var Id = "echomuse-" + DeviceId;
                await using var Scope = Scopes.CreateAsyncScope();
                var Database = Scope.ServiceProvider.GetRequiredService<AssisterDbContext>();
                var Registration = await Database.Satellites.FindAsync([Id], Lifetime.Token);
                if (Registration is null)
                {
                    Registration = new Satellite { Id = Id, ProviderType = "EchoMuse", Name = EchoMuseConnection.Text(Item, "label")?[..Math.Min(128, EchoMuseConnection.Text(Item, "label")!.Length)] ?? Id,
                        Endpoint = Address.Host, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                    Database.Satellites.Add(Registration); await Database.SaveChangesAsync(Lifetime.Token);
                }
                if (!Owned || !Registration.Enabled || Registration.ProviderType != "EchoMuse" || !True(Item, "connected")) { continue; }
                Online.Add(DeviceId);
                EchoMuseConnection? Connection;
                lock (Devices) { Connection = Devices.GetValueOrDefault(DeviceId); }
                if (Connection is null)
                {
                    Connection = new(DeviceId, Id, Registration.Name, Registration.AreaId, Send, Audio, Configuration, Manager);
                    Connection.FeedbackPlaybackSupported = FeedbackPlayback;
                    if (!Manager.Register(Connection)) { continue; }
                    lock (Devices) { Devices.Add(DeviceId, Connection); }
                }
                var Wake = Item.TryGetProperty("config", out var Config) ? EchoMuseConnection.Text(Config, "owwModel") : null;
                Manager.Update(Id, State => State with { VoiceOwnership = VoiceOwnership.OwnedByAssister, Model = "EchoMuse satellite",
                    DeviceName = EchoMuseConnection.Text(Item, "label") ?? Registration.Name, FirmwareVersion = EchoMuseConnection.Text(Item, "firmware_ver") ?? "",
                    ApiVersion = "EchoMuse voice v1", MuteState = True(Item, "muted"),
                    CurrentVolume = Item.TryGetProperty("volume", out var Volume) && Volume.TryGetDouble(out var Level) ? Level : null,
                    CurrentPlaybackState = State.CurrentPlaybackState == "Unknown" ? "Assister output idle" : State.CurrentPlaybackState,
                    Capabilities = new() { VoiceAssistant = true, Microphone = true, Speaker = true, VolumeControl = VolumeControl, WakeWord = Wake is not null, AnnouncementPlayback = true },
                    VoiceConfiguration = Wake is null ? new([], [], 0) : new([], [Wake], 0) });
            }
            EchoMuseConnection[] Removed;
            lock (Devices) { Removed = Devices.Where(Pair => !Online.Contains(Pair.Key)).Select(Pair => Pair.Value).ToArray(); foreach (var Device in Removed) { Devices.Remove(Device.ControllerDeviceId); } }
            foreach (var Device in Removed) { Device.Disconnect(); Manager.Disconnect(Device); }
        }
    }
    private static bool True(JsonElement Item, string Name) => Item.TryGetProperty(Name, out var Value) && Value.ValueKind == JsonValueKind.True;
    private sealed class OwnershipConflict : IOException;
    public static bool ValidAudio(JsonElement Message) => Message.TryGetProperty("audio", out var Format) &&
        Format.TryGetProperty("sampleRate", out var Rate) && Rate.TryGetInt32(out var RateValue) && RateValue == 16000 &&
        Format.TryGetProperty("sampleWidth", out var Width) && Width.TryGetInt32(out var WidthValue) && WidthValue == 2 &&
        Format.TryGetProperty("channels", out var Channels) && Channels.TryGetInt32(out var ChannelValue) && ChannelValue == 1 &&
        EchoMuseConnection.Text(Format, "encoding") == "pcm_s16le";
    private static async Task<JsonDocument> ReadAsync(ClientWebSocket Socket, CancellationToken Token)
    {
        using var Message = new MemoryStream(); var Buffer = new byte[16384];
        while (true)
        {
            var Part = await Socket.ReceiveAsync(Buffer.AsMemory(), Token);
            if (Part.MessageType != WebSocketMessageType.Text || Message.Length + Part.Count > 128 * 1024) { throw new IOException("Controller message invalid."); }
            Message.Write(Buffer, 0, Part.Count);
            if (Part.EndOfMessage) { return JsonDocument.Parse(Message.ToArray()); }
        }
    }
}
