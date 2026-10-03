using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

var Builder = WebApplication.CreateBuilder(args);
Builder.Logging.ClearProviders();
Builder.Logging.AddJsonConsole();
Builder.Services.AddDbContext<AssisterDbContext>((Services, Options) =>
{
    var Configuration = Services.GetRequiredService<IConfiguration>();
    var DataPath = Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
    Directory.CreateDirectory(DataPath);
    Options.UseSqlite($"Data Source={Path.Combine(DataPath, "assister.db")}");
});
var App = Builder.Build();
await using (var Scope = App.Services.CreateAsyncScope())
{
    var Database = Scope.ServiceProvider.GetRequiredService<AssisterDbContext>();
    await Database.Database.MigrateAsync();
}
App.MapGet("/health", async (AssisterDbContext Database, CancellationToken CancellationToken) =>
    await Database.Database.CanConnectAsync(CancellationToken)
        ? Results.Ok(new { Status = "Healthy" })
        : Results.StatusCode(503));
App.MapGet("/api/status", async (AssisterDbContext Database, CancellationToken CancellationToken) =>
    Results.Ok(new
    {
        Status = "Degraded",
        Database = await Database.Database.CanConnectAsync(CancellationToken) ? "Healthy" : "Unavailable",
        HomeAssistant = "NotConfigured",
        SpeechToText = "NotConfigured",
        TextToSpeech = "NotConfigured",
        LanguageModel = "NotConfigured",
        SatelliteCount = 0,
        ActiveConversations = 0,
        ImplementationMilestone = 1
    }));
App.MapGet("/", () => Results.Content("Assister is running. See /health and /api/status.", "text/plain"));
await App.RunAsync();

public partial class Program
{
}
