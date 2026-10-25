using System.Text.Json;
using Assister.Contracts;
using Assister.Intents;
using Assister.Llm;
using Assister.Modules.HomeAssistant;
using Assister.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Reads the real HA registry; every control is intercepted in memory. No device changes or model inference.
internal static class ControlReceiptProbe
{
    public static async Task<int> RunAsync()
    {
        var Configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var Cache = new HomeAssistantStateCache();
        using var Home = new HomeAssistantClient(Configuration, Cache, () => new HomeAssistantConnection(), NullLogger<HomeAssistantClient>.Instance);
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var Http = new HttpClient();
        var Checks = new List<object>();
        try
        {
            await Home.StartAsync(Deadline.Token);
            while (Cache.Snapshot().IsStale) await Task.Delay(100, Deadline.Token);
            var Snapshot = Cache.Snapshot();
            var Expected = Snapshot.Entities.Where(Entity => Entity.Domain == "light" && Entity.AreaName == "Kitchen")
                .Select(Entity => Entity.EntityId).Order().ToArray();
            if (Expected.Length < 2) throw new InvalidDataException("The audited Kitchen topology requires at least two assigned lights.");
            foreach (var Text in new[] { "turn off all kitchen lights", "turn off all the kitchen lights", "turn off all lights in the kitchen" })
            {
                var Intent = new IntentClassifier().Classify(Text)!;
                var Resolution = new HomeAssistantEntityResolver().Resolve(Intent, null, Snapshot);
                if (!Resolution.Entities.Select(Entity => Entity.EntityId).Order().SequenceEqual(Expected))
                    throw new InvalidDataException("Group resolution did not match Kitchen's assigned lights.");
                Checks.Add(new { text = Text, passed = true, targets = Expected });
            }
            foreach (var Streaming in new[] { false, true })
            foreach (var Partial in new[] { false, true })
            {
                var Actions = new InterceptedControls();
                var Model = new FailingReplyModel(Partial ? Expected.Take(1).ToArray() : Expected);
                var Registry = new ToolRegistry(new[] { "ha_search", "ha_control" }
                    .Select(Name => new HomeAssistantTool(Name, Cache, Actions, Http, Configuration)));
                var Loop = new ToolLoop(Model, Registry, new(Registry, Model: Model), Configuration);
                string Response;
                try
                {
                    Response = await Loop.RespondAsync(new("Hey Jarvis, set kitchen lights to off."), [], Deadline.Token,
                        OnText: Streaming ? (_, _) => Task.CompletedTask : null);
                    if (Partial) throw new InvalidDataException("Partial completion was reported as full success.");
                }
                catch (ControlNotConfirmedException Error) when (Partial)
                {
                    Response = Error.Response ?? throw new InvalidDataException("Missing partial receipt.");
                    if (!Response.Contains("remaining")) throw new InvalidDataException("Missing remaining-target warning.");
                }
                var ExpectedControls = Partial ? Expected.Take(1) : Expected;
                if (Actions.Calls.Count != 1 || !Actions.Calls[0].EntityIds.Order().SequenceEqual(ExpectedControls)
                    || Model.LastChoice != (Partial ? "required" : "none") || !Response.Contains("Turned off"))
                    throw new InvalidDataException("Control receipt regression failed.");
                Checks.Add(new { streaming = Streaming, partial = Partial, passed = true, response = Response, interceptedControls = Actions.Calls.Count });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, scope = "Live HA metadata; intercepted controls; injected post-action model failure", checks = Checks }));
            return 0;
        }
        finally { await Home.StopAsync(CancellationToken.None); }
    }

    private sealed class InterceptedControls : IHomeAssistantClient
    {
        public List<HomeAssistantControl> Calls { get; } = [];
        public Task ControlAsync(HomeAssistantControl Control, CancellationToken Token) { Calls.Add(Control); return Task.CompletedTask; }
    }

    private sealed class FailingReplyModel(string[] Targets) : ILanguageModel
    {
        private int Round;
        public string? LastChoice;
        public Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken Token)
        {
            if (Request.Tools?.Any(Tool => Tool.Function.Name == "interpret_device_request") == true)
                return Task.FromResult(new LlmResponse(null, [new("interpret", new("interpret_device_request", """{"action":"turn_off","target":"kitchen lights"}"""))], "tool_calls"));
            LastChoice = Request.ToolChoice;
            return ++Round switch
            {
                1 => Task.FromResult(new LlmResponse(null, [new("search", new("ha_search", """{"query":"kitchen lights","area":"kitchen"}"""))], "tool_calls")),
                2 => Task.FromResult(new LlmResponse(null, [new("control", new("ha_control", JsonSerializer.Serialize(new { entity_ids = Targets, action = "turn_off" })))], "tool_calls")),
                _ => throw new HttpRequestException("Injected reply failure after intercepted control")
            };
        }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { yield return new(Completed: await CompleteAsync(Request, Token)); }
    }
}
