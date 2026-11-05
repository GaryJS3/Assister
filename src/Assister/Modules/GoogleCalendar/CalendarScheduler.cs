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
        await using var Scope = Scopes.CreateAsyncScope();
        var SatelliteTargets = Calendar.AllSatellites
            ? await Scope.ServiceProvider.GetRequiredService<AssisterDbContext>().Satellites.AsNoTracking().Where(S => S.Enabled).Select(S => S.Id).ToArrayAsync(Ct)
            : Calendar.SatelliteTargets.Distinct(StringComparer.Ordinal).ToArray();
        var RichTargets = Recipients?.ResolveClients(Calendar) ?? [];
        if (!SatelliteTargets.Any(Id => Satellites.ConnectedIds.Contains(Id) && !Satellites.IsBusy(Id)) && RichTargets.Length == 0) return;
        var Metadata = await Client.Send(Account, HttpMethod.Get, "users/me/calendarList/" + GoogleCalendarClient.Escape(Calendar.Id), null, Ct);
        var Store = Scope.ServiceProvider.GetRequiredService<LocalStore>();
        var Speech = Scope.ServiceProvider.GetRequiredService<ITextToSpeechProvider>();
        string? Next = null;
        do
        {
            var Page = await Client.Events(Account, Calendar.Id, Now.AddMinutes(-5), Now.AddDays(28).AddMinutes(1), Next, Ct);
            foreach (var Item in Page["items"]!.AsArray().OfType<JsonObject>())
            {
                if (Item["status"]?.GetValue<string>() == "cancelled" || Start(Item, Metadata["timeZone"]?.GetValue<string>() ?? "UTC") is not { } At || At < Now.AddMinutes(-5)) continue;
                foreach (var Minutes in ReminderMinutes(Item, Metadata["defaultReminders"] as JsonArray ?? []))
                {
                    if (At < Now && Minutes != 0) continue;
                    var Due = At.AddMinutes(-Minutes);
                    // A five-minute grace period tolerates polling delays without replaying old notifications after downtime.
                    if (Due > Now || Due < Now.AddMinutes(-5)) continue;
                    var Text = "Upcoming event: " + (Item["summary"]?.GetValue<string>() ?? "Untitled event") + (Minutes == 0 ? ". Starting now." : $". Starts in {Math.Max(1, (int)Math.Ceiling((At - Now).TotalMinutes))} minutes.");
                    foreach (var Target in RichTargets)
                    {
                        var Key = System.Text.Json.JsonSerializer.Serialize(new { Account, Calendar.Id, Event = Item["id"]!.GetValue<string>(), At, Minutes, Target.ClientId, Target.Owner });
                        await Deliver(Key, async () =>
                        {
                            if (Notifications is null || Recipients?.Available(Target) != true) return false;
                            // Deterministic request fields let the notification store deduplicate a retry after a crash.
                            var Title = Item["summary"]?.GetValue<string>() ?? "Untitled event";
                            if (Title.Length > 500) Title = Title[..500];
                            var StableText = "Upcoming event: " + Title + ". Starts " + At.ToString("MMMM d, yyyy 'at' h:mm tt zzz", CultureInfo.InvariantCulture) + ".";
                            var RetryKey = "calendar:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key)));
                            Notifications.Create(Target.Owner, new("reminder", StableText, RetryKey, DueAt: Due,
                                ExpiresAt: Minutes == 0 ? At.AddMinutes(5) : At, TargetClientId: Target.ClientId));
                            await Task.CompletedTask;
                            return true; // Persisted for the client to receive; never a claim of display/playback.
                        });
                    }
                    foreach (var Target in SatelliteTargets)
                    {
                        // Keep the original key shape so a migrated single-target subscription cannot replay delivery.
                        var Key = System.Text.Json.JsonSerializer.Serialize(new { Account, Calendar.Id, Event = Item["id"]!.GetValue<string>(), At, Minutes, SatelliteId = Target });
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
            Next = Page["nextPageToken"]?.GetValue<string>();
        } while (Next is not null);
        await Store.ExecuteAsync("DELETE FROM GoogleCalendarDeliveries WHERE ExpiresAt < $p0", Ct, Now.ToUnixTimeSeconds());
    }
}
