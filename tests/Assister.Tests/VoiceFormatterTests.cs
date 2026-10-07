using Assister.Voice;

namespace Assister.Tests;

public sealed class VoiceFormatterTests
{
    [Theory]
    [InlineData("The highest wind speed recorded today was 13.2 mph.", "The highest wind speed recorded today was 13.2 miles per hour.")]
    [InlineData("Wind is 0 MPH, gusting to 10 Mph.", "Wind is 0 miles per hour, gusting to 10 miles per hour.")]
    [InlineData("It is **74°F** with wind at `5 mph`.", "It is 74 degrees Fahrenheit with wind at 5 miles per hour.")]
    [InlineData("The mphometer is unavailable.", "The mphometer is unavailable.")]
    public void ExpandsWindUnitsWithoutChangingOtherWords(string Raw, string Spoken)
    {
        Assert.Equal(Spoken, VoiceFormatter.Format(Raw));
        Assert.Equal(Spoken, VoiceFormatter.Format(Spoken));
    }
}
