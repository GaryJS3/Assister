using System.Text.Json;
using Assister.Conversations;
using Assister.Persistence;
using Assister.Tools;
using Microsoft.EntityFrameworkCore;

namespace Assister.Tests;

public sealed class ConversationPlaybackTests
{
    [Fact]
    public async Task CompletionWaitsForTurnAndReloadsContextFromAnotherScope()
    {
        var Options = new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=" + Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")).Options;
        await using var First = new AssisterDbContext(Options);
        await First.Database.MigrateAsync();
        var Run = Guid.NewGuid();
        var Row = new Conversation { Id = Guid.NewGuid(), SatelliteId = "speaker", DeviceContextJson = JsonSerializer.Serialize(new DeviceConversationContext { SourceDeviceId = "device", LastResponseRunId = Run }) };
        First.Conversations.Add(Row);
        await First.SaveChangesAsync();
        var Locks = new ConversationLocks();
        var Coordinator = new ConversationCoordinator(First, null!, Locks);
        await Locks.For("speaker").WaitAsync();
        Task Completion;
        try
        {
            Completion = Coordinator.MarkResponseFinishedAsync(Row.Id, default, "device", Run);
            Assert.False(Completion.IsCompleted);
            await using var Second = new AssisterDbContext(Options);
            var Current = await Second.Conversations.SingleAsync();
            Current.DeviceContextJson = JsonSerializer.Serialize(new DeviceConversationContext { SourceDeviceId = "device", LastResponseRunId = Run, References = [new("light.new", "New reference")] });
            await Second.SaveChangesAsync();
        }
        finally { Locks.For("speaker").Release(); }
        await Completion;
        var Context = JsonSerializer.Deserialize<DeviceConversationContext>(Row.DeviceContextJson)!;
        Assert.Equal("light.new", Assert.Single(Context.References).EntityId);
        Assert.NotNull(Context.ResponseFinishedAt);
        var Saved = Row.DeviceContextJson;
        await Coordinator.MarkResponseFinishedAsync(Row.Id, default);
        Assert.Equal(Saved, Row.DeviceContextJson);
        await Coordinator.MarkResponseFinishedAsync(Row.Id, default, "device", Guid.NewGuid());
        Assert.Equal(Saved, Row.DeviceContextJson);
        await Coordinator.MarkResponseFinishedAsync(Row.Id, default, "other", Run);
        Assert.Equal(Saved, Row.DeviceContextJson);
    }
}
