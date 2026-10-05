using System.Text.Json;
using Assister.Contracts;
using Assister.Llm;
using Assister.Tools;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class ToolLoopTests
{
    [Theory]
    [InlineData("How much solar power did I get yesterday?")]
    [InlineData("What's the wind speed right now?")]
    [InlineData("Can you explain why the sky is blue?")]
    public async Task GeneralQuestionsAlwaysOfferHomeToolsWithoutRequiringToolUse(string Message)
    {
        var Names = new[] { "ha_search", "ha_get_state", "ha_get_history", "ha_control", "weather_forecast" };
        var Registry = new ToolRegistry(Names.Select(Name => new NamedTool(Name)));
        var Model = new FakeModel();
        Model.Responses.Enqueue(new("An answer.", [], "stop"));
        var Answer = await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new(Message), [], CancellationToken.None);
        Assert.Equal("An answer.", Answer);
        Assert.Equal(Names.Order(), Model.Requests[0].Tools!.Select(Tool => Tool.Function.Name).Order());
        Assert.Equal("auto", Model.Requests[0].ToolChoice);
    }

    [Fact]
    public async Task ControlOutsideKeywordGateCanSearchAndExecute()
    {
        var Search = new FakeTool();
        var Control = new FakeControlTool();
        var Registry = new ToolRegistry([Search, Control]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new(null, [new("search", new("ha_search", "{\"query\":\"desk\"}"))], "tool_calls"));
        Model.Responses.Enqueue(new(null, [new("control", new("ha_control", "{}"))], "tool_calls"));
        Model.Responses.Enqueue(new("Done.", [], "stop"));
        Assert.Equal("Done.", await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new("Could you turn it off?"), [], CancellationToken.None));
        Assert.Equal(1, Control.Calls);
    }

    [Fact]
    public async Task ForecastQuestionCannotSkipTheSourceAndInventWeather()
    {
        var Registry = new ToolRegistry([new SourceTool("weather_forecast", "{\"forecast\":[]}")]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new("It will be sunny.", [], "stop"));
        var Answer = await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new("What is the weather this weekend?"), [], CancellationToken.None);
        Assert.Contains("could not retrieve", Answer);
        Assert.Equal("required", Model.Requests.Single().ToolChoice);
    }

    [Fact]
    public async Task ForecastToolFailureIsExplainedWithoutRepeatedRequests()
    {
        var Registry = new ToolRegistry([new SourceTool("weather_forecast", "{\"error\":\"Hourly forecasts are unavailable.\"}")]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new(null, [new("forecast", new("weather_forecast", "{}"))], "tool_calls"));
        Model.Responses.Enqueue(new("Hourly forecasts are unavailable.", [], "stop"));
        var Answer = await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new("Weather tonight?"), [], CancellationToken.None);
        Assert.Equal("Hourly forecasts are unavailable.", Answer);
        Assert.Equal("none", Model.Requests.Last().ToolChoice);
    }

    private sealed class SourceTool(string Name, string Result) : IAssisterTool
    {
        public bool StateChanging => false;
        public LlmTool Definition => new(new(Name, Name, JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")));
        public Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken Token) => Task.FromResult(Result);
    }
    [Theory]
    [InlineData("2026-10-04T23:00:00Z", "2026-10-04T19:00:00-04:00")]
    [InlineData("2026-12-04T23:00:00Z", "2026-12-04T18:00:00-05:00")]
    public void LocalClockPreservesSeasonalUtcOffset(string Utc, string Expected)
        => Assert.Equal(DateTimeOffset.Parse(Expected).ToString("O"), LocalClock.At(DateTimeOffset.Parse(Utc), "America/New_York").ToString("O"));
    [Fact]
    public async Task StreamingBuffersToolRoundProseAndExecutesOnlyCompletedCalls()
    {
        var Tool = new FakeTool();
        var Registry = new ToolRegistry([Tool]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new("I will look that up.", [new("search", new("ha_search", "{\"query\":\"office\"}"))], "tool_calls"));
        Model.Responses.Enqueue(new("The office is warm.", [], "stop"));
        var Spoken = new List<string>();
        var Answer = await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new("Was the office hot?"), [], CancellationToken.None, OnText: (Text, _) => { Spoken.Add(Text); return Task.CompletedTask; });
        Assert.Equal("The office is warm.", Answer);
        Assert.Equal([Answer], Spoken);
        Assert.Equal(1, Tool.Calls);
    }

    [Fact]
    public async Task StreamingUnconfirmedControlCannotSpeakModelConfirmation()
    {
        var Registry = new ToolRegistry([new FakeControlTool()]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new("Done, I dimmed the lights.", [], "stop"));
        var Spoken = new List<string>();
        await Assert.ThrowsAsync<ControlNotConfirmedException>(() => new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new("Dim living room lights"), [], CancellationToken.None, OnText: (Text, _) => { Spoken.Add(Text); return Task.CompletedTask; }));
        Assert.Empty(Spoken);
    }

    [Fact]
    public async Task PersonalPreferenceQuestionOffersSearchWithoutMemoryMutation()
    {
        var Registry = new ToolRegistry([new NamedTool("memory_search"), new NamedTool("memory_store"), new NamedTool("memory_delete")]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new("I will check your preferences.", [], "stop"));
        await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build()).RespondAsync(new("What is my favorite tea?"), [], CancellationToken.None);
        Assert.Equal("memory_search", Assert.Single(Model.Requests[0].Tools!).Function.Name);
    }
    private sealed class NamedTool(string Name) : IAssisterTool
    {
        public bool StateChanging => Name != "memory_search";
        public LlmTool Definition => new(new(Name, Name, JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")));
        public Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken Token) => throw new NotSupportedException();
    }
    [Fact]
    public async Task SelectedSearchExecutesAndToolResultsStayInThisRequestOnly()
    {
        var Tool = new FakeTool();
        var Registry = new ToolRegistry([Tool]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new(null, [new("call1", new("ha_search", "{\"query\":\"office\"}"))], "tool_calls"));
        Model.Responses.Enqueue(new("The office is warm.", [], "stop"));
        var Loop = new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build());
        Assert.Equal("The office is warm.", await Loop.RespondAsync(new("Was the office hot?"), [], CancellationToken.None));
        Assert.Equal(1, Tool.Calls);
        Assert.Contains(Model.Requests[1].Messages, Message => Message.Role == "tool" && Message.ToolCallId == "call1");
        Model.Responses.Enqueue(new("Hello", [], "stop"));
        await Loop.RespondAsync(new("Hello"), [], CancellationToken.None);
        Assert.Equal("ha_search", Assert.Single(Model.Requests[2].Tools!).Function.Name);
        Assert.DoesNotContain(Model.Requests[2].Messages, Message => Message.Role == "tool");
    }

    [Fact]
    public async Task BrokerRejectsUnselectedExtraDuplicateAndWrongTypeArguments()
    {
        var Tool = new FakeTool();
        var Broker = new ToolBroker(new([Tool]));
        var Context = new ToolExecutionContext(new("office"), []);
        foreach (var Arguments in new[] { "{\"query\":2}", "{\"query\":\"x\",\"url\":\"bad\"}", "{\"query\":\"a\",\"query\":\"b\"}", "{}", "[]" })
        {
            Assert.Contains("error", await Broker.ExecuteAsync(new("id", new("ha_search", Arguments)), new HashSet<string> { "ha_search" }, Context, CancellationToken.None));
        }
        Assert.Contains("error", await Broker.ExecuteAsync(new("id", new("ha_search", "{\"query\":\"x\"}")), new HashSet<string>(), Context, CancellationToken.None));
        Assert.Equal(0, Tool.Calls);
    }

    [Fact]
    public async Task ToolLoopCannotRunPastConfiguredIterationLimit()
    {
        var Tool = new FakeTool();
        var Registry = new ToolRegistry([Tool]);
        var Model = new FakeModel();
        for (var Index = 0; Index < 3; Index++) { Model.Responses.Enqueue(new(null, [new("id" + Index, new("ha_search", "{\"query\":\"office\"}"))], "tool_calls")); }
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LanguageModel:MaxToolIterations"] = "1" }).Build();
        Assert.Contains("limit", await new ToolLoop(Model, Registry, new(Registry), Config).RespondAsync(new("office temperature history"), [], CancellationToken.None));
        Assert.Equal(1, Tool.Calls);
        Assert.Equal("none", Model.Requests.Last().ToolChoice);
    }

    [Theory]
    [InlineData("Set living room light to 100.")]
    [InlineData("Could you make the living room lights fully bright?")]
    [InlineData("Dim the living room lights to 40 percent")]
    public async Task ControlRequestsOfferControlAndCannotInventSuccess(string Message)
    {
        var Registry = new ToolRegistry([new FakeControlTool()]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new("Done, I set the brightness.", [], "stop"));
        var Loop = new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<ControlNotConfirmedException>(() => Loop.RespondAsync(new(Message), [], CancellationToken.None));
        Assert.Contains(Model.Requests[0].Tools!, Tool => Tool.Function.Name == "ha_control");
        Assert.Equal("required", Model.Requests[0].ToolChoice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedControlsCannotBecomeSuccessfulModelProse(bool Fails)
    {
        var Tool = new FakeControlTool { Fails = Fails };
        var Registry = new ToolRegistry([Tool]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new(null, [new("control", new("ha_control", "{}"))], "tool_calls"));
        Model.Responses.Enqueue(new("Done.", [], "stop"));
        var Loop = new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build());
        if (Fails)
        {
            await Assert.ThrowsAsync<ControlNotConfirmedException>(() => Loop.RespondAsync(new("Dim living room lights"), [], CancellationToken.None));
        }
        else
        {
            Assert.Equal("Done.", await Loop.RespondAsync(new("Dim living room lights"), [], CancellationToken.None));
            Assert.Equal("none", Model.Requests.Last().ToolChoice);
        }
        Assert.Equal(1, Tool.Calls);
    }

    [Fact]
    public async Task ReadOnlyBrightnessQuestionOffersOptionalControl()
    {
        var Registry = new ToolRegistry([new FakeControlTool()]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new("The brightness is 50 percent.", [], "stop"));
        await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new("What is the living room light brightness?"), [], CancellationToken.None);
        Assert.Equal("ha_control", Assert.Single(Model.Requests[0].Tools!).Function.Name);
        Assert.Equal("auto", Model.Requests[0].ToolChoice);
    }

    [Fact]
    public async Task EarlierConfirmationsDoNotReplaceCurrentSearchAndControl()
    {
        var Search = new FakeTool();
        var Control = new FakeControlTool();
        var Registry = new ToolRegistry([Search, Control]);
        var Model = new FakeModel();
        Model.Responses.Enqueue(new(null, [new("search", new("ha_search", "{\"query\":\"living room lights\"}"))], "tool_calls"));
        Model.Responses.Enqueue(new(null, [new("control", new("ha_control", "{}"))], "tool_calls"));
        Model.Responses.Enqueue(new("Done.", [], "stop"));
        var History = new LlmMessage[] { new("user", "Set living room light to 100."), new("assistant", "Set to 100 percent.") };
        Assert.Equal("Done.", await new ToolLoop(Model, Registry, new(Registry), new ConfigurationBuilder().Build())
            .RespondAsync(new("Dim living room lights to 40 percent"), History, CancellationToken.None));
        Assert.Equal("ha_search", Assert.Single(Model.Requests[0].Tools!).Function.Name);
        Assert.Contains("Current device-control request", Model.Requests[0].Messages.Last().Content);
        Assert.Contains(Model.Requests[1].Tools!, Tool => Tool.Function.Name == "ha_control");
        Assert.Equal("required", Model.Requests[1].ToolChoice);
        Assert.Equal("none", Model.Requests[2].ToolChoice);
        Assert.Equal(1, Control.Calls);
    }

    private sealed class FakeControlTool : IAssisterTool
    {
        public int Calls { get; private set; }
        public bool Fails { get; init; }
        public bool StateChanging => true;
        public LlmTool Definition => new(new("ha_control", "Control", JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{},"additionalProperties":false}""")));
        public Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
        {
            Calls++;
            if (Fails) { throw new InvalidOperationException(); }
            return Task.FromResult("{\"status\":\"completed\"}");
        }
    }

    private sealed class FakeTool : IAssisterTool
    {
        public int Calls { get; private set; }
        public bool StateChanging => false;
        public LlmTool Definition => new(new("ha_search", "Search", JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""")));
        public Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
        {
            Calls++;
            Context.ObservedEntities.Add("light.living_room_lights");
            return Task.FromResult("[{\"entity_id\":\"light.living_room_lights\",\"name\":\"Living Room Lights\",\"state\":\"74\"}]");
        }
    }
    private sealed class FakeModel : ILanguageModel
    {
        public Queue<LlmResponse> Responses { get; } = new();
        public List<LlmRequest> Requests { get; } = [];
        public Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken CancellationToken)
        {
            Requests.Add(Request with { Messages = Request.Messages.ToArray() });
            return Task.FromResult(Responses.Dequeue());
        }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken CancellationToken)
        {
            var Response = await CompleteAsync(Request, CancellationToken);
            if (Response.Content is { } Text) { yield return new(TextDelta: Text); }
            yield return new(Completed: Response);
        }
    }
}
