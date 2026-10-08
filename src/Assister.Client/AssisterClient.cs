using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Assister.Contracts;

namespace Assister.Client;

// UI independent scaffolding for Windows/.NET Android clients. The caller owns HttpClient and its credentials.
public sealed class AssisterClient(HttpClient Http, Func<Uri, CancellationToken, Task<WebSocket>>? Connect = null, Action<string>? ConnectionState = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<ClientConversation> CreateConversationAsync(CancellationToken Token = default)
    {
        using var Response = await Http.PostAsync("api/client/conversations", null, Token);
        Response.EnsureSuccessStatusCode();
        return (await Response.Content.ReadFromJsonAsync<ClientConversation>(Token))!;
    }
    public Task<InteractionSnapshot> SubmitAsync(Guid ConversationId, string Message, string IdempotencyKey, CancellationToken Token = default)
        => SubmitAsync(ConversationId, new SubmitInteraction(Message, IdempotencyKey), Token);
    public async Task<InteractionSnapshot> SubmitAsync(Guid ConversationId, SubmitInteraction Request, CancellationToken Token = default)
    {
        using var Response = await Http.PostAsJsonAsync($"api/client/conversations/{ConversationId}/interactions", Request, Token);
        Response.EnsureSuccessStatusCode();
        return (await Response.Content.ReadFromJsonAsync<InteractionSnapshot>(Token))!;
    }
    public async Task CancelAsync(Guid InteractionId, CancellationToken Token = default)
    {
        using var Response = await Http.PostAsync($"api/client/interactions/{InteractionId}/cancel", null, Token);
        Response.EnsureSuccessStatusCode();
    }
    public Task<InteractionSnapshot?> GetAsync(Guid InteractionId, CancellationToken Token = default)
        => Http.GetFromJsonAsync<InteractionSnapshot>($"api/client/interactions/{InteractionId}", Token);
    public Task<InteractionContext[]?> GetContextAsync(Guid InteractionId, CancellationToken Token = default)
        => Http.GetFromJsonAsync<InteractionContext[]>($"api/client/interactions/{InteractionId}/context", Token);
    public Task<ClientAttachment[]?> GetAttachmentsAsync(Guid InteractionId, CancellationToken Token = default)
        => Http.GetFromJsonAsync<ClientAttachment[]>($"api/client/interactions/{InteractionId}/attachments", Token);
    public Task<RegisteredClient[]?> GetClientsAsync(CancellationToken Token = default) => Http.GetFromJsonAsync<RegisteredClient[]>("api/client/clients", Token);
    public Task<DeviceRequest[]?> GetDeviceRequestsAsync(CancellationToken Token = default) => Http.GetFromJsonAsync<DeviceRequest[]>("api/client/device/requests", Token);
    public Task<DeviceResponse?> GetDeviceOutcomeAsync(Guid Id, CancellationToken Token = default) => Http.GetFromJsonAsync<DeviceResponse>($"api/client/device/responses/{Id}", Token);
    public async Task RegisterAsync(ClientRegistration Registration, CancellationToken Token = default)
    { using var Response = await Http.PostAsJsonAsync("api/client/clients/register", Registration, Token); Response.EnsureSuccessStatusCode(); }
    public async Task RespondAsync(DeviceResponse Result, CancellationToken Token = default)
    { using var Response = await Http.PostAsJsonAsync("api/client/device/responses", Result, Token); Response.EnsureSuccessStatusCode(); }
    public async Task ReportPlaybackAsync(Guid Id, PlaybackReport Report, CancellationToken Token = default)
    { using var Response = await Http.PostAsJsonAsync($"api/client/interactions/{Id}/playback", Report, Token); Response.EnsureSuccessStatusCode(); }
    public Task<Stream> DownloadAudioAsync(Guid Id, CancellationToken Token = default) => Http.GetStreamAsync($"api/client/interactions/{Id}/audio", Token);
    public Task<Stream> DownloadToneAsync(string Name, CancellationToken Token = default)
        => Http.GetStreamAsync($"api/voice/tones/{Uri.EscapeDataString(Name)}.wav", Token);
    public async Task<ClientAttachment> UploadAttachmentAsync(ReadOnlyMemory<byte> Bytes, string Name, string MimeType,
        string Source = "user", CancellationToken Token = default)
    {
        using var Content = new ByteArrayContent(Bytes.ToArray());
        Content.Headers.ContentType = new(MimeType);
        using var Response = await Http.PostAsync($"api/client/attachments?name={Uri.EscapeDataString(Name)}&source={Uri.EscapeDataString(Source)}", Content, Token);
        Response.EnsureSuccessStatusCode();
        return (await Response.Content.ReadFromJsonAsync<ClientAttachment>(Token))!;
    }

    // Delivery resumes from the last yielded sequence. Consumers persist that cursor with their rendered state.
    public async IAsyncEnumerable<InteractionEvent> ObserveAsync(Guid InteractionId, long AfterSequence = 0,
        [EnumeratorCancellation] CancellationToken Token = default, bool FollowPlayback = false)
    {
        if (AfterSequence < 0) throw new ArgumentOutOfRangeException(nameof(AfterSequence));
        var Cursor = AfterSequence;
        var Base = Http.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress is required.");
        while (true)
        {
            Token.ThrowIfCancellationRequested();
            // HTTP checks fail visibly for revoked credentials, invalid cursors and missing interactions.
            using (var Check = await Http.GetAsync($"api/client/interactions/{InteractionId}/events?afterSequence={Cursor}", Token)) Check.EnsureSuccessStatusCode();
            var Uri = new UriBuilder(new Uri(Base, $"api/client/interactions/{InteractionId}/stream?afterSequence={Cursor}&followPlayback={FollowPlayback.ToString().ToLowerInvariant()}"))
            { Scheme = Base.Scheme == "https" ? "wss" : "ws" }.Uri;
            WebSocket Socket;
            ConnectionState?.Invoke("connecting");
            try { Socket = await OpenAsync(Uri, Token); }
            catch (WebSocketException) { ConnectionState?.Invoke("reconnecting"); await Task.Delay(500, Token); continue; }
            ConnectionState?.Invoke("connected");
            using (Socket)
            {
                while (!Token.IsCancellationRequested)
                {
                    JsonElement? Message;
                    try { Message = await ReceiveAsync(Socket, Token); }
                    catch (WebSocketException) { break; }
                    if (Message is null) break;
                    if (Message.Value.TryGetProperty("type", out var MessageType) && MessageType.GetString() == "subscription.ready"
                        && (!Message.Value.TryGetProperty("protocolVersion", out var Version) || Version.GetInt32() != 1))
                        throw new NotSupportedException("The server interaction protocol version is unsupported.");
                    if (!Message.Value.TryGetProperty("sequence", out _)) continue;
                    var Event = Message.Value.Deserialize<InteractionEvent>(Json)!;
                    if (Event.Sequence <= Cursor) continue;
                    if (Event.Sequence != Cursor + 1) break; // Reconnect to recover a gap from durable storage.
                    Cursor = Event.Sequence;
                    yield return Event;
                    if (Event.Type is "interaction.completed" or "interaction.failed" or "interaction.cancelled")
                    { ConnectionState?.Invoke("completed"); if (!FollowPlayback) yield break; }
                }
            }
            // A caller may resume from the terminal sequence itself; no new events will arrive in that case.
            var Snapshot = await GetAsync(InteractionId, Token);
            if (!FollowPlayback && Snapshot is not null && Snapshot.LastSequence <= Cursor && Snapshot.Status is "completed" or "failed" or "cancelled") yield break;
            await Task.Delay(500, Token);
            ConnectionState?.Invoke("reconnecting");
        }
    }
    private async Task<WebSocket> OpenAsync(Uri Uri, CancellationToken Token)
    {
        if (Connect is not null) return await Connect(Uri, Token);
        var Socket = new ClientWebSocket();
        if (Http.DefaultRequestHeaders.Authorization is { } Authorization) Socket.Options.SetRequestHeader("Authorization", Authorization.ToString());
        try { await Socket.ConnectAsync(Uri, Token); return Socket; }
        catch { Socket.Dispose(); throw; }
    }
    // Register capabilities first. This only delivers requests; the application owns permission prompts and a durable execution ledger.
    public async IAsyncEnumerable<DeviceRequest> ObserveDeviceRequestsAsync([EnumeratorCancellation] CancellationToken Token = default)
    {
        var Seen = new HashSet<Guid>();
        var Base = Http.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress is required.");
        var Uri = new UriBuilder(new Uri(Base, "api/client/device/stream")) { Scheme = Base.Scheme == "https" ? "wss" : "ws" }.Uri;
        while (true)
        {
            Token.ThrowIfCancellationRequested();
            var Pending = await GetDeviceRequestsAsync(Token) ?? [];
            Seen.IntersectWith(Pending.Select(Request => Request.RequestId));
            WebSocket Socket;
            ConnectionState?.Invoke("connecting");
            try { Socket = await OpenAsync(Uri, Token); }
            catch (WebSocketException) { ConnectionState?.Invoke("reconnecting"); await Task.Delay(500, Token); continue; }
            using (Socket)
            {
                ConnectionState?.Invoke("connected");
                while (!Token.IsCancellationRequested)
                {
                    JsonElement? Message;
                    try { Message = await ReceiveAsync(Socket, Token); }
                    catch (WebSocketException) { break; }
                    if (Message is null) break;
                    if (!Message.Value.TryGetProperty("type", out var Type)) continue;
                    if (Type.GetString() == "connection.ready" && (!Message.Value.TryGetProperty("protocolVersion", out var Version) || Version.GetInt32() != 1))
                        throw new NotSupportedException("The server device protocol version is unsupported.");
                    if (Type.GetString() == "device.resolved") Seen.Remove(Message.Value.GetProperty("requestId").GetGuid());
                    if (Type.GetString() != "device.request") continue;
                    var Request = Message.Value.GetProperty("data").Deserialize<DeviceRequest>(Json)!;
                    if (Seen.Add(Request.RequestId)) yield return Request;
                }
            }
            ConnectionState?.Invoke("reconnecting");
            await Task.Delay(500, Token);
        }
    }
    private static async Task<JsonElement?> ReceiveAsync(WebSocket Socket, CancellationToken Token)
    {
        using var Message = new MemoryStream();
        var Buffer = new byte[8192];
        while (true)
        {
            var Result = await Socket.ReceiveAsync(Buffer, Token);
            if (Result.MessageType == WebSocketMessageType.Close) return null;
            if (Result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Expected a JSON text event.");
            Message.Write(Buffer, 0, Result.Count);
            if (Message.Length > 1024 * 1024) throw new InvalidDataException("Event exceeds the client limit.");
            if (Result.EndOfMessage) return JsonSerializer.Deserialize<JsonElement>(Message.ToArray());
        }
    }
}
