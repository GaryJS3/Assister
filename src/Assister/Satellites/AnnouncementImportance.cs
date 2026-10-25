using System.Text.RegularExpressions;

namespace Assister.Satellites;

public enum AnnouncementImportance { Notify, HighImportance, Acknowledge, Alarm }

public static class AnnouncementLevels
{
    public static bool TryParse(string? Value, out AnnouncementImportance Importance)
    {
        Importance = Value?.ToLowerInvariant() switch
        {
            null or "notify" => AnnouncementImportance.Notify,
            "high-importance" => AnnouncementImportance.HighImportance,
            "acknowledge" => AnnouncementImportance.Acknowledge,
            "alarm" => AnnouncementImportance.Alarm,
            _ => (AnnouncementImportance)(-1)
        };
        return Enum.IsDefined(Importance);
    }

    public static bool IsAcknowledgement(string Text) => Regex.IsMatch(Text,
        @"\b(?:acknowledged?|stop|cancel|heard|dismiss|dismissed|got\s+it|understood)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

// Providers implement this only when they can initiate a microphone-only activation.
// The activation must have no wake word; VoicePipeline then accepts acknowledgements only.
public interface IAcknowledgementListener
{
    bool SupportsAcknowledgementListening => true;
    Task ListenForAcknowledgementAsync(TimeSpan Duration, CancellationToken Token);
    Task StopAcknowledgementListeningAsync(CancellationToken Token) => Task.CompletedTask;
}

public sealed class AnnouncementRepeatService(SatelliteManager Satellites, IServiceScopeFactory Scopes,
    ILogger<AnnouncementRepeatService> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        using var Timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var Deliveries = new Dictionary<string, Task>();
        try
        {
            while (await Timer.WaitForNextTickAsync(StoppingToken))
            {
                foreach (var Id in Deliveries.Where(Item => Item.Value.IsCompleted).Select(Item => Item.Key).ToArray())
                    Deliveries.Remove(Id);
                foreach (var Id in Satellites.PendingAnnouncementIds)
                    if (!Deliveries.ContainsKey(Id)) Deliveries[Id] = DeliverAsync(Id);
            }
        }
        catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(Deliveries.Values); }

        async Task DeliverAsync(string Id)
        {
            try
            {
                using var Scope = Scopes.CreateScope();
                await Satellites.RepeatAnnouncementAsync(Id,
                    Scope.ServiceProvider.GetRequiredService<Assister.Contracts.ITextToSpeechProvider>(), StoppingToken);
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { }
            catch (Exception Error) { Logger.LogWarning(Error, "Announcement repeat failed for {SatelliteId}.", Id); }
        }
    }
}
