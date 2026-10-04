using System.Text.Json;
using Assister.Contracts;
using Assister.Llm;
using Assister.Tools;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class ToolLoopTests
{
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
        Assert.Empty(Model.Requests[2].Tools!);
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

    private sealed class FakeTool : IAssisterTool
    {
        public int Calls { get; private set; }
        public bool StateChanging => false;
        public LlmTool Definition => new(new("ha_search", "Search", JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""")));
        public Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
        {
            Calls++;
            return Task.FromResult("{\"state\":\"74\"}");
        }
    }
    private sealed class FakeModel : ILanguageModel
    {
        public Queue<LlmResponse> Responses { get; } = new();
        public List<LlmRequest> Requests { get; } = [];
        public Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken CancellationToken)
        {
            Requests.Add(Request);
            return Task.FromResult(Responses.Dequeue());
        }
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request, CancellationToken CancellationToken) => throw new NotSupportedException();
    }
}
