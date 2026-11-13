using Assister.Llm;
using Assister.Tools;

namespace Assister.Tests;

public sealed class ToolSelectionPolicyTests
{
    [Theory]
    [InlineData("what do you remember", "memory_store", false)]
    [InlineData("remember my favorite tea", "memory_store", true)]
    [InlineData("what is the weather", "weather_forecast", false)]
    [InlineData("what is the weather", "ha_get_state", true)]
    public void DefaultPolicyPreservesScopedToolSelection(string Message, string Name, bool Expected)
    {
        var Context = new ToolSelectionContext(Message, false, true, WeatherRequestKind.Current);
        Assert.Equal(Expected, new BuiltInToolSelectionPolicy().Includes(Name, Context));
    }

    [Fact]
    public void CalendarPolicyDoesNotSelectReminderToolsForCalendarRequests()
    {
        var Context = new ToolSelectionContext("schedule a calendar reminder", true, false, WeatherRequestKind.None);
        var Policy = new BuiltInToolSelectionPolicy();
        Assert.True(Policy.Includes("calendar_events", Context));
        Assert.False(Policy.Includes("reminder_create", Context));
    }
}
