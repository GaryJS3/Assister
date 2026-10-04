using Assister.Intents;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class IntentEngineTests
{
    private static IntentDefinition Brightness(string Id = "dim-lights") => new(Id, "Dim a light", "SetBrightness", true,
        ["dim {target:light} to {brightness:percent} percent"]);

    [Fact]
    public void TemplatesExtractTypedSlotsAndValidateOutOfRangeValues()
    {
        var Definition = Brightness();
        IntentStore.Validate(Definition);
        var Result = IntentMatching.Match("Please dim the desk to 40 percent.", [Definition], new());
        Assert.Equal("matched", Result.Status);
        Assert.Equal("desk", Result.Match!.Intent!.Target);
        Assert.Equal("light", Result.Match.Intent.TargetDomain);
        Assert.Equal(40, Result.Match.Intent.BrightnessPercent);
        Assert.Equal("invalid-request", IntentMatching.Match("dim desk to 101 percent", [Definition], new()).Status);
        Assert.Equal("invalid-request", IntentMatching.Match("dim desk to -1 percent", [Definition], new()).Status);
        Assert.Equal("unmatched", IntentMatching.Match("dim desk to forty percent", [Definition], new()).Status);
    }

    [Theory]
    [InlineData("{target}")]
    [InlineData("dim {target}{area}")]
    [InlineData("dim {target} and {target}")]
    [InlineData("dim {unknown}")]
    [InlineData("dim {brightness:light}")]
    public void MalformedOrOverbroadTemplatesAreRejected(string Pattern)
    {
        Assert.Throws<ArgumentException>(() => IntentTemplate.Compile(Pattern));
    }

    [Fact]
    public void LiteralPhrasesCannotInjectRegexAndMultipleRulesDoNotSilentlyWin()
    {
        var Reply = new IntentDefinition("hello", "Greeting", "Reply", true, ["hello .*"], Response: "Hi.");
        Assert.Equal("unmatched", IntentMatching.Match("hello anything", [Reply], new()).Status);
        Assert.Equal("matched", IntentMatching.Match("hello .*", [Reply], new()).Status);
        var Conflicting = new[] { Brightness(), Brightness("other-dim") };
        Assert.Equal("ambiguous", IntentMatching.Match("dim desk to 40 percent", Conflicting, new()).Status);
        Assert.Null(IntentMatching.Match("dim desk to 40 percent", Conflicting, new()).Match);
    }

    [Fact]
    public void BuiltInAliasesPreserveNativeRulesAndDisabledRulesStopMatching()
    {
        var Definition = IntentCatalog.BuiltIns.Single(Row => Row.Handler == "TurnOff")
            with { Patterns = ["turn {target:light} off", "disable {target:light}"] };
        Assert.Equal("matched", IntentMatching.Match("turn desk light off", [Definition], new()).Status);
        Assert.Equal("light", IntentMatching.Match("turn desk light off", [Definition], new()).Match!.Intent!.TargetDomain);
        Assert.Equal("matched", IntentMatching.Match("disable desk", [Definition], new()).Status);
        Assert.Equal("unmatched", IntentMatching.Match("turn desk light off", [Definition with { Enabled = false }], new()).Status);
        Assert.Equal("timer-route", IntentMatching.Match("set a timer for 5 minutes", [Definition], new()).Status);
    }

    [Fact]
    public void FixedSlotsAndNativeClockResponsesDoNotNeedTheModel()
    {
        var Movie = new IntentDefinition("movie", "Movie lighting", "SetBrightness", true, ["movie lighting"], "light.living_room_lights", 20, Response: "Set {target} to {brightness} percent.");
        IntentStore.Validate(Movie);
        var Match = IntentMatching.Match("movie lighting", [Movie], new()).Match!;
        Assert.Equal(20, Match.Intent!.BrightnessPercent);
        Assert.Equal("Set light.living_room_lights to 20 percent.", IntentResponses.Render(Match, "Done.", new ConfigurationBuilder().Build()));
        var Time = IntentMatching.Match("What time is it?", IntentCatalog.BuiltIns, new()).Match!;
        Assert.Equal("It is 12:00 PM.", IntentResponses.Render(Time, "", new ConfigurationBuilder().Build(), new DateTimeOffset(2026, 10, 4, 16, 0, 0, TimeSpan.Zero)));
    }
}
