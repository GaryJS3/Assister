using Assister.Diagnostics;
using Assister.Persistence;
using Assister.Modules.HomeAssistant;
using Assister.Contracts;
using Assister.Intents;
using Assister.Voice;
using Assister.Llm;
using Assister.Tools;
using Assister.Conversations;
using Assister.Modules.Timers;
using Assister.Speech.Wyoming;
using Assister.Satellites;
using Microsoft.EntityFrameworkCore;

var Builder = WebApplication.CreateBuilder(args);
if (Builder.Configuration.GetValue("SatelliteBridge:Enabled", false))
{
    Builder.WebHost.ConfigureKestrel(Options =>
    {
        Options.ListenAnyIP(8080);
        Options.ListenAnyIP(8082, Listener => Listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });
}
Builder.Services.AddGrpc(Options => Options.MaxReceiveMessageSize = 128 * 1024);
Builder.Services.AddSingleton<VoiceAudioStore>();
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
Builder.Services.AddTransient<RequestCoordinator>();
Builder.Services.AddSingleton<ConversationLocks>();
Builder.Services.AddScoped<IRequestCoordinator, ConversationCoordinator>();
Builder.Services.AddTransient<ToolLoop>();
Builder.Services.AddTransient<ToolBroker>();
Builder.Services.AddTransient<ToolRegistry>();
Builder.Services.AddScoped<LocalStore>();
Builder.Services.AddScoped<TimerIntentHandler>();
Builder.Services.AddSingleton<SatelliteManager>();
Builder.Services.AddScoped<SatelliteConfiguration>();
Builder.Services.AddScoped<VoicePipeline>();
Builder.Services.AddHttpClient("echomuse").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
Builder.Services.AddHostedService<EchoMuseProvider>();
Builder.Services.AddTransient<ISpeechToTextProvider>(Services =>
{
    var Config = Services.GetRequiredService<IConfiguration>();
    return new WyomingSpeechToTextProvider(new(Config["SpeechToText:Host"] ?? "", Config.GetValue("SpeechToText:Port", 10300)));
});
Builder.Services.AddTransient<ITextToSpeechProvider>(Services =>
{
    var Config = Services.GetRequiredService<IConfiguration>();
    return new WyomingTextToSpeechProvider(new(Config["TextToSpeech:Host"] ?? "", Config.GetValue("TextToSpeech:Port", 10200)));
});
Builder.Services.AddHostedService<TimerExpiryService>();
foreach (var Name in new[] { "memory_store", "memory_search", "memory_delete" })
{
    Builder.Services.AddTransient<IAssisterTool>(Services => new MemoryTool(Name, Services.GetRequiredService<LocalStore>()));
}
Builder.Services.AddHttpClient("homeassistant-tools").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
Builder.Services.AddTransient<IAssisterTool>(Services => new WeatherTool(Services.GetRequiredService<HomeAssistantStateCache>(),
    Services.GetRequiredService<IHttpClientFactory>().CreateClient("homeassistant-tools"), Services.GetRequiredService<IConfiguration>()));
foreach (var Name in new[] { "ha_search", "ha_get_state", "ha_control", "ha_get_history" })
{
    Builder.Services.AddTransient<IAssisterTool>(Services => new HomeAssistantTool(Name,
        Services.GetRequiredService<HomeAssistantStateCache>(), Services.GetRequiredService<IHomeAssistantClient>(),
        Services.GetRequiredService<IHttpClientFactory>().CreateClient("homeassistant-tools"), Services.GetRequiredService<IConfiguration>()));
}
Builder.Services.AddHttpClient<ILanguageModel, OpenAiCompatibleLanguageModel>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
Builder.Services.AddSingleton<RunStore>();
Builder.Services.AddHttpClient("diagnostics").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
Builder.Services.AddSingleton<ComponentHealth>();
Builder.Services.AddHostedService(Services => Services.GetRequiredService<ComponentHealth>());
var App = Builder.Build();
if (Builder.Configuration.GetValue("SatelliteBridge:Enabled", false)) { App.MapGrpcService<BridgeTransportService>(); }
App.MapGet("/api/voice/audio/{id:guid}.{extension}", (Guid Id, VoiceAudioStore Audio) => Audio.Get(Id) is { } Data
    ? Results.File(Data, Data.AsSpan().StartsWith("fLaC"u8) ? "audio/flac" : "audio/wav", enableRangeProcessing: true) : Results.NotFound());
App.UseDefaultFiles();
App.UseStaticFiles();
App.MapDashboard();
App.MapSatellites();
await using (var Scope = App.Services.CreateAsyncScope())
{
    var Database = Scope.ServiceProvider.GetRequiredService<AssisterDbContext>();
    var Connection = (Microsoft.Data.Sqlite.SqliteConnection)Database.Database.GetDbConnection();
    if (File.Exists(Connection.DataSource) && (await Database.Database.GetPendingMigrationsAsync()).Any())
    {
        await Database.Database.OpenConnectionAsync();
        await using var Backup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Connection.DataSource}.before-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.db");
        await Backup.OpenAsync();
        Connection.BackupDatabase(Backup);
        await Database.Database.CloseConnectionAsync();
    }
    await Database.Database.MigrateAsync();
    var SatelliteId = Builder.Configuration["EspHome:SatelliteId"];
    if (!string.IsNullOrWhiteSpace(SatelliteId) && !await Database.Satellites.AnyAsync(Row => Row.Id == SatelliteId))
    {
        Database.Satellites.Add(new Satellite { Id = SatelliteId, Name = Builder.Configuration["EspHome:Name"] ?? SatelliteId,
            AreaId = Builder.Configuration["EspHome:Area"], Endpoint = Builder.Configuration["EspHome:Host"] ?? "",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Database.SaveChangesAsync();
    }
}
App.MapGet("/health", async (AssisterDbContext Database, CancellationToken CancellationToken) =>
    await Database.Database.CanConnectAsync(CancellationToken)
        ? Results.Ok(new
        {
            Status = "Healthy"
        })
        : Results.StatusCode(503));
App.MapGet("/api/status", async (AssisterDbContext Database, HomeAssistantClient HomeAssistant, HomeAssistantStateCache Cache,
    ComponentHealth Components, SatelliteManager Satellites, CancellationToken CancellationToken) =>
{
    var Cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300;
    return Results.Ok(new
    {
        Status = "Degraded",
        Database = await Database.Database.CanConnectAsync(CancellationToken) ? "Healthy" : "Unavailable",
        HomeAssistant = HomeAssistant.Status,
        HomeAssistantCache = Cache.Status(),
        Components = Components.Snapshot(),
        SatelliteCount = Satellites.Count,
        ActiveVoiceSessions = Satellites.ActiveSessionCount,
        ActiveConversations = await Database.Conversations.CountAsync(Row => Row.UpdatedAt > Cutoff, CancellationToken),
        ImplementationMilestone = 6
    });
});
App.MapPost("/api/test/message", async (UserRequest Request, IRequestCoordinator Coordinator, RunStore Store, CancellationToken CancellationToken) =>
{
    using var Run = RunTracing.BeginRun(Store, "debug", Request.SatelliteId, Request.Area, Conversation: Request.ConversationId, Text: Request.Message);
    using (var Input = RunTracing.Start("Input", "Text input", "Process debug text through the same conversation and request coordinators as voice."))
    {
        Input.Metadata(new { Request.SatelliteId, Request.Area, Request.ConversationId });
        Input.Output(new { text = Request.Message });
        Input.Complete();
    }
    var Result = await Coordinator.ProcessAsync(Request, CancellationToken);
    RunTracing.Response(Result.Response, Result.SpokenResponse ?? VoiceFormatter.Format(Result.Response), Result.Outcome, Result.HandledBy, Result.ConversationId);
    Run.Complete(Result.Outcome);
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

await App.RunAsync();

public partial class Program
{
}
