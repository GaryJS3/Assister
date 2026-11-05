using System.Text.Json;
using Assister.Components.Services;

namespace Assister.Tests;

public class BlazorFormattingTests
{
    [Theory]
    [InlineData("false", "false")]
    [InlineData("true", "true")]
    [InlineData("42", "42")]
    public void JsonValuesMatchHtmlOptionValues(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.String());
    }

    [Theory]
    [InlineData("America/New_York", "2026-11-05T09:55")]
    [InlineData("Europe/London", "2026-11-05T14:55")]
    [InlineData("Asia/Tokyo", "2026-11-05T23:55")]
    public void EditorUsesBrowserZone(string zone, string expected)
    {
        var clock = new BrowserClock(); clock.Configure(zone, "en-US");
        Assert.Equal(expected, clock.Input("2026-11-05T09:55:00-05:00"));
        Assert.True(clock.Unchanged(expected, "2026-11-05T09:55:00-05:00"));
        Assert.False(clock.Unchanged("2026-11-06T09:55", "2026-11-05T09:55:00-05:00"));
    }

    [Theory]
    [InlineData("2026-11-01T01:30:45-04:00")]
    [InlineData("2026-11-01T01:30:45-05:00")]
    public void UnchangedEditorPreservesBothRepeatedDstInstants(string iso)
    {
        var clock = new BrowserClock(); clock.Configure("America/New_York", "en-US");
        Assert.True(clock.Unchanged("2026-11-01T01:30", iso));
    }

    [Fact]
    public void DatesCrossMidnightInTheBrowserZone()
    {
        var clock = new BrowserClock(); clock.Configure("America/New_York", "en-US");
        Assert.Equal("11/4/2026 9:00 PM", clock.Format("2026-11-05T02:00:00Z"));
    }
}
