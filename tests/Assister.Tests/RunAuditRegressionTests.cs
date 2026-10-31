using System.Text.Json;
using Assister.Intents;
using Assister.Modules.Reminders;
using Assister.Persistence;
using Assister.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class RunAuditRegressionTests
{
    [Theory]
    [InlineData("Shut off kitchen lights.", DirectIntentKind.TurnOff)]
    [InlineData("shut kitchen lights off", DirectIntentKind.TurnOff)]
    [InlineData("turn on kitchen lights", DirectIntentKind.TurnOn)]
    [InlineData("turn kitchen lights on", DirectIntentKind.TurnOn)]
    public void PowerAliasesUseDirectClassificationAndAuthorization(string Text, DirectIntentKind Kind)
    {
        Assert.Equal(Kind, new IntentClassifier().Classify(Text)!.Kind);
        Assert.Equal(Kind == DirectIntentKind.TurnOn ? "turn_on" : "turn_off", ControlRequest.Parse(Text)!.Action);
    }

    [Theory]
    [InlineData("remind me in one hour to make sure I enjoy the game", true, "acknowledge")]
    [InlineData("create an alarm reminder for 7:58 pm to refund game", true, "alarm")]
    [InlineData("set an alarm for 7:56pm to refund game", true, "alarm")]
    [InlineData("remind me every day at 7:58 pm to refund game", false, "acknowledge")]
    public async Task SchedulePolicyPreservesOneTimeIntentAndImportance(string Text, bool OneTime, string Importance)
    {
        await using var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=:memory:").Options);
        await Database.Database.OpenConnectionAsync();
        await Database.Database.MigrateAsync();
        var Store = new LocalStore(Database);
        var Tool = new ReminderTool("reminder_create", Store, new ConfigurationBuilder().Build());
        var Context = new ToolExecutionContext(new(Text), []);
        var Result = JsonSerializer.Deserialize<JsonElement>(await Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { message = "game", hour = 19, minute = 58, weekday = -1 }), Context, default));
        Assert.Equal("created", Result.GetProperty("status").GetString());
        Assert.Equal(OneTime, Result.GetProperty("reminder").GetProperty("OneTime").GetBoolean());
        Assert.Equal(Importance, Result.GetProperty("reminder").GetProperty("Importance").GetString());
        var Row = Assert.Single(await Store.QueryAsync("SELECT OneTime,Importance FROM ReminderPolicies", default));
        Assert.Equal(OneTime ? "1" : "0", Row[0]); Assert.Equal(Importance, Row[1]);
        if (Text.Contains("one hour")) Assert.Equal(Context.ReceivedAt.AddHours(1), Result.GetProperty("reminder").GetProperty("NextDueAt").GetDateTimeOffset());
        if (Text.Contains("7:56")) Assert.Equal(56, Result.GetProperty("reminder").GetProperty("Minute").GetInt32());
    }

    [Theory]
    [InlineData("set an alarm for 7:57 to refund game")]
    [InlineData("schedule a reminder to stretch")]
    public async Task MissingOrAmbiguousTimeCannotCreateSchedule(string Text)
    {
        await using var Database = new AssisterDbContext(new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite("Data Source=:memory:").Options);
        await Database.Database.OpenConnectionAsync(); await Database.Database.MigrateAsync();
        var Store = new LocalStore(Database);
        var Tool = new ReminderTool("reminder_create", Store, new ConfigurationBuilder().Build());
        var Result = await Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { message = "game", hour = 7, minute = 57, weekday = -1 }), new(new(Text), []), default);
        Assert.Contains("error", Result);
        Assert.Empty(await Store.QueryAsync("SELECT Id FROM Reminders", default));
    }

    [Theory]
    [InlineData("set an alarm for 7:56pm to refund game", true)]
    [InlineData("arm the security alarm", false)]
    public void ScheduledAlarmDoesNotConflateSecurityAlarm(string Text, bool Expected) => Assert.Equal(Expected, ReminderTool.Relevant(Text));
}
