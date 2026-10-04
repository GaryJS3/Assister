using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.Timers;
using Assister.Persistence;
using Assister.Tools;
using Microsoft.EntityFrameworkCore;

namespace Assister.Tests;

public sealed class LocalModuleTests
{
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
        var Result = await Search.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>("""{"query":"tea"}"""), Context, CancellationToken.None);
        Assert.Contains("green tea", Result);
        Assert.DoesNotContain("car", Result);
        await new MemoryTool("memory_delete", Store).ExecuteAsync(JsonSerializer.SerializeToElement(new { id = Tea.RootElement.GetProperty("id").GetString() }),
            Context with { Request = new("forget tea preferences") }, CancellationToken.None);
        Assert.Equal("[]", await Search.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>("""{"query":"tea"}"""), Context, CancellationToken.None));
    }
}
