using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Assister.Modules.GoogleCalendar;

namespace Assister.IntegrationTests;

public sealed class GoogleCalendarDashboardTests
{
    [Fact]
    public async Task SubscriptionAcceptsMultipleRecipientsRejectsWrongOwnerAndPersistsLegacyMigration()
    {
        var PathName=Path.Combine(Path.GetTempPath(),"assister-calendar-recipients-"+Guid.NewGuid());
        try
        {
            using(var App=new Application(PathName))
            using(var Http=App.CreateClient())
            {
                var Store=App.Services.GetRequiredService<CalendarStore>();
                using var Rsa=RSA.Create(2048);
                var Account=Store.Add("Family",JsonSerializer.Serialize(new {type="service_account",client_email="test@example.iam.gserviceaccount.com",private_key=Rsa.ExportPkcs8PrivateKeyPem()}));
                Store.Update(Account.Id,A=>A with {Calendars=[new("shared","Family",true,"kitchen",false)]});
                var Recipients=await Http.GetStringAsync("/api/calendars/recipients");
                Assert.Contains("phone",Recipients); Assert.DoesNotContain(new string('x',32),Recipients);
                var Path="/api/calendars/"+Account.Id+"/subscriptions/shared";
                var Bad=await Http.PutAsJsonAsync(Path,new {enabled=true,announce=true,satelliteIds=new[]{"kitchen"},richClients=new[]{new{clientId="phone",owner="wrong"}}});
                Assert.Equal(HttpStatusCode.BadRequest,Bad.StatusCode);
                var NoTargets=await Http.PutAsJsonAsync(Path,new {enabled=true,announce=true,satelliteIds=Array.Empty<string>(),richClients=Array.Empty<object>()});
                Assert.Equal(HttpStatusCode.BadRequest,NoTargets.StatusCode);
                var InvalidSchedule=await Http.PutAsJsonAsync(Path,new {enabled=true,announce=true,allRichClients=true,notifications=new {before=new[]{new{amount=-1,unit="days"}},dailyTimes=new[]{"08:00"},timeZone="America/New_York"}});
                Assert.Equal(HttpStatusCode.BadRequest,InvalidSchedule.StatusCode);
                var All=await Http.PutAsJsonAsync(Path,new {enabled=true,announce=true,allSatellites=true,allRichClients=true,notifications=new{before=new[]{new{amount=10,unit="minutes"},new{amount=2,unit="hours"},new{amount=1,unit="days"}},dailyTimes=new[]{"08:00"},timeZone="America/New_York"}});
                Assert.Equal(HttpStatusCode.NoContent,All.StatusCode);
                var Dynamic=Assert.Single(Store.Get(Account.Id).Calendars); Assert.True(Dynamic.AllSatellites); Assert.True(Dynamic.AllRichClients); Assert.Empty(Dynamic.SatelliteTargets); Assert.Empty(Dynamic.RichClients!);
                Assert.Equal(new[]{10,120,1440},Dynamic.Notifications!.Before.Select(R=>R.Minutes));Assert.Equal(["08:00"],Dynamic.Notifications.DailyTimes);
                var Save=await Http.PutAsJsonAsync(Path,new {enabled=true,announce=true,satelliteIds=new[]{"kitchen","office","kitchen"},richClients=new[]{new{clientId="phone",owner="gary"}}});
                Assert.Equal(HttpStatusCode.NoContent,Save.StatusCode);
                var Saved=Assert.Single(Store.Get(Account.Id).Calendars); Assert.Equal(["kitchen","office"],Saved.SatelliteTargets); Assert.Equal("",Saved.SatelliteId); Assert.Equal(new CalendarRichRecipient("phone","gary"),Assert.Single(Saved.RichClients!));
                Assert.False(Saved.AllSatellites);Assert.False(Saved.AllRichClients);
                // Existing API callers can still send one satellite using the old field.
                var Legacy=await Http.PutAsJsonAsync(Path,new {enabled=true,announce=false,satelliteId="kitchen"}); Legacy.EnsureSuccessStatusCode();
                Assert.Equal(["kitchen"],Assert.Single(Store.Get(Account.Id).Calendars).SatelliteTargets);
            }
            var Persisted=new CalendarStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Assister:DataPath"]=PathName}).Build());
            Assert.Equal(["kitchen"],Assert.Single(Assert.Single(Persisted.Snapshot()).Calendars).SatelliteTargets);
            Assert.Equal(["08:00"],Assert.Single(Assert.Single(Persisted.Snapshot()).Calendars).Notifications!.DailyTimes);
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(PathName))Directory.Delete(PathName,true);}
    }
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
        protected override void ConfigureWebHost(IWebHostBuilder Builder) => Builder.ConfigureAppConfiguration((Context, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = DataPath, ["RichClients:Clients:phone:Token"] = new string('x',32), ["RichClients:Clients:phone:Owner"] = "gary" }));
    }
}
