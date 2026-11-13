using System.Threading.Channels;
using Assister.Contracts;

namespace Assister.Satellites;

// The requesting voice satellite cannot play an announcement until its response releases the session.
public sealed class AnnouncementQueue(SatelliteManager Satellites, IServiceScopeFactory Scopes, ILogger<AnnouncementQueue> Logger,
    TimeProvider? Clock = null) : BackgroundService
{
    private sealed record Delivery(string Id, string Message, DateTimeOffset Deadline, AnnouncementImportance Importance, SatelliteManager.AnnouncementBroadcast? Broadcast);
    private readonly TimeProvider Time = Clock ?? TimeProvider.System;
    private readonly SemaphoreSlim Slots = new(100, 100);
    private readonly Channel<Delivery> Queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });
    public bool Enqueue(string Id, string Message, AnnouncementImportance Importance = AnnouncementImportance.Notify, SatelliteManager.AnnouncementBroadcast? Broadcast = null)
    {
        // Bound all accepted work, including deferred and currently playing announcements.
        if (!Slots.Wait(0)) return false;
        if (Queue.Writer.TryWrite(new(Id, Message, Time.GetUtcNow().AddMinutes(2), Importance, Broadcast))) return true;
        Slots.Release();
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        var Pending = new List<Delivery>();
        var Active = new Dictionary<string, Task>(StringComparer.Ordinal);
        try
        {
            while (!StoppingToken.IsCancellationRequested)
            {
                foreach (var Finished in Active.Where(Item => Item.Value.IsCompleted).ToArray())
                {
                    await Finished.Value;
                    Active.Remove(Finished.Key);
                }
                while (Queue.Reader.TryRead(out var Item)) Pending.Add(Item);
                for (var Index = 0; Index < Pending.Count;)
                {
                    var Item = Pending[Index];
                    if (Time.GetUtcNow() >= Item.Deadline)
                    {
                        Pending.RemoveAt(Index);
                        Slots.Release();
                        Satellites.Record(Item.Id, "Queued announcement unavailable");
                    }
                    else if (Active.Count < 4 && !Active.ContainsKey(Item.Id) && !Satellites.IsBusy(Item.Id))
                    {
                        Pending.RemoveAt(Index);
                        Active.Add(Item.Id, DeliverAsync(Item, StoppingToken));
                    }
                    else Index++;
                }
                // Busy destinations do not occupy workers or block unrelated destinations.
                // One active delivery per destination preserves its enqueue order.
                if (Pending.Count == 0 && Active.Count == 0)
                {
                    if (!await Queue.Reader.WaitToReadAsync(StoppingToken)) break;
                }
                else await Task.Delay(100, StoppingToken);
            }
        }
        catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { }
        finally
        {
            Queue.Writer.TryComplete();
            foreach (var Item in Pending) Slots.Release();
            while (Queue.Reader.TryRead(out _)) Slots.Release();
            await Task.WhenAll(Active.Values);
        }
    }

    private async Task DeliverAsync(Delivery Item, CancellationToken Token)
    {
        try
        {
            await using var Scope = Scopes.CreateAsyncScope();
            var Played = Time.GetUtcNow() < Item.Deadline && await Satellites.AnnounceAsync(Item.Id, Item.Message,
                Scope.ServiceProvider.GetRequiredService<ITextToSpeechProvider>(), Token, RequireVolume: true, Importance: Item.Importance, Broadcast: Item.Broadcast);
            if (!Played) Satellites.Record(Item.Id, "Queued announcement unavailable");
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception Error)
        {
            Satellites.Record(Item.Id, "Queued announcement failed");
            Logger.LogWarning(Error, "Queued announcement failed for {SatelliteId}.", Item.Id);
        }
        finally { Slots.Release(); }
    }
}
