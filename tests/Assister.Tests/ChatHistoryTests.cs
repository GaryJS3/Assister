using System.Text;
using System.Text.Json;
using Assister.Contracts;
using Assister.Persistence;
using Assister.Tools;
using Microsoft.EntityFrameworkCore;

namespace Assister.Tests;

public sealed class ChatHistoryTests
{
    [Fact]
    public async Task HistoryIsChronologicalBoundedAndScopedToTheCurrentSatelliteAndConversation()
    {
        await using var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=:memory:").Options);
        await Database.Database.OpenConnectionAsync();
        await Database.Database.EnsureCreatedAsync();
        var Own = new Conversation { Id = Guid.NewGuid(), SatelliteId = "own" };
        var Other = new Conversation { Id = Guid.NewGuid(), SatelliteId = "other" };
        Database.Conversations.AddRange(Own, Other);
        for (var Index = 0; Index < 15; Index++)
            Database.ConversationTurns.Add(new() { ConversationId = Own.Id, UserText = $"question {Index}", AssistantText = $"answer {Index}", Outcome = "succeeded" });
        Database.ConversationTurns.Add(new() { ConversationId = Other.Id, UserText = "private", AssistantText = "secret", Outcome = "succeeded" });
        await Database.SaveChangesAsync();
        var Tool = new ChatHistoryTool(new(Database));
        using var Arguments = JsonDocument.Parse("{\"limit\":2}");
        using var Result = JsonDocument.Parse(await Tool.ExecuteAsync(Arguments.RootElement, new(new("repeat", "own", ConversationId: Own.Id), []), default));
        var Turns = Result.RootElement.GetProperty("turns");
        Assert.Equal(2, Turns.GetArrayLength());
        Assert.Equal("question 13", Turns[0].GetProperty("user").GetString());
        Assert.Equal("answer 14", Turns[1].GetProperty("assistant").GetString());
        using var CrossSatellite = JsonDocument.Parse(await Tool.ExecuteAsync(Arguments.RootElement, new(new("repeat", "own", ConversationId: Other.Id), []), default));
        Assert.Empty(CrossSatellite.RootElement.GetProperty("turns").EnumerateArray());
        using var NewChat = JsonDocument.Parse(await Tool.ExecuteAsync(Arguments.RootElement, new(new("repeat", "own"), []), default));
        Assert.Empty(NewChat.RootElement.GetProperty("turns").EnumerateArray());
        Database.ConversationTurns.Add(new() { ConversationId = Own.Id, UserText = new string('界', 2000), AssistantText = new string('界', 4000), Outcome = "succeeded" });
        await Database.SaveChangesAsync();
        var Large = await Tool.ExecuteAsync(Arguments.RootElement, new(new("repeat", "own", ConversationId: Own.Id), []), default);
        Assert.InRange(Encoding.UTF8.GetByteCount(Large), 1, 16384);
        using var Limited = JsonDocument.Parse(Large);
        Assert.True(Limited.RootElement.GetProperty("truncated").GetBoolean());
    }
}
