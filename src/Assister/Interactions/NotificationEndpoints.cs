using Assister.Contracts;

namespace Assister.Interactions;

public static class NotificationEndpoints
{
    public static void MapNotifications(RouteGroupBuilder Group)
    {
        Group.MapGet("/notifications", (bool? DueOnly, NotificationStore Store, HttpContext Http) =>
            Store.List(Owner(Http), Client(Http), DueOnly ?? false));
        Group.MapPost("/notifications", (CreateNotification Request, NotificationStore Store,
            ClientAuthentication Auth, IConfiguration Configuration, HttpContext Http) =>
        {
            if (Request.TargetClientId is { } Target && (string.IsNullOrWhiteSpace(Configuration[$"RichClients:Clients:{Target}:Token"])
                || Auth.Owner(Target) != Owner(Http))) return Results.NotFound();
            try
            {
                var Item = Store.Create(Owner(Http), Request);
                return Results.Created($"/api/client/notifications/{Item.Id}", Item);
            }
            catch (ArgumentException Error) { return Results.BadRequest(new ProtocolError(Error.Message, "Supply a supported kind, importance, text, retry key and valid schedule.")); }
            catch (InvalidOperationException Error) { return Results.Conflict(new ProtocolError(Error.Message, "The retry key conflicts or the notification quota is full.")); }
        });
        Group.MapGet("/notifications/{id:guid}", (Guid Id, NotificationStore Store, HttpContext Http) =>
            Store.List(Owner(Http), Client(Http)).FirstOrDefault(Item => Item.Id == Id) is { } Item ? Results.Ok(Item) : Results.NotFound());
        Group.MapDelete("/notifications/{id:guid}", (Guid Id, NotificationStore Store, HttpContext Http) =>
            Store.Cancel(Owner(Http), Client(Http), Id) ? Results.NoContent() : Results.NotFound());
        Group.MapPost("/notifications/{id:guid}/receipts", (Guid Id, NotificationAcknowledgement Request, NotificationStore Store, HttpContext Http) =>
        {
            try
            {
                return Store.Acknowledge(Owner(Http), Client(Http), Id, Request.State) is { } Receipt
                    ? Results.Ok(Receipt) : Results.NotFound();
            }
            catch (ArgumentException) { return Results.BadRequest(new ProtocolError("invalid_notification_receipt", "Unknown receipt state.")); }
        });
    }
    private static string Owner(HttpContext Http) => (string)Http.Items["ClientOwner"]!;
    private static string Client(HttpContext Http) => (string)Http.Items["ClientId"]!;
}
