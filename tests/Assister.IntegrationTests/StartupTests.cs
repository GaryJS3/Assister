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
        Assert.True(File.Exists(Path.Combine(DataPath, "assister.db")));
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
