using Assister.Persistence;

namespace Assister.Interactions;

public static class DeviceGroupEndpoints
{
    public static string[] Owners(IConfiguration Configuration, ClientSignals Signals) => Configuration.GetSection("RichClients:Clients")
        .GetChildren().Where(Client => !string.IsNullOrWhiteSpace(Client["Token"]))
        .Select(Client => Client["Owner"] ?? Client.Key).Concat(Signals.Devices().Select(Client => Client.Owner))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static void MapDeviceGroups(this WebApplication App)
    {
        App.MapGet("/api/devices/groups", (NotificationStore Store, ClientSignals Signals, IConfiguration Configuration) =>
            new { Owners = Owners(Configuration, Signals), Satellites = Store.SatelliteGroups() });
        App.MapPut("/api/devices/satellites/{id}/groups", async (string Id, SatelliteGroupsInput Request,
            NotificationStore Store, AssisterDbContext Database, ClientSignals Signals, IConfiguration Configuration, CancellationToken Token) =>
        {
            if (await Database.Satellites.FindAsync([Id], Token) is null) return Results.NotFound();
            if (!Owners(Configuration, Signals).Contains(Request.Owner, StringComparer.Ordinal))
                return Results.BadRequest(new { Error = "Choose a configured client owner." });
            try { Store.SetSatelliteGroups(Id, Request.Owner, Request.Groups); return Results.NoContent(); }
            catch (ArgumentException Error) { return Results.BadRequest(new { Error = Error.Message }); }
        });
    }
}
