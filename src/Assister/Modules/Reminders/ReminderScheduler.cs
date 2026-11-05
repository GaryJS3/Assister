using System.Globalization;
using Assister.Contracts;
using Assister.Persistence;
using Assister.Satellites;

namespace Assister.Modules.Reminders;

public sealed class ReminderScheduler(IServiceScopeFactory Scopes, SatelliteManager Satellites, ILogger<ReminderScheduler> Logger,
    Assister.Interactions.RichNotificationBridge? Rich = null) : BackgroundService
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
        if (Rich is not null)
        {
            var Rows = await Store.QueryAsync("SELECT Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt,COALESCE((SELECT OneTime FROM ReminderPolicies WHERE ReminderId=Reminders.Id),0),COALESCE((SELECT Importance FROM ReminderPolicies WHERE ReminderId=Reminders.Id),'acknowledge') FROM Reminders WHERE Status='active' AND SatelliteId LIKE 'client-conversation-%' AND NextDueAt <= $p0 ORDER BY NextDueAt,Id LIMIT 100", Token, Now.ToUnixTimeSeconds());
            foreach (var Row in Rows)
            {
                Token.ThrowIfCancellationRequested();
                try
                {
                    if (!Rich.Publish(Row[2], "reminder", "Reminder: " + Row[1] + ".", "reminder:" + Row[0] + ":" + Row[7], Row[9])) continue;
                }
                catch (InvalidOperationException Error)
                {
                    Logger.LogWarning(Error, "Rich reminder delivery deferred for {ReminderId}.", Row[0]);
                    continue;
                }
                var Schedule = new ReminderSchedule(int.Parse(Row[4], CultureInfo.InvariantCulture), int.Parse(Row[5], CultureInfo.InvariantCulture),
                    Row[6].Length == 0 ? null : (DayOfWeek)int.Parse(Row[6], CultureInfo.InvariantCulture));
                var Next = Schedule.Next(DateTimeOffset.UtcNow > Now ? DateTimeOffset.UtcNow : Now, TimeZoneInfo.FindSystemTimeZoneById(Row[3]));
                await Store.ExecuteAsync("UPDATE Reminders SET NextDueAt=$p0,Status=CASE WHEN COALESCE((SELECT OneTime FROM ReminderPolicies WHERE ReminderId=Reminders.Id),0)=1 THEN 'completed' ELSE 'active' END WHERE Id=$p1 AND Status='active' AND NextDueAt=$p2", Token,
                    Next.ToUnixTimeSeconds(), Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture));
            }
        }
        await BroadcastsAsync(Store, Speech, Now, Token);
        var Candidates = new List<string[]>();
        foreach (var Id in Satellites.ConnectedIds)
        {
            if (Satellites.IsBusy(Id) || !Satellites.State(Id).Capabilities.AnnouncementPlayback) { continue; }
            Candidates.AddRange(await Store.QueryAsync("SELECT Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt,COALESCE((SELECT OneTime FROM ReminderPolicies WHERE ReminderId=Reminders.Id),0),COALESCE((SELECT Importance FROM ReminderPolicies WHERE ReminderId=Reminders.Id),'acknowledge') FROM Reminders WHERE Status='active' AND SatelliteId=$p0 AND NextDueAt <= $p1 ORDER BY NextDueAt LIMIT 1", Token, Id, Now.ToUnixTimeSeconds()));
        }
        foreach (var Batch in Candidates.Chunk(4))
        {
            var Deliveries = Batch.Select(async Row =>
            {
                try { return (Row, Delivered: await Satellites.AnnounceAsync(Row[2], "Reminder: " + Row[1] + ".", Speech, Token, Importance: Enum.Parse<AnnouncementImportance>(Row[9].Replace("high-importance", "HighImportance"), true))); }
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
                    await Store.ExecuteAsync("UPDATE Reminders SET NextDueAt=$p0,Status=CASE WHEN COALESCE((SELECT OneTime FROM ReminderPolicies WHERE ReminderId=Reminders.Id),0)=1 THEN 'completed' ELSE 'active' END WHERE Id=$p1 AND Status='active' AND NextDueAt=$p2",
                        Token, Next.ToUnixTimeSeconds(), Row[0], long.Parse(Row[7], CultureInfo.InvariantCulture));
                }
            }
            finally { await Task.WhenAll(Deliveries); }
        }
    }

    private readonly Dictionary<(string Reminder, long Due), SatelliteManager.AnnouncementBroadcast> Broadcasts = new();

    private async Task BroadcastsAsync(LocalStore Store, ITextToSpeechProvider Speech, DateTimeOffset Now, CancellationToken Token)
    {
        var Rows = await Store.QueryAsync("SELECT Id,Message,SatelliteId,TimeZone,Hour,Minute,Weekday,NextDueAt,COALESCE((SELECT OneTime FROM ReminderPolicies WHERE ReminderId=Reminders.Id),0),COALESCE((SELECT Importance FROM ReminderPolicies WHERE ReminderId=Reminders.Id),'acknowledge') FROM Reminders WHERE Status='active' AND SatelliteId='all' AND NextDueAt <= $p0 ORDER BY NextDueAt LIMIT 100", Token, Now.ToUnixTimeSeconds());
        foreach (var Row in Rows)
        {
            var Due = long.Parse(Row[7], CultureInfo.InvariantCulture);
            var Schedule = new ReminderSchedule(int.Parse(Row[4], CultureInfo.InvariantCulture), int.Parse(Row[5], CultureInfo.InvariantCulture),
                Row[6].Length == 0 ? null : (DayOfWeek)int.Parse(Row[6], CultureInfo.InvariantCulture));
            var Next = Schedule.Next(DateTimeOffset.UtcNow > Now ? DateTimeOffset.UtcNow : Now, TimeZoneInfo.FindSystemTimeZoneById(Row[3]));
            // Snapshot and advance atomically. Offline delivery retries must not hold the recurrence hostage.
            await using (var Transaction = await Store.BeginTransactionAsync(Token))
            {
                await Store.ExecuteAsync("DELETE FROM ReminderBroadcastDeliveries WHERE ReminderId=$p0 AND DueAt<>$p1", Token, Row[0], Due);
                await Store.ExecuteAsync("""
                    INSERT INTO ReminderBroadcastDeliveries(ReminderId,DueAt,SatelliteId)
                    SELECT $p0,$p1,Id FROM Satellites WHERE Enabled=1
                    AND NOT EXISTS (SELECT 1 FROM ReminderBroadcastDeliveries WHERE ReminderId=$p0 AND DueAt=$p1)
                    """, Token, Row[0], Due);
                await Store.ExecuteAsync("UPDATE Reminders SET NextDueAt=$p0,Status=CASE WHEN COALESCE((SELECT OneTime FROM ReminderPolicies WHERE ReminderId=Reminders.Id),0)=1 THEN 'completed' ELSE 'active' END WHERE Id=$p1 AND Status='active' AND SatelliteId='all' AND NextDueAt=$p2", Token, Next.ToUnixTimeSeconds(), Row[0], Due);
                await Transaction.CommitAsync(Token);
            }
        }
        await Store.ExecuteAsync("DELETE FROM ReminderBroadcastDeliveries WHERE NOT EXISTS (SELECT 1 FROM Reminders WHERE Id=ReminderId AND Status IN ('active','completed') AND SatelliteId='all')", Token);
        var Occurrences = await Store.QueryAsync("""
            SELECT DISTINCT d.ReminderId,d.DueAt,r.Message,COALESCE(p.Importance,'acknowledge')
            FROM ReminderBroadcastDeliveries d JOIN Reminders r ON r.Id=d.ReminderId
            LEFT JOIN ReminderPolicies p ON p.ReminderId=r.Id ORDER BY d.DueAt,d.ReminderId LIMIT 100
            """, Token);
        var Keys = Occurrences.Select(Row => (Row[0], long.Parse(Row[1], CultureInfo.InvariantCulture))).ToHashSet();
        foreach (var Key in Broadcasts.Keys.Where(Key => !Keys.Contains(Key)).ToArray())
        {
            await Satellites.CancelBroadcastAsync(Broadcasts[Key], Token);
            Broadcasts.Remove(Key);
        }
        foreach (var Row in Occurrences)
        {
            var Due = long.Parse(Row[1], CultureInfo.InvariantCulture);
            var Key = (Row[0], Due);
            if (!Broadcasts.TryGetValue(Key, out var Broadcast)) Broadcasts[Key] = Broadcast = new();
            bool Acknowledged;
            lock (Broadcast.Gate) Acknowledged = Broadcast.Acknowledged;
            if (Acknowledged)
            {
                // Persist dismissal for recipients that have not connected yet, including across restarts.
                await Store.ExecuteAsync("UPDATE ReminderBroadcastDeliveries SET Delivered=1 WHERE ReminderId=$p0 AND DueAt=$p1", Token, Row[0], Due);
                continue;
            }
            var Targets = await Store.QueryAsync("SELECT SatelliteId FROM ReminderBroadcastDeliveries WHERE ReminderId=$p0 AND DueAt=$p1 AND Delivered=0 LIMIT 100", Token, Row[0], Due);
            foreach (var Target in Targets)
            {
                if (Satellites.IsBusy(Target[0])) continue;
                try
                {
                    if (await Satellites.AnnounceAsync(Target[0], "Reminder: " + Row[2] + ".", Speech, Token,
                        Importance: Enum.Parse<AnnouncementImportance>(Row[3].Replace("high-importance", "HighImportance"), true), Broadcast: Broadcast))
                        await Store.ExecuteAsync("UPDATE ReminderBroadcastDeliveries SET Delivered=1 WHERE ReminderId=$p0 AND DueAt=$p1 AND SatelliteId=$p2", Token, Row[0], Due, Target[0]);
                }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
                catch (Exception Error) { Logger.LogWarning(Error, "Broadcast reminder delivery deferred for {SatelliteId}.", Target[0]); }
            }
        }
    }
}
