using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Assister.IntegrationTests;

public sealed class GoogleCalendarDashboardTests
{
    [Fact]
    public async Task DashboardAccountsPersistWithoutReturningSecretsAndUnconfirmedDeleteIsRejected()
    {
        var PathName = Path.Combine(Path.GetTempPath(), "assister-calendar-api-" + Guid.NewGuid());
        try
        {
            using (var App = new Application(PathName))
            using (var Http = App.CreateClient())
            {
                Assert.Contains("Connected accounts", await Http.GetStringAsync("/calendars.html"));
                using var Rsa = RSA.Create(2048);
                var Credential = JsonSerializer.Serialize(new { type = "service_account", client_email = "test@example.iam.gserviceaccount.com", private_key = Rsa.ExportPkcs8PrivateKeyPem() });
                var Response = await Http.PostAsJsonAsync("/api/calendars/accounts", new { name = "Family", credential = Credential });
                Response.EnsureSuccessStatusCode();
                var Body = await Response.Content.ReadAsStringAsync(); Assert.DoesNotContain("private", Body, StringComparison.OrdinalIgnoreCase);
                var Accounts = await Http.GetStringAsync("/api/calendars/accounts"); Assert.Contains("Family", Accounts); Assert.DoesNotContain("protectedKey", Accounts); Assert.DoesNotContain("PRIVATE KEY", Accounts);
                var Invalid = await Http.PostAsJsonAsync("/api/calendars/confirm-delete", new { confirmation = "invented" });
                Assert.Equal(HttpStatusCode.BadRequest, Invalid.StatusCode);
                var BadKey = await Http.PostAsJsonAsync("/api/calendars/accounts", new { name = "Invalid", credential = "{}" });
                Assert.Equal(HttpStatusCode.BadRequest, BadKey.StatusCode);
            }
            using (var App = new Application(PathName))
            using (var Http = App.CreateClient())
                Assert.Contains("Family", await Http.GetStringAsync("/api/calendars/accounts"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(PathName)) Directory.Delete(PathName, true); }
    }
    private sealed class Application(string DataPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder) => Builder.ConfigureAppConfiguration((Context, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = DataPath }));
    }
}
