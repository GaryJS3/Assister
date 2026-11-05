using System.Globalization;
using System.Text.RegularExpressions;

namespace Assister.Modules.Reminders;

public static class ReminderRequestPolicy
{
    private static Match Match(string Text, string Pattern) => Regex.Match(Text, Pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool Recurring(string Text) => Match(Text,
        @"\b(?:every|each|daily|weekly|nightly|on (?:sunday|monday|tuesday|wednesday|thursday|friday|saturday))\b").Success;

    public static bool Alarm(string Text) => Match(Text, @"\balarm\b").Success;

    public static (int Hour, int Minute)? Clock(string Text)
    {
        var Found = Match(Text, @"\b(?:at|for) (?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<period>am|pm)?\b");
        if (!Found.Success) return null;
        var Hour = int.Parse(Found.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var Minute = Found.Groups["minute"].Success ? int.Parse(Found.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (Found.Groups["period"].Success) Hour = Hour % 12 + (Found.Groups["period"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        return (Hour, Minute);
    }

    public static string Confirmation(ReminderItem Item) => $"I've set {(Item.OneTime ? "a one-time" : Item.Weekday is null ? "a daily" : "a weekly")} {(Item.Importance == "alarm" ? "alarm reminder" : "reminder")} for {TimeZoneInfo.ConvertTime(Item.NextDueAt, TimeZoneInfo.FindSystemTimeZoneById(Item.TimeZone)):MMM d, h:mm:ss tt} to {Item.Message}.";

    // A relative duration is computed from request arrival, never the model's rounded clock.
    public static DateTimeOffset? Relative(string Text, DateTimeOffset Now)
    {
        var Found = Match(Text, @"\bin (?<amount>\d{1,6}|a|an|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|sixty) (?<unit>seconds?|minutes?|hours?|days?)(?=\s+to\b|[.!?]?$)");
        if (!Found.Success) return null;
        var Amount = Found.Groups["amount"].Value.ToLowerInvariant();
        var Words = new Dictionary<string, int> { ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3,
            ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
            ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30, ["sixty"] = 60 };
        var Count = Words.TryGetValue(Amount, out var Value) ? Value : int.Parse(Amount, CultureInfo.InvariantCulture);
        var Seconds = Count * (Found.Groups["unit"].Value.ToLowerInvariant()[0] switch { 'd' => 86400L, 'h' => 3600L, 'm' => 60L, _ => 1L });
        if (Seconds is <= 0 or > 31536000) return null;
        return Now.AddSeconds(Seconds);
    }

    public static string? ValidateTime(string Text)
    {
        if (Match(Text, @"\bin\b").Success && !Match(Text, @"\b(?:at|for) \d").Success)
            return Relative(Text, DateTimeOffset.UnixEpoch) is not null ? null : "Please give one relative duration between one second and 365 days, such as in 90 minutes.";
        var Clock = Match(Text, @"\b(?:at|for) (?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<period>am|pm)?\b");
        if (!Clock.Success) return "What time should I remind you? Include AM or PM, or use a 24-hour time.";
        var Hour = int.Parse(Clock.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var Minute = Clock.Groups["minute"].Success ? int.Parse(Clock.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (Hour > 23 || Minute > 59 || Clock.Groups["period"].Success && Hour is < 1 or > 12)
            return "Please give a valid reminder time.";
        return Hour is >= 1 and <= 12 && !Clock.Groups["period"].Success
            ? "Do you mean AM or PM? Please include it in the reminder request." : null;
    }
}
