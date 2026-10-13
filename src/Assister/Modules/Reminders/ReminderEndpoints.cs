using System.Globalization;
using Assister.Persistence;

namespace Assister.Modules.Reminders;

public sealed record ReminderInput(string Message, string SatelliteId, int Hour, int Minute, int? Weekday, string? TimeZone);
public sealed record ReminderItem(string Id, string Message, string SatelliteId, string TimeZone, int Hour, int Minute, int? Weekday, DateTimeOffset NextDueAt);

public static class ReminderEndpoints
{
    public static void MapReminders(this WebApplication App)
    {
        App.MapGet("/api/reminders", async (int? Offset, LocalStore Store, IConfiguration Config, CancellationToken Token) =>
        {
            if (Offset is < 0) { return Results.BadRequest(new { Error = "Offset must be nonnegative." }); }
            var Rows = await Store.QueryAsync("SELECT Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt FROM Reminders WHERE Status='active' ORDER BY NextDueAt,Id LIMIT 100 OFFSET $p0", Token, Offset ?? 0);
            return Results.Ok(new { Items = Rows.Select(Item), DefaultTimeZone = Config["Assister:TimeZone"] ?? "America/New_York" });
        });
        App.MapPost("/api/reminders", async (ReminderInput Input, LocalStore Store, IConfiguration Config, CancellationToken Token) =>
        {
            var Error = Validate(Input, Config, out var Zone);
            if (Error is not null) { return Results.BadRequest(new { Error }); }
            var Id = Guid.NewGuid().ToString();
            var Next = new ReminderSchedule(Input.Hour, Input.Minute, Input.Weekday is { } Day ? (DayOfWeek)Day : null).Next(DateTimeOffset.UtcNow, Zone!);
            var Added = await Store.ExecuteAsync("""
                INSERT INTO Reminders(Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt)
                SELECT $p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7
                WHERE (SELECT COUNT(*) FROM Reminders WHERE SatelliteId=$p2 AND Status='active') < 100
                """, Token, Id, Input.Message.Trim(), Input.SatelliteId.Trim(), Zone!.Id, Input.Hour, Input.Minute, Input.Weekday, Next.ToUnixTimeSeconds());
            return Added == 0 ? Results.Conflict(new { Error = "This satellite already has 100 reminders." }) : Results.Created("/api/reminders/" + Id, new { Id });
        });
        App.MapPut("/api/reminders/{id:guid}", async (Guid Id, ReminderInput Input, LocalStore Store, IConfiguration Config, CancellationToken Token) =>
        {
            var Error = Validate(Input, Config, out var Zone);
            if (Error is not null) { return Results.BadRequest(new { Error }); }
            var Rows = await Store.QueryAsync("SELECT Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt FROM Reminders WHERE Id=$p0 AND Status='active'", Token, Id.ToString());
            if (Rows.Count == 0) { return Results.NotFound(); }
            var Previous = Item(Rows[0]);
            var Changed = Previous.Hour != Input.Hour || Previous.Minute != Input.Minute || Previous.Weekday != Input.Weekday || Previous.TimeZone != Zone!.Id;
            var Next = Changed ? new ReminderSchedule(Input.Hour, Input.Minute, Input.Weekday is { } Day ? (DayOfWeek)Day : null).Next(DateTimeOffset.UtcNow, Zone!) : Previous.NextDueAt;
            var Updated = await Store.ExecuteAsync("""
                UPDATE Reminders SET Message=$p0,SatelliteId=$p1,TimeZone=$p2,Hour=$p3,Minute=$p4,Weekday=$p5,NextDueAt=$p6
                WHERE Id=$p7 AND Status='active'
                AND (SELECT COUNT(*) FROM Reminders WHERE SatelliteId=$p1 AND Status='active' AND Id<>$p7) < 100
                """, Token, Input.Message.Trim(), Input.SatelliteId.Trim(), Zone!.Id, Input.Hour, Input.Minute, Input.Weekday, Next.ToUnixTimeSeconds(), Id.ToString());
            return Updated == 0 ? Results.Conflict(new { Error = "Reminder changed or the target satellite has 100 reminders. Refresh and try again." }) : Results.NoContent();
        });
        App.MapDelete("/api/reminders/{id:guid}", async (Guid Id, LocalStore Store, CancellationToken Token) =>
            await Store.ExecuteAsync("UPDATE Reminders SET Status='cancelled' WHERE Id=$p0 AND Status='active'", Token, Id.ToString()) == 0 ? Results.NotFound() : Results.NoContent());
    }

    private static ReminderItem Item(string[] Row) => new(Row[0], Row[1], Row[2], Row[3], int.Parse(Row[4], CultureInfo.InvariantCulture),
        int.Parse(Row[5], CultureInfo.InvariantCulture), Row[6] == "" ? null : int.Parse(Row[6], CultureInfo.InvariantCulture),
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(Row[7], CultureInfo.InvariantCulture)));

    private static string? Validate(ReminderInput Input, IConfiguration Config, out TimeZoneInfo? Zone)
    {
        Zone = null;
        if (string.IsNullOrWhiteSpace(Input.Message) || Input.Message.Trim().Length > 500) { return "Enter a reminder message of 1 to 500 characters."; }
        if (string.IsNullOrWhiteSpace(Input.SatelliteId) || Input.SatelliteId.Trim().Length > 128) { return "Enter a satellite ID of 1 to 128 characters."; }
        if (Input.Hour is < 0 or > 23 || Input.Minute is < 0 or > 59 || Input.Weekday is < 0 or > 6) { return "Choose a valid time and recurrence."; }
        try { Zone = TimeZoneInfo.FindSystemTimeZoneById(Input.TimeZone ?? Config["Assister:TimeZone"] ?? "America/New_York"); }
        catch (Exception Error) when (Error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return "Enter a valid time zone, such as America/New_York."; }
        return null;
    }
}
