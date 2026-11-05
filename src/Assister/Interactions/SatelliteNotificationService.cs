using Assister.Contracts;
using Assister.Satellites;

namespace Assister.Interactions;

// Client notifications and satellite playback share one schedule and retry key.
public sealed class SatelliteNotificationService(NotificationStore Notifications, SatelliteManager Satellites,
    IServiceScopeFactory Scopes, ILogger<SatelliteNotificationService> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        using var Timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await Timer.WaitForNextTickAsync(StoppingToken))
            {
                try
                {
                    using var Scope = Scopes.CreateScope();
                    await TickAsync(Scope.ServiceProvider.GetRequiredService<ITextToSpeechProvider>(), StoppingToken);
                }
                catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { throw; }
                catch (Exception Error) { Logger.LogWarning("Group notification delivery deferred ({FailureType}).", Error.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { }
    }

    public async Task TickAsync(ITextToSpeechProvider Speech, CancellationToken Token)
    {
        foreach (var Item in Notifications.ResolvedSatelliteNotifications())
        {
            await Satellites.StopNotificationAsync(Item.SatelliteId, Item.Notification.Id, Token);
            Notifications.ResolveSatelliteNotification(Item.Notification.Id, Item.SatelliteId);
        }
        foreach (var Batch in Satellites.ConnectedIds.Chunk(4))
            await Task.WhenAll(Batch.Select(DeliverAsync));

        async Task DeliverAsync(string Id)
        {
            // Never replace an outstanding alarm/acknowledgement with another notification.
            if (Satellites.IsBusy(Id) || Satellites.PendingAnnouncementIds.Contains(Id)) return;
            var Item = Notifications.PendingForSatellite(Id).FirstOrDefault();
            if (Item is null) return;
            try
            {
                AnnouncementLevels.TryParse(Item.Importance, out var Importance);
                if (await Satellites.AnnounceAsync(Id, Item.Text, Speech, Token, Importance: Importance, NotificationId: Item.Id))
                    Notifications.SatelliteDelivered(Item.Id, Id);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch (Exception Error) { Logger.LogWarning("Group notification deferred for {SatelliteId} ({FailureType}).", Id, Error.GetType().Name); }
        }
    }
}
