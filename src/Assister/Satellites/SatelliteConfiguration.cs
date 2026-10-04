using System.Text.Json;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assister.Satellites;

public sealed record DesiredVoiceConfiguration(string[]? ActiveWakeWords = null);

public sealed class SatelliteConfiguration(AssisterDbContext Database, SatelliteManager Manager)
{
    public static string? Validate(VoiceConfiguration Observed, string[] Desired)
    {
        if (Desired.Length != Desired.Distinct(StringComparer.Ordinal).Count()) { return "Duplicate wake words are not allowed."; }
        if (Desired.Length > Observed.MaxActiveWakeWords) { return "Too many active wake words for this device."; }
        if (Desired.Any(Id => !Observed.AvailableWakeWords.Any(Word => Word.Id == Id))) { return "A desired wake word is no longer available on the device."; }
        return null;
    }

    public async Task ReconcileAsync(string Id, ISatelliteConnection Connection, CancellationToken Token, bool Apply = false)
    {
        var Record = await Database.Satellites.AsNoTracking().SingleOrDefaultAsync(Row => Row.Id == Id, Token);
        var Desired = Record is null ? null : JsonSerializer.Deserialize<DesiredVoiceConfiguration>(Record.Configuration)?.ActiveWakeWords;
        if (Desired is null) { return; }
        var State = Manager.State(Id);
        var Error = Validate(State.VoiceConfiguration, Desired);
        if (Error is null && !State.VoiceConfiguration.ActiveWakeWords.SequenceEqual(Desired))
        {
            Error = "Desired wake words differ from device configuration; update pending.";
            if (Apply && State.VoiceOwnership == VoiceOwnership.OwnedByAssister)
                await Connection.SendEventAsync(new("set-wake-words", JsonSerializer.Serialize(Desired)), Token);
        }
        Manager.Update(Id, State => State with { ConfigurationDrift = Error });
    }
}
