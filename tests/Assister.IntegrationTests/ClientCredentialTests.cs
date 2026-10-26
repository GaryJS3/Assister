using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Assister.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Assister.IntegrationTests;

public sealed class ClientCredentialTests
{
    private const string Admin = "test-administrator-upload-token-123456789";
    private const string WebToken = "existing-web-client-token-123456789";

    [Fact]
    public async Task AdministrationRequiresAdminAndSameOriginAndNeverListsSecrets()
    {
        using var App = new TestApplication();
        using var Http = App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/devices/credentials")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new("Bearer", WebToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/devices/credentials")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new("Bearer", Admin);
        Http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/devices/credentials")).StatusCode);
        Http.DefaultRequestHeaders.Remove("Origin");
        var Response = await Http.GetAsync("/api/devices/credentials");
        Assert.Equal("no-store", Response.Headers.CacheControl!.ToString());
        var Body = await Response.Content.ReadAsStringAsync();
        Assert.Contains("web", Body);
        Assert.DoesNotContain(WebToken, Body);
        Assert.DoesNotContain(Admin, Body);
        Assert.Contains("One identity", await Http.GetStringAsync("/client-tokens.html"));
    }

    [Fact]
    public async Task PhoneIdentityRegistersSeparatelyAndRotationRevocationPersist()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assister-credential-tests", Guid.NewGuid().ToString());
        string Rotated;
        using (var App = new TestApplication(Path))
        {
            using var AdminHttp = App.CreateClient(); AdminHttp.DefaultRequestHeaders.Authorization = new("Bearer", Admin);
            var Created = await AdminHttp.PostAsJsonAsync("/api/devices/credentials", new { clientId = "pixel-8-pro", owner = "gary" });
            Created.EnsureSuccessStatusCode();
            var Token = (await Created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Assert.Equal(64, Token.Length);
            using var Phone = App.CreateClient(); Phone.DefaultRequestHeaders.Authorization = new("Bearer", Token);
            using var Web = App.CreateClient(); Web.DefaultRequestHeaders.Authorization = new("Bearer", WebToken);
            (await Phone.PostAsJsonAsync("/api/client/clients/register", new ClientRegistration("android_phone", "Pixel 8 Pro", []))).EnsureSuccessStatusCode();
            (await Web.PostAsJsonAsync("/api/client/clients/register", new ClientRegistration("web", "Web", []))).EnsureSuccessStatusCode();
            var Devices = (await Phone.GetFromJsonAsync<RegisteredClient[]>("/api/client/clients"))!;
            Assert.Equal(2, Devices.Length);
            Assert.Contains(Devices, Device => Device.ClientId == "pixel-8-pro" && Device.Owner == "gary");
            var Rotate = await AdminHttp.PostAsync("/api/devices/credentials/pixel-8-pro/rotate", null);
            Rotated = (await Rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Assert.NotEqual(Token, Rotated);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Phone.GetAsync("/api/client/protocol")).StatusCode);
            Phone.DefaultRequestHeaders.Authorization = new("Bearer", Rotated);
            (await Phone.GetAsync("/api/client/protocol")).EnsureSuccessStatusCode();
            (await Web.GetAsync("/api/client/protocol")).EnsureSuccessStatusCode();
            var Disk = File.ReadAllText(System.IO.Path.Combine(Path, "client-credentials.json"));
            Assert.DoesNotContain(Token, Disk); Assert.DoesNotContain(Rotated, Disk);
            var Listing = await AdminHttp.GetStringAsync("/api/devices/credentials");
            Assert.DoesNotContain(Rotated, Listing);
            Assert.Equal(HttpStatusCode.Conflict, (await AdminHttp.PostAsJsonAsync("/api/devices/credentials", new {clientId="pixel-8-pro", owner="gary"})).StatusCode);
        }
        using (var App = new TestApplication(Path))
        {
            using var Http = App.CreateClient(); Http.DefaultRequestHeaders.Authorization = new("Bearer", Rotated);
            (await Http.GetAsync("/api/client/protocol")).EnsureSuccessStatusCode();
            Http.DefaultRequestHeaders.Authorization = new("Bearer", Admin);
            (await Http.DeleteAsync("/api/devices/credentials/pixel-8-pro")).EnsureSuccessStatusCode();
            // A configuration credential is overridden durably too, without changing environment variables.
            (await Http.DeleteAsync("/api/devices/credentials/web")).EnsureSuccessStatusCode();
        }
        using (var App = new TestApplication(Path))
        {
            using var Http = App.CreateClient();
            foreach (var Token in new[] {Rotated, WebToken})
            {
                Http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
                Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/client/protocol")).StatusCode);
            }
        }
    }

    [Fact]
    public async Task ReassignmentChangesOwnerAccessWithoutMovingExistingHistory()
    {
        using var App = new TestApplication();
        using var Http = App.CreateClient(); Http.DefaultRequestHeaders.Authorization = new("Bearer", WebToken);
        var Conversation = (await Http.PostAsJsonAsync("/api/client/conversations", new {})).EnsureSuccessStatusCode();
        var Id = (await Conversation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        using var Management = App.CreateClient(); Management.DefaultRequestHeaders.Authorization = new("Bearer", Admin);
        (await Management.PutAsJsonAsync("/api/devices/credentials/web/owner", new {owner="someone-else"})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"/api/client/conversations/{Id}")).StatusCode);
        (await Management.PutAsJsonAsync("/api/devices/credentials/web/owner", new {owner="gary"})).EnsureSuccessStatusCode();
        (await Http.GetAsync($"/api/client/conversations/{Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await Management.PostAsJsonAsync("/api/devices/credentials", new {clientId="bad:id", owner="gary"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Management.PostAsJsonAsync("/api/devices/credentials", new {clientId="phone", owner=" "})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Management.DeleteAsync("/api/devices/credentials/missing")).StatusCode);
    }

    private sealed class TestApplication(string? DirectoryPath = null) : WebApplicationFactory<Program>
    {
        private readonly string DataPath = DirectoryPath ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assister-credential-tests", Guid.NewGuid().ToString());
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Configuration) => Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = DataPath,
                ["ClientUpdates:UploadToken"] = Admin,
                ["RichClients:Clients:web:Token"] = WebToken,
                ["RichClients:Clients:web:Owner"] = "gary",
                ["SatelliteBridge:Enabled"] = "false",
                ["EspHome:Host"] = "",
                ["HomeAssistant:Token"] = ""
            }));
        }
    }
}
