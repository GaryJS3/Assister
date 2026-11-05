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
using Assister.Interactions;

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
    [Fact]
    public void LegacySatelliteSurvivesRestartAndExplicitEmptySelectionClearsIt()
    {
        var Store = this.Store(); var A = Store.Add("Home", Credential());
        Store.Update(A.Id, A => A with { Calendars = [new("c", "Calendar", true, "kitchen", true)] });
        var Legacy = Assert.Single(this.Store().Get(A.Id).Calendars);
        Assert.Equal(["kitchen"], Legacy.SatelliteTargets);
        Assert.Empty((Legacy with { SatelliteIds = [] }).SatelliteTargets);
    }
    [Theory]
    [InlineData("overrides", 10)]
    [InlineData("defaults", 30)]
    [InlineData("none", -1)]
    [InlineData("cancelled", -1)]
    [InlineData("failure", 10)]
    public async Task EventSettingsControlMultipleSatellitesAndRichRecipients(string Mode, int ExpectedMinutes)
    {
        var Store = this.Store(); var A = Store.Add("Home", Credential()); var Now = DateTimeOffset.UtcNow;
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Assister:DataPath"] = DirectoryName, ["RichClients:Clients:phone:Token"] = new string('a',32), ["RichClients:Clients:phone:Owner"] = "alice",
            ["RichClients:Clients:tablet:Token"] = new string('b',32), ["RichClients:Clients:tablet:Owner"] = "bob"
        }).Build();
        using var Notifications = new NotificationStore(Config);
        var Recipients = new CalendarRecipients(Config);
        var Calendar = new CalendarSubscription("shared", "Shared", true, Announce:true, SatelliteIds:["kitchen","office","offline"], RichClients:[new("phone","alice"),new("tablet","bob")]);
        Store.Update(A.Id, A => A with { Calendars = [Calendar] });
        var Handler = new FakeGoogle { EventStart = Now.AddMinutes(30), EventStatus = Mode == "cancelled" ? "cancelled" : "confirmed",
            CalendarDefaults = JsonNode.Parse("[{\"method\":\"popup\",\"minutes\":30}]")!.AsArray(),
            EventReminders = JsonNode.Parse(Mode == "defaults" ? "{\"useDefault\":true}" : Mode == "none" ? "{\"useDefault\":false,\"overrides\":[]}" : "{\"useDefault\":false,\"overrides\":[{\"method\":\"popup\",\"minutes\":10},{\"method\":\"email\",\"minutes\":10}]}")!.AsObject() };
        var Client = new GoogleCalendarClient(Store, new Factory(Handler));
        var Services = new ServiceCollection(); Services.AddDbContext<AssisterDbContext>(O => O.UseSqlite("Data Source=" + Path.Combine(DirectoryName,"recipients.db") + ";Pooling=False"));
        Services.AddScoped<LocalStore>(); Services.AddSingleton<ITextToSpeechProvider,Speech>();
        await using var Provider = Services.BuildServiceProvider();
        await using (var Scope = Provider.CreateAsyncScope()) await Scope.ServiceProvider.GetRequiredService<AssisterDbContext>().Database.MigrateAsync();
        var Manager = new SatelliteManager(); var Kitchen = new Speaker { Fail = Mode == "failure" }; var Office = new Speaker("office");
        foreach (var Speaker in new[] {Kitchen,Office}) { Manager.Register(Speaker); Manager.Update(Speaker.SatelliteId, S => S with { Capabilities = new() { AnnouncementPlayback = true } }); }
        CalendarScheduler Scheduler() => new(Store, Client, Provider.GetRequiredService<IServiceScopeFactory>(), Manager, Microsoft.Extensions.Logging.Abstractions.NullLogger<CalendarScheduler>.Instance, Notifications, Recipients);
        await Scheduler().Tick(A.Id, Calendar, Now, default);
        if (Mode == "overrides") { Assert.Equal(0, Kitchen.Deliveries); Assert.Empty(Notifications.List("alice","phone")); }
        var Due = ExpectedMinutes == 10 ? Now.AddMinutes(20) : Now;
        await Scheduler().Tick(A.Id, Calendar, Due, default);
        await Scheduler().Tick(A.Id, Calendar, Due.AddSeconds(30), default);
        if (ExpectedMinutes == -1) { Assert.Equal(0,Kitchen.Deliveries); Assert.Equal(0,Office.Deliveries); Assert.Empty(Notifications.List("alice","phone")); return; }
        var KitchenDeliveries = Mode == "failure" ? 0 : 1;
        Assert.Equal(KitchenDeliveries,Kitchen.Deliveries); Assert.Equal(1,Office.Deliveries);
        var Notice = Assert.Single(Notifications.List("alice","phone"));
        Assert.Equal(Due, Notice.DueAt); Assert.Equal(Now.AddMinutes(30), Notice.ExpiresAt); Assert.Equal("phone",Notice.TargetClientId);
        Assert.Single(Notifications.List("bob","tablet")); Assert.Empty(Notifications.List("alice","laptop")); Assert.Empty(Notifications.List("bob","phone"));
        var Offline = new Speaker("offline"); Manager.Register(Offline); Manager.Update("offline", S => S with { Capabilities = new() { AnnouncementPlayback = true } });
        await Scheduler().Tick(A.Id, Calendar, Due.AddMinutes(1), default);
        Assert.Equal(1,Offline.Deliveries); Assert.Equal(KitchenDeliveries,Kitchen.Deliveries); Assert.Single(Notifications.List("alice","phone"));
        // Reassignment/revocation must not send the next occurrence to a different owner or a revoked credential.
        Config["RichClients:Clients:phone:Owner"] = "other"; Config["RichClients:Clients:tablet:Token"] = "";
        Handler.EventStart = Now.AddDays(1).AddMinutes(30);
        await Scheduler().Tick(A.Id, Calendar, Due.AddDays(1), default);
        Assert.Empty(Notifications.List("other","phone")); Assert.Single(Notifications.List("bob","tablet"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RichOnlySubscriptionQueuesEvenWithNoConnectedSatellites(bool DynamicAll)
    {
        var Store = this.Store(); var A = Store.Add("Home", Credential()); var Now = DateTimeOffset.UtcNow;
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Assister:DataPath"]=DirectoryName, ["RichClients:Clients:phone:Token"]=new string('a',32), ["RichClients:Clients:phone:Owner"]="alice" }).Build();
        using var Notices = new NotificationStore(Config);
        var Calendar = new CalendarSubscription("shared","Shared",true,Announce:true,SatelliteIds:[],RichClients:DynamicAll ? [] : [new("phone","alice")],AllSatellites:DynamicAll,AllRichClients:DynamicAll); Store.Update(A.Id,A=>A with { Calendars=[Calendar] });
        var Persisted = Assert.Single(this.Store().Get(A.Id).Calendars); Assert.Equal(DynamicAll,Persisted.AllSatellites); Assert.Equal(DynamicAll,Persisted.AllRichClients);
        var Client = new GoogleCalendarClient(Store,new Factory(new FakeGoogle {EventStart=Now.AddMinutes(10), EventReminders=JsonNode.Parse("{\"useDefault\":false,\"overrides\":[{\"method\":\"popup\",\"minutes\":10},{\"method\":\"popup\",\"minutes\":5}]}")!.AsObject()}));
        var Services = new ServiceCollection(); Services.AddDbContext<AssisterDbContext>(O=>O.UseSqlite("Data Source="+Path.Combine(DirectoryName,"rich.db")+";Pooling=False")); Services.AddScoped<LocalStore>(); Services.AddSingleton<ITextToSpeechProvider,Speech>();
        await using var Provider=Services.BuildServiceProvider(); await using(var Scope=Provider.CreateAsyncScope()) await Scope.ServiceProvider.GetRequiredService<AssisterDbContext>().Database.MigrateAsync();
        var Manager=new SatelliteManager();
        var Scheduler=new CalendarScheduler(Store,Client,Provider.GetRequiredService<IServiceScopeFactory>(),Manager,Microsoft.Extensions.Logging.Abstractions.NullLogger<CalendarScheduler>.Instance,Notices,new CalendarRecipients(Config));
        await Scheduler.Tick(A.Id,Calendar,Now,default); Assert.Single(Notices.List("alice","phone",true));
        if(DynamicAll)
        {
            // New recipients are discovered without saving the subscription again.
            Config["RichClients:Clients:tablet:Token"]=new string('b',32); Config["RichClients:Clients:tablet:Owner"]="bob";
            Config["RichClients:Clients:revoked:Token"]=""; Config["RichClients:Clients:revoked:Owner"]="bob";
            await using(var Scope=Provider.CreateAsyncScope())
            {
                var Database=Scope.ServiceProvider.GetRequiredService<AssisterDbContext>();
                Database.Satellites.Add(new Assister.Satellites.Satellite{Id="office",Name="Office",Enabled=true});
                Database.Satellites.Add(new Assister.Satellites.Satellite{Id="disabled",Name="Disabled",Enabled=false});
                await Database.SaveChangesAsync();
            }
            var Office=new Speaker("office");var Disabled=new Speaker("disabled");
            foreach(var Speaker in new[]{Office,Disabled}){Manager.Register(Speaker);Manager.Update(Speaker.SatelliteId,S=>S with{Capabilities=new(){AnnouncementPlayback=true}});}
            await Scheduler.Tick(A.Id,Calendar,Now.AddMinutes(1),default);
            await Scheduler.Tick(A.Id,Calendar,Now.AddMinutes(1).AddSeconds(30),default);
            Assert.Equal(1,Office.Deliveries);Assert.Equal(0,Disabled.Deliveries);
            Assert.Single(Notices.List("bob","tablet"));Assert.Empty(Notices.List("bob","revoked"));Assert.Single(Notices.List("alice","phone"));
        }
        await Scheduler.Tick(A.Id,Calendar,Now.AddMinutes(5),default);
        await Scheduler.Tick(A.Id,Calendar,Now.AddMinutes(5).AddSeconds(30),default);
        var Queued = Notices.List("alice","phone"); Assert.Equal(2,Queued.Length); Assert.Equal(new[] {Now,Now.AddMinutes(5)},Queued.Select(N=>N.DueAt));
    }
    private CalendarStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = DirectoryName }).Build());
    private async Task<ServiceProvider> SchedulerServices()
    {
        var Services = new ServiceCollection();
        Services.AddDbContext<AssisterDbContext>(O => O.UseSqlite("Data Source=" + Path.Combine(DirectoryName,"schedule-tests.db") + ";Pooling=False"));
        Services.AddScoped<LocalStore>(); Services.AddSingleton<ITextToSpeechProvider,Speech>();
        var Provider=Services.BuildServiceProvider();
        await using var Scope=Provider.CreateAsyncScope(); await Scope.ServiceProvider.GetRequiredService<AssisterDbContext>().Database.MigrateAsync();
        return Provider;
    }
    [Theory]
    [InlineData(10,"minutes",10)]
    [InlineData(0,"minutes",0)]
    [InlineData(2,"hours",120)]
    [InlineData(2,"days",2880)]
    public async Task AssisterAdvanceRulesNotifyEventsEvenWhenGoogleRemindersAreDisabled(int Amount,string Unit,int Minutes)
    {
        var Store=this.Store();var A=Store.Add("Home",Credential());var Now=DateTimeOffset.UtcNow;
        var Config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Assister:DataPath"]=DirectoryName,["RichClients:Clients:phone:Token"]=new string('a',32),["RichClients:Clients:phone:Owner"]="alice"}).Build();
        using var Notices=new NotificationStore(Config);
        var Rules=new CalendarNotificationSettings([new(Amount,Unit)],[],"America/New_York").Validate();
        var Calendar=new CalendarSubscription("shared","Shared",true,Announce:true,RichClients:[new("phone","alice")],Notifications:Rules);
        Store.Update(A.Id,A=>A with{Calendars=[Calendar]}); Assert.Equal(Minutes,Assert.Single(this.Store().Get(A.Id).Calendars).Notifications!.Before[0].Minutes);
        var Handler=new FakeGoogle{EventStart=Now.AddMinutes(Minutes+1),EventReminders=JsonNode.Parse("{\"useDefault\":false,\"overrides\":[]}")!.AsObject()};
        var Client=new GoogleCalendarClient(Store,new Factory(Handler));await using var Provider=await SchedulerServices();
        CalendarScheduler Scheduler()=>new(Store,Client,Provider.GetRequiredService<IServiceScopeFactory>(),new SatelliteManager(),Microsoft.Extensions.Logging.Abstractions.NullLogger<CalendarScheduler>.Instance,Notices,new CalendarRecipients(Config));
        await Scheduler().Tick(A.Id,Calendar,Now,default);Assert.Empty(Notices.List("alice","phone"));
        await Scheduler().Tick(A.Id,Calendar,Now.AddMinutes(1),default);await Scheduler().Tick(A.Id,Calendar,Now.AddMinutes(1).AddSeconds(30),default);
        var Items=Notices.List("alice","phone");Assert.Equal(2,Items.Length);Assert.All(Items,N=>Assert.Equal(Now.AddMinutes(1),N.DueAt));
        Assert.Contains(Items,N=>N.Text.Contains("No reminder"));
    }
    [Fact]
    public async Task DailyAgendaIncludesEveryPageAndEarlierAllDayEventsAndKeepsStableSnapshotForRetries()
    {
        var Store=this.Store();var A=Store.Add("Home",Credential());var Now=DateTimeOffset.UtcNow;
        var Config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Assister:DataPath"]=DirectoryName,["RichClients:Clients:phone:Token"]=new string('a',32),["RichClients:Clients:phone:Owner"]="alice"}).Build();
        using var Notices=new NotificationStore(Config);await using var Provider=await SchedulerServices();
        var Zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");var Local=TimeZoneInfo.ConvertTime(Now,Zone);var Date=DateOnly.FromDateTime(Local.DateTime);
        var Time=Local.ToString("HH:mm");var Due=CalendarNotificationSettings.LocalTime(Date,TimeOnly.Parse(Time),Zone);
        var Calendar=new CalendarSubscription("shared","Shared",true,Announce:true,AllRichClients:true,Notifications:new([], [Time], Zone.Id));Store.Update(A.Id,A=>A with{Calendars=[Calendar]});
        JsonObject Timed(string Id,string Title,string? Status=null)=>new(){["id"]=Id,["summary"]=Title,["status"]=Status??"confirmed",["start"]=new JsonObject{["dateTime"]=Due.AddHours(-1).ToString("O")}};
        var Handler=new FakeGoogle{EventStart=Now,EventPages=[new JsonArray(Timed("a","Earlier meeting"),new JsonObject{["id"]="all-day",["summary"]="All-day birthday",["start"]=new JsonObject{["date"]=Date.ToString("yyyy-MM-dd")}}),new JsonArray(Timed("recurring-1","Recurring instance"),Timed("gone","Cancelled meeting","cancelled"))]};
        var Client=new GoogleCalendarClient(Store,new Factory(Handler));
        CalendarScheduler Scheduler()=>new(Store,Client,Provider.GetRequiredService<IServiceScopeFactory>(),new SatelliteManager(),Microsoft.Extensions.Logging.Abstractions.NullLogger<CalendarScheduler>.Instance,Notices,new CalendarRecipients(Config));
        await Scheduler().Tick(A.Id,Calendar,Due.AddSeconds(-1),default);Assert.Empty(Notices.List("alice","phone"));
        await Scheduler().Tick(A.Id,Calendar,Now,default);
        var First=Assert.Single(Notices.List("alice","phone"));Assert.Equal(Due,First.DueAt);Assert.Contains("3 events",First.Text);Assert.Contains("Earlier meeting",First.Text);Assert.Contains("All day: All-day birthday",First.Text);Assert.Contains("Recurring instance",First.Text);Assert.DoesNotContain("Cancelled meeting",First.Text);
        Handler.EventPages=[new JsonArray(Timed("new","Changed after snapshot"))];
        Config["RichClients:Clients:tablet:Token"]=new string('b',32);Config["RichClients:Clients:tablet:Owner"]="bob";
        await Scheduler().Tick(A.Id,Calendar,Now.AddSeconds(30),default);
        Assert.Single(Notices.List("alice","phone"));Assert.Equal(First.Text,Assert.Single(Notices.List("bob","tablet")).Text);
        Handler.EventPages=[new JsonArray()];
        var Tomorrow=CalendarNotificationSettings.LocalTime(Date.AddDays(1),TimeOnly.Parse(Time),Zone);
        await Scheduler().Tick(A.Id,Calendar,Tomorrow.AddSeconds(30),default);
        Assert.Equal(2,Notices.List("alice","phone").Length);Assert.Contains(Notices.List("alice","phone"),N=>N.Text.Contains("No events today"));
    }
    [Fact]
    public void LongAgendasAreSplitWithoutDroppingEventsAndSchedulesHandleDst()
    {
        var Items=Enumerable.Range(0,120).Select(I=>new JsonObject{["summary"]="Event "+I+new string('x',60),["start"]=new JsonObject{["date"]="2026-11-04"}}).ToArray();
        var Parts=CalendarScheduler.AgendaParts("Family",Items,TimeZoneInfo.Utc,new(2026,11,4));
        Assert.True(Parts.Length>1);Assert.All(Parts,P=>Assert.True(P.Length<=1700));Assert.Contains("Event 119",string.Join(" ",Parts));
        var Zone=TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:00:00Z"),CalendarNotificationSettings.LocalTime(new(2026,3,8),new(2,30),Zone));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T06:30:00Z"),CalendarNotificationSettings.LocalTime(new(2026,11,1),new(1,30),Zone));
        Assert.Throws<ArgumentException>(()=>new CalendarNotificationSettings([new(366,"days")],[],"UTC").Validate());
        Assert.Throws<ArgumentException>(()=>new CalendarNotificationSettings([], ["25:00"],"UTC").Validate());
        Assert.Throws<ArgumentException>(()=>new CalendarNotificationSettings([], [],"invalid-zone").Validate());
    }
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
        Calendar = Calendar with { SatelliteId = "", SatelliteIds = ["kitchen"] };
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
        public JsonArray CalendarDefaults = [];
        public JsonObject? EventReminders;
        public string EventStatus = "confirmed";
        public JsonArray[]? EventPages;
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
                if (Request.RequestUri.AbsolutePath.Contains("calendarList")) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { timeZone="UTC",defaultReminders=CalendarDefaults }) };
                if(EventPages is not null)
                {
                    var Index=Request.RequestUri.Query.Contains("pageToken=")?int.Parse(System.Text.RegularExpressions.Regex.Match(Request.RequestUri.Query,@"pageToken=(\d+)").Groups[1].Value):0;
                    var Page=new JsonObject{["items"]=EventPages[Index].DeepClone()};if(Index+1<EventPages.Length)Page["nextPageToken"]=(Index+1).ToString();
                    return new(HttpStatusCode.OK){Content=JsonContent.Create(Page)};
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { items = new object[] {
                    new { id = "recurring-instance", summary = "Dentist", status=EventStatus, start = new { dateTime = At }, reminders = EventReminders ?? JsonSerializer.SerializeToNode(new { useDefault = false, overrides = new[] { new { method = "popup", minutes = ReminderLead } } })!.AsObject() },
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
    private sealed class Speaker(string Id = "kitchen") : ISatelliteConnection
    {
        public string SatelliteId => Id; public string Name => Id; public string? Area => null;
        public int Deliveries; public string Text = "";
        public bool Fail;
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { Text += JsonSerializer.Serialize(Event); return Task.CompletedTask; }
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) { if (Fail) throw new IOException("Simulated speaker failure"); await foreach (var Chunk in Audio.WithCancellation(Token)) { } Deliveries++; }
    }
}
