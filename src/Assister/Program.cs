using Assister.Persistence;
using Assister.Modules.HomeAssistant;
using Assister.Contracts;
using Assister.Intents;
using Assister.Voice;
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
Builder.Services.AddSingleton<HomeAssistantStateCache>();
Builder.Services.AddSingleton<Func<IHomeAssistantConnection>>(_ => () => new HomeAssistantConnection());
Builder.Services.AddSingleton<HomeAssistantClient>();
Builder.Services.AddHostedService(Services => Services.GetRequiredService<HomeAssistantClient>());
Builder.Services.AddHttpClient<IHomeAssistantClient, HomeAssistantActionClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
Builder.Services.AddSingleton<IntentClassifier>();
Builder.Services.AddSingleton<IEntityResolver, HomeAssistantEntityResolver>();
Builder.Services.AddTransient<DirectIntentHandler>();
Builder.Services.AddTransient<IRequestCoordinator, RequestCoordinator>();
var App = Builder.Build();
await using (var Scope = App.Services.CreateAsyncScope())
{
    var Database = Scope.ServiceProvider.GetRequiredService<AssisterDbContext>();
    await Database.Database.MigrateAsync();
}
App.MapGet("/health", async (AssisterDbContext Database, CancellationToken CancellationToken) =>
    await Database.Database.CanConnectAsync(CancellationToken)
        ? Results.Ok(new
        {
            Status = "Healthy"
        })
        : Results.StatusCode(503));
App.MapGet("/api/status", async (AssisterDbContext Database, HomeAssistantClient HomeAssistant, HomeAssistantStateCache Cache, CancellationToken CancellationToken) =>
    Results.Ok(new
    {
        Status = "Degraded",
        Database = await Database.Database.CanConnectAsync(CancellationToken) ? "Healthy" : "Unavailable",
        HomeAssistant = HomeAssistant.Status,
        HomeAssistantCache = Cache.Status(),
        SpeechToText = "NotConfigured",
        TextToSpeech = "NotConfigured",
        LanguageModel = "NotConfigured",
        SatelliteCount = 0,
        ActiveConversations = 0,
        ImplementationMilestone = 4
    }));
App.MapPost("/api/test/message", async (UserRequest Request, IRequestCoordinator Coordinator, CancellationToken CancellationToken) =>
{
    var Result = await Coordinator.ProcessAsync(Request, CancellationToken);
    return Result.Outcome == "invalid-request" ? Results.BadRequest(Result) : Results.Ok(Result);
});
App.MapGet("/api/homeassistant/entities", (string? Query, int? Limit, HomeAssistantStateCache Cache) =>
{
    if (string.IsNullOrWhiteSpace(Query) || Query.Length > 128 || Limit is < 1 or > 50)
    {
        return Results.BadRequest(new
        {
            Error = "Provide a query of 1 to 128 characters and a limit of 1 to 50."
        });
    }
    var Snapshot = Cache.Snapshot();
    var Entities = Snapshot.Entities.Where(Entity => new[] { Entity.EntityId, Entity.Name, Entity.AreaName ?? "" }
            .Concat(Entity.Aliases).Any(Value => Value.Contains(Query, StringComparison.OrdinalIgnoreCase)))
        .Take(Limit ?? 10).Select(Entity => new
        {
            Entity.EntityId,
            Entity.Name,
            Entity.Domain,
            Entity.AreaId,
            Entity.AreaName,
            State = Entity.State.GetProperty("state").GetString(),
            Entity.SupportsBrightness,
            Entity.IsTemperature
        }).ToArray();
    return Results.Ok(new
    {
        Snapshot.IsStale,
        Entities
    });
});
App.MapGet("/", () => Results.Content("Assister is running. See /health and /api/status.", "text/plain"));
await App.RunAsync();

public partial class Program
{
}
