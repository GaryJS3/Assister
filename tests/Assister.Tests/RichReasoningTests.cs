using System.Text.Json;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Interactions;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class RichReasoningTests
{
    [Theory]
    [InlineData("completed", "reasoning")]
    [InlineData("completed", "model.output")]
    [InlineData("failed", "reasoning")]
    [InlineData("failed", "model.output")]
    [InlineData("cancelled", "reasoning")]
    [InlineData("cancelled", "model.output")]
    public void OrderedRoundsAndDetailsPersistForReplay(string Outcome, string Prefix)
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Assister:DataPath"] = Path,
            ["Diagnostics:PersistHistory"] = "false",
            ["LanguageModel:ApiKey"] = "private-value"
        }).Build();
        Guid Id;
        InteractionEvent[] Original;
        using (var Store = new InteractionStore(Config))
        {
            var Conversation = Store.CreateConversation("owner");
            Id = Store.Submit(Conversation.Id, "owner", new("hello", "key")).Id;
            using var Runs = new RunStore(Config);
            using var Run = RunTracing.BeginRun(Runs, "test");
            using var Feedback = InteractionFeedback.Observe((Type, Data) => Store.Append(Id, Type, Data));
            for (var Round = 1; Round <= 2; Round++)
            {
                using var Step = RunTracing.Start("LanguageModel", "Round", "test");
                using var Thinking = Prefix == "reasoning" ? Step.Thinking(Round) : Step.ModelOutput(Round);
                Thinking.Delta("api_key=private-");
                Thinking.Delta("value\n");
                Thinking.Delta("Remaining thought");
                Thinking.Complete(Round == 2 ? Outcome : "completed");
                Step.Input(new
                {
                    password = "private-value",
                    query = "hello"
                });
                Step.Output(new
                {
                    result = new string('x', 5000)
                });
                Step.Finish();
            }
            Store.Append(Id, "interaction." + Outcome, new
            {
            }, Outcome);
            Original = Store.Events(Id, 0).ToArray();
            Assert.Equal(Enumerable.Range(1, Original.Length).Select(Value => (long)Value), Original.Select(Item => Item.Sequence));
            Assert.Equal(2, Original.Count(Item => Item.Type == Prefix + ".started"));
            Assert.Equal(new[] { 1, 2 }, Original.Where(Item => Item.Type == Prefix + ".completed").Select(Item => Item.Data.GetProperty("modelRound").GetInt32()));
            Assert.Equal(Outcome, Original.Last(Item => Item.Type == Prefix + ".completed").Data.GetProperty("status").GetString());
            Assert.DoesNotContain("private-value", JsonSerializer.Serialize(Original));
            Assert.DoesNotContain(Original, Item => Item.Type == "response.delta");
            Assert.Contains(Original, Item => Item.Type == "step.updated" && Item.Data.GetProperty("outputTruncated").GetBoolean());
            foreach (var Started in Original.Where(Item => Item.Type == Prefix + ".started"))
            {
                var StepId = Started.Data.GetProperty("stepId").GetGuid();
                Assert.Contains(Original, Item => Item.Type == "step.updated" && Item.Data.GetProperty("stepId").GetGuid() == StepId);
            }
        }
        using (var Replay = new InteractionStore(Config))
        {
            var Events = Replay.Events(Id, 0);
            Assert.Equal(Original.Select(Item => Item.EventId), Events.Select(Item => Item.EventId));
            Assert.Equal(Original.Skip(4).Select(Item => Item.EventId), Replay.Events(Id, 4).Select(Item => Item.EventId));
            // Repeated delivery uses the same sequence/event ID; normal cursor suppression applies to every new event type.
            Assert.Equal(Events.Count, Events.Concat(Replay.Events(Id, 0)).DistinctBy(Item => Item.Sequence).Count());
            Assert.Empty(Replay.Events(Id, Events.Last().Sequence));
        }
        using (var Pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={System.IO.Path.Combine(Path, "interactions.db")}"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(Pool);
        Directory.Delete(Path, true);
    }

    [Fact]
    public void SafePrefixesStreamBeforeCompletionAndPayloadCaptureCanBeDisabled()
    {
        var Events = new List<(string Type, JsonElement Data)>();
        using var Feedback = InteractionFeedback.Observe((Type, Data) => Events.Add((Type, JsonSerializer.SerializeToElement(Data, RunStore.Json))));
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LanguageModel:ApiKey"] = "secret words" }).Build();
        using var Store = new RunStore(Config);
        using var Run = RunTracing.BeginRun(Store, "test");
        using var Step = RunTracing.Start("LanguageModel", "Round", "test");
        using var Thinking = Step.Thinking(1);
        Thinking.Delta("Looking at available tools and choosing the appropriate tool. secret ");
        Thinking.Delta("words api_key=credential");
        Thinking.Delta("more text and a long final thought that should remain safe. ");
        Assert.Contains(Events, Event => Event.Type == "reasoning.delta");
        Assert.DoesNotContain(Events, Event => Event.Type == "reasoning.completed");
        Thinking.Complete("completed");
        Assert.DoesNotContain("secret words", JsonSerializer.Serialize(Events.Select(Event => Event.Data)));
        Assert.DoesNotContain("credentialmore", JsonSerializer.Serialize(Events.Select(Event => Event.Data)));
        var Disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Diagnostics:CapturePayloads"] = "false" }).Build();
        using var DisabledStore = new RunStore(Disabled);
        using var DisabledRun = RunTracing.BeginRun(DisabledStore, "test");
        using var DisabledStep = RunTracing.Start("LanguageModel", "Round", "test");
        Events.Clear();
        using (var Omitted = DisabledStep.Thinking(1)) { Omitted.Delta("private reasoning"); Omitted.Complete("completed"); }
        Assert.DoesNotContain(Events, Event => Event.Type == "reasoning.delta");
    }
    [Theory]
    [InlineData("reasoning")]
    [InlineData("model.output")]
    public void CancellationAndRestartCloseUnfinishedThinking(string Prefix)
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Path }).Build();
        using (var Store = new InteractionStore(Config))
        {
            var Conversation = Store.CreateConversation("owner");
            var Item = Store.Submit(Conversation.Id, "owner", new("hello", "key"));
            using var Runs = new RunStore();
            using var Run = RunTracing.BeginRun(Runs, "test");
            using var Feedback = InteractionFeedback.Observe((Type, Data) => Store.Append(Item.Id, Type, Data));
            using var Step = RunTracing.Start("LanguageModel", "Round", "test");
            using var Cancel = new CancellationTokenSource();
            using (var Thinking = Prefix == "reasoning" ? Step.Thinking(1, Cancel.Token) : Step.ModelOutput(1, Cancel.Token))
            {
                Thinking.Delta("partial thought");
                Cancel.Cancel();
            }
            Assert.Equal("cancelled", Store.Events(Item.Id, 0).Last().Data.GetProperty("status").GetString());
            Store.Append(Item.Id, Prefix + ".started", new
            {
                stepId = Guid.NewGuid(),
                modelRound = 2
            });
            Store.Recover();
            var Events = Store.Events(Item.Id, 0);
            Assert.Equal(Prefix + ".completed", Events[^2].Type);
            Assert.Equal("failed", Events[^2].Data.GetProperty("status").GetString());
            Assert.Equal("interaction.failed", Events[^1].Type);
        }
        using (var Pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={System.IO.Path.Combine(Path, "interactions.db")}"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(Pool);
        Directory.Delete(Path, true);
    }
}
