using System.Globalization;
using System.Text.RegularExpressions;
using Assister.Contracts;
using Assister.Persistence;

namespace Assister.Modules.Reminders;

public sealed record ReminderSchedule(int Hour, int Minute, DayOfWeek? Weekday)
{
    public DateTimeOffset Next(DateTimeOffset After, TimeZoneInfo Zone)
    {
        var Date = TimeZoneInfo.ConvertTime(After, Zone).Date;
        for (var Index = 0; Index < 9; Index++, Date = Date.AddDays(1))
        {
            if (Weekday is { } Day && Date.DayOfWeek != Day) { continue; }
            var Local = Date.AddHours(Hour).AddMinutes(Minute);
            while (Zone.IsInvalidTime(Local)) { Local = Local.AddMinutes(1); }
            var Offset = Zone.IsAmbiguousTime(Local) ? Zone.GetAmbiguousTimeOffsets(Local).Min() : Zone.GetUtcOffset(Local);
            var Candidate = new DateTimeOffset(Local, Offset).ToUniversalTime();
            if (Candidate > After) { return Candidate; }
        }
        throw new InvalidOperationException("Unable to calculate next occurrence.");
    }
}

public sealed partial class ReminderIntentHandler(LocalStore Store, IConfiguration Configuration,
    Assister.Interactions.RichNotificationBridge? Rich = null)
{
    [GeneratedRegex(@"^remind me to (?<message>.+?) (?<schedule>every night|every evening|every morning|every day|daily|(?:every |on )?(?:sunday|monday|tuesday|wednesday|thursday|friday|saturday)(?: night| evening| morning)?) at (?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<period>am|pm)?[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreatePattern();

    public static bool Recognizes(string Text) => Text.Trim().StartsWith("remind me", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(Text.Trim(), @"^(?:(?:list|show)(?: me)?(?: my)? reminders|what are my reminders|cancel (?:my )?reminder .+)[.!?]?$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    public async Task<string?> TryHandleAsync(UserRequest Request, CancellationToken Token, bool AllowModelFallback = false)
    {
        var Text = Request.Message.Trim();
        var RichClient = Rich?.Owner(Request.SatelliteId) is not null;
        var Broadcast = RichClient ? Request.SatelliteId : "all";
        if (!Recognizes(Text)) { return null; }
        if (!Text.StartsWith("remind me", StringComparison.OrdinalIgnoreCase))
        {
            var Rows = await Store.QueryAsync("SELECT Id,Message,NextDueAt FROM Reminders WHERE (SatelliteId=$p0 OR SatelliteId=$p1) AND Status='active' ORDER BY NextDueAt LIMIT 100", Token, Request.SatelliteId, Broadcast);
            var Cancel = Regex.Match(Text, @"^cancel (?:my )?reminder (?<name>.+?)[.!]?$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (Cancel.Success)
            {
                var Name = Cancel.Groups["name"].Value;
                var Matches = Rows.Where(Row => Row[0].Equals(Name, StringComparison.OrdinalIgnoreCase) || Row[1].Equals(Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (Matches.Length != 1) { return Matches.Length == 0 ? "There is no matching reminder on this satellite." : "Several reminders have that name. Please cancel using its ID from the reminder list."; }
                await Store.ExecuteAsync("UPDATE Reminders SET Status='cancelled' WHERE Id=$p0", Token, Matches[0][0]);
                return $"Cancelled your reminder to {Matches[0][1]}.";
            }
            return Rows.Count == 0 ? "There are no active reminders on this satellite." : string.Join(" ", Rows.Select(Row => $"{Row[1]} (ID {Row[0]}), next at {LocalTime(long.Parse(Row[2], CultureInfo.InvariantCulture))}."));
        }
        var Match = CreatePattern().Match(Text);
        if (!Match.Success && AllowModelFallback) { return null; }
        if (!Match.Success) { return "Try: remind me to take my vitamin every night at 8, or remind me to take out the trash every Sunday night at 6. Include AM or PM when the time is ambiguous."; }
        var Hour = int.Parse(Match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var Minute = Match.Groups["minute"].Success ? int.Parse(Match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        var Period = Match.Groups["period"].Value.ToLowerInvariant();
        var ScheduleText = Match.Groups["schedule"].Value.ToLowerInvariant();
        if (Minute > 59 || Hour > 23 || (Period.Length > 0 && (Hour < 1 || Hour > 12))) { return "Please use a valid time, such as 8 PM or 20:00."; }
        if (Period.Length == 0 && Hour is >= 1 and <= 12)
        {
            Period = ScheduleText.Contains("night") || ScheduleText.Contains("evening") ? "pm" : ScheduleText.Contains("morning") ? "am" : "";
            if (Period.Length == 0) { return "Please repeat the reminder with AM or PM, or use a 24-hour time."; }
        }
        if (Period.Length > 0) { Hour = Hour % 12 + (Period == "pm" ? 12 : 0); }
        DayOfWeek? Weekday = null;
        foreach (var Day in Enum.GetValues<DayOfWeek>())
        {
            if (ScheduleText.Contains(Day.ToString(), StringComparison.OrdinalIgnoreCase)) { Weekday = Day; break; }
        }
        var Zone = TimeZoneInfo.FindSystemTimeZoneById(Configuration["Assister:TimeZone"] ?? "America/New_York");
        var Message = Match.Groups["message"].Value.Trim();
        if (Message.Length > 500) { return "Please keep the reminder message under 500 characters."; }
        var Count = await Store.QueryAsync("SELECT Id FROM Reminders WHERE (SatelliteId=$p0 OR SatelliteId=$p1) AND Status='active' LIMIT 100", Token, Request.SatelliteId, Broadcast);
        if (Count.Count >= 100) { return "This satellite already has 100 reminders. Cancel one first."; }
        var Next = new ReminderSchedule(Hour, Minute, Weekday).Next(DateTimeOffset.UtcNow, Zone);
        await Store.ExecuteAsync("INSERT INTO Reminders(Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)", Token,
            Guid.NewGuid().ToString(), Message, Request.SatelliteId, Zone.Id, Hour, Minute, Weekday is { } D ? (int)D : null, Next.ToUnixTimeSeconds());
        return $"I'll remind you to {Message} {(Weekday is { } W ? "every " + W : "every day")} at {Hour:00}:{Minute:00} ({Zone.Id}), {(RichClient ? "by notification to your clients" : "by announcement on this satellite")}. Next: {TimeZoneInfo.ConvertTime(Next, Zone):ddd, MMM d HH:mm}.";
    }

    private string LocalTime(long Timestamp) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(Timestamp),
        TimeZoneInfo.FindSystemTimeZoneById(Configuration["Assister:TimeZone"] ?? "America/New_York")).ToString("ddd, MMM d HH:mm zzz", CultureInfo.InvariantCulture);
}
