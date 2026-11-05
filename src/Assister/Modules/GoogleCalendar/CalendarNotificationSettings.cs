using System.Globalization;

namespace Assister.Modules.GoogleCalendar;

public sealed record CalendarAdvanceNotice(int Amount, string Unit)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int Minutes => Unit switch { "minutes" => Amount, "hours" => checked(Amount * 60), "days" => checked(Amount * 1440), _ => throw new ArgumentException("Choose minutes, hours or days.") };
}
public sealed record CalendarNotificationSettings(CalendarAdvanceNotice[] Before, string[] DailyTimes, string TimeZone)
{
    public CalendarNotificationSettings Validate()
    {
        if (Before is null || Before.Length > 10 || DailyTimes is null || DailyTimes.Length > 4) throw new ArgumentException("Choose up to ten advance notifications and four daily agenda times.");
        foreach (var Rule in Before)
        {
            if (Rule is null || Rule.Amount < 0 || Rule.Amount > 525600 || Rule.Unit is not ("minutes" or "hours" or "days")) throw new ArgumentException("Supply a nonnegative notification amount and minutes, hours or days.");
            if ((long)Rule.Amount * (Rule.Unit == "days" ? 1440 : Rule.Unit == "hours" ? 60 : 1) > 525600) throw new ArgumentException("Advance notifications can be at most 365 days before an event.");
        }
        if (DailyTimes.Any(T => T is null || !TimeOnly.TryParseExact(T, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) throw new ArgumentException("Daily agenda times must use HH:mm.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        catch (Exception E) when (E is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Choose a valid time zone for the daily agenda."); }
        return this with { Before = Before.DistinctBy(R => R.Minutes).ToArray(), DailyTimes = DailyTimes.Distinct().Order().ToArray() };
    }
    public static DateTimeOffset LocalTime(DateOnly Date, TimeOnly Time, TimeZoneInfo Zone)
    {
        var Local = Date.ToDateTime(Time, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(Local)) Local = Local.AddMinutes(1);
        // Pick one occurrence on a fall-back day, so a daily agenda cannot repeat across both offsets.
        return new(Local, Zone.IsAmbiguousTime(Local) ? Zone.GetAmbiguousTimeOffsets(Local).Min() : Zone.GetUtcOffset(Local));
    }
}
