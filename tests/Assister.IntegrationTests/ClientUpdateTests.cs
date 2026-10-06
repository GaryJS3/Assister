using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Assister.Client;
using Assister.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Assister.IntegrationTests;

public sealed class ClientUpdateTests
{
    [Fact]
    public async Task AuthenticatedPublishPublicCheckDownloadAndPersistence()
    {
        var Data = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        var Bytes = "test-apk-package"u8.ToArray();
        await using (var Factory = new UpdateApplication(Data))
        {
            using var Http = Factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await Publish(Http, "1.2", Bytes, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Publish(Http, "1.2", Bytes, "wrong")).StatusCode);
            using var Published = await Publish(Http, "1.2", Bytes);
            Assert.Equal(HttpStatusCode.Created, Published.StatusCode);
            var Release = (await Published.Content.ReadFromJsonAsync<ClientRelease>())!;
            Assert.Equal("1.2.0.0", Release.Version);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes)), Release.Sha256);
            Assert.Equal(HttpStatusCode.Conflict, (await Publish(Http, "1.2.0", Bytes)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Publish(Http, "1.10", Bytes)).StatusCode);
            var Client = new ClientUpdateClient(Http);
            var Check = await Client.CheckAsync("assister", "android-arm64", "1.2");
            Assert.True(Check.UpdateAvailable);
            Assert.Equal("1.10.0.0", Check.Release!.Version);
            Assert.False((await Client.CheckAsync("assister", "android-arm64", "1.10")).UpdateAvailable);
            Assert.False((await Client.CheckAsync("assister", "android-arm64", "2.0")).UpdateAvailable);
            Assert.False((await Client.CheckAsync("other", "windows-x64", "1.0")).UpdateAvailable);
            Assert.Equal(Bytes, await Http.GetByteArrayAsync(Release.DownloadUrl));
            using var Range = new HttpRequestMessage(HttpMethod.Get, Release.DownloadUrl);
            Range.Headers.Range = new RangeHeaderValue(0, 3);
            using var Partial = await Http.SendAsync(Range);
            Assert.Equal(HttpStatusCode.PartialContent, Partial.StatusCode);
            Assert.Equal(Bytes[..4], await Partial.Content.ReadAsByteArrayAsync());
            var Destination = Path.Combine(Data, "download.apk");
            var Installed = false;
            Assert.True(await Client.UpdateAsync("assister", "android-arm64", "1.0", Destination, async (Path, Token) =>
            {
                Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path, Token));
                Installed = true;
            }));
            Assert.True(Installed);
            await Assert.ThrowsAsync<InvalidDataException>(() => Client.DownloadAsync(Release with { Sha256 = new string('0', 64) }, Destination));
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(Destination));
            Assert.Empty(Directory.GetFiles(Data, "*.partial"));
            Assert.Contains("Publish a release", await Http.GetStringAsync("/releases.html"));
        }
        await using var Restarted = new UpdateApplication(Data);
        using var Public = Restarted.CreateClient();
        Assert.Equal(2, (await Public.GetFromJsonAsync<ClientRelease[]>("/api/updates"))!.Length);
    }

    [Fact]
    public async Task InvalidPackagesNeverPublish()
    {
        var Data = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        await using var Factory = new UpdateApplication(Data);
        using var Http = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Publish(Http, "one", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Publish(Http, "1.0", [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Publish(Http, "1.0", [1], Name: "../bad.apk")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Publish(Http, "1.0", [1], Name: "client.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync("/api/updates/assister/android-arm64/check?currentVersion=beta")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync("/api/updates/assister%0A/android-arm64/check?currentVersion=1.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync("/api/updates/assister/android-arm64/1.0/download")).StatusCode);
        using var Request = new HttpRequestMessage(HttpMethod.Put, "/api/updates/assister/android-arm64/1.0?name=client.apk");
        Request.Headers.Authorization = new("Bearer", "dev");
        Request.Content = new ByteArrayContent([1]);
        Request.Content.Headers.ContentLength = 513L * 1024 * 1024;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Http.SendAsync(Request)).StatusCode);
        Assert.Empty((await Http.GetFromJsonAsync<ClientRelease[]>("/api/updates"))!);
        Assert.Empty(Directory.GetDirectories(Path.Combine(Data, "client-releases")));
    }

    [Fact]
    public async Task ProductionRequiresConfiguredUploadToken()
    {
        await using var Factory = new UpdateApplication(Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString()), "Production");
        using var Http = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Publish(Http, "1.0", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/api/updates")).StatusCode);
    }

    [Fact]
    public async Task ConfiguredProductionTokenAndConcurrentExeUploads()
    {
        var Data = Path.Combine(Path.GetTempPath(), "assister-tests", Guid.NewGuid().ToString());
        await using var Factory = new UpdateApplication(Data, "Production", "test-upload-token");
        using var Http = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Publish(Http, "1.0", [1])).StatusCode);
        var Uploads = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Publish(Http, "1.0", [1, 2, 3], "test-upload-token", "client.exe")));
        Assert.Single(Uploads, Response => Response.StatusCode == HttpStatusCode.Created);
        Assert.Single(Uploads, Response => Response.StatusCode == HttpStatusCode.Conflict);
        var Release = Assert.Single((await Http.GetFromJsonAsync<ClientRelease[]>("/api/updates"))!);
        using var Download = await Http.GetAsync(Release.DownloadUrl);
        Assert.Contains("client.exe", Download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(new byte[] { 1, 2, 3 }, await Download.Content.ReadAsByteArrayAsync());
        Assert.Single(Directory.GetDirectories(Path.Combine(Data, "client-releases")));
        foreach (var Response in Uploads) Response.Dispose();
    }

    private static Task<HttpResponseMessage> Publish(HttpClient Http, string Version, byte[] Bytes, string? Token = "dev", string Name = "client.apk")
    {
        var Request = new HttpRequestMessage(HttpMethod.Put, $"/api/updates/assister/android-arm64/{Version}?name={Uri.EscapeDataString(Name)}") { Content = new ByteArrayContent(Bytes) };
        if (Token is not null) Request.Headers.Authorization = new("Bearer", Token);
        return Http.SendAsync(Request);
    }

    private sealed class UpdateApplication(string Data, string Environment = "Development", string? UploadToken = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.UseEnvironment(Environment);
            Builder.ConfigureAppConfiguration((_, Configuration) => Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = Data,
                ["ClientUpdates:UploadToken"] = UploadToken
            }));
        }
    }
}
