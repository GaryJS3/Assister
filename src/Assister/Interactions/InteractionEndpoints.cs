using System.Net.WebSockets;
using System.Text.Json;
using Assister.Contracts;

namespace Assister.Interactions;

public static class InteractionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void MapRichClients(this WebApplication App)
    {
        var Group = App.MapGroup("/api/client");
        // Browser requests must be same-origin. Native clients use Authorization headers without Origin.
        Group.AddEndpointFilter(async (Context, Next) =>
        {
            var Http = Context.HttpContext;
            var Origin = Http.Request.Headers.Origin.ToString();
            if (Origin.Length > 0 && (!Uri.TryCreate(Origin, UriKind.Absolute, out var OriginUri) || OriginUri.Authority != Http.Request.Host.Value || OriginUri.Scheme != Http.Request.Scheme))
                return Results.StatusCode(403);
            if (Http.Request.Path == "/api/client/session" && Http.Request.Method == "POST") return await Next(Context);
            var Auth = Http.RequestServices.GetRequiredService<ClientAuthentication>();
            var ClientId = Auth.Authenticate(Http);
            if (ClientId is null) return Results.Unauthorized();
            Http.Items["ClientId"] = ClientId;
            Http.Items["ClientOwner"] = Auth.Owner(ClientId);
            return await Next(Context);
        });
        Group.MapPost("/session", (ClientLogin Login, ClientAuthentication Auth, HttpContext Http) =>
        {
            if (Login.Token is null || Auth.Identify(Login.Token) is not { } Owner) return Results.Unauthorized();
            Auth.SignIn(Http, Login.Token);
            return Results.Ok(new { clientId = Owner, protocolVersion = 1 });
        });
        Group.MapDelete("/session", (HttpContext Http) => { Http.Response.Cookies.Delete("assister-client", new CookieOptions { Path = "/api/client" }); return Results.NoContent(); });
        Group.MapGet("/protocol", () => new { protocolVersion = 1, eventDelivery = "at_least_once", maxMessageCharacters = 1000, heartbeatSeconds = 15,
            maxAttachmentBytes = 1048576, maxAttachments = 8, maxAttachmentContextCharacters = 12000,
            inputAudioFormat = "pcm_s16le_16000_mono", outputAudioFormat = "wav", voiceInputMode = "buffered_upload",
            features = new[] { "text.input", "text.output", "events.replay", "interaction.cancel", "execution.steps", "reasoning.stream", "model.output.stream", "execution.details", "context.inspect", "attachments.text", "attachments.image.storage", "audio.input", "audio.output", "audio.tones", "client.capabilities", "device.requests" } });
        Group.MapPost("/attachments", AttachmentUpload.ReceiveAsync);
        Group.MapPost("/clients/register", (ClientRegistration Request, ClientSignals Signals, HttpContext Http) =>
            Signals.Register((string)Http.Items["ClientId"]!, Owner(Http), Request) ? Results.Ok() : Results.BadRequest(new ProtocolError("invalid_capabilities", "Supply a device name and at most 32 canonical capability names.")));
        Group.MapGet("/clients", (ClientSignals Signals, HttpContext Http) => Signals.Clients(Owner(Http)));
        Group.MapDelete("/clients/current", (ClientSignals Signals, HttpContext Http) => { Signals.Disconnect((string)Http.Items["ClientId"]!); return Results.NoContent(); });
        Group.MapGet("/device/requests", (ClientSignals Signals, HttpContext Http) => Signals.Pending((string)Http.Items["ClientId"]!));
        Group.MapGet("/device/stream", ClientSignalStream.HandleAsync);
        Group.MapGet("/device/responses/{id:guid}", (Guid Id, ClientSignals Signals, HttpContext Http) =>
            Signals.Outcome((string)Http.Items["ClientId"]!, Id) is { } Response ? Results.Ok(Response) : Results.NotFound());
        Group.MapPost("/device/responses", (DeviceResponse Response, ClientSignals Signals, HttpContext Http) =>
            Signals.Respond((string)Http.Items["ClientId"]!, Response) ? Results.Ok() : Results.Conflict(new ProtocolError("device_response_rejected", "The response has the wrong target, conflicts with an earlier response or has expired.")));
        Group.MapGet("/attachments/{id:guid}", (Guid Id, InteractionStore Store, HttpContext Http) =>
        {
            if (Store.Attachment(Id, Owner(Http)) is not { } Item) return Results.NotFound();
            Http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(Item.Content, Item.Metadata.MimeType, fileDownloadName: Item.Metadata.Name);
        });
        Group.MapGet("/attachments/{id:guid}/metadata", (Guid Id, InteractionStore Store, HttpContext Http) =>
            Store.Attachment(Id, Owner(Http)) is { } Item ? Results.Ok(Item.Metadata) : Results.NotFound());
        Group.MapGet("/conversations", (InteractionStore Store, HttpContext Http) => Store.Conversations(Owner(Http)));
        Group.MapPost("/conversations", (InteractionStore Store, HttpContext Http) => Results.Ok(Store.CreateConversation(Owner(Http))));
        Group.MapGet("/conversations/{id:guid}", (Guid Id, InteractionStore Store, HttpContext Http) =>
            Store.OwnsConversation(Id, Owner(Http)) ? Results.Ok(Store.History(Id)) : Results.NotFound());
        Group.MapPost("/conversations/{id:guid}/interactions", (Guid Id, SubmitInteraction Request, InteractionStore Store, HttpContext Http) =>
        {
            if (string.IsNullOrWhiteSpace(Request.Message) || Request.Message.Length > 1000 || string.IsNullOrWhiteSpace(Request.IdempotencyKey) || Request.IdempotencyKey.Length > 128)
                return Results.BadRequest(new ProtocolError("invalid_request", "Supply 1–1000 characters and an idempotency key of at most 128 characters."));
            if (!Store.OwnsConversation(Id, Owner(Http))) return Results.NotFound();
            try
            {
                var Item = Store.Submit(Id, Owner(Http), Request, (string)Http.Items["ClientId"]!);
                return Results.Accepted($"/api/client/interactions/{Item.Id}", Item);
            }
            catch (InvalidOperationException Error)
            {
                var Message = Error.Message switch
                {
                    "attachment_processing_unsupported" => "This attachment is stored, but its content type cannot be processed yet. Remove it before sending.",
                    "attachment_not_found" => "An attachment is unavailable or belongs to another owner.",
                    "attachment_context_too_large" => "Combined attachment text exceeds 12,000 characters.",
                    "invalid_attachments" => "Supply at most eight distinct attachment IDs.",
                    _ => "The request conflicts with an earlier submission or the queue is full."
                };
                return Results.Conflict(new ProtocolError(Error.Message, Message, true));
            }
        });
        Group.MapGet("/interactions/{id:guid}", (Guid Id, InteractionStore Store, HttpContext Http) => Store.Get(Id, Owner(Http)) is { } Item ? Results.Ok(Item) : Results.NotFound());
        Group.MapGet("/interactions/{id:guid}/audio", (Guid Id, InteractionStore Store, HttpContext Http) =>
            Store.Get(Id, Owner(Http)) is not null && Store.Audio(Id) is { } Audio ? Results.File(Audio, "audio/wav", enableRangeProcessing: true) : Results.NotFound());
        Group.MapPost("/interactions/{id:guid}/playback", (Guid Id, PlaybackReport Report, InteractionStore Store, HttpContext Http) =>
        {
            if (Store.Get(Id, Owner(Http)) is null) return Results.NotFound();
            return Store.Playback(Id, (string)Http.Items["ClientId"]!, Report) ? Results.Ok() : Results.Conflict(new ProtocolError("invalid_playback_state", "The playback transition or audio is unavailable."));
        });
        Group.MapGet("/interactions/{id:guid}/context", (Guid Id, InteractionStore Store, HttpContext Http) =>
            Store.Get(Id, Owner(Http)) is not null ? Results.Ok(Store.Context(Id)) : Results.NotFound());
        Group.MapGet("/interactions/{id:guid}/attachments", (Guid Id, InteractionStore Store, HttpContext Http) =>
            Store.Get(Id, Owner(Http)) is not null ? Results.Ok(Store.Attachments(Id)) : Results.NotFound());
        Group.MapGet("/interactions/{id:guid}/trace", (Guid Id, InteractionStore Store, Assister.Diagnostics.RunStore Traces, HttpContext Http) =>
        {
            if (Store.Get(Id, Owner(Http)) is not { } Item) return Results.NotFound();
            return Item.RunId is { } RunId && Traces.Get(RunId) is { } Trace ? Results.Ok(Trace) : Results.NotFound();
        });
        Group.MapGet("/interactions/{id:guid}/events", (Guid Id, long? AfterSequence, InteractionStore Store, HttpContext Http) =>
        {
            if (Store.Get(Id, Owner(Http)) is not { } Item) return Results.NotFound();
            if (AfterSequence is < 0 || AfterSequence > Item.LastSequence) return Results.BadRequest(new ProtocolError("invalid_cursor", "The sequence is outside the available stream."));
            return Results.Ok(Store.Events(Id, AfterSequence ?? 0));
        });
        Group.MapPost("/interactions/{id:guid}/cancel", (Guid Id, InteractionStore Store, InteractionRunner Runner, ClientSignals Signals, HttpContext Http) =>
        {
            if (Store.Get(Id, Owner(Http)) is null) return Results.NotFound();
            Runner.Cancel(Id);
            Signals.CancelInteraction(Id);
            return Results.Accepted($"/api/client/interactions/{Id}", Store.Get(Id));
        });
        Group.MapGet("/interactions/{id:guid}/stream", StreamAsync);
    }
    private static string Owner(HttpContext Http) => (string)Http.Items["ClientOwner"]!;
    private static async Task StreamAsync(Guid Id, long? AfterSequence, bool? FollowPlayback, HttpContext Http, InteractionStore Store, ClientAuthentication Auth)
    {
        if (Store.Get(Id, Owner(Http)) is not { } Initial) { Http.Response.StatusCode = 404; return; }
        if (AfterSequence is < 0 || AfterSequence > Initial.LastSequence) { Http.Response.StatusCode = 400; return; }
        if (!Http.WebSockets.IsWebSocketRequest) { Http.Response.StatusCode = 400; return; }
        using var Socket = await Http.WebSockets.AcceptWebSocketAsync();
        using var Lifetime = CancellationTokenSource.CreateLinkedTokenSource(Http.RequestAborted);
        var Receive = ReceiveUntilCloseAsync(Socket, Lifetime);
        try
        {
            var Cursor = AfterSequence ?? 0;
            await SendAsync(Socket, new { type = "subscription.ready", interactionId = Id, protocolVersion = 1, afterSequence = Cursor, highWaterSequence = Initial.LastSequence }, Lifetime.Token);
            var Heartbeat = DateTimeOffset.UtcNow;
            while (!Lifetime.IsCancellationRequested && Socket.State == WebSocketState.Open)
            {
                // Persisted rows are the only delivery source: replay/live cannot race or lose a commit.
                if (Auth.Authenticate(Http) != (string)Http.Items["ClientId"]! || Auth.Owner((string)Http.Items["ClientId"]!) != Owner(Http)) { await Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Session expired", Lifetime.Token); break; }
                foreach (var Event in Store.Events(Id, Cursor))
                {
                    await SendAsync(Socket, Event, Lifetime.Token);
                    Cursor = Event.Sequence;
                }
                var Item = Store.Get(Id)!;
                if (FollowPlayback != true && Cursor >= Item.LastSequence && Item.Status is "completed" or "failed" or "cancelled")
                { await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Interaction complete", Lifetime.Token); break; }
                if (DateTimeOffset.UtcNow - Heartbeat > TimeSpan.FromSeconds(15))
                { await SendAsync(Socket, new { type = "connection.heartbeat", timestamp = DateTimeOffset.UtcNow }, Lifetime.Token); Heartbeat = DateTimeOffset.UtcNow; }
                await Task.Delay(50, Lifetime.Token);
            }
        }
        catch (Exception Error) when (Error is OperationCanceledException or WebSocketException) { }
        finally { Lifetime.Cancel(); await Receive; }
    }
    private static async Task ReceiveUntilCloseAsync(WebSocket Socket, CancellationTokenSource Lifetime)
    {
        try
        {
            var Buffer = new byte[1024];
            while (!Lifetime.IsCancellationRequested)
            {
                var Message = await Socket.ReceiveAsync(Buffer, Lifetime.Token);
                if (Message.MessageType == WebSocketMessageType.Close) break;
                // This endpoint is receive-only for clients; commands use authenticated HTTP.
                await Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Use HTTP for commands", Lifetime.Token);
                break;
            }
        }
        catch (Exception Error) when (Error is OperationCanceledException or WebSocketException) { }
        finally { Lifetime.Cancel(); }
    }
    private static async Task SendAsync(WebSocket Socket, object Value, CancellationToken Token)
    {
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(Value, Json), WebSocketMessageType.Text, true, Deadline.Token);
    }
    public sealed record ClientLogin(string Token);
}
