using System.Text.Json;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assister.Satellites;

public static class SatelliteEndpoints
{
    public static void MapSatellites(this WebApplication App)
    {
        App.MapPost("/api/satellites", async (SatelliteRegistration Input, AssisterDbContext Database, CancellationToken Token) =>
        {
            if (!Valid(Input)) { return Results.BadRequest(); }
            if (await Database.Satellites.AnyAsync(Row => Row.Id == Input.Id, Token)) { return Results.Conflict(); }
            var Device = new Satellite { Id = Input.Id, Name = Input.Name, AreaId = Input.AreaId,
                Endpoint = Input.Endpoint, ProviderType = Input.ProviderType, Enabled = Input.Enabled,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            Database.Satellites.Add(Device);
            await Database.SaveChangesAsync(Token);
            return Results.Created($"/api/satellites/{Uri.EscapeDataString(Device.Id)}", Device);
        });
        App.MapPatch("/api/satellites/{id}", async (string Id, SatelliteRegistration Input, AssisterDbContext Database,
            SatelliteManager Manager, CancellationToken Token) =>
        {
            if (Id != Input.Id || !Valid(Input)) { return Results.BadRequest(); }
            if (Manager.TryGet(Id, out _)) { return Results.Conflict(new { Error = "Disconnect the provider before changing device identity or connection policy." }); }
            var Device = await Database.Satellites.FindAsync([Id], Token);
            if (Device is null) { return Results.NotFound(); }
            Device.Name = Input.Name; Device.AreaId = Input.AreaId; Device.Endpoint = Input.Endpoint;
            Device.Enabled = Input.Enabled; Device.UpdatedAt = DateTimeOffset.UtcNow;
            await Database.SaveChangesAsync(Token);
            return Results.Ok(Device);
        });
        App.MapDelete("/api/satellites/{id}", async (string Id, AssisterDbContext Database, SatelliteManager Manager, CancellationToken Token) =>
        {
            if (Manager.TryGet(Id, out _)) { return Results.Conflict(new { Error = "Disconnect the provider before deleting its registration." }); }
            var Device = await Database.Satellites.FindAsync([Id], Token);
            if (Device is null) { return Results.NotFound(); }
            Database.Satellites.Remove(Device);
            await Database.SaveChangesAsync(Token);
            return Results.NoContent();
        });
        App.MapGet("/api/satellites", async (AssisterDbContext Database, SatelliteManager Manager, CancellationToken Token) =>
            Results.Ok((await Database.Satellites.AsNoTracking().ToListAsync(Token)).Select(Device => new { Device, Runtime = Manager.State(Device.Id) })));
        App.MapGet("/api/satellites/{id}", async (string Id, AssisterDbContext Database, SatelliteManager Manager, CancellationToken Token) =>
            await Database.Satellites.AsNoTracking().SingleOrDefaultAsync(Row => Row.Id == Id, Token) is { } Device
                ? Results.Ok(new { Device, Runtime = Manager.State(Id) }) : Results.NotFound());
        App.MapGet("/api/satellites/{id}/events", (string Id, SatelliteManager Manager) => Results.Ok(Manager.Events(Id)));
        App.MapPost("/api/satellites/{id}/tones/{name}", async (string Id, string Name, SatelliteManager Manager,
            Assister.Voice.ToneCatalog Tones, CancellationToken Token) =>
        {
            if (!Assister.Voice.ToneCatalog.Names.Contains(Name, StringComparer.Ordinal)) { return Results.BadRequest(new { Error = "Unknown tone." }); }
            try
            {
                return await Manager.AnnounceAsync(Id, "Tone: " + Name, new Assister.Voice.TonePreviewSpeech(Tones, Name), Token)
                    ? Results.Ok(new { Outcome = "played", Tone = Name }) : Results.Conflict(new { Error = "Satellite is unavailable, busy, or does not support announcements." });
            }
            catch (Exception Error) when (Error is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException
                or UnauthorizedAccessException or System.Net.WebSockets.WebSocketException or OperationCanceledException && !Token.IsCancellationRequested)
            { return Results.Problem("Tone playback failed.", statusCode: 502); }
        });
        App.MapGet("/api/satellites/{id}/traces", (string Id, RunStore Store) => Results.Ok(Store.Snapshot().Where(Run => Run.SatelliteId == Id)));
        App.MapPut("/api/satellites/{id}/wake-words", async (string Id, string[] Words, AssisterDbContext Database,
            SatelliteManager Manager, SatelliteConfiguration Configuration, CancellationToken Token) =>
        {
            var Device = await Database.Satellites.SingleOrDefaultAsync(Row => Row.Id == Id, Token);
            if (Device is null) { return Results.NotFound(); }
            var State = Manager.State(Id);
            if (!State.Capabilities.WakeWordConfiguration || State.VoiceOwnership != VoiceOwnership.OwnedByAssister || !Manager.TryGet(Id, out var Connection))
                return Results.Conflict(new { Error = "Wake-word configuration requires a connected device with Assister voice ownership." });
            var Error = SatelliteConfiguration.Validate(State.VoiceConfiguration, Words);
            if (Error is not null) { return Results.BadRequest(new { Error }); }
            Device.Configuration = JsonSerializer.Serialize(new DesiredVoiceConfiguration(Words));
            Device.UpdatedAt = DateTimeOffset.UtcNow;
            await Database.SaveChangesAsync(Token);
            await Configuration.ReconcileAsync(Id, Connection!, Token, true);
            Manager.Record(Id, "Wake-word configuration requested");
            return Results.Accepted(value: new { Desired = Words, Actual = State.VoiceConfiguration.ActiveWakeWords });
        });
        App.MapPost("/api/satellites/{id}/announcement", async (string Id, AnnouncementRequest Request,
            SatelliteManager Manager, ITextToSpeechProvider Tts, CancellationToken Token) =>
        {
            if (string.IsNullOrWhiteSpace(Request.Message) || Request.Message.Length > 500) { return Results.BadRequest(); }
            return await Manager.AnnounceAsync(Id, Request.Message, Tts, Token) ? Results.Ok() : Results.Conflict(new { Error = "Satellite is unavailable, busy, or does not support announcements." });
        });
        App.MapPut("/api/satellites/{id}/volume", async (string Id, VolumeRequest Request, SatelliteManager Manager, CancellationToken Token) =>
        {
            if (!double.IsFinite(Request.Volume) || Request.Volume is < 0 or > 1) { return Results.BadRequest(); }
            if (!Manager.State(Id).Capabilities.VolumeControl || !Manager.TryGet(Id, out var Connection)) { return Results.Conflict(); }
            await Connection!.SendEventAsync(new("set-volume", Request.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture)), Token);
            return Results.Accepted();
        });
        App.MapPost("/api/satellites/{id}/stop", async (string Id, SatelliteManager Manager, CancellationToken Token) =>
        {
            if (!await Manager.StopAsync(Id, Token)) { return Results.Conflict(); }
            return Results.Accepted();
        });
        App.MapPost("/api/satellites/{id}/retry-ownership", async (string Id, SatelliteManager Manager, CancellationToken Token) =>
        {
            if (!Manager.TryGet(Id, out var Connection) || Manager.State(Id).VoiceOwnership != VoiceOwnership.Conflict) { return Results.Conflict(); }
            await Connection!.SendEventAsync(new("retry-ownership"), Token);
            return Results.Accepted();
        });
        App.MapPost("/api/satellites/{id}/logs", async (string Id, LogSubscriptionRequest Request, SatelliteManager Manager, CancellationToken Token) =>
        {
            if (!Manager.TryGet(Id, out var Connection)) { return Results.Conflict(); }
            await Connection!.SendEventAsync(new("device-logs", Request.Enabled ? "start" : "stop"), Token);
            return Results.Accepted();
        });
    }
    private static bool Valid(SatelliteRegistration Input) => !string.IsNullOrWhiteSpace(Input.Id) && Input.Id.Length <= 128 &&
        Input.Id.All(Character => char.IsAsciiLetterOrDigit(Character) || Character is '-' or '_') &&
        !string.IsNullOrWhiteSpace(Input.Name) && Input.Name.Length <= 128 && (Input.AreaId?.Length ?? 0) <= 128 &&
        Input.ProviderType is "ESPHome" or "EchoMuse" && Input.Endpoint is not null && Input.Endpoint.Length <= 253 &&
        (Input.Endpoint.Length == 0 || Uri.CheckHostName(Input.Endpoint) != UriHostNameType.Unknown);
}

public sealed record AnnouncementRequest(string Message);
public sealed record VolumeRequest(double Volume);
public sealed record LogSubscriptionRequest(bool Enabled);
public sealed record SatelliteRegistration(string Id, string Name, string? AreaId, string Endpoint, string ProviderType = "ESPHome", bool Enabled = true);
