using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assister.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assister.IntegrationTests;

public sealed class LanguageModelSettingsTests
{
    [Fact]
    public async Task SettingsApplyImmediatelyPersistAndNeverReturnSecrets()
    {
        var Data = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        var Settings = new LanguageModelSettings("http://localhost:1234/v1", "test-model", 0.7, 1024, 60, false, "private-key");
        await using (var App = new Application(Data))
        {
            using var Client = App.CreateClient();
            Assert.Contains("AI server", await Client.GetStringAsync("/ai-server.html"));
            var Response = await Client.PutAsJsonAsync("/api/settings/ai-server", Settings);
            Response.EnsureSuccessStatusCode();
            Assert.DoesNotContain("private-key", await Response.Content.ReadAsStringAsync());
            var Configuration = App.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("test-model", Configuration["LanguageModel:Model"]);
            Assert.Equal("private-key", Configuration["LanguageModel:ApiKey"]);
            Assert.False(Configuration.GetValue<bool>("LanguageModel:EnableThinking"));
            (await Client.PutAsJsonAsync("/api/settings/ai-server", Settings with { ApiKey = "", Model = "updated" })).EnsureSuccessStatusCode();
            Assert.Equal("private-key", Configuration["LanguageModel:ApiKey"]);
            foreach (var Invalid in new[] { Settings with { BaseUrl = "file:///tmp" }, Settings with { MaxTokens = 0 }, Settings with { TimeoutSeconds = 121 }, Settings with { Temperature = 3 }, Settings with { Model = " " }, Settings with { ApiKey = "bad\nkey" } })
                Assert.Equal(HttpStatusCode.BadRequest, (await Client.PutAsJsonAsync("/api/settings/ai-server", Invalid)).StatusCode);
            Assert.Equal("updated", Configuration["LanguageModel:Model"]);
            using var CrossOrigin = new HttpRequestMessage(HttpMethod.Put, "/api/settings/ai-server") { Content = JsonContent.Create(Settings) };
            CrossOrigin.Headers.Add("Origin", "https://example.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await Client.SendAsync(CrossOrigin)).StatusCode);
        }
        await using (var App = new Application(Data))
        {
            using var Client = App.CreateClient();
            var Json = await Client.GetStringAsync("/api/settings/ai-server");
            Assert.DoesNotContain("private-key", Json);
            using var Document = JsonDocument.Parse(Json);
            Assert.Equal("updated", Document.RootElement.GetProperty("model").GetString());
            Assert.True(Document.RootElement.GetProperty("hasApiKey").GetBoolean());
            (await Client.PutAsJsonAsync("/api/settings/ai-server", Settings with { ApiKey = null, ClearApiKey = true, EnableThinking = null })).EnsureSuccessStatusCode();
            var Configuration = App.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("", Configuration["LanguageModel:ApiKey"]);
            Assert.Null(Configuration.GetValue<bool?>("LanguageModel:EnableThinking"));
        }
    }

    private sealed class Application(string Data) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder) => Builder.ConfigureAppConfiguration((_, Configuration) =>
            Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Data, ["LanguageModel:ApiKey"] = "environment-key" }));
    }
}
