using System.Text.Json;
using Assister.Contracts;
using Assister.Conversations;
using Assister.Diagnostics;
using Assister.Intents;
using Assister.Llm;
using Assister.Modules.HomeAssistant;
using Assister.Persistence;
using Assister.Tools;
using Assister.Voice;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class DeviceInteractionTests
{
    [Fact]
    public async Task ModelFanControlUsesAuthorizedPercentageAndNeverLightBrightness()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office fan","domains":["fan"]}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("control", "ha_control", """{"entity_id":"fan.office_fan","action":"set_fan_speed","speed_pct":50}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("Fan speed command completed.", [], "stop"));
        var Answer = await App.Loop.RespondAsync(new("Set the office fan speed to 50%"), [], CancellationToken.None);
        Assert.Contains("completed", Answer);
        var Action = Assert.Single(App.Actions.Calls);
        Assert.Equal(HomeAssistantAction.SetFanSpeed, Action.Action);
        Assert.Equal(50, Action.SpeedPercent);
        Assert.Null(Action.BrightnessPercent);
        var Search = Json(App.Model.Requests[1].Messages.Single(Message => Message.ToolCallId == "search").Content!)[0];
        Assert.Equal(30, Search.GetProperty("speed_pct").GetDouble());
        Assert.True(Search.GetProperty("supports_fan_speed").GetBoolean());
        Assert.Equal(10, Search.GetProperty("percentage_step").GetDouble());
    }

    [Theory]
    [InlineData("light.office_fan", 50)]
    [InlineData("fan.office_fan", 25)]
    public async Task FanControlCannotChangeLightsOrUseUnauthorizedSpeed(string Target, int Speed)
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("control", "ha_control", JsonSerializer.Serialize(new { entity_id = Target, action = "set_fan_speed", speed_pct = Speed }))], "tool_calls"));
        App.Model.Responses.Enqueue(new("Done.", [], "stop"));
        if (Target.StartsWith("light.", StringComparison.Ordinal))
        {
            var Answer = await App.Loop.RespondAsync(new("Set the office fan speed to 50%"), [], CancellationToken.None);
            Assert.Contains("Which fan", Answer);
        }
        else
        {
            await Assert.ThrowsAsync<ControlNotConfirmedException>(() => App.Loop.RespondAsync(new("Set the office fan speed to 50%"), [], CancellationToken.None));
        }
        Assert.Empty(App.Actions.Calls);
    }

    [Fact]
    public async Task WholeHouseStatusUsesModelSearchWithoutDeviceChanges()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"lights","domains":["light"],"limit":10}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("All four lights are off.", [], "stop"));
        var Result = await App.Send("What's the status of all the current lights in the house?");
        Assert.Equal("language-model", Result.HandledBy);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal("All four lights are off.", Result.Response);
        Assert.Equal(2, App.Model.Requests.Count);
        var SearchResult = App.Model.Requests[1].Messages.Single(Message => Message.ToolCallId == "search").Content!;
        Assert.Equal(4, Json(SearchResult).GetArrayLength());
        Assert.DoesNotContain("device_tracker.office_lights", SearchResult);
        Assert.Empty(App.Actions.Calls);
    }

    [Theory]
    [InlineData("Hey Jarvis, can you turn the kitchen lights on 100%?")]
    [InlineData("Hey Jarvis, can you turn on the kitchen lights to 100% please?")]
    [InlineData("Please could you turn the kitchen lights on at 100 percent?")]
    public async Task SpokenBrightnessRequestsUseTheDirectPath(string Message)
    {
        await using var App = await Fixture.Create();
        var Result = await App.Send(Message);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal("direct-intent", Result.HandledBy);
        var Action = Assert.Single(App.Actions.Calls);
        Assert.Equal(["light.kitchen_main_lights"], Action.EntityIds);
        Assert.Equal(100, Action.BrightnessPercent);
        Assert.Empty(App.Model.Requests);
    }

    [Fact]
    public async Task UnmatchedNaturalLanguageCanControlThroughIndependentCurrentRequestInterpretation()
    {
        await using var App = await Fixture.Create(Semantic: true);
        const string Message = "Hey Jarvis, I'd like some light in the kitchen, make it as bright as possible.";
        Assert.Null(ControlRequest.Parse(Message));
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"kitchen lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("control", "ha_control", """{"entity_id":"light.kitchen_main_lights","action":"set_brightness","brightness_pct":100}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("interpret", "interpret_device_request", """{"action":"set_brightness","target":"kitchen lights","brightness_pct":100}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("Set the kitchen lights to full brightness.", [], "stop"));
        var Result = await App.Send(Message);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal("language-model", Result.HandledBy);
        var Action = Assert.Single(App.Actions.Calls);
        Assert.Equal(["light.kitchen_main_lights"], Action.EntityIds);
        Assert.Equal(100, Action.BrightnessPercent);
        var Interpretation = App.Model.Requests[2];
        Assert.Equal(Message, Interpretation.Messages.Single(Item => Item.Role == "user").Content);
        Assert.Equal(2, Interpretation.Messages.Count);
        Assert.Equal("interpret_device_request", Assert.Single(Interpretation.Tools!).Function.Name);
        Assert.DoesNotContain("light.kitchen_main_lights", string.Join(' ', Interpretation.Messages.Select(Item => Item.Content)));
    }

    [Theory]
    [InlineData("What lights are in the kitchen?")]
    [InlineData("Don't turn the kitchen lights on")]
    [InlineData("Turn on the kitchen lights when I get home")]
    public async Task SemanticFallbackCannotReplayHistoryOrUseProposedControlAsAuthorization(string Message)
    {
        await using var App = await Fixture.Create(Semantic: true);
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"kitchen lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("control", "ha_control", """{"entity_id":"light.kitchen_main_lights","action":"turn_on"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("interpret", "interpret_device_request", """{"action":"none"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("I have not changed the lights.", [], "stop"));
        await App.Loop.RespondAsync(new(Message), [new("user", "Turn the office lights off")], CancellationToken.None);
        Assert.Empty(App.Actions.Calls);
        Assert.Equal(Message, App.Model.Requests[2].Messages.Single(Item => Item.Role == "user").Content);
        Assert.DoesNotContain("office", string.Join(' ', App.Model.Requests[2].Messages.Select(Item => Item.Content)));
        Assert.Contains(App.Model.Requests.Last().Messages, Item => Item.Role == "tool" && Item.Content!.Contains("control_not_authorized"));
    }

    [Theory]
    [InlineData("light.office_fan", "set_brightness", 100, "ambiguous_targets")]
    [InlineData("light.kitchen_main_lights", "turn_off", null, "control_not_authorized")]
    public async Task SemanticInterpretationStillEnforcesRequestedTargetsAndActions(string Target, string Action, int? Brightness, string Error)
    {
        await using var App = await Fixture.Create(Semantic: true);
        App.Model.Responses.Enqueue(new(null, [Call("interpret", "interpret_device_request", """{"action":"set_brightness","target":"kitchen lights","brightness_pct":100}""")], "tool_calls"));
        var Registry = new ToolRegistry([App.Tool("ha_control")]);
        var Broker = new ToolBroker(Registry, Model: App.Model);
        var Context = new ToolExecutionContext(new("I'd like the kitchen at full brightness"), [Target]);
        var Arguments = new Dictionary<string, object> { ["entity_id"] = Target, ["action"] = Action };
        if (Brightness is not null) { Arguments["brightness_pct"] = Brightness; }
        var Result = await Broker.ExecuteAsync(Call("control", "ha_control", JsonSerializer.Serialize(Arguments)),
            new HashSet<string> { "ha_control" }, Context, CancellationToken.None);
        Assert.Contains(Error, Result);
        Assert.Empty(App.Actions.Calls);
    }

    [Theory]
    [InlineData("{\"action\":\"set_brightness\",\"target\":\"kitchen lights\"}")]
    [InlineData("{\"action\":\"set_brightness\",\"target\":\"kitchen lights\",\"brightness_pct\":200}")]
    [InlineData("not json")]
    public async Task IncompleteOrInvalidSemanticInterpretationCannotExecuteControls(string Interpretation)
    {
        await using var App = await Fixture.Create(Semantic: true);
        App.Model.Responses.Enqueue(new(null, [Call("interpret", "interpret_device_request", Interpretation)], "tool_calls"));
        var Registry = new ToolRegistry([App.Tool("ha_control")]);
        var Broker = new ToolBroker(Registry, Model: App.Model);
        var Context = new ToolExecutionContext(new("I'd like the kitchen at full brightness"), ["light.kitchen_main_lights"]);
        var Proposed = Call("control", "ha_control", """{"entity_id":"light.kitchen_main_lights","action":"set_brightness","brightness_pct":100}""");
        var Selected = new HashSet<string> { "ha_control" };
        Assert.Contains("error", await Broker.ExecuteAsync(Proposed, Selected, Context, CancellationToken.None));
        Assert.Contains("error", await Broker.ExecuteAsync(Proposed, Selected, Context, CancellationToken.None));
        Assert.Single(App.Model.Requests);
        Assert.Empty(App.Actions.Calls);
    }

    [Theory]
    [InlineData(" Set kitchen lights at 50%.", "direct-intent")]
    [InlineData("Adjust kitchen lights at 50 percent", "language-model")]
    public async Task BrightnessAtPercentageResolvesAndAuthorizesTheExactKitchenTarget(string Message, string Handler)
    {
        await using var App = await Fixture.Create();
        var Authorized = ControlRequest.Parse(Message)!;
        Assert.Equal("kitchen lights", Authorized.Target);
        Assert.Equal(50, Authorized.Brightness);
        Assert.Equal("light.kitchen_main_lights", Assert.Single(Authorized.Resolve(App.Cache.Snapshot(), null)).EntityId);
        if (Handler == "language-model")
        {
            App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"kitchen lights","domains":["light"]}""")], "tool_calls"));
            App.Model.Responses.Enqueue(new(null, [Call("control", "ha_control", """{"entity_id":"light.kitchen_main_lights","action":"set_brightness","brightness_pct":50}""")], "tool_calls"));
            App.Model.Responses.Enqueue(new("Set to 50 percent.", [], "stop"));
        }
        var Result = await App.Send(Message);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal(Handler, Result.HandledBy);
        var Control = Assert.Single(App.Actions.Calls);
        Assert.Equal(["light.kitchen_main_lights"], Control.EntityIds);
        Assert.Equal(50, Control.BrightnessPercent);
        if (Handler == "direct-intent") { Assert.Empty(App.Model.Requests); }
    }

    [Theory]
    [InlineData("lights", "Office", "light.office_fan,light.office_fan_light_2")]
    [InlineData("light", "office", "light.office_fan,light.office_fan_light_2")]
    [InlineData("office lights", null, "light.office_fan,light.office_fan_light_2")]
    [InlineData("lights", "Kitchen", "light.kitchen_main_lights")]
    [InlineData("kitchen lights", null, "light.kitchen_main_lights")]
    [InlineData("missing lights", null, "")]
    public async Task DiscoveryUsesDomainsRoomsAndNullableAttributes(string Query, string? Area, string Expected)
    {
        await using var App = await Fixture.Create();
        var Context = new ToolExecutionContext(new("What lights are there?"), []);
        var Arguments = JsonSerializer.SerializeToElement(Area is null ? new Dictionary<string, object> { ["query"] = Query }
            : new Dictionary<string, object> { ["query"] = Query, ["area"] = Area });
        var Result = Json(await App.Tool("ha_search").ExecuteAsync(Arguments, Context, CancellationToken.None));
        Assert.Equal(Expected, string.Join(',', Result.EnumerateArray().Select(Item => Item.GetProperty("entity_id").GetString())));
        Assert.All(Result.EnumerateArray(), Item => Assert.Equal(JsonValueKind.Null, Item.GetProperty("brightness_pct").ValueKind));
    }

    [Theory]
    [InlineData("turn off kitchen lights", "light.kitchen_main_lights")]
    [InlineData("turn off white office light", "light.office_fan_light_2")]
    public async Task DirectAndModelSearchResolveTheSameDevices(string Message, string Expected)
    {
        await using var App = await Fixture.Create();
        var Result = await App.Send(Message);
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal(Expected, Assert.Single(Assert.Single(App.Actions.Calls).EntityIds));
    }

    [Fact]
    public async Task InformationQuestionCannotExecuteAnEarlierCommandEvenWhenModelTries()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"kitchen lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("control", "ha_control", """{"entity_id":"light.kitchen_main_lights","action":"turn_off"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("The device is Kitchen Main Lights.", [], "stop"));
        var History = new LlmMessage[] { new("user", "Turn off the kitchen lights."), new("assistant", "I could not find them.") };
        var Answer = await App.Loop.RespondAsync(new("What lights are in the kitchen?"), History, CancellationToken.None);
        Assert.Contains("Kitchen Main Lights", Answer);
        Assert.Empty(App.Actions.Calls);
        Assert.Contains(App.Model.Requests.Last().Messages, Message => Message.Role == "tool" && Message.Content!.Contains("control_not_authorized"));
    }

    [Fact]
    public async Task OnOrOffMustBeClarifiedAndReferencesPersistAcrossCoordinatorInstances()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("I found Yellow Light and White Light. Which should I turn on or off?", [], "stop"));
        var First = await App.Send("What lights are in the office?");
        App.RecreateCoordinator();
        var Choice = await App.Send("Both of them?", First.ConversationId);
        Assert.Equal("ambiguous", Choice.Outcome);
        Assert.Contains("on or off", Choice.Response);
        Assert.Empty(App.Actions.Calls);
        var Answer = await App.Send("Off", First.ConversationId);
        Assert.Equal("succeeded", Answer.Outcome);
        Assert.Equal(["light.office_fan", "light.office_fan_light_2"], Assert.Single(App.Actions.Calls).EntityIds);
        var Row = await App.Database.Conversations.SingleAsync();
        var Saved = JsonSerializer.Deserialize<DeviceConversationContext>(Row.DeviceContextJson)!;
        Assert.Equal("turn_off", Saved.LastCompleted!.Action);
        Assert.Equal(2, Saved.LastCompleted.EntityIds.Length);
    }

    [Fact]
    public async Task ExplicitPendingCommandCanSelectBothButInformationQuestionConsumesIt()
    {
        await using var App = await Fixture.Create();
        var Question = await App.Send("Turn off the office light");
        Assert.Equal("ambiguous", Question.Outcome);
        Assert.Empty(App.Actions.Calls);
        Assert.Equal("succeeded", (await App.Send("Both of them", Question.ConversationId)).Outcome);
        App.Actions.Calls.Clear();
        await App.Send("Turn on the office light", Question.ConversationId);
        App.Model.Responses.Enqueue(new("A general answer.", [], "stop"));
        await App.Send("Why is the sky blue?", Question.ConversationId);
        Assert.Equal("ambiguous", (await App.Send("Both of them", Question.ConversationId)).Outcome);
        Assert.Empty(App.Actions.Calls);
    }

    [Fact]
    public async Task BatchControlValidatesAllTargetsBeforeSendingAndBlocksWrongActions()
    {
        await using var App = await Fixture.Create();
        var Context = new ToolExecutionContext(new("Dim Yellow Light and White Light to 40 percent"),
            ["light.office_fan", "light.office_fan_light_2", "light.kitchen_main_lights"]);
        var Tool = App.Tool("ha_control");
        Assert.Contains("ambiguous_targets", await Tool.ExecuteAsync(Json("""{"entity_ids":["light.office_fan","light.kitchen_main_lights"],"action":"set_brightness","brightness_pct":40}"""), Context, CancellationToken.None));
        Assert.Contains("control_not_authorized", await Tool.ExecuteAsync(Json("""{"entity_ids":["light.office_fan","light.office_fan_light_2"],"action":"turn_off"}"""), Context, CancellationToken.None));
        Assert.Empty(App.Actions.Calls);
        var Valid = Json("""{"entity_ids":["light.office_fan","light.office_fan_light_2"],"action":"set_brightness","brightness_pct":40}""");
        Assert.Contains("completed", await Tool.ExecuteAsync(Valid, Context, CancellationToken.None));
        Assert.Equal(2, Assert.Single(App.Actions.Calls).EntityIds.Count);
        Assert.Contains("already_attempted", await Tool.ExecuteAsync(Valid, Context, CancellationToken.None));
        Assert.Single(App.Actions.Calls);
    }

    [Fact]
    public async Task ModelBatchControlsBothLightsAndKeepsAReceipt()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("control", "ha_control", """{"entity_ids":["light.office_fan","light.office_fan_light_2"],"action":"set_brightness","brightness_pct":40}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("Both lights are at 40 percent.", [], "stop"));
        var Result = await App.Send("Dim Yellow Light and White Light to 40 percent");
        Assert.Equal("succeeded", Result.Outcome);
        Assert.Equal(2, Assert.Single(App.Actions.Calls).EntityIds.Count);
        var Status = await App.Send("Did you change them?", Result.ConversationId);
        Assert.Equal("action-receipt", Status.HandledBy);
        Assert.Contains("40 percent", Status.Response);
        Assert.Contains("White Light", Status.Response);
    }

    [Fact]
    public async Task ExplicitBackOnUsesReferencesButNeitherASearchNorExpiredReferencesCanRedirectThem()
    {
        await using var App = await Fixture.Create();
        var Conversation = new DeviceConversationContext { UpdatedAt = DateTimeOffset.UtcNow, References = [new("light.kitchen_main_lights", "Kitchen Main Lights")] };
        var Context = new ToolExecutionContext(new("You just turned the lights off. Can you turn them back on?"), ["light.office_fan"], Conversation: Conversation);
        // Simulate the model searching an unrelated device during this turn.
        Conversation.References = [new("light.office_fan", "Yellow Light")];
        var Rejected = await App.Tool("ha_control").ExecuteAsync(Json("""{"entity_id":"light.office_fan","action":"turn_on"}"""), Context, CancellationToken.None);
        Assert.Contains("ambiguous_targets", Rejected);
        Assert.Empty(App.Actions.Calls);
        Conversation.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        Assert.Empty(ControlRequest.Parse("Turn them off", Conversation)!.Resolve(App.Cache.Snapshot(), Conversation));
    }

    [Fact]
    public async Task SearchFailureCannotBecomeAClaimThatDevicesAreMissing()
    {
        await using var App = await Fixture.Create();
        App.Cache.SetStale(true);
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("There are no office lights.", [], "stop"));
        var Result = await App.Send("What lights are in the office?");
        Assert.Equal("unavailable", Result.Outcome);
        Assert.Contains("does not mean", Result.Response);
        Assert.Empty(App.Actions.Calls);
    }

    [Fact]
    public async Task NamedClarificationSelectsOnlyTheNamedDeviceAndNewConversationsDoNotInheritTargets()
    {
        await using var App = await Fixture.Create();
        var Question = await App.Send("Turn off the office light");
        var Answer = await App.Send("White Light", Question.ConversationId);
        Assert.Equal("succeeded", Answer.Outcome);
        Assert.Equal("light.office_fan_light_2", Assert.Single(Assert.Single(App.Actions.Calls).EntityIds));
        Assert.Equal("ambiguous", (await App.Send("Turn it on", Satellite: "another-satellite")).Outcome);
        Assert.Single(App.Actions.Calls);
    }

    [Fact]
    public async Task SeparateControlsReportPartialCompletionAndDoNotAutomaticallyRetry()
    {
        await using var App = await Fixture.Create();
        App.Actions.FailOn = "light.office_fan_light_2";
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null,
            [Call("one", "ha_control", """{"entity_id":"light.office_fan","action":"set_brightness","brightness_pct":40}"""),
             Call("two", "ha_control", """{"entity_id":"light.office_fan_light_2","action":"set_brightness","brightness_pct":40}""")], "tool_calls"));
        var Result = await App.Send("Dim Yellow Light and White Light to 40 percent");
        Assert.Equal("failed", Result.Outcome);
        Assert.Contains("completed for light.office_fan", Result.Response);
        Assert.Contains("remaining", Result.Response);
        Assert.Equal(2, App.Actions.Calls.Count);
        var Saved = JsonSerializer.Deserialize<DeviceConversationContext>((await App.Database.Conversations.SingleAsync()).DeviceContextJson)!;
        Assert.Equal("light.office_fan", Assert.Single(Saved.LastCompleted!.EntityIds));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletionRequiresEveryRequestedTargetAcrossModelRounds(bool CompletesBoth)
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new(null, [Call("one", "ha_control", """{"entity_id":"light.office_fan","action":"set_brightness","brightness_pct":40}""")], "tool_calls"));
        if (CompletesBoth)
            App.Model.Responses.Enqueue(new(null, [Call("two", "ha_control", """{"entity_id":"light.office_fan_light_2","action":"set_brightness","brightness_pct":40}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("Both are now dimmed.", [], "stop"));
        var Result = await App.Send("Dim Yellow Light and White Light to 40 percent");
        Assert.Equal(CompletesBoth ? "succeeded" : "failed", Result.Outcome);
        Assert.Equal(CompletesBoth ? 2 : 1, App.Actions.Calls.Count);
        Assert.Equal("required", App.Model.Requests[2].ToolChoice);
        if (!CompletesBoth) { Assert.Contains("remaining targets", Result.Response); }
    }

    [Fact]
    public async Task GenericControlWithoutRoomCannotSelectAnArbitrarySearchedLight()
    {
        await using var App = await Fixture.Create();
        var Context = new ToolExecutionContext(new("Dim the lights to 40 percent"), ["light.kitchen_main_lights"]);
        var Result = await App.Tool("ha_control").ExecuteAsync(Json("""{"entity_id":"light.kitchen_main_lights","action":"set_brightness","brightness_pct":40}"""), Context, CancellationToken.None);
        Assert.Contains("ambiguous_targets", Result);
        Assert.Empty(App.Actions.Calls);
    }

    [Fact]
    public async Task BareOffDoesNotChooseBothWithoutATargetSelection()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"office lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("Yellow Light and White Light are in the office. Which do you mean?", [], "stop"));
        var First = await App.Send("What lights are in the office?");
        Assert.Equal("ambiguous", (await App.Send("Off", First.ConversationId)).Outcome);
        Assert.Equal("ambiguous", (await App.Send("It", First.ConversationId)).Outcome);
        Assert.Empty(App.Actions.Calls);
    }

    [Fact]
    public async Task EmptySearchAllowsClarificationInsteadOfForcingRepeatedToolCalls()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new(null, [Call("search", "ha_search", """{"query":"missing lights"}""")], "tool_calls"));
        App.Model.Responses.Enqueue(new("Which light do you mean?", [], "stop"));
        var Result = await App.Send("Dim the missing lights to 40 percent");
        Assert.Contains("Which light", Result.Response);
        Assert.Equal("required", App.Model.Requests[0].ToolChoice);
        Assert.Equal("auto", App.Model.Requests[1].ToolChoice);
        Assert.Empty(App.Actions.Calls);
    }

    [Fact]
    public async Task GeneralQuestionAfterWeatherDoesNotRequireAnotherForecast()
    {
        await using var App = await Fixture.Create();
        App.Model.Responses.Enqueue(new("Blue light scatters more.", [], "stop"));
        var Answer = await App.Loop.RespondAsync(new("Why is the sky blue?"),
            [new("user", "What is the weather this weekend?"), new("assistant", "Rain.")], CancellationToken.None);
        Assert.Contains("Blue light", Answer);
        Assert.Equal("auto", App.Model.Requests[0].ToolChoice);
    }

    [Theory]
    [InlineData("What lights are in the kitchen?")]
    [InlineData("What should I call the lights when I want to turn them off?")]
    [InlineData("Don't turn the kitchen lights off")]
    [InlineData("Tell me how to turn off the kitchen lights")]
    [InlineData("If I say turn off the kitchen lights, what happens?")]
    [InlineData("Turn off the kitchen lights when I leave")]
    public void QuestionsNegationAndFutureConditionsDoNotAuthorizeChanges(string Text)
        => Assert.Null(ControlRequest.Parse(Text));

    private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);
    private static LlmToolCall Call(string Id, string Name, string Arguments) => new(Id, new(Name, Arguments));
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection Connection = new("Data Source=:memory:");
        public AssisterDbContext Database = null!;
        public HomeAssistantStateCache Cache { get; } = new();
        public RecordingActions Actions { get; } = new();
        public FakeModel Model { get; } = new();
        public ToolLoop Loop = null!;
        private ConversationCoordinator Coordinator = null!;
        private readonly ConversationLocks Locks = new();
        private readonly HttpClient Http = new();
        private readonly IConfiguration Configuration = new ConfigurationBuilder().Build();
        public HomeAssistantTool Tool(string Name) => new(Name, Cache, Actions, Http, Configuration);
        public static async Task<Fixture> Create(bool Semantic = false)
        {
            var App = new Fixture();
            await App.Connection.OpenAsync();
            App.Database = new(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite(App.Connection).Options);
            await App.Database.Database.MigrateAsync();
            App.Cache.Load(Json("""
                [
                  {"entity_id":"light.office_fan","state":"off","attributes":{"friendly_name":"Yellow Light","brightness":null,"supported_color_modes":["brightness"]}},
                  {"entity_id":"light.office_fan_light_2","state":"off","attributes":{"friendly_name":"White Light","supported_color_modes":[null,"brightness"]}},
                  {"entity_id":"light.kitchen_main_lights","state":"off","attributes":{"friendly_name":"Kitchen Main Lights","brightness":null,"supported_color_modes":["brightness"]}},
                  {"entity_id":"light.bedroom","state":"off","attributes":{"friendly_name":"Bedroom Light"}},
                  {"entity_id":"device_tracker.office_lights","state":"home","attributes":{"friendly_name":"Office Lights"}},
                  {"entity_id":"fan.office_fan","state":"on","attributes":{"friendly_name":"Office Fan","supported_features":49,"percentage":30,"percentage_step":10}}
                ]
                """), Json("{}"), Json("""[{"entity_id":"light.office_fan","area_id":"office"},{"entity_id":"light.office_fan_light_2","area_id":"office"}]"""),
                Json("[]"), Json("""[{"area_id":"office","name":"Office"}]"""));
            App.Cache.SetStale(false);
            var Registry = new ToolRegistry(new[] { "ha_search", "ha_get_state", "ha_control", "ha_get_history" }.Select(App.Tool)
                .Cast<IAssisterTool>().Append(new WeatherTool(App.Cache, App.Http, App.Configuration)));
            App.Loop = new(App.Model, Registry, new(Registry, Model: Semantic ? App.Model : null), App.Configuration);
            App.RecreateCoordinator();
            return App;
        }
        public void RecreateCoordinator()
        {
            Database.Dispose();
            Database = new(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite(Connection).Options);
            Coordinator = new(Database, new RequestCoordinator(new IntentClassifier(), new HomeAssistantEntityResolver(),
                new(Actions), Cache, NullLogger<RequestCoordinator>.Instance, Loop), Locks);
        }
        public Task<RequestResult> Send(string Message, Guid? Conversation = null, string Satellite = "test")
            => Coordinator.ProcessAsync(new(Message, Satellite, ConversationId: Conversation), CancellationToken.None);
        public async ValueTask DisposeAsync() { Http.Dispose(); await Database.DisposeAsync(); await Connection.DisposeAsync(); }
    }
    private sealed class RecordingActions : IHomeAssistantClient
    {
        public List<HomeAssistantControl> Calls { get; } = [];
        public string? FailOn;
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken Token)
        {
            Calls.Add(Control);
            if (Control.EntityIds.Contains(FailOn)) { throw new IOException("Readback failed after sending the command."); }
            return Task.CompletedTask;
        }
    }
    private sealed class FakeModel : ILanguageModel
    {
        public Queue<LlmResponse> Responses { get; } = [];
        public List<LlmRequest> Requests { get; } = [];
        public Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken Token)
        { Requests.Add(Request with { Messages = Request.Messages.ToArray() }); return Task.FromResult(Responses.Dequeue()); }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        {
            var Response = await CompleteAsync(Request, Token);
            if (Response.Content is { } Text) { yield return new(TextDelta: Text); }
            yield return new(Completed: Response);
        }
    }
}
