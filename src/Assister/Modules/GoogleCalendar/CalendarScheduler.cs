using System.Globalization;
using System.Text.Json.Nodes;
using Assister.Contracts;
using Assister.Persistence;
using Assister.Satellites;
using Assister.Interactions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Assister.Modules.GoogleCalendar;

public sealed class CalendarScheduler(CalendarStore Settings, GoogleCalendarClient Client, IServiceScopeFactory Scopes,
    SatelliteManager Satellites, ILogger<CalendarScheduler> Logger, NotificationStore? Notifications = null,
    CalendarRecipients? Recipients = null) : BackgroundService
{
    public string? LastError { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    protected override async Task ExecuteAsync(CancellationToken Ct)
    {
        while (!Ct.IsCancellationRequested)
        {
            LastError = null;
            foreach (var Account in Settings.Snapshot())
                foreach (var Calendar in Account.Calendars.Where(C => C.Enabled && C.Announce))
                    try { await Tick(Account.Id, Calendar, DateTimeOffset.UtcNow, Ct); }
                    catch (OperationCanceledException) when (Ct.IsCancellationRequested) { return; }
                    catch (Exception Error)
                    { LastError = "Calendar polling or announcement failed. Check credentials, sharing and satellite connectivity."; Logger.LogWarning("Calendar polling failed ({FailureType}).", Error.GetType().Name); }
            LastChecked = DateTimeOffset.UtcNow;
            try { await Task.Delay(TimeSpan.FromSeconds(30), Ct); } catch (OperationCanceledException) when (Ct.IsCancellationRequested) { return; }
        }
    }
    public static DateTimeOffset? Start(JsonObject Event, string Zone)
    {
        if (Event["start"]?["dateTime"] is { } Time) return DateTimeOffset.Parse(Time.GetValue<string>(), CultureInfo.InvariantCulture);
        if (Event["start"]?["date"] is not { } Date) return null;
        var Local = DateTime.SpecifyKind(DateTime.ParseExact(Date.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        var TimeZone = TimeZoneInfo.FindSystemTimeZoneById(Event["start"]?["timeZone"]?.GetValue<string>() ?? Zone);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(Local, TimeZone), TimeSpan.Zero);
    }
    public static int[] ReminderMinutes(JsonObject Event, JsonArray Defaults)
    {
        var Reminders = Event["reminders"];
        var Items = Reminders?["useDefault"]?.GetValue<bool>() == true ? Defaults : Reminders?["overrides"] as JsonArray;
        return Items?.Where(R => R?["method"]?.GetValue<string>() is "popup" or "email")
            .Select(R => R!["minutes"]!.GetValue<int>()).Where(M => M is >= 0 and <= 40320).Distinct().ToArray() ?? [];
    }
    public async Task Tick(string Account, CalendarSubscription Calendar, DateTimeOffset Now, CancellationToken Ct)
    {
        if (Calendar.Notifications is { Before.Length: 0, DailyTimes.Length: 0 }) return;
        await using var Scope = Scopes.CreateAsyncScope();
        var SatelliteTargets = Calendar.AllSatellites
            ? await Scope.ServiceProvider.GetRequiredService<AssisterDbContext>().Satellites.AsNoTracking().Where(S => S.Enabled).Select(S => S.Id).ToArrayAsync(Ct)
            : Calendar.SatelliteTargets.Distinct(StringComparer.Ordinal).ToArray();
        var RichTargets = Recipients?.ResolveClients(Calendar) ?? [];
        if (!SatelliteTargets.Any(Id => Satellites.ConnectedIds.Contains(Id) && !Satellites.IsBusy(Id)) && RichTargets.Length == 0) return;
        var Metadata = await Client.Send(Account, HttpMethod.Get, "users/me/calendarList/" + GoogleCalendarClient.Escape(Calendar.Id), null, Ct);
        var Store = Scope.ServiceProvider.GetRequiredService<LocalStore>();
        var Speech = Scope.ServiceProvider.GetRequiredService<ITextToSpeechProvider>();
        var ZoneId = Metadata["timeZone"]?.GetValue<string>() ?? "UTC";
        var LocalSchedule = Calendar.Notifications;
        var LeadTimes = LocalSchedule?.Before.Select(R => R.Minutes).Distinct().ToArray();
        if (LocalSchedule is not null)
        {
            var Zone = TimeZoneInfo.FindSystemTimeZoneById(LocalSchedule.TimeZone);
            var Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, Zone).DateTime);
            foreach (var Date in new[] { Today.AddDays(-1), Today })
                foreach (var Time in LocalSchedule.DailyTimes)
                {
                    var Due = CalendarNotificationSettings.LocalTime(Date, TimeOnly.ParseExact(Time, "HH:mm", CultureInfo.InvariantCulture), Zone);
                    if (Due > Now || Due < Now.AddMinutes(-5)) continue;
                    var From = CalendarNotificationSettings.LocalTime(Date, TimeOnly.MinValue, Zone);
                    var To = CalendarNotificationSettings.LocalTime(Date.AddDays(1), TimeOnly.MinValue, Zone);
                    var AgendaKey = System.Text.Json.JsonSerializer.Serialize(new { Account, Calendar.Id, Date, Time, Zone = Zone.Id });
                    var Saved = await Store.QueryAsync("SELECT Payload FROM GoogleCalendarAgendas WHERE AgendaKey=$p0", Ct, AgendaKey);
                    string[] Parts;
                    if (Saved.Count != 0) Parts = System.Text.Json.JsonSerializer.Deserialize<string[]>(Saved[0][0])!;
                    else
                    {
                        var DailyEvents = await Events(From, To);
                        Parts = AgendaParts(Calendar.Name, DailyEvents, Zone, Date);
                        await Store.ExecuteAsync("INSERT OR IGNORE INTO GoogleCalendarAgendas VALUES($p0,$p1,$p2)", Ct, AgendaKey,
                            System.Text.Json.JsonSerializer.Serialize(Parts), To.AddDays(1).ToUnixTimeSeconds());
                    }
                    for (var Part = 0; Part < Parts.Length; Part++)
                        await SendToRecipients("", To, -1, Parts[Part], Parts[Part], Due, To.AddMinutes(5), AgendaKey, Part);
                }
        }
        if (LeadTimes is null || LeadTimes.Length != 0)
        {
            var LookAhead = LeadTimes is null ? 40320 : LeadTimes.Max();
            foreach (var Item in await Events(Now.AddMinutes(-5), Now.AddMinutes(LookAhead).AddMinutes(1)))
            {
                if (Item["status"]?.GetValue<string>() == "cancelled" || Start(Item, ZoneId) is not { } At || At < Now.AddMinutes(-5)) continue;
                foreach (var Minutes in LeadTimes ?? ReminderMinutes(Item, Metadata["defaultReminders"] as JsonArray ?? []))
                {
                    if (At < Now && Minutes != 0) continue;
                    var Due = At.AddMinutes(-Minutes);
                    // A five-minute grace period tolerates polling delays without replaying old notifications after downtime.
                    if (Due > Now || Due < Now.AddMinutes(-5)) continue;
                    var Title = Item["summary"]?.GetValue<string>() ?? "Untitled event";
                    if (Title.Length > 500) Title = Title[..500];
                    var StableText = "Upcoming event: " + Title + ". Starts " + At.ToString("MMMM d, yyyy 'at' h:mm tt zzz", CultureInfo.InvariantCulture) + ".";
                    var Text = Minutes >= 60 ? StableText : "Upcoming event: " + Title + (Minutes == 0 ? ". Starting now." : $". Starts in {Math.Max(1, (int)Math.Ceiling((At - Now).TotalMinutes))} minutes.");
                    await SendToRecipients(Item["id"]!.GetValue<string>(), At, Minutes, Text, StableText, Due, Minutes == 0 ? At.AddMinutes(5) : At);
                }
            }
        }
        await Store.ExecuteAsync("DELETE FROM GoogleCalendarDeliveries WHERE ExpiresAt < $p0; DELETE FROM GoogleCalendarAgendas WHERE ExpiresAt < $p0", Ct, Now.ToUnixTimeSeconds());

        async Task<List<JsonObject>> Events(DateTimeOffset From, DateTimeOffset To)
        {
            var Items = new List<JsonObject>(); string? Next = null;
            do
            {
                var Page = await Client.Events(Account, Calendar.Id, From, To, Next, Ct);
                Items.AddRange(Page["items"]!.AsArray().OfType<JsonObject>());
                Next = Page["nextPageToken"]?.GetValue<string>();
            } while (Next is not null);
            return Items;
        }

        async Task SendToRecipients(string EventId, DateTimeOffset At, int Minutes, string Text, string StableText,
            DateTimeOffset Due, DateTimeOffset Expires, string? AgendaKey = null, int Part = 0)
        {
            foreach (var Target in RichTargets)
            {
                var Key = AgendaKey is null
                    ? System.Text.Json.JsonSerializer.Serialize(new { Account, Calendar.Id, Event = EventId, At, Minutes, Target.ClientId, Target.Owner })
                    : System.Text.Json.JsonSerializer.Serialize(new { AgendaKey, Part, Target.ClientId, Target.Owner });
                await Deliver(Key, async () =>
                {
                    if (Notifications is null || Recipients?.Available(Target) != true) return false;
                    var RetryKey = "calendar:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key)));
                    Notifications.Create(Target.Owner, new("reminder", StableText, RetryKey, DueAt: Due,
                        ExpiresAt: Expires, TargetClientId: Target.ClientId));
                    await Task.CompletedTask;
                    return true; // Persisted for the client to receive; never a claim of display/playback.
                });
            }
            foreach (var Target in SatelliteTargets)
            {
                // Keep the original key shape so a migrated single-target subscription cannot replay delivery.
                var Key = AgendaKey is null
                    ? System.Text.Json.JsonSerializer.Serialize(new { Account, Calendar.Id, Event = EventId, At, Minutes, SatelliteId = Target })
                    : System.Text.Json.JsonSerializer.Serialize(new { AgendaKey, Part, SatelliteId = Target });
                await Deliver(Key, async () => Satellites.ConnectedIds.Contains(Target) && !Satellites.IsBusy(Target)
                    && await Satellites.AnnounceAsync(Target, Text, Speech, Ct));
            }

            async Task Deliver(string Key, Func<Task<bool>> Send)
            {
                if ((await Store.QueryAsync("SELECT DeliveryKey FROM GoogleCalendarDeliveries WHERE DeliveryKey=$p0", Ct, Key)).Count != 0) return;
                try
                {
                    if (await Send()) await Store.ExecuteAsync("INSERT OR IGNORE INTO GoogleCalendarDeliveries VALUES($p0,$p1)", Ct, Key, At.AddDays(1).ToUnixTimeSeconds());
                }
                catch (OperationCanceledException) when (Ct.IsCancellationRequested) { throw; }
                catch (Exception Error)
                {
                    LastError = "A calendar notification recipient could not be reached. Other recipients continue independently.";
                    Logger.LogWarning("Calendar recipient delivery deferred ({FailureType}).", Error.GetType().Name);
                }
            }
        }
    }

    public static string[] AgendaParts(string CalendarName, IReadOnlyList<JsonObject> Events, TimeZoneInfo Zone, DateOnly Date)
    {
        var Lines = new List<string>();
        foreach (var Item in Events.Where(E => E["status"]?.GetValue<string>() != "cancelled"))
        {
            var At = Start(Item, Zone.Id);
            if (At is null) continue;
            var Local = TimeZoneInfo.ConvertTime(At.Value, Zone);
            var When = Item["start"]?["date"] is not null ? "All day" : Local.ToString(DateOnly.FromDateTime(Local.DateTime) == Date ? "h:mm tt" : "MMM d 'at' h:mm tt", CultureInfo.InvariantCulture);
            var Title = Item["summary"]?.GetValue<string>() ?? "Untitled event";
            Lines.Add(When + ": " + Title[..Math.Min(Title.Length, 500)] + ".");
        }
        CalendarName = CalendarName[..Math.Min(CalendarName.Length, 100)];
        var Header = "Today's events for " + CalendarName + ": " + (Lines.Count == 0 ? "No events today." : Lines.Count + " event" + (Lines.Count == 1 ? ". " : "s. "));
        var Parts = new List<string>(); var Text = Header;
        foreach (var Line in Lines)
        {
            if (Text.Length + Line.Length + 1 > 1700) { Parts.Add(Text); Text = "Today's events for " + CalendarName + ", continued. "; }
            Text += Line + " ";
        }
        Parts.Add(Text.TrimEnd());
        return Parts.ToArray();
    }
}
