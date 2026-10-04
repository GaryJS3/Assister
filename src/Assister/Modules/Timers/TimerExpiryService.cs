using Assister.Contracts;
using Assister.Persistence;
using Assister.Satellites;

namespace Assister.Modules.Timers;

public sealed class TimerExpiryService(IServiceScopeFactory Scopes, SatelliteManager Satellites, ILogger<TimerExpiryService> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        while (!StoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var Scope = Scopes.CreateAsyncScope();
                await TickAsync(Scope.ServiceProvider.GetRequiredService<LocalStore>(), Scope.ServiceProvider.GetRequiredService<ITextToSpeechProvider>(), StoppingToken);
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { break; }
            catch (Exception Error) when (Error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                Logger.LogWarning("Timer notification deferred ({FailureType}).", Error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), StoppingToken);
        }
    }

    public async Task TickAsync(LocalStore Store, ITextToSpeechProvider Tts, CancellationToken Token)
    {
        await Store.ExecuteAsync("UPDATE Timers SET Status='notification-pending' WHERE Status='active' AND DueAt <= $p0", Token, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        // Query each connected satellite separately: an offline backlog cannot occupy the entire batch.
        var Candidates = new List<string[]>();
        foreach (var Id in Satellites.ConnectedIds)
        {
            if (Satellites.IsBusy(Id) || !Satellites.State(Id).Capabilities.AnnouncementPlayback) { continue; }
            var Pending = await Store.QueryAsync("SELECT Id,Name,SatelliteId FROM Timers WHERE Status='notification-pending' AND SatelliteId=$p0 ORDER BY DueAt,Id LIMIT 1", Token, Id);
            Candidates.AddRange(Pending);
        }
        // Bound concurrent synthesis; no SQLite operations run concurrently on the scoped connection.
        foreach (var Batch in Candidates.Chunk(4))
        {
            var Deliveries = Batch.Select(DeliverAsync).ToList();
            try
            {
                while (Deliveries.Count > 0)
                {
                    var Finished = await Task.WhenAny(Deliveries);
                    Deliveries.Remove(Finished);
                    var (Id, Delivered) = await Finished;
                    if (Delivered)
                    {
                        // At-least-once delivery: a crash between playback and this update may repeat an announcement.
                        await Store.ExecuteAsync("UPDATE Timers SET Status='completed' WHERE Id=$p0 AND Status='notification-pending'", Token, Id);
                    }
                }
            }
            finally { await Task.WhenAll(Deliveries); }
        }
        async Task<(string Id, bool Delivered)> DeliverAsync(string[] Row)
        {
            try { return (Row[0], await Satellites.AnnounceAsync(Row[2], $"Your {Row[1]} has finished.", Tts, Token)); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { return (Row[0], false); }
            catch (Exception Error) when (Error is IOException or InvalidOperationException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                Logger.LogWarning("Timer notification deferred for {SatelliteId} ({FailureType}).", Row[2], Error.GetType().Name);
                return (Row[0], false);
            }
        }
        Token.ThrowIfCancellationRequested();
    }
}
