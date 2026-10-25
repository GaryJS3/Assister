using System.Text.RegularExpressions;
using Assister.Contracts;
using Assister.Tools;

namespace Assister.Satellites;

public sealed record PendingAnnouncement(string Message, string[] Targets, DateTimeOffset CreatedAt,
    AnnouncementImportance Importance = AnnouncementImportance.Notify);
public sealed class AnnouncementHandler(SatelliteManager Satellites, ITextToSpeechProvider Tts, AnnouncementQueue? Queue = null,
    Assister.Interactions.RichNotificationBridge? Rich = null)
{
    public static bool Recognizes(string Text) => Regex.IsMatch(Text.Trim(), @"^(?:please )?(?:announce|broadcast)\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    public async Task<string?> TryHandleAsync(UserRequest Request, DeviceConversationContext? Context, CancellationToken Token)
    {
        if (Context?.Announcement is { } Pending)
        {
            Context.Announcement = null;
            if (Pending.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                var Selection = Request.Message.Trim().TrimEnd('.', '!', '?');
                if (Selection.Equals("cancel", StringComparison.OrdinalIgnoreCase)) return "Announcement cancelled.";
                if (int.TryParse(Selection, out var Index) && Index >= 1 && Index <= Pending.Targets.Length + 1)
                    Selection = Index == 1 ? "all" : Pending.Targets[Index - 2];
                if (Selection.Equals("all satellites", StringComparison.OrdinalIgnoreCase) || Selection.Equals("all clients", StringComparison.OrdinalIgnoreCase)) Selection = "all";
                if (Selection.Equals("all", StringComparison.OrdinalIgnoreCase) || Pending.Targets.Any(Id => Matches(Id, Selection, Request.SatelliteId)))
                    return await ExecuteAsync(Pending.Message, Selection, Context, Token, Request.SatelliteId, Pending.Importance);
            }
        }
        if (!Recognizes(Request.Message)) return null;
        var Text = Regex.Replace(Request.Message.Trim(), @"^(?:please )?(?:announce|broadcast)\s*", "", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        var Importance = AnnouncementImportance.Notify;
        var Level = Regex.Match(Text, @"^(notify|high-importance|acknowledge|alarm)\s+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (Level.Success)
        {
            AnnouncementLevels.TryParse(Level.Groups[1].Value, out Importance);
            Text = Text[Level.Length..];
        }
        var Match = Regex.Match(Text, @"^(?:to|on) (?<target>.+?)(?::\s*| (?:that|saying) )(?<message>.+)$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (Match.Success) return await ExecuteAsync(Match.Groups["message"].Value, Match.Groups["target"].Value, Context, Token, Request.SatelliteId, Importance);
        foreach (var Target in Available(Request.SatelliteId).Select(Id => Name(Id, Request.SatelliteId)).Prepend("all").Prepend("all satellites").Prepend("all clients").OrderByDescending(Target => Target.Length))
        {
            foreach (var Prefix in new[] { "to " + Target + " ", "on " + Target + " " })
                if (Text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    return await ExecuteAsync(Text[Prefix.Length..].TrimStart(':', ' '), Target, Context, Token, Request.SatelliteId, Importance);
            foreach (var Suffix in new[] { " to " + Target, " on " + Target })
                if (Text.TrimEnd('.', '!', '?').EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                    return await ExecuteAsync(Text.TrimEnd('.', '!', '?')[..^Suffix.Length], Target, Context, Token, Request.SatelliteId, Importance);
        }
        return await ExecuteAsync(Text, null, Context, Token, Request.SatelliteId, Importance);
    }

    private string Name(string Id, string? Source = null) => Rich?.Owner(Source) is not null
        ? Rich.Clients(Source!).FirstOrDefault(Client => Client.Id == Id).Name ?? Id
        : Satellites.TryGet(Id, out var Connection) ? Connection!.Name : Id;
    private bool Matches(string Id, string Target, string? Source = null) => Id.Equals(Target, StringComparison.OrdinalIgnoreCase) || Name(Id, Source).Equals(Target, StringComparison.OrdinalIgnoreCase);
    private string[] Available(string? Source = null) => Rich?.Owner(Source) is not null ? Rich.Clients(Source!).Select(Client => Client.Id).ToArray()
        : Satellites.ConnectedIds.Where(Id => Satellites.State(Id).Capabilities.AnnouncementPlayback)
        .OrderBy(Id => Name(Id), StringComparer.OrdinalIgnoreCase).ThenBy(Id => Id, StringComparer.Ordinal).ToArray();

    public async Task<string> ExecuteAsync(string Message, string? Target, DeviceConversationContext? Context, CancellationToken Token, string? SourceId = null,
        AnnouncementImportance Importance = AnnouncementImportance.Notify)
    {
        if (string.IsNullOrWhiteSpace(Message)) return "What would you like to announce?";
        if (Message.Length > 500) return "Announcements must be at most 500 characters.";
        if (Rich?.Owner(SourceId) is not null)
        {
            Token.ThrowIfCancellationRequested();
            var Clients = Available(SourceId);
            var Broadcast = Target is not null && (Target.Equals("all", StringComparison.OrdinalIgnoreCase)
                || Target.Equals("all clients", StringComparison.OrdinalIgnoreCase) || Target.Equals("all satellites", StringComparison.OrdinalIgnoreCase));
            var Matches = Clients.Where(Id => Target is not null && this.Matches(Id, Target, SourceId)).ToArray();
            if (!Broadcast && Matches.Length != 1)
            {
                if (Context is not null) Context.Announcement = new(Message, Clients, DateTimeOffset.UtcNow, Importance);
                return "Which client should receive the announcement?\n1. All clients\n" + string.Join("\n", Clients.Select((Id, Index) => $"{Index + 2}. {Name(Id, SourceId)}"));
            }
            var Run = Assister.Diagnostics.RunTracing.RunId;
            var Key = Run != Guid.Empty ? Run : Guid.NewGuid();
            Rich.Publish(SourceId!, "announcement", Message, "announcement:" + Key.ToString("N"), Importance switch
            {
                AnnouncementImportance.HighImportance => "high-importance",
                AnnouncementImportance.Acknowledge => "acknowledge",
                AnnouncementImportance.Alarm => "alarm",
                _ => "notify"
            }, Broadcast ? null : Matches[0]);
            return Broadcast ? "Sent announcement to your clients." : "Sent announcement to " + Name(Matches[0], SourceId) + ".";
        }
        var AvailableIds = Available();
        if (AvailableIds.Length == 0) return "There are no connected satellites that support announcements.";
        var All = Target is not null && (Target.Equals("all", StringComparison.OrdinalIgnoreCase) || Target.Equals("all satellites", StringComparison.OrdinalIgnoreCase));
        var Targets = All ? AvailableIds : AvailableIds.Where(Id => Target is not null && Matches(Id, Target)).ToArray();
        if (!All && Targets.Length != 1)
        {
            if (Context is not null) Context.Announcement = new(Message, AvailableIds, DateTimeOffset.UtcNow, Importance);
            return "Which satellite should receive the announcement?\n1. All satellites\n" + string.Join("\n", AvailableIds.Select((Id, Index) => $"{Index + 2}. {Name(Id)}"));
        }
        var Queued = Targets.Where(Id => Id == SourceId && Satellites.IsBusy(Id) && Queue?.Enqueue(Id, Message, Importance) == true).ToArray();
        var Results = await Task.WhenAll(Targets.Except(Queued).Select(async Id =>
        {
            try { return (Id, Played: await Satellites.AnnounceAsync(Id, Message, Tts, Token, RequireVolume: true, Importance: Importance)); }
            catch (Exception Error) when (Error is IOException or InvalidOperationException or HttpRequestException || Error is OperationCanceledException && !Token.IsCancellationRequested)
            { return (Id, Played: false); }
        }));
        var Played = Results.Where(Item => Item.Played).Select(Item => Name(Item.Id)).ToArray();
        var Failed = Results.Where(Item => !Item.Played).Select(Item => Name(Item.Id)).ToArray();
        return (Played.Length > 0 ? "Announced on " + string.Join(", ", Played) + "." : "")
            + (Queued.Length > 0 ? " Queued for " + string.Join(", ", Queued.Select(Id => Name(Id))) + " after this response." : "")
            + (Failed.Length > 0 ? " Could not announce on " + string.Join(", ", Failed) + "; unavailable, busy, volume unknown or unsupported, or playback failed." : "");
    }
}


