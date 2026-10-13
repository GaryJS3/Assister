using System.Text.Json;
using Assister.Contracts;
using Assister.Llm;
using Assister.Persistence;
using Assister.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class ReminderToolTests
{
    [Theory]
    [InlineData("Please schedule a nudge", true)]
    [InlineData("Change the reminder to 9 PM", true)]
    [InlineData("Can you remind me each evening?", true)]
    [InlineData("Put it on my calander", true)]
    [InlineData("Check my calendar", true)]
    [InlineData("Explain solar power", false)]
    public async Task ToolsAreOfferedOnlyForSchedulingRequests(string Text, bool Expected)
    {
        var Config = new ConfigurationBuilder().Build();
        var Registry = new ToolRegistry(new[] { "reminder_get", "reminder_create", "reminder_edit", "reminder_cancel" }.Select(Name => new ReminderTool(Name, null!, Config)));
        var Model = new CapturingModel();
        await new ToolLoop(Model, Registry, new(Registry), Config).RespondAsync(new(Text), [], default);
        Assert.Equal(Expected ? 4 : 0, Model.Request!.Tools!.Count);
    }

    [Fact]
    public async Task BrokerCreatesGetsEditsAndCancelsWithScopeValidationAndAudit()
    {
        await using var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=:memory:").Options);
        await Database.Database.OpenConnectionAsync();
        await Database.Database.MigrateAsync();
        var Store = new LocalStore(Database);
        var Config = new ConfigurationBuilder().Build();
        var Registry = new ToolRegistry(new[] { "reminder_get", "reminder_create", "reminder_edit", "reminder_cancel" }.Select(Name => new ReminderTool(Name, Store, Config)));
        var Broker = new ToolBroker(Registry, Store);
        var Selected = Registry.All.Keys.ToHashSet();
        async Task<JsonElement> Call(string Name, object Args, string Text, string Satellite = "desk", ToolExecutionContext? Context = null)
        {
            var Result = await Broker.ExecuteAsync(new(Guid.NewGuid().ToString(), new(Name, JsonSerializer.Serialize(Args))), Selected,
                Context ?? new(new(Text, SatelliteId: Satellite), []), default);
            return JsonSerializer.Deserialize<JsonElement>(Result);
        }
        var CreateArgs = new { message = "Stretch", hour = 20, minute = 0, weekday = -1 };
        var Context = new ToolExecutionContext(new("Schedule a reminder to stretch", SatelliteId: "desk"), []);
        var Created = await Call("reminder_create", CreateArgs, "", Context: Context);
        Assert.Equal("created", Created.GetProperty("status").GetString());
        var Id = Created.GetProperty("reminder").GetProperty("Id").GetString()!;
        Assert.True((await Call("reminder_create", CreateArgs, "", Context: Context)).TryGetProperty("error", out _));
        var List = await Call("reminder_get", new { }, "Get my reminders");
        Assert.Single(List.GetProperty("items").EnumerateArray());
        var Due = Created.GetProperty("reminder").GetProperty("NextDueAt").GetString();
        var Edited = await Call("reminder_edit", new { id = Id, message = "Stretch legs" }, "Edit my reminder message");
        Assert.Equal(Due, Edited.GetProperty("reminder").GetProperty("NextDueAt").GetString());
        Edited = await Call("reminder_edit", new { id = Id, hour = 18, weekday = 0 }, "Move my reminder to Sunday at 6 PM");
        Assert.Equal(0, Edited.GetProperty("reminder").GetProperty("Weekday").GetInt32());
        Assert.Equal("Stretch legs", Edited.GetProperty("reminder").GetProperty("Message").GetString());
        Assert.True((await Call("reminder_edit", new { id = Id, hour = 99 }, "Edit my reminder")).TryGetProperty("error", out _));
        Assert.Empty((await Call("reminder_get", new { }, "Get reminders", "other")).GetProperty("items").EnumerateArray());
        Assert.True((await Call("reminder_cancel", new { id = Id }, "Cancel reminder", "other")).TryGetProperty("error", out _));
        Assert.True((await Call("reminder_create", CreateArgs, "Hello")).TryGetProperty("error", out _));
        Assert.Equal("cancelled", (await Call("reminder_cancel", new { id = Id }, "Cancel my reminder")).GetProperty("status").GetString());
        Assert.Empty((await Call("reminder_get", new { }, "Get reminders")).GetProperty("items").EnumerateArray());
        Assert.NotEmpty(await Store.QueryAsync("SELECT Id FROM ToolAudit WHERE Outcome='succeeded'", default));
    }

    private sealed class CapturingModel : ILanguageModel
    {
        public LlmRequest? Request { get; private set; }
        public Task<LlmResponse> CompleteAsync(LlmRequest Input, CancellationToken Token)
        {
            Request = Input;
            return Task.FromResult(new LlmResponse("Done.", [], "stop"));
        }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        {
            yield return new(Completed: await CompleteAsync(Input, Token));
        }
    }
}
