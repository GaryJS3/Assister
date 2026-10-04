using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.Timers;
using Assister.Persistence;
using Assister.Tools;
using Microsoft.EntityFrameworkCore;

namespace Assister.Tests;

public sealed class LocalModuleTests
{
    [Theory]
    [InlineData("What do you remember about my tea?", false, false)]
    [InlineData("Do not forget my tea preference", false, false)]
    [InlineData("I remember that my car is red", false, false)]
    [InlineData("Please remember my tea preference", true, false)]
    [InlineData("Could you forget my tea preference", false, true)]
    public void MemoryMutationRequiresAnExplicitCurrentRequest(string Text, bool Store, bool Delete)
    {
        Assert.Equal(Store, MemoryAuthorization.CanStore(Text));
        Assert.Equal(Delete, MemoryAuthorization.CanDelete(Text));
    }

    [Fact]
    public async Task MemoriesSurviveRestartAndBrokerRejectsUnauthorizedMutation()
    {
        var FileName = Path.Combine(Path.GetTempPath(), "assister-memory-" + Guid.NewGuid() + ".db");
        var Options = new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=" + FileName).Options;
        string Id;
        try
        {
            await using (var Database = new AssisterDbContext(Options))
            {
                await Database.Database.MigrateAsync();
                var Memory = new MemoryTool("memory_store", new(Database));
                var Result = JsonSerializer.Deserialize<JsonElement>(await Memory.ExecuteAsync(JsonSerializer.SerializeToElement(new { content = "My favorite tea is green", subject = "tea" }),
                    new(new("please remember my favorite tea"), []), CancellationToken.None));
                Id = Result.GetProperty("id").GetString()!;
            }
            await using (var Database = new AssisterDbContext(Options))
            {
                var Store = new LocalStore(Database);
                var Registry = new ToolRegistry(new[] { "memory_search", "memory_store", "memory_delete" }.Select(Name => new MemoryTool(Name, Store)));
                var Broker = new ToolBroker(Registry, Store);
                var Context = new ToolExecutionContext(new("What do you remember about my tea?"), []);
                var Selected = Registry.All.Keys.ToHashSet();
                var Result = await Broker.ExecuteAsync(new("search", new("memory_search", "{\"query\":\"What is my tea?\"}")), Selected, Context, CancellationToken.None);
                Assert.Contains("green", Result);
                Assert.Contains("error", await Broker.ExecuteAsync(new("delete", new("memory_delete", JsonSerializer.Serialize(new { id = Id }))), Selected, Context, CancellationToken.None));
                Assert.Single(await Store.QueryAsync("SELECT Id FROM Memories", CancellationToken.None));
                Assert.Contains("deleted", await Broker.ExecuteAsync(new("forget", new("memory_delete", JsonSerializer.Serialize(new { id = Id }))), Selected,
                    Context with { Request = new("forget my tea preference") }, CancellationToken.None));
                Assert.Equal("[]", await Broker.ExecuteAsync(new("search", new("memory_search", "{\"query\":\"tea\"}")), Selected, Context, CancellationToken.None));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(FileName); }
    }
    [Fact]
    public async Task TimersSurviveRestartAndCancellationIsSatelliteScoped()
    {
        var Options = new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")}").Options;
        await using (var Database = new AssisterDbContext(Options))
        {
            await Database.Database.MigrateAsync();
            var Handler = new TimerIntentHandler(new(Database));
            Assert.Contains("Started", await Handler.TryHandleAsync(new("set a tea timer for 5 minutes", "kitchen"), CancellationToken.None));
            Assert.Contains("between", await Handler.TryHandleAsync(new("set a timer for 999 hours", "kitchen"), CancellationToken.None));
        }
        await using (var Database = new AssisterDbContext(Options))
        {
            var Store = new LocalStore(Database);
            var Handler = new TimerIntentHandler(Store);
            Assert.Contains("no matching", await Handler.TryHandleAsync(new("cancel the tea timer", "bedroom"), CancellationToken.None));
            Assert.Single(await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='active'", CancellationToken.None));
            Assert.Contains("Cancelled", await Handler.TryHandleAsync(new("cancel the tea timer", "kitchen"), CancellationToken.None));
            Assert.Empty(await Store.QueryAsync("SELECT Id FROM Timers WHERE Status='active'", CancellationToken.None));
        }
    }

    [Fact]
    public async Task FtsRetrievesOnlyRelevantMemoriesAndDeleteRemovesTheIndexEntry()
    {
        await using var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=:memory:").Options);
        await Database.Database.OpenConnectionAsync();
        await Database.Database.MigrateAsync();
        var Store = new LocalStore(Database);
        var Context = new ToolExecutionContext(new("remember tea preferences", ConversationId: Guid.NewGuid()), []);
        var Save = new MemoryTool("memory_store", Store);
        using var Tea = JsonDocument.Parse(await Save.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>("""{"content":"I like green tea","subject":"tea"}"""), Context, CancellationToken.None));
        await Save.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>("""{"content":"My car is red","subject":"car"}"""), Context, CancellationToken.None);
        var Search = new MemoryTool("memory_search", Store);
        var Result = await Search.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>("""{"query":"What is my tea preference?"}"""), Context, CancellationToken.None);
        Assert.Contains("green tea", Result);
        Assert.DoesNotContain("car", Result);
        await new MemoryTool("memory_delete", Store).ExecuteAsync(JsonSerializer.SerializeToElement(new { id = Tea.RootElement.GetProperty("id").GetString() }),
            Context with { Request = new("forget tea preferences") }, CancellationToken.None);
        Assert.Equal("[]", await Search.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>("""{"query":"tea"}"""), Context, CancellationToken.None));
    }
}
