using System.Globalization;

namespace Assister.Components.Services;

/// <summary>Circuit-local browser time zone; never uses the server's local time zone.</summary>
public sealed class BrowserClock
{
    private TimeZoneInfo? zone;
    private CultureInfo culture = CultureInfo.InvariantCulture;
    public void Configure(string timeZone, string locale)
    {
        zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        culture = CultureInfo.GetCultureInfo(locale);
    }
    public DateTimeOffset Now => Convert(DateTimeOffset.UtcNow);
    public DateTimeOffset Convert(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone ?? TimeZoneInfo.Utc);
    public string Format(string? iso) => zone is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
        ? Convert(instant).ToString("g", culture) : "—";
    public string Input(string iso) => Convert(DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture)).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    // An unchanged editor value must retain the exact instant, including seconds and an ambiguous DST offset.
    public bool Unchanged(string input, string? original) => !string.IsNullOrEmpty(original) && Input(original) == input;
}
