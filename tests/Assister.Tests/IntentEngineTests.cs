using Assister.Intents;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class IntentEngineTests
{
    [Theory]
    [InlineData(50, "matched")]
    [InlineData(101, "invalid-request")]
    public void FanSpeedTemplateUsesItsOwnNumericSlot(int Speed, string Status)
    {
        var Definition = new IntentDefinition("fan-alias", "Fan alias", "home-assistant.set-fan-speed", true,
            ["spin {target:fan} at {speed:percent} percent"]);
        IntentStore.Validate(Definition);
        var Decision = IntentMatching.Match($"spin office fan at {Speed} percent", [Definition], new IntentClassifier());
        Assert.Equal(Status, Decision.Status);
        var Intent = Assert.Single(Decision.Candidates).Intent!;
        Assert.Equal(Speed, Intent.SpeedPercent);
        Assert.Null(Intent.BrightnessPercent);
        Assert.Equal("fan", Intent.TargetDomain);
    }

    [Fact]
    public void RegistryRequiresUniqueQualifiedActionsAndValidationUsesRegisteredInputs()
    {
        var Registry = IntentActionRegistry.Default;
        Assert.Equal(2, Registry.Integrations.Length);
        Assert.Equal(9, Registry.Actions.Length);
        Assert.Throws<ArgumentException>(() => new IntentActionRegistry([new HomeAssistantIntentActions(), new HomeAssistantIntentActions()]));
        Assert.Throws<ArgumentException>(() => Registry.Get("SetBrightness"));
        Assert.Throws<ArgumentException>(() => IntentStore.Validate(new("bad-input", "Bad input", "assister.reply", true, ["hello {target}"], Response: "Hello.")));
        Assert.Throws<ArgumentException>(() => IntentStore.Validate(new("missing-input", "Missing target", "home-assistant.turn-on", true, ["turn on something"])));
    }

    private static IntentDefinition Brightness(string Id = "dim-lights") => new(Id, "Dim a light", "home-assistant.set-brightness", true,
        ["dim {target:light} to {brightness:percent} percent"]);

    [Fact]
    public async Task DispatcherRequiresAnExecutorAndRejectsMismatchedDeviceActions()
    {
        var Executor = new RecordingExecutor();
        var Dispatcher = new IntegrationActionDispatcher(IntentActionRegistry.Default, [Executor]);
        var Candidate = IntentMatching.Match("dim desk to 40 percent", [Brightness()], new()).Match!;
        Assert.True(Dispatcher.CanExecute(Candidate.Definition.ActionId));
        Assert.False(Dispatcher.CanExecute("assister.reply"));
        await Dispatcher.ExecuteAsync(Candidate, null, CancellationToken.None);
        Assert.Equal(1, Executor.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatcher.ExecuteAsync(Candidate with { Intent = new(DirectIntentKind.TurnOff, "desk") }, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatcher.ExecuteAsync(new(new("reply", "Reply", "assister.reply", true, ["hello"], Response: "Hi."), null, "hello"), null, CancellationToken.None));
        Assert.Equal(1, Executor.Calls);
    }

    private sealed class RecordingExecutor : IIntegrationIntentExecutor
    {
        public string IntegrationId => "home-assistant";
        public int Calls { get; private set; }
        public Task<IntentResult> ExecuteAsync(IntentCandidate Candidate, EntityResolutionResult? Resolution, CancellationToken Token)
        {
            Calls++;
            return Task.FromResult(new IntentResult("Done.", "succeeded"));
        }
    }

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
        var Reply = new IntentDefinition("hello", "Greeting", "assister.reply", true, ["hello .*"], Response: "Hi.");
        Assert.Equal("unmatched", IntentMatching.Match("hello anything", [Reply], new()).Status);
        Assert.Equal("matched", IntentMatching.Match("hello .*", [Reply], new()).Status);
        var Conflicting = new[] { Brightness(), Brightness("other-dim") };
        Assert.Equal("ambiguous", IntentMatching.Match("dim desk to 40 percent", Conflicting, new()).Status);
        Assert.Null(IntentMatching.Match("dim desk to 40 percent", Conflicting, new()).Match);
    }

    [Fact]
    public void BuiltInAliasesPreserveNativeRulesAndDisabledRulesStopMatching()
    {
        var Definition = IntentCatalog.BuiltIns.Single(Row => Row.ActionId == "home-assistant.turn-off")
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
        var Movie = new IntentDefinition("movie", "Movie lighting", "home-assistant.set-brightness", true, ["movie lighting"], "light.living_room_lights", 20, Response: "Set {target} to {brightness} percent.");
        IntentStore.Validate(Movie);
        var Match = IntentMatching.Match("movie lighting", [Movie], new()).Match!;
        Assert.Equal(20, Match.Intent!.BrightnessPercent);
        Assert.Equal("Set light.living_room_lights to 20 percent.", IntentResponses.Render(Match, "Done.", new ConfigurationBuilder().Build()));
        var Time = IntentMatching.Match("What time is it?", IntentCatalog.BuiltIns, new()).Match!;
        Assert.Equal("It is 12:00 PM.", IntentResponses.Render(Time, "", new ConfigurationBuilder().Build(), new DateTimeOffset(2026, 10, 4, 16, 0, 0, TimeSpan.Zero)));
    }
}
