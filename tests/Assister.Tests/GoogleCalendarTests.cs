using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assister.Modules.GoogleCalendar;
using Assister.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Assister.Contracts;
using Assister.Persistence;
using Assister.Satellites;

namespace Assister.Tests;

public sealed class GoogleCalendarTests : IDisposable
{
    [Theory]
    [InlineData("accessNotConfigured", "Enable the Google Calendar API")]
    [InlineData("SERVICE_DISABLED", "Enable the Google Calendar API")]
    [InlineData("rateLimitExceeded", "quota")]
    [InlineData("insufficientPermissions", "authorization scope")]
    public async Task GoogleFailureIncludesSpecificReasonAndActionWithoutRawMessage(string Reason, string Expected)
    {
        using var Response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = new { message = "sensitive-body-should-not-appear", errors = new[] { new { reason = Reason } } } }) };
        var Message = await GoogleCalendarClient.FailureMessage(Response, HttpMethod.Get, "users/me/calendarList", default);
        Assert.Contains(Reason, Message); Assert.Contains(Expected, Message);
        Assert.DoesNotContain("sensitive-body", Message);
    }
    [Fact]
    public async Task NonJsonFailureStillProvidesSafeDiagnostic()
    {
        using var Response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private proxy response") };
        var Message = await GoogleCalendarClient.FailureMessage(Response, HttpMethod.Get, "users/me/calendarList", default);
        Assert.Contains("Google Calendar API is enabled", Message); Assert.DoesNotContain("private proxy", Message);
    }
    private readonly string DirectoryName = Path.Combine(Path.GetTempPath(), "assister-calendar-tests-" + Guid.NewGuid());
    private CalendarStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = DirectoryName }).Build());
    private static string Credential()
    {
        using var Rsa = RSA.Create(2048);
        return JsonSerializer.Serialize(new { type = "service_account", client_email = "test@example.iam.gserviceaccount.com", private_key = Rsa.ExportPkcs8PrivateKeyPem(), token_uri = "https://untrusted.invalid/token" });
    }
    [Fact]
    public async Task TokenIsRenewedWhenNearExpiry()
    {
        var Store = this.Store(); var A = Store.Add("Home", Credential());
        var Handler = new FakeGoogle { TokenLifetime = 1 }; var Client = new GoogleCalendarClient(Store, new Factory(Handler));
        await Client.Discover(A.Id, default); await Client.Discover(A.Id, default);
        Assert.Equal(2, Handler.AuthCalls);
    }
    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task SchedulerAnnouncesNotifiedOccurrencesOnlyAndDoesNotRepeatAfterRestart(int Lead)
    {
        var Store = this.Store(); var A = Store.Add("Home", Credential());
        var Calendar = new CalendarSubscription("shared", "Shared", true, "kitchen", true);
        Store.Update(A.Id, A => A with { Calendars = [Calendar] });
        var Now = DateTimeOffset.UtcNow;
        var Handler = new FakeGoogle { EventStart = Lead == 0 ? Now.AddSeconds(-30) : Now.AddMinutes(Lead), ReminderLead = Lead };
        var Client = new GoogleCalendarClient(Store, new Factory(Handler));
        var Services = new ServiceCollection();
        Services.AddDbContext<AssisterDbContext>(O => O.UseSqlite("Data Source=" + Path.Combine(DirectoryName, "test.db") + ";Pooling=False"));
        Services.AddScoped<LocalStore>(); Services.AddSingleton<ITextToSpeechProvider, Speech>();
        await using var Provider = Services.BuildServiceProvider();
        await using (var Scope = Provider.CreateAsyncScope()) await Scope.ServiceProvider.GetRequiredService<AssisterDbContext>().Database.MigrateAsync();
        var Manager = new SatelliteManager(); var Speaker = new Speaker(); Manager.Register(Speaker);
        Manager.Update("kitchen", S => S with { Capabilities = new() { AnnouncementPlayback = true } });
        CalendarScheduler Scheduler() => new(Store, Client, Provider.GetRequiredService<IServiceScopeFactory>(), Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<CalendarScheduler>.Instance);
        await Scheduler().Tick(A.Id, Calendar, Now, default);
        await Scheduler().Tick(A.Id, Calendar, Now.AddSeconds(30), default);
        Assert.Equal(1, Speaker.Deliveries);
        Assert.Contains("Dentist", Speaker.Text);
        Handler.EventStart = Lead == 0 ? Now.AddDays(1).AddSeconds(-30) : Now.AddDays(1).AddMinutes(Lead);
        await Scheduler().Tick(A.Id, Calendar, Now.AddDays(1), default);
        Assert.Equal(2, Speaker.Deliveries);
    }
    [Fact]
    public void CredentialIsEncryptedAndDecryptableAfterRestart()
    {
        var Original = Store(); var Key = Credential(); var A = Original.Add("Home", Key);
        var Disk = File.ReadAllText(Path.Combine(DirectoryName, "google-calendars.json"));
        Assert.DoesNotContain("PRIVATE KEY", Disk); Assert.DoesNotContain("private_key", Disk);
        Assert.Equal(Key, Store().Key(Store().Get(A.Id)));
        Original.Update(A.Id, A => A with { Calendars = [new("shared", "Family", true)] });
        Assert.True(Assert.Single(Store().Get(A.Id).Calendars).Enabled);
    }
    [Fact]
    public async Task AuthUsesFixedGoogleEndpointCachesTokenAndEscapesCalendarId()
    {
        var Store = this.Store(); var A = Store.Add("Home", Credential());
        var Handler = new FakeGoogle(); var Client = new GoogleCalendarClient(Store, new Factory(Handler));
        await Client.Discover(A.Id, default); await Client.Discover(A.Id, default);
        Assert.Equal(1, Handler.AuthCalls);
        Assert.Equal("shared@example.com", Assert.Single(Store.Get(A.Id).Calendars).Id);
        Assert.Equal("calendars/shared%2Fid%40example.com/events", GoogleCalendarClient.EventsPath("shared/id@example.com"));
        Assert.Throws<ArgumentException>(() => Client.Subscribed(A.Id, "shared@example.com"));
    }
    [Fact]
    public async Task ConfirmationIsScopedSingleUseAndRequiresLaterRequest()
    {
        var Store = this.Store(); var A = Store.Add("Home", Credential());
        Store.Update(A.Id, A => A with { Calendars = [new("shared", "Shared", true)] });
        var Handler = new FakeGoogle(); var Client = new GoogleCalendarClient(Store, new Factory(Handler));
        var Deletion = new CalendarDeletion(); var Request = Guid.NewGuid();
        var Key = Deletion.Prepare(A.Id, "shared", "event", "\"v1\"", "speaker:conversation", Request);
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion.Confirm(Key, "other", Client, default, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion.Confirm(Key, "speaker:conversation", Client, default, Request));
        Assert.Equal(0, Handler.Deletes);
        await Deletion.Confirm(Key, "speaker:conversation", Client, default, Guid.NewGuid());
        Assert.Equal(1, Handler.Deletes); Assert.Equal("\"v1\"", Handler.IfMatch);
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion.Confirm(Key, "speaker:conversation", Client, default, Guid.NewGuid()));
    }
    [Fact]
    public async Task ToolCannotPrepareAndConfirmDeletionInOneUserRequest()
    {
        var Tool = new GoogleCalendarTool("calendar_delete", null!, null!, new());
        var Result = await Tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { account = "a", calendar = "c", @event = "e" }), new(new("confirm delete the event"), []), default);
        Assert.Contains("Do not prepare", Result);
    }
    [Theory]
    [InlineData("{\"summary\":\"Test\",\"start\":{\"dateTime\":\"2026-11-04T10:00:00\"},\"end\":{\"dateTime\":\"2026-11-04T11:00:00Z\"}}")]
    [InlineData("{\"summary\":\"Test\",\"start\":{\"date\":\"2026-11-05\"},\"end\":{\"date\":\"2026-11-04\"}}")]
    [InlineData("{\"summary\":\"Test\",\"start\":null,\"end\":{\"date\":\"2026-11-04\"}}")]
    [InlineData("{\"attendees\":[]}")]
    public void InvalidEventsAreRejected(string Json) => Assert.Throws<ArgumentException>(() => GoogleCalendarClient.ValidateEvent(JsonNode.Parse(Json)!.AsObject(), true));
    [Fact]
    public void NotificationsRespectOverridesDefaultsAndExplicitDisable()
    {
        var Defaults = JsonNode.Parse("[{\"method\":\"popup\",\"minutes\":30}]")!.AsArray();
        Assert.Equal([30], CalendarScheduler.ReminderMinutes(JsonNode.Parse("{\"reminders\":{\"useDefault\":true}}")!.AsObject(), Defaults));
        Assert.Empty(CalendarScheduler.ReminderMinutes(JsonNode.Parse("{\"reminders\":{\"useDefault\":false,\"overrides\":[]}}")!.AsObject(), Defaults));
        Assert.Equal([5], CalendarScheduler.ReminderMinutes(JsonNode.Parse("{\"reminders\":{\"overrides\":[{\"method\":\"popup\",\"minutes\":5},{\"method\":\"email\",\"minutes\":5}]}}")!.AsObject(), Defaults));
        var AllDay = JsonNode.Parse("{\"start\":{\"date\":\"2026-11-04\"}}")!.AsObject();
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T05:00:00Z"), CalendarScheduler.Start(AllDay, "America/New_York"));
    }
    public void Dispose() { if (Directory.Exists(DirectoryName)) Directory.Delete(DirectoryName, true); }
    private sealed class Factory(FakeGoogle Handler) : IHttpClientFactory { public HttpClient CreateClient(string Name) => new(Handler, false); }
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public int TokenLifetime = 3600;
        public int ReminderLead = 10;
        public DateTimeOffset? EventStart;
        public int AuthCalls, Deletes; public string? IfMatch;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Ct)
        {
            if (Request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                AuthCalls++; Assert.Equal("/token", Request.RequestUri.AbsolutePath);
                var Body = await Request.Content!.ReadAsStringAsync(Ct); Assert.Contains("assertion=", Body);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "test-token", expires_in = TokenLifetime })) };
            }
            Assert.Equal("www.googleapis.com", Request.RequestUri.Host); Assert.Equal("test-token", Request.Headers.Authorization!.Parameter);
            if (Request.Method == HttpMethod.Delete) { Deletes++; IfMatch = Request.Headers.GetValues("If-Match").Single(); return new(HttpStatusCode.NoContent); }
            if (EventStart is { } At)
            {
                if (Request.RequestUri.AbsolutePath.Contains("calendarList")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"timeZone\":\"UTC\",\"defaultReminders\":[]}") };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { items = new object[] {
                    new { id = "recurring-instance", summary = "Dentist", start = new { dateTime = At }, reminders = new { useDefault = false, overrides = new[] { new { method = "popup", minutes = ReminderLead } } } },
                    new { id = "silent", summary = "No reminder", start = new { dateTime = At }, reminders = new { useDefault = false, overrides = Array.Empty<object>() } }
                } })) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[{\"id\":\"shared@example.com\",\"summary\":\"Shared\"}]}") };
        }
    }
    private sealed class Speech : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { await Task.CompletedTask; yield return new(new byte[2], 16000, 2, 1); }
    }
    private sealed class Speaker : ISatelliteConnection
    {
        public string SatelliteId => "kitchen"; public string Name => "Kitchen"; public string? Area => null;
        public int Deliveries; public string Text = "";
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { Text += JsonSerializer.Serialize(Event); return Task.CompletedTask; }
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) { await foreach (var Chunk in Audio.WithCancellation(Token)) { } Deliveries++; }
    }
}
