using Assister.Contracts;
using Assister.Interactions;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class InteractionStoreTests
{
    private static IConfiguration Configuration(string Path) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Path }).Build();
    [Fact]
    public void ContextUpdatesPersistSeparatelyAndInspectionIsBoundedAndRedacted()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        Guid Id;
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Path, ["LanguageModel:ApiKey"] = "secret-context-key" }).Build();
        using (var Store = new InteractionStore(Config))
        {
            var Conversation = Store.CreateConversation("owner");
            Id = Store.Submit(Conversation.Id, "owner", new("hello", "context")).Id;
            Store.SelectContext(Id, new("tool-1", "tool_result", "tool", "Selected records", "secret-context-key " + new string('x', 5000), new { toolCallId = "call-1", token = "secret" }, 1));
            Store.SelectContext(Id, new("tool-1", "tool_result", "tool", "Selected records", "secret-context-key " + new string('x', 5000), new { toolCallId = "call-1", token = "secret" }, 2));
            Store.SelectContext(Id, new("tool-1", "tool_result", "tool", "Selected records", "duplicate", new { }, 2));
            var Context = Assert.Single(Store.Context(Id));
            Assert.Equal(new[] { 1, 2 }, Context.ModelRounds);
            Assert.True(Context.Truncated);
            Assert.DoesNotContain("secret-context-key", Context.Content);
            Assert.Equal("[redacted]", Context.Provenance.GetProperty("token").GetString());
            Assert.Equal(new[] { "interaction.created", "context.added", "context.updated" }, Store.Events(Id, 0).Select(Event => Event.Type));
        }
        using var Reopened = new InteractionStore(Config);
        Assert.Single(Reopened.Context(Id));
    }

    [Fact]
    public async Task ConcurrentEventsAreOrderedAndRecoverableAfterReopen()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        Guid Id;
        using (var Store = new InteractionStore(Configuration(Path)))
        {
            var Conversation = Store.CreateConversation("one");
            var Item = Store.Submit(Conversation.Id, "one", new("hello", "key"));
            Id = Item.Id;
            Assert.Equal(Id, Store.Submit(Conversation.Id, "one", new("hello", "key")).Id);
            Assert.Throws<InvalidOperationException>(() => Store.Submit(Conversation.Id, "one", new("different", "key")));
            Assert.Null(Store.Get(Id, "other"));
            Assert.False(Store.OwnsConversation(Conversation.Id, "other"));
            await Task.WhenAll(Enumerable.Range(0, 100).Select(Index => Task.Run(() => Store.Append(Id, "step.progress", new { index = Index }))));
            Assert.Equal(Enumerable.Range(1, 101).Select(Value => (long)Value), Store.Events(Id, 0).Select(Event => Event.Sequence));
            Assert.Equal(51, Store.Events(Id, 50).Count);
            Store.Append(Id, "response.completed", new { text = "answer" }, Response: "answer");
            Store.Append(Id, "interaction.completed", new { }, "completed");
            Store.Append(Id, "step.progress", new { ignored = true });
            Assert.Equal(103, Store.Get(Id)!.LastSequence);
        }
        using (var Store = new InteractionStore(Configuration(Path)))
        {
            Store.Recover();
            Assert.Equal("answer", Store.Get(Id)!.Response);
            Assert.Equal("completed", Store.Get(Id)!.Status);
            Assert.Equal(103, Store.Events(Id, 0).Count);
        }
    }
    [Fact]
    public void RestartTerminatesUnfinishedWorkWithoutReexecuting()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        using var Store = new InteractionStore(Configuration(Path));
        var Conversation = Store.CreateConversation("one");
        var Item = Store.Submit(Conversation.Id, "one", new("control something", "key"));
        Assert.True(Store.Cancel(Item.Id));
        Assert.False(Store.Cancel(Item.Id));
        Store.Recover();
        Assert.Equal("failed", Store.Get(Item.Id)!.Status);
        Assert.Empty(Store.Pending());
        Assert.Equal("server_restarted", Store.Events(Item.Id, 0).Last().Data.GetProperty("code").GetString());
        var Race = Store.Submit(Conversation.Id, "one", new("another request", "race"));
        Store.Cancel(Race.Id);
        Store.Append(Race.Id, "interaction.completed", new { }, "completed");
        Assert.Equal("cancelled", Store.Get(Race.Id)!.Status);
        Assert.Equal("interaction.cancelled", Store.Events(Race.Id, 0).Last().Type);
    }
}
