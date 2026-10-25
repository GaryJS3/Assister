using Assister;
using Assister.Diagnostics;
using Assister.Interactions;
using Assister.Persistence;
using Assister.Modules.HomeAssistant;
using Assister.Contracts;
using Assister.Intents;
using Assister.Voice;
using Assister.Satellites;
using Microsoft.EntityFrameworkCore;
using Assister.Updates;

var Builder = WebApplication.CreateBuilder(args);
Builder.Logging.ClearProviders();
Builder.Logging.AddJsonConsole();
Builder.Services.AddAssisterServices();
if (Builder.Configuration.GetValue("SatelliteBridge:Enabled", false))
{
    Builder.WebHost.ConfigureKestrel(Options =>
    {
        Options.ListenAnyIP(8080);
        Options.ListenAnyIP(8082, Listener => Listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });
}
var App = Builder.Build();
Assister.Llm.LanguageModelSettingsEndpoints.MapLanguageModelSettings(App, Builder.Configuration);
App.MapClientUpdates();
if (Builder.Configuration.GetValue("SatelliteBridge:Enabled", false)) { App.MapGrpcService<BridgeTransportService>(); }
App.MapGet("/api/voice/audio/{id:guid}.{extension}", (Guid Id, VoiceAudioStore Audio) => Audio.Get(Id) is { } Data
    ? Results.File(Data, Data.AsSpan().StartsWith("fLaC"u8) ? "audio/flac" : "audio/wav", enableRangeProcessing: true) : Results.NotFound());
App.MapGet("/api/voice/stream/{id:guid}.wav", async (Guid Id, VoiceAudioStore Audio, HttpContext Context) =>
{
    if (Audio.GetStream(Id) is not { } Stream) { Context.Response.StatusCode = 404; return; }
    Context.Response.ContentType = "audio/wav";
    Context.Response.Headers.CacheControl = "no-store";
    try
    {
        if (!await Stream.WriteAsync(Context.Response.Body, Context.RequestAborted)) Context.Response.StatusCode = 410;
    }
    catch (Exception Error) when (Error is IOException or OperationCanceledException or InvalidOperationException)
    { Context.Abort(); }
});
App.UseDefaultFiles();
App.UseStaticFiles();
App.UseWebSockets();
App.MapRichClients();
App.MapDashboard();
Assister.Modules.Reminders.ReminderEndpoints.MapReminders(App);
App.MapSatellites();
App.MapGet("/api/voice/tones", () => Assister.Voice.ToneCatalog.Names);
App.MapGet("/api/voice/tones/{name}.wav", async (string Name, Assister.Voice.ToneCatalog Tones, CancellationToken Token) =>
{
    if (!Assister.Voice.ToneCatalog.Names.Contains(Name, StringComparer.Ordinal)) { return Results.NotFound(); }
    try { return Results.File(await VoiceAudioStore.WaveAsync(Tones.Audio(Name), Token), "audio/wav"); }
    catch (Exception Error) when (Error is IOException or ArgumentException or System.Text.Json.JsonException)
    { return Results.Problem("Tone definition is unavailable or invalid."); }
});
App.MapIntents();
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
    await Scope.ServiceProvider.GetRequiredService<IntentStore>().InitializeAsync(CancellationToken.None);
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
    var DatabaseHealthy = await Database.Database.CanConnectAsync(CancellationToken);
    var CacheStatus = Cache.Status();
    var ComponentStatuses = Components.Snapshot();
    var HomeAssistantStatus = HomeAssistant.Status;
    return Results.Ok(new
    {
        Status = ServiceStatus.Overall(DatabaseHealthy, HomeAssistantStatus, CacheStatus.IsStale, ComponentStatuses),
        Database = DatabaseHealthy ? "Healthy" : "Unavailable",
        HomeAssistant = HomeAssistantStatus,
        HomeAssistantCache = CacheStatus,
        Components = ComponentStatuses,
        SatelliteCount = Satellites.Count,
        ActiveVoiceSessions = Satellites.ActiveSessionCount,
        ActiveConversations = DatabaseHealthy ? await Database.Conversations.CountAsync(Row => Row.UpdatedAt > Cutoff, CancellationToken) : 0,
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
App.MapPost("/api/homeassistant/cache/rebuild", async (HomeAssistantClient HomeAssistant, HomeAssistantStateCache Cache, CancellationToken Token) =>
{
    return await HomeAssistant.RequestCacheRebuildAsync(Token)
        ? Results.Ok(Cache.Status())
        : Results.Conflict(new { Error = "Home Assistant is unavailable or the cache rebuild failed." });
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
            Entity.SupportsFanSpeed, Entity.FanSpeedPercent, Entity.FanPercentageStep,
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
