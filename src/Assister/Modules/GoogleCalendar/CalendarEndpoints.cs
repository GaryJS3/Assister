using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Assister.Modules.GoogleCalendar;

public sealed class CalendarDeletion
{
    private sealed record Pending(string Account, string Calendar, string Event, string Etag, string Owner, DateTimeOffset Expires, Guid Request);
    private readonly ConcurrentDictionary<string, Pending> Requests = new();
    public string Prepare(string Account, string Calendar, string Event, string Etag, string Owner, Guid Request = default)
    {
        foreach (var Item in Requests.Where(P => P.Value.Expires < DateTimeOffset.UtcNow)) Requests.TryRemove(Item.Key, out _);
        var Key = Guid.NewGuid().ToString("N");
        Requests[Key] = new(Account, Calendar, Event, Etag, Owner, DateTimeOffset.UtcNow.AddMinutes(5), Request);
        return Key;
    }
    public async Task<JsonObject> Confirm(string Key, string Owner, GoogleCalendarClient Client, CancellationToken Ct, Guid Request = default)
    {
        if (!Requests.TryGetValue(Key, out var Item) || Item.Owner != Owner || Item.Expires < DateTimeOffset.UtcNow || Request != default && Item.Request == Request || !Requests.TryRemove(Key, out _)) throw new ArgumentException("Deletion confirmation expired, requires a later request, or does not belong to this session.");
        Client.Subscribed(Item.Account, Item.Calendar);
        return await Client.Send(Item.Account, HttpMethod.Delete, GoogleCalendarClient.EventsPath(Item.Calendar) + "/" + GoogleCalendarClient.Escape(Item.Event), null, Ct, Item.Etag);
    }
}
public sealed record AccountInput(string Name, string Credential);
public sealed record SubscriptionInput(bool Enabled, string? SatelliteId = null, bool Announce = false,
    string[]? SatelliteIds = null, CalendarRichRecipient[]? RichClients = null,
    bool AllSatellites = false, bool AllRichClients = false, CalendarNotificationSettings? Notifications = null);
public sealed record CalendarIdInput(string CalendarId);
public sealed record ConfirmationInput(string Confirmation);
public sealed record DefaultReminderInput(int? Minutes);
public static class CalendarEndpoints
{
    public static void MapGoogleCalendars(this WebApplication App)
    {
        var Group = App.MapGroup("/api/calendars");
        Group.AddEndpointFilter(async (Context, Next) =>
        {
            Context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await Next(Context); }
            catch (Exception Error) when (Error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or KeyNotFoundException or HttpRequestException)
            { return Results.BadRequest(new { error = Error is InvalidOperationException or ArgumentException ? Error.Message : "Calendar request failed. Check the credential and calendar permissions." }); }
        });
        Group.MapGet("/accounts", (CalendarStore Store) => Store.Snapshot().Select(A => new { A.Id, A.Name, A.Email, A.Calendars }));
        Group.MapGet("/status", (CalendarScheduler Scheduler) => new { Scheduler.LastChecked, Scheduler.LastError });
        Group.MapGet("/recipients", (CalendarRecipients Recipients) => Recipients.Clients());
        Group.MapPost("/accounts", (AccountInput Input, CalendarStore Store) => { var A = Store.Add(Input.Name, Input.Credential); return Results.Ok(new { A.Id, A.Name, A.Email }); });
        Group.MapDelete("/accounts/{account}", (string Account, CalendarStore Store) => { Store.Remove(Account); return Results.NoContent(); });
        Group.MapPost("/{account}/discover", async (string Account, GoogleCalendarClient Client, CancellationToken Ct) => await Client.Discover(Account, Ct));
        Group.MapPost("/{account}/calendar-list", async (string Account, CalendarIdInput Input, GoogleCalendarClient Client, CancellationToken Ct) =>
        {
            if (string.IsNullOrWhiteSpace(Input.CalendarId) || Input.CalendarId.Length > 1024) throw new ArgumentException("Supply a calendar ID.");
            await Client.Send(Account, HttpMethod.Post, "users/me/calendarList", new JsonObject { ["id"] = Input.CalendarId }, Ct);
            return await Client.Discover(Account, Ct);
        });
        Group.MapPut("/{account}/defaults/{calendar}", async (string Account, string Calendar, DefaultReminderInput Input, GoogleCalendarClient Client, CancellationToken Ct) =>
        {
            if (Input.Minutes is < 0 or > 40320) throw new ArgumentException("Notification minutes must be between 0 and 40320.");
            return await Client.Send(Account, HttpMethod.Patch, "users/me/calendarList/" + GoogleCalendarClient.Escape(Calendar), new JsonObject { ["defaultReminders"] = Input.Minutes is { } Minutes ? new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = Minutes }) : new JsonArray() }, Ct);
        });
        Group.MapPut("/{account}/subscriptions/{calendar}", (string Account, string Calendar, SubscriptionInput Input, CalendarStore Store, CalendarRecipients Recipients) =>
        {
            if (!Store.Get(Account).Calendars.Any(C => C.Id == Calendar)) throw new ArgumentException("Fetch this calendar before subscribing.");
            var Satellites = Input.AllSatellites ? [] : Input.SatelliteIds ?? (string.IsNullOrWhiteSpace(Input.SatelliteId) ? [] : new[] { Input.SatelliteId });
            var Clients = Input.AllRichClients ? [] : Input.RichClients ?? [];
            if (Satellites.Length > 64 || Satellites.Any(Id => string.IsNullOrWhiteSpace(Id) || Id.Length > 128)) throw new ArgumentException("Choose at most 64 valid satellites.");
            if (Clients.Length > 64 || Clients.Any(C => C is null || !Recipients.Available(C))) throw new ArgumentException("Choose available Rich API clients with their current owners. Refresh recipients if a client was revoked or reassigned.");
            Satellites = Satellites.Distinct(StringComparer.Ordinal).ToArray(); Clients = Clients.Distinct().ToArray();
            var Schedule = Input.Notifications?.Validate();
            if (Input.Announce && !Input.AllSatellites && !Input.AllRichClients && Satellites.Length + Clients.Length == 0) throw new ArgumentException("Choose at least one satellite or Rich API client for notifications.");
            Store.Update(Account, A => A with { Calendars = A.Calendars.Select(C => C.Id == Calendar ? C with { Enabled = Input.Enabled, Announce = Input.Announce, SatelliteId = "", SatelliteIds = Satellites, RichClients = Clients, AllSatellites = Input.AllSatellites, AllRichClients = Input.AllRichClients, Notifications = Schedule ?? C.Notifications } : C).ToArray() });
            return Results.NoContent();
        });
        Group.MapGet("/{account}/{calendar}/events", async (string Account, string Calendar, DateTimeOffset From, DateTimeOffset To, string? Page, GoogleCalendarClient Client, CancellationToken Ct) => await Client.Events(Account, Calendar, From, To, Page, Ct));
        Group.MapPost("/{account}/{calendar}/events", async (string Account, string Calendar, JsonObject Body, GoogleCalendarClient Client, CancellationToken Ct) =>
        {
            Client.Subscribed(Account, Calendar); GoogleCalendarClient.ValidateEvent(Body, true);
            if (!Body.ContainsKey("reminders")) Body["reminders"] = new JsonObject { ["useDefault"] = true };
            return await Client.Send(Account, HttpMethod.Post, GoogleCalendarClient.EventsPath(Calendar), Body, Ct);
        });
        Group.MapPatch("/{account}/{calendar}/events/{eventId}", async (string Account, string Calendar, string EventId, JsonObject Body, HttpContext Context, GoogleCalendarClient Client, CancellationToken Ct) =>
        {
            Client.Subscribed(Account, Calendar); GoogleCalendarClient.ValidateEvent(Body, false);
            var Etag = Context.Request.Headers.IfMatch.ToString();
            if (string.IsNullOrEmpty(Etag)) throw new ArgumentException("Refresh the event before editing (If-Match required).");
            var Path = GoogleCalendarClient.EventsPath(Calendar) + "/" + GoogleCalendarClient.Escape(EventId);
            var Previous = await Client.Send(Account, HttpMethod.Get, Path, null, Ct);
            var Times = new JsonObject { ["start"] = (Body["start"] ?? Previous["start"])!.DeepClone(), ["end"] = (Body["end"] ?? Previous["end"])!.DeepClone() };
            GoogleCalendarClient.ValidateEvent(Times, false);
            return await Client.Send(Account, HttpMethod.Patch, Path, Body, Ct, Etag);
        });
        Group.MapPost("/{account}/{calendar}/events/{eventId}/prepare-delete", async (string Account, string Calendar, string EventId, GoogleCalendarClient Client, CalendarDeletion Deletion, CancellationToken Ct) =>
        {
            Client.Subscribed(Account, Calendar);
            var Event = await Client.Send(Account, HttpMethod.Get, GoogleCalendarClient.EventsPath(Calendar) + "/" + GoogleCalendarClient.Escape(EventId), null, Ct);
            return new { confirmation = Deletion.Prepare(Account, Calendar, EventId, Event["etag"]!.GetValue<string>(), "dashboard"), summary = Event["summary"]?.GetValue<string>() };
        });
        Group.MapPost("/confirm-delete", async (ConfirmationInput Input, CalendarDeletion Deletion, GoogleCalendarClient Client, CancellationToken Ct) => await Deletion.Confirm(Input.Confirmation, "dashboard", Client, Ct));
    }
}
