using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assister.Modules.Reminders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Assister.IntegrationTests;

public sealed class ReminderDashboardTests
{
    [Fact]
    public async Task DashboardCreatesEditsListsAndRemovesPersistedReminders()
    {
        var PathName = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        string Id;
        await using (var Factory = new Application(PathName))
        {
            using var Http = Factory.CreateClient();
            Assert.Contains("reminder-form", await Http.GetStringAsync("/reminders.html"));
            Assert.Contains("/reminders.html", await Http.GetStringAsync("/app-shell.js"));
            var Input = new ReminderInput("Take vitamin", "bedroom", 20, 0, null, "America/New_York");
            using var Created = await Http.PostAsJsonAsync("/api/reminders", Input);
            Assert.Equal(HttpStatusCode.Created, Created.StatusCode);
            Id = (await Created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var List = await Http.GetFromJsonAsync<JsonElement>("/api/reminders");
            var Row = Assert.Single(List.GetProperty("items").EnumerateArray());
            Assert.Equal("Take vitamin", Row.GetProperty("message").GetString());
            var Changed = Input with { Message = "Take out trash", Hour = 18, Weekday = 0, SatelliteId = "all" };
            Assert.Equal(HttpStatusCode.NoContent, (await Http.PutAsJsonAsync("/api/reminders/" + Id, Changed)).StatusCode);
        }
        await using (var Factory = new Application(PathName))
        {
            using var Http = Factory.CreateClient();
            var List = await Http.GetFromJsonAsync<JsonElement>("/api/reminders");
            var Row = Assert.Single(List.GetProperty("items").EnumerateArray());
            Assert.Equal("Take out trash", Row.GetProperty("message").GetString());
            Assert.Equal("all", Row.GetProperty("satelliteId").GetString());
            Assert.Equal(18, Row.GetProperty("hour").GetInt32());
            Assert.Equal(0, Row.GetProperty("weekday").GetInt32());
            Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync("/api/reminders/" + Id)).StatusCode);
            Assert.Empty((await Http.GetFromJsonAsync<JsonElement>("/api/reminders")).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync("/api/reminders/" + Id)).StatusCode);
        }
    }

    [Fact]
    public async Task InvalidSchedulesAreRejectedWithoutSaving()
    {
        await using var Factory = new Application(Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString()));
        using var Http = Factory.CreateClient();
        var Input = new ReminderInput("Stretch", "desk", 8, 0, null, "America/New_York");
        foreach (var Invalid in new[] { Input with { Hour = 24 }, Input with { Minute = -1 }, Input with { Weekday = 7 },
            Input with { Message = " " }, Input with { SatelliteId = "" }, Input with { TimeZone = "unknown/zone" } })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync("/api/reminders", Invalid)).StatusCode);
        }
        Assert.Empty((await Http.GetFromJsonAsync<JsonElement>("/api/reminders")).GetProperty("items").EnumerateArray());
    }

    private sealed class Application(string DataPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder) =>
            Builder.ConfigureAppConfiguration((Context, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = DataPath,
                ["Assister:TimeZone"] = "America/New_York"
            }));
    }
}
