using System.Threading.Channels;
using Assister.Contracts;

namespace Assister.Satellites;

// The requesting voice satellite cannot play an announcement until its response releases the session.
public sealed class AnnouncementQueue(SatelliteManager Satellites, IServiceScopeFactory Scopes, ILogger<AnnouncementQueue> Logger) : BackgroundService
{
    private sealed record Delivery(string Id, string Message, DateTimeOffset Deadline, AnnouncementImportance Importance);
    private readonly Channel<Delivery> Queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });
    public bool Enqueue(string Id, string Message, AnnouncementImportance Importance = AnnouncementImportance.Notify) => Queue.Writer.TryWrite(new(Id, Message, DateTimeOffset.UtcNow.AddMinutes(2), Importance));

    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        await foreach (var Item in Queue.Reader.ReadAllAsync(StoppingToken))
        {
            try
            {
                while (Satellites.IsBusy(Item.Id) && DateTimeOffset.UtcNow < Item.Deadline)
                    await Task.Delay(200, StoppingToken);
                using var Scope = Scopes.CreateScope();
                var Played = DateTimeOffset.UtcNow < Item.Deadline && await Satellites.AnnounceAsync(Item.Id, Item.Message,
                    Scope.ServiceProvider.GetRequiredService<ITextToSpeechProvider>(), StoppingToken, RequireVolume: true, Importance: Item.Importance);
                if (!Played) Satellites.Record(Item.Id, "Queued announcement unavailable");
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch (Exception Error)
            {
                Satellites.Record(Item.Id, "Queued announcement failed");
                Logger.LogWarning(Error, "Queued announcement failed for {SatelliteId}.", Item.Id);
            }
        }
    }
}
