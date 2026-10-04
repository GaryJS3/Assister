using Assister.Modules.HomeAssistant;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assister.Diagnostics;

public static class DashboardEndpoints
{
    public static void MapDashboard(this WebApplication App)
    {
        App.Services.GetRequiredService<RunStore>();
        App.MapGet("/api/diagnostics/runs", (RunStore Store) => Results.Ok(Store.Snapshot()));
        App.MapGet("/api/diagnostics/runs/{id}", (string Id, RunStore Store) =>
            Store.Snapshot().FirstOrDefault(Run => Run.Id == Id) is { } Run ? Results.Ok(Run) : Results.NotFound());
        App.MapGet("/api/diagnostics/health", async (AssisterDbContext Database, HomeAssistantClient HomeAssistant,
            HomeAssistantStateCache Cache, ComponentHealth Health, CancellationToken Token) =>
        {
            var DatabaseHealthy = false;
            try { DatabaseHealthy = await Database.Database.CanConnectAsync(Token); }
            catch (Exception Error) when (Error is not OperationCanceledException) { }
            return Results.Ok(new
            {
                CheckedAt = DateTimeOffset.UtcNow,
                Components = new[]
                {
                    new ComponentStatus("Database", DatabaseHealthy ? "Healthy" : "Unavailable", "Live SQLite connectivity check"),
                    new ComponentStatus("Home Assistant", HomeAssistant.Status, Cache.Snapshot().IsStale ? "Entity cache is stale" : "Entity cache is current"),
                    new ComponentStatus("Satellites", "NotWired", "No satellite transport registered")
                }.Concat(Health.Snapshot())
            });
        });
    }
}
