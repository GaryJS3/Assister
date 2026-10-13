using System.Globalization;
using Assister.Contracts;
using Assister.Persistence;
using Assister.Satellites;

namespace Assister.Modules.Reminders;

public sealed class ReminderScheduler(IServiceScopeFactory Scopes, SatelliteManager Satellites, ILogger<ReminderScheduler> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken Token)
    {
        while (!Token.IsCancellationRequested)
        {
            try
            {
                await using var Scope = Scopes.CreateAsyncScope();
                await TickAsync(Scope.ServiceProvider.GetRequiredService<LocalStore>(), Scope.ServiceProvider.GetRequiredService<ITextToSpeechProvider>(), DateTimeOffset.UtcNow, Token);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { break; }
            catch (Exception Error) { Logger.LogWarning(Error, "Reminder scheduler tick failed; pending reminders will be retried."); }
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
        }
    }

    public async Task TickAsync(LocalStore Store, ITextToSpeechProvider Speech, DateTimeOffset Now, CancellationToken Token)
    {
        var Candidates = new List<string[]>();
        foreach (var Id in Satellites.ConnectedIds)
        {
            if (Satellites.IsBusy(Id) || !Satellites.State(Id).Capabilities.AnnouncementPlayback) { continue; }
            Candidates.AddRange(await Store.QueryAsync("SELECT Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt FROM Reminders WHERE Status='active' AND SatelliteId=$p0 AND NextDueAt <= $p1 ORDER BY NextDueAt LIMIT 1", Token, Id, Now.ToUnixTimeSeconds()));
        }
        foreach (var Batch in Candidates.Chunk(4))
        {
            var Deliveries = Batch.Select(async Row =>
            {
                try { return (Row, Delivered: await Satellites.AnnounceAsync(Row[2], "Reminder: " + Row[1] + ".", Speech, Token)); }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (Exception Error)
                {
                    Logger.LogWarning(Error, "Reminder delivery deferred for {SatelliteId}.", Row[2]);
                    return (Row, Delivered: false);
                }
            }).ToList();
            try
            {
                while (Deliveries.Count > 0)
                {
                    var Finished = await Task.WhenAny(Deliveries);
                    Deliveries.Remove(Finished);
                    var (Row, Delivered) = await Finished;
                    if (!Delivered) { continue; }
                    var Schedule = new ReminderSchedule(int.Parse(Row[4], CultureInfo.InvariantCulture), int.Parse(Row[5], CultureInfo.InvariantCulture),
                        Row[6].Length == 0 ? null : (DayOfWeek)int.Parse(Row[6], CultureInfo.InvariantCulture));
                    var Next = Schedule.Next(DateTimeOffset.UtcNow > Now ? DateTimeOffset.UtcNow : Now, TimeZoneInfo.FindSystemTimeZoneById(Row[3]));
                    await Store.ExecuteAsync("UPDATE Reminders SET NextDueAt=$p0 WHERE Id=$p1 AND Status='active' AND NextDueAt=$p2",
                        Token, Next.ToUnixTimeSeconds(), Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture));
                }
            }
            finally { await Task.WhenAll(Deliveries); }
        }
    }
}
