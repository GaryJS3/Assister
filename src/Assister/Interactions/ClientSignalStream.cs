using System.Net.WebSockets;
using System.Text.Json;

namespace Assister.Interactions;

public static class ClientSignalStream
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task HandleAsync(HttpContext Http, ClientSignals Signals, ClientAuthentication Auth)
    {
        var ClientId = (string)Http.Items["ClientId"]!;
        var Owner = (string)Http.Items["ClientOwner"]!;
        if (!Http.WebSockets.IsWebSocketRequest || !Signals.Heartbeat(ClientId)) { Http.Response.StatusCode = 400; return; }
        using var Socket = await Http.WebSockets.AcceptWebSocketAsync();
        using var Lifetime = CancellationTokenSource.CreateLinkedTokenSource(Http.RequestAborted);
        var Receive = ReceiveAsync();
        var Sent = new HashSet<Guid>();
        var LastHeartbeat = DateTimeOffset.UtcNow;
        try
        {
            await SendAsync(new { type = "connection.ready", protocolVersion = 1, clientId = ClientId });
            while (!Lifetime.IsCancellationRequested && Socket.State == WebSocketState.Open)
            {
                if (Auth.Authenticate(Http) != ClientId || Auth.Owner(ClientId) != Owner || !Signals.Heartbeat(ClientId)) break;
                var Pending = Signals.Pending(ClientId);
                foreach (var Request in Pending)
                    if (Sent.Add(Request.RequestId)) await SendAsync(new { type = "device.request", data = Request });
                foreach (var Resolved in Sent.Except(Pending.Select(Item => Item.RequestId)).ToArray())
                {
                    await SendAsync(new { type = "device.resolved", requestId = Resolved });
                    Sent.Remove(Resolved);
                }
                if (DateTimeOffset.UtcNow - LastHeartbeat > TimeSpan.FromSeconds(15))
                { await SendAsync(new { type = "connection.heartbeat" }); LastHeartbeat = DateTimeOffset.UtcNow; }
                await Task.Delay(50, Lifetime.Token);
            }
            if (Socket.State == WebSocketState.Open) await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", Lifetime.Token);
        }
        catch (Exception Error) when (Error is OperationCanceledException or WebSocketException) { }
        finally { Lifetime.Cancel(); await Receive; }
        async Task SendAsync(object Value)
        {
            using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); Deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(Value, Json), WebSocketMessageType.Text, true, Deadline.Token);
        }
        async Task ReceiveAsync()
        {
            try
            {
                var Buffer = new byte[1024];
                while (!Lifetime.IsCancellationRequested)
                {
                    var Message = await Socket.ReceiveAsync(Buffer, Lifetime.Token);
                    if (Message.MessageType == WebSocketMessageType.Close) break;
                    // Commands are acknowledged through HTTP, keeping retransmission semantics explicit.
                    break;
                }
            }
            catch (Exception Error) when (Error is OperationCanceledException or WebSocketException) { }
            finally { Lifetime.Cancel(); }
        }
    }
}
