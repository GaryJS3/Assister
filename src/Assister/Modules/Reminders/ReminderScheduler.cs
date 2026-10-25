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
        await BroadcastsAsync(Store, Speech, Now, Token);
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
                try { return (Row, Delivered: await Satellites.AnnounceAsync(Row[2], "Reminder: " + Row[1] + ".", Speech, Token, Importance: AnnouncementImportance.Acknowledge)); }
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

    private async Task BroadcastsAsync(LocalStore Store, ITextToSpeechProvider Speech, DateTimeOffset Now, CancellationToken Token)
    {
        var Rows = await Store.QueryAsync("SELECT Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt FROM Reminders WHERE Status='active' AND SatelliteId='all' AND NextDueAt <= $p0 ORDER BY NextDueAt LIMIT 100", Token, Now.ToUnixTimeSeconds());
        foreach (var Row in Rows)
        {
            // Snapshot recipients once per occurrence so reconnects and retries cannot repeat successful delivery.
            await Store.ExecuteAsync("""
                INSERT INTO ReminderBroadcastDeliveries(ReminderId,DueAt,SatelliteId)
                SELECT $p0,$p1,Id FROM Satellites WHERE Enabled=1
                AND NOT EXISTS (SELECT 1 FROM ReminderBroadcastDeliveries WHERE ReminderId=$p0 AND DueAt=$p1)
                """, Token, Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture));
            var Targets = await Store.QueryAsync("SELECT SatelliteId,Delivered FROM ReminderBroadcastDeliveries WHERE ReminderId=$p0 AND DueAt=$p1 LIMIT 100", Token, Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture));
            if (Targets.Count == 0) continue;
            foreach (var Target in Targets.Where(Target => Target[1] == "0"))
            {
                if (Satellites.IsBusy(Target[0])) continue;
                try
                {
                    if (await Satellites.AnnounceAsync(Target[0], "Reminder: " + Row[1] + ".", Speech, Token, Importance: AnnouncementImportance.Acknowledge))
                        await Store.ExecuteAsync("UPDATE ReminderBroadcastDeliveries SET Delivered=1 WHERE ReminderId=$p0 AND DueAt=$p1 AND SatelliteId=$p2", Token, Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture), Target[0]);
                }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (Exception Error) { Logger.LogWarning(Error, "Broadcast reminder delivery deferred for {SatelliteId}.", Target[0]); }
            }
            var Pending = await Store.QueryAsync("SELECT SatelliteId FROM ReminderBroadcastDeliveries WHERE ReminderId=$p0 AND DueAt=$p1 AND Delivered=0 LIMIT 1", Token, Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture));
            if (Pending.Count != 0) continue;
            var Schedule = new ReminderSchedule(int.Parse(Row[4], CultureInfo.InvariantCulture), int.Parse(Row[5], CultureInfo.InvariantCulture),
                Row[6].Length == 0 ? null : (DayOfWeek)int.Parse(Row[6], CultureInfo.InvariantCulture));
            var Next = Schedule.Next(DateTimeOffset.UtcNow > Now ? DateTimeOffset.UtcNow : Now, TimeZoneInfo.FindSystemTimeZoneById(Row[3]));
            await Store.ExecuteAsync("UPDATE Reminders SET NextDueAt=$p0 WHERE Id=$p1 AND Status='active' AND SatelliteId='all' AND NextDueAt=$p2", Token, Next.ToUnixTimeSeconds(), Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture));
        }
        await Store.ExecuteAsync("DELETE FROM ReminderBroadcastDeliveries WHERE NOT EXISTS (SELECT 1 FROM Reminders WHERE Id=ReminderId AND Status='active' AND SatelliteId='all' AND NextDueAt=DueAt)", Token);
    }
}
