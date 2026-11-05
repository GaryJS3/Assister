using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Assister.IntegrationTests;

public sealed class StartupTests
{
    [Fact]
    public async Task HealthAndStatusRemainAvailableWithoutExternalServices()
    {
        var DataPath = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        await using var Factory = new TestApplication(DataPath);
        using var Client = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health")).StatusCode);
        var Status = JsonDocument.Parse(await Client.GetStringAsync("/api/status"));
        Assert.Equal("Degraded", Status.RootElement.GetProperty("status").GetString());
        Assert.Equal("Healthy", Status.RootElement.GetProperty("database").GetString());
        Assert.DoesNotContain("secret-test-token", Status.RootElement.GetRawText());
        Assert.Contains("Assistant operations", await Client.GetStringAsync("/"));
        var Diagnostics = await Client.GetStringAsync("/api/diagnostics/health");
        Assert.Contains("Database", Diagnostics);
        Assert.Contains("Satellites", Diagnostics);
        Assert.DoesNotContain("secret-test-token", Diagnostics);
        Assert.True(File.Exists(Path.Combine(DataPath, "assister.db")));
    }

    [Fact]
    public async Task EditorsHaveDirectRoutesAndListsStartWithoutAnEditor()
    {
        await using var Factory = new TestApplication(Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString()));
        using var Client = Factory.CreateClient();
        var List = await Client.GetStringAsync("/intents");
        Assert.Contains("New custom intent", List);
        Assert.DoesNotContain("Save intent", List);
        var Editor = await Client.GetStringAsync("/intents/edit/builtin-date");
        Assert.Contains("Back to definitions", Editor);
        Assert.Contains("Save intent", Editor);
        Assert.DoesNotContain("New custom intent", Editor);
        Assert.Contains("Create intent", await Client.GetStringAsync("/intents/edit/new"));
        Assert.Contains("Back to devices", await Client.GetStringAsync("/devices/view/test-device"));
        Assert.DoesNotContain("Remind me to", await Client.GetStringAsync("/reminders"));
    }
    private sealed class TestApplication(string DataPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((Context, Configuration) => Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Assister:DataPath"] = DataPath,
                    ["HomeAssistant:Token"] = "secret-test-token"
                }));
        }
    }
}
