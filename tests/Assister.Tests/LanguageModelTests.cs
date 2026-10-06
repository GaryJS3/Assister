using System.Net;
using System.Text;
using System.Text.Json;
using Assister.Contracts;
using Assister.Llm;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class LanguageModelTests
{
    [Fact]
    public async Task ThinkingExtensionIsOptionalAndUsesABooleanWhenConfigured()
    {
        var Handler = new FakeHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""");
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LanguageModel:BaseUrl"] = "http://localhost/v1", ["LanguageModel:Model"] = "qwen", ["LanguageModel:EnableThinking"] = "false"
        }).Build();
        await new OpenAiCompatibleLanguageModel(new HttpClient(Handler), Configuration).CompleteAsync(new([new("user", "test")]), default);
        using var Request = JsonDocument.Parse(Handler.Body!);
        Assert.False(Request.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        await Create(Handler).CompleteAsync(new([new("user", "test")]), default);
        using var Default = JsonDocument.Parse(Handler.Body!);
        Assert.False(Default.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public async Task CompletionUsesConfiguredRouteFixedLengthAndSelectedTools()
    {
        var Handler = new FakeHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call1","type":"function","function":{"name":"ha.search","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""");
        var Model = Create(Handler);
        var Tool = new LlmTool(new("ha.search", "Find entities", JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""")));
        var Result = await Model.CompleteAsync(new([new("user", "Find the office sensor")], [Tool]), default);
        Assert.Equal("ha.search", Assert.Single(Result.ToolCalls).Function.Name);
        Assert.Equal("http://localhost:8080/v1/chat/completions", Handler.Url);
        Assert.True(Handler.ContentLength > 0);
        using var Body = JsonDocument.Parse(Handler.Body!);
        Assert.Equal("local-model", Body.RootElement.GetProperty("model").GetString());
        Assert.Equal("auto", Body.RootElement.GetProperty("tool_choice").GetString());
        Assert.Single(Body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.False(Body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task StreamingReassemblesToolArgumentsAcrossFragmentedReads()
    {
        var Data = """
            : heartbeat

            data: {"choices":[{"index":0,"delta":{"content":"Café "},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call1","type":"function","function":{"name":"ha.search","arguments":"{\"query\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"office\"}"}}]},"finish_reason":"tool_calls"}]}

            data: {"choices":[],"usage":{"total_tokens":12}}

            data: [DONE]


            """;
        var Handler = new FakeHandler(Data, Fragmented: true);
        var Events = new List<LlmStreamEvent>();
        await foreach (var Event in Create(Handler).StreamAsync(new([new("user", "test")]), default)) { Events.Add(Event); }
        Assert.Equal("Café ", Events[0].TextDelta);
        Assert.Null(Events[0].Completed);
        var Completed = Events[^1].Completed!;
        Assert.Equal("{\"query\":\"office\"}", Assert.Single(Completed.ToolCalls).Function.Arguments);
        Assert.Equal("tool_calls", Completed.FinishReason);
        Assert.Contains("\"stream\":true", Handler.Body);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"partial\"},\"finish_reason\":\"length\"}]}")]
    [InlineData("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":\"stop\"}]}")]
    public async Task InvalidCompletionsAreRejected(string Body)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new(Body)).CompleteAsync(new([new("user", "test")]), default));
    }

    [Fact]
    public async Task ResponseLimitAndRemoteErrorsDoNotExposePayloads()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new(new string('x', 262145)))
            .CompleteAsync(new([new("user", "test")]), default));
        var Error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new("secret credential", Status: HttpStatusCode.Unauthorized))
            .CompleteAsync(new([new("user", "test")]), default));
        Assert.DoesNotContain("secret", Error.Message);
        Assert.Contains("401", Error.Message);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: broken\n\n")]
    public async Task IncompleteStreamsNeverPublishCompletedResults(string Data)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var Event in Create(new(Data)).StreamAsync(new([new("user", "test")]), default))
            {
                Assert.Null(Event.Completed);
            }
        });
    }

    [Fact]
    public async Task StreamingTextCompletesAndOmitsUnusedTools()
    {
        var Handler = new FakeHandler("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":\"stop\"}]}\r\n\r\ndata: [DONE]\r\n\r\n");
        var Events = new List<LlmStreamEvent>();
        await foreach (var Event in Create(Handler).StreamAsync(new([new("user", "hello")]), default)) { Events.Add(Event); }
        Assert.Equal("Hello", Events[^1].Completed!.Content);
        Assert.Empty(Events[^1].Completed!.ToolCalls);
        using var Body = JsonDocument.Parse(Handler.Body!);
        Assert.False(Body.RootElement.TryGetProperty("tools", out _));
        Assert.False(Body.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var Cancellation = new CancellationTokenSource();
        Cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(new("{}"))
            .CompleteAsync(new([new("user", "test")]), Cancellation.Token));
    }

    [Fact]
    public async Task ProviderReasoningStaysSeparateFromAnswerAndCanBeDisabled()
    {
        var Data = "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Thinking\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var Events = new List<LlmStreamEvent>();
        await foreach (var Event in Create(new(Data, Fragmented: true)).StreamAsync(new([new("user", "test")]), default))
            Events.Add(Event);
        Assert.Equal("Thinking", Events[0].ReasoningDelta);
        Assert.Null(Events[0].TextDelta);
        Assert.Equal("Answer", Events[^1].Completed!.Content);
        Assert.Equal("Thinking", Events[^1].Completed!.Reasoning);
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LanguageModel:BaseUrl"] = "http://localhost/v1",
            ["LanguageModel:Model"] = "test",
            ["LanguageModel:EnableThinking"] = "false"
        }).Build();
        await foreach (var Event in new OpenAiCompatibleLanguageModel(new HttpClient(new FakeHandler(Data)), Config).StreamAsync(new([new("user", "test")]), default))
            Assert.Null(Event.ReasoningDelta);
    }

    private static OpenAiCompatibleLanguageModel Create(FakeHandler Handler) => new(new HttpClient(Handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LanguageModel:BaseUrl"] = "http://localhost:8080/v1",
            ["LanguageModel:Model"] = "local-model"
        }).Build());

    private sealed class FakeHandler(string Data, bool Fragmented = false, HttpStatusCode Status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Url { get; private set; }
        public long? ContentLength { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Token.ThrowIfCancellationRequested();
            Url = Request.RequestUri!.ToString();
            ContentLength = Request.Content!.Headers.ContentLength;
            Body = await Request.Content.ReadAsStringAsync(Token);
            return new(Status)
            {
                Content = Fragmented ? new StreamContent(new FragmentStream(Encoding.UTF8.GetBytes(Data))) : new StringContent(Data)
            };
        }
    }

    private sealed class FragmentStream(byte[] Data) : MemoryStream(Data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> Buffer, CancellationToken Token = default)
            => base.ReadAsync(Buffer[..Math.Min(Buffer.Length, 1)], Token);
    }
}
