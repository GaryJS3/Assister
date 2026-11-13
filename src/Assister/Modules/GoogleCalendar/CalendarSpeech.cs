using System.Globalization;

namespace Assister.Modules.GoogleCalendar;

internal static class CalendarSpeech
{
    public static string Date(DateOnly Value, bool IncludeYear = false)
    {
        var Suffix = Value.Day is >= 11 and <= 13 ? "th" : (Value.Day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };
        var Day = Value.ToString("dddd, MMMM d", CultureInfo.InvariantCulture) + Suffix;
        return IncludeYear ? Day + ", " + Value.Year.ToString(CultureInfo.InvariantCulture) : Day;
    }

    public static string Time(DateTimeOffset Value)
    {
        if (Value.Hour == 0 && Value.Minute == 0) return "midnight";
        if (Value.Hour == 12 && Value.Minute == 0) return "noon";
        if (Value.Hour == 0) return Value.Minute.ToString(CultureInfo.InvariantCulture)
            + (Value.Minute == 1 ? " minute after midnight" : " minutes after midnight");

        var Hour = Value.ToString("%h", CultureInfo.InvariantCulture);
        var Minutes = Value.Minute switch
        {
            0 => "",
            < 10 => " oh " + Value.Minute.ToString(CultureInfo.InvariantCulture),
            _ => " " + Value.Minute.ToString(CultureInfo.InvariantCulture)
        };
        var Period = Value.Hour switch
        {
            < 12 => " in the morning",
            < 18 => " in the afternoon",
            _ => " in the evening"
        };
        return Hour + Minutes + Period;
    }
}
