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
        var Pending = await Store.QueryAsync("SELECT Id,Name,SatelliteId FROM Timers WHERE Status='notification-pending' ORDER BY DueAt LIMIT 20", Token);
        foreach (var Row in Pending)
        {
            if (await Satellites.AnnounceAsync(Row[2], $"Your {Row[1]} has finished.", Tts, Token))
            {
                // At-least-once delivery: a crash between playback and this update may repeat an announcement.
                await Store.ExecuteAsync("UPDATE Timers SET Status='completed' WHERE Id=$p0 AND Status='notification-pending'", Token, Row[0]);
            }
        }
    }
}
