namespace Assister.Llm;

public static class LocalClock
{
    public static DateTimeOffset At(DateTimeOffset Utc, string? TimeZone)
        => TimeZoneInfo.ConvertTime(Utc, TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(TimeZone) ? "America/New_York" : TimeZone));
}
