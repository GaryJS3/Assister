using System.Globalization;
using System.Text.RegularExpressions;
using Assister.Contracts;
using Assister.Persistence;

namespace Assister.Modules.Timers;

public sealed partial class TimerIntentHandler(LocalStore Store)
{
    [GeneratedRegex(@"^(?:set|start) (?:a |an )?(?:(?<name>[a-z ]{1,40}) )?timer for (?<amount>\d{1,5}) (?<unit>seconds?|minutes?|hours?)[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex StartPattern();
    [GeneratedRegex(@"^(?:cancel|stop) (?:the |my |a )?(?:(?<name>[a-z ]{1,40}) )?timer[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex CancelPattern();

    public async Task<string?> TryHandleAsync(UserRequest Request, CancellationToken Token)
    {
        var Match = StartPattern().Match(Request.Message.Trim());
        if (Match.Success)
        {
            var Amount = int.Parse(Match.Groups["amount"].Value, CultureInfo.InvariantCulture);
            var Seconds = Amount * (Match.Groups["unit"].Value.ToLowerInvariant()[0] switch { 'h' => 3600L, 'm' => 60L, _ => 1L });
            if (Seconds is < 1 or > 86400) { return "Timers must be between one second and 24 hours."; }
            var Name = Match.Groups["name"].Success ? Match.Groups["name"].Value.Trim().ToLowerInvariant() : "timer";
            var Existing = await Store.QueryAsync("SELECT Id FROM Timers WHERE SatelliteId=$p0 AND Status IN ('active','notification-pending') LIMIT 20", Token, Request.SatelliteId);
            if (Existing.Count >= 20) { return "This satellite already has 20 timers. Please cancel one first."; }
            var Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await Store.ExecuteAsync("INSERT INTO Timers VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,'active')", Token,
                Guid.NewGuid().ToString(), Name, Request.SatelliteId, Request.Area, Request.ConversationId?.ToString(), Now, Now + Seconds);
            return $"Started your {Name} for {Amount} {Match.Groups["unit"].Value.ToLowerInvariant()}.";
        }
        Match = CancelPattern().Match(Request.Message.Trim());
        if (!Match.Success) { return null; }
        var TimerName = Match.Groups["name"].Success ? Match.Groups["name"].Value.Trim().ToLowerInvariant() : null;
        var Candidates = await Store.QueryAsync("SELECT Id,Name FROM Timers WHERE SatelliteId=$p0 AND Status IN ('active','notification-pending') AND ($p1 IS NULL OR Name=$p1) LIMIT 21", Token, Request.SatelliteId, TimerName);
        if (Candidates.Count == 0) { return "There is no matching active timer."; }
        if (Candidates.Count > 1) { return "Which timer should I cancel? Please use its name."; }
        await Store.ExecuteAsync("UPDATE Timers SET Status='cancelled' WHERE Id=$p0", Token, Candidates[0][0]);
        return $"Cancelled your {Candidates[0][1]}.";
    }
}
