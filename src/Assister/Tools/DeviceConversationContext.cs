using System.Text.RegularExpressions;
using Assister.Intents;
using Assister.Modules.HomeAssistant;

namespace Assister.Tools;

public sealed record DeviceReference(string EntityId, string Name);
public sealed record ControlReceipt(string Action, string[] EntityIds, DateTimeOffset CompletedAt, int? BrightnessPercent = null, int? SpeedPercent = null);
public sealed record ControlAttempt(string Action, string[] EntityIds, string Outcome, DateTimeOffset AttemptedAt, int? BrightnessPercent = null, int? SpeedPercent = null);
public sealed class DeviceConversationContext
{
    public Assister.Satellites.PendingAnnouncement? Announcement { get; set; }
    public string? SourceDeviceId { get; set; }
    public DateTimeOffset? ResponseFinishedAt { get; set; }
    public DeviceReference[] References { get; set; } = [];
    public ControlRequest? Pending { get; set; }
    public bool AwaitingAction { get; set; }
    public bool AwaitingStateQuery { get; set; }
    public ControlReceipt? LastCompleted { get; set; }
    public ControlAttempt? LastAttempted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public void Expire()
    {
        if (UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-5)) { References = []; Pending = null; AwaitingAction = false; AwaitingStateQuery = false; }
    }
}

// Authorization is independent of the model. History/prose can describe a previous
// action but cannot authorize it. Only a current command or an answer to a bounded,
// structured pending command supplies an action.
public sealed record ControlRequest(string Action, string Target, int? Brightness = null, string? Area = null, int? SpeedPercent = null)
{
    private static Match Match(string Text, string Pattern) => Regex.Match(Text, Pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool IsSelection(string Text) => Match(LanguageParser.Noun(Text),
        @"^(?:both(?: of them)?|all(?: of them)?|them|it|yes|that one)$").Success;
    public static bool IsReference(string Text) => Match(LanguageParser.Noun(Text),
        @"^(?:both(?: of them)?|all(?: of them)?|them|it|those(?: lights)?|these(?: lights)?|that (?:one|light|fan|switch|device)|this (?:light|fan|switch|device)|(?:light|fan|switch|device|one) we were just talking about|yes)$").Success;

    public static bool IsSingularReference(string Text) => IsReference(Text)
        && !Match(LanguageParser.Noun(Text), @"^(?:both|all|them|those|these)\b").Success;

    public static string NormalizeTargetList(string Text)
    {
        var Target = Regex.Replace(Text, @"\s+(?:as well|too)$", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return Regex.Replace(Target, @"\s*,\s*(?:and\s+)?(?:the\s+)?|\s+(?:as well as|and also)\s+(?:the\s+)?", " and ",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public static ControlRequest? Parse(string Text, DeviceConversationContext? Conversation = null)
    {
        Conversation?.Expire();
        var Normalized = LanguageParser.Normalize(Text);
        var FollowUp = Match(Normalized, @"^(?<reference>(?:the )?(?:light|fan|switch|device|one) we were just talking about),? (?:can|could|would) you (?:go ahead and )?(?<command>turn (?:it|that|them) (?:on|off))$");
        if (FollowUp.Success)
            Normalized = FollowUp.Groups["command"].Value.Replace(" it ", " " + FollowUp.Groups["reference"].Value + " ", StringComparison.Ordinal)
                .Replace(" that ", " " + FollowUp.Groups["reference"].Value + " ", StringComparison.Ordinal);
        if (Conversation is { References.Length: > 0 })
        {
            if (Normalized is "on" or "off" && (Conversation.References.Length == 1 || Conversation.AwaitingAction))
                return new("turn_" + Normalized, "them");
            if (IsSelection(Normalized) && Conversation.Pending is { } Pending)
            { return Pending with { Target = Normalized }; }
            if (Conversation.Pending is { } NamedPending)
            {
                var Named = Conversation.References.Where(Reference => HomeAssistantEntitySearch.Words(Reference.Name).Order()
                    .SequenceEqual(HomeAssistantEntitySearch.Words(Normalized).Order()) || Reference.EntityId == Normalized).ToArray();
                if (Named.Length == 1) { return NamedPending with { Target = Named[0].EntityId }; }
            }
        }
        // Inspect complete clauses, never a verb buried inside an information question.
        foreach (var Clause in Regex.Split(Normalized, @"[.!?;]\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            var Command = IntentClassifier.Normalize(Clause);
            if (Match(Command, @"\b(?:don't|do not|never|not|if|when|unless)\b").Success) { continue; }
            Command = Regex.Replace(Command, @"\bback (on|off)\b", "$1", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var Parsed = new IntentClassifier().Classify(Command);
            if (Parsed is { Kind: DirectIntentKind.TurnOn or DirectIntentKind.TurnOff or DirectIntentKind.SetBrightness or DirectIntentKind.SetFanSpeed })
                return new(Parsed.Kind == DirectIntentKind.TurnOn ? "turn_on" : Parsed.Kind == DirectIntentKind.TurnOff ? "turn_off" : Parsed.Kind == DirectIntentKind.SetFanSpeed ? "set_fan_speed" : "set_brightness",
                    Parsed.Target, Parsed.BrightnessPercent, Parsed.ExplicitArea, Parsed.SpeedPercent);
            var Power = Match(Command, @"^(?:switch|put) (?<target>.+?) (?:back )?(?<power>on|off)$");
            if (!Power.Success) { Power = Match(Command, @"^turn (?<target>.+?) back (?<power>on|off)$"); }
            if (Power.Success) { return new("turn_" + Power.Groups["power"].Value, LanguageParser.Noun(Power.Groups["target"].Value)); }
            var Brightness = Match(Command, @"^(?:dim|brighten|set|make|bring|adjust) (?<target>.+?) (?:(?:to|at) )?(?<percent>\d{1,3})\s*(?:percent|%)?$");
            if (Brightness.Success)
            {
                var Target = LanguageParser.Noun(Brightness.Groups["target"].Value);
                var Fan = HomeAssistantEntitySearch.Words(Target).Contains("fan") && !HomeAssistantEntitySearch.Words(Target).Contains("light");
                return Fan ? new("set_fan_speed", Regex.Replace(Target, @"\s+speed\b", ""), SpeedPercent: int.Parse(Brightness.Groups["percent"].Value))
                    : new("set_brightness", Target, int.Parse(Brightness.Groups["percent"].Value));
            }
            var Full = Match(Command, @"^(?:make|set|bring) (?<target>.+?) (?:to )?(?:fully bright|full brightness|maximum brightness)$");
            if (Full.Success) { return new("set_brightness", LanguageParser.Noun(Full.Groups["target"].Value), 100); }
            var Relative = Match(Command, @"^(?:dim|brighten) (?<target>.+)$");
            if (Relative.Success) { return new("set_brightness", LanguageParser.Noun(Relative.Groups["target"].Value)); }
        }
        return null;
    }

    public HomeAssistantEntity[] Resolve(HomeAssistantSnapshot Snapshot, DeviceConversationContext? Conversation, string? SatelliteArea = null)
    {
        if (IsReference(Target))
        {
            var References = Conversation?.References ?? [];
            if (References.Length == 0 || IsSingularReference(Target) && References.Length != 1) { return []; }
            if (Target.StartsWith("both", StringComparison.Ordinal) && References.Length != 2) { return []; }
            var Resolved = References.Select(Reference => Snapshot.Entities.FirstOrDefault(Entity => Entity.EntityId == Reference.EntityId))
                .OfType<HomeAssistantEntity>().Where(Entity => Entity.Domain is "light" or "switch" or "fan")
                .Where(Entity => Action != "set_fan_speed" || Entity.Domain == "fan")
                .Where(Entity => Action != "set_brightness" || Entity.Domain == "light").ToArray();
            var ReferenceWords = HomeAssistantEntitySearch.Words(Target);
            if (ReferenceWords.Contains("light")) { Resolved = Resolved.Where(Entity => Entity.Domain == "light").ToArray(); }
            if (ReferenceWords.Contains("fan")) { Resolved = Resolved.Where(Entity => Entity.Domain == "fan").ToArray(); }
            if (ReferenceWords.Contains("switch")) { Resolved = Resolved.Where(Entity => Entity.Domain == "switch").ToArray(); }
            return Resolved.Length == References.Length ? Resolved : [];
        }
        var NamedTarget = LanguageParser.WithoutGroupQuantifier(Target);
        NamedTarget = Regex.Replace(NamedTarget, @"^(?:both|all)\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var Domain = Action == "set_fan_speed" ? "fan" : HomeAssistantEntitySearch.Words(NamedTarget).Contains("light") || Action == "set_brightness" ? "light"
            : HomeAssistantEntitySearch.Words(Target).Contains("switch") ? "switch" : null;
        var Generic = Domain is not null && HomeAssistantEntitySearch.Words(NamedTarget).SequenceEqual([Domain]);
        var EffectiveArea = Area ?? (Generic ? SatelliteArea : null);
        if (Generic && EffectiveArea is null && !Target.StartsWith("all ", StringComparison.Ordinal)) { return []; }
        // An entity ID can contain an old room name. Use the same area-aware resolution as
        // direct commands so incidental ID matches cannot expand the authorized target set.
        var Scoped = Area is not null || Snapshot.Entities.Select(Entity => Entity.AreaName).OfType<string>()
            .Any(Name => NamedTarget.StartsWith(LanguageParser.Normalize(Name) + " ", StringComparison.Ordinal));
        if (Scoped)
            return new HomeAssistantEntityResolver().Resolve(new(Action switch
            {
                "turn_on" => DirectIntentKind.TurnOn, "turn_off" => DirectIntentKind.TurnOff,
                "set_fan_speed" => DirectIntentKind.SetFanSpeed, _ => DirectIntentKind.SetBrightness
            }, NamedTarget, Brightness, Area, Domain, SpeedPercent), SatelliteArea, Snapshot).Entities.ToArray();
        var Parts = NormalizeTargetList(NamedTarget).Split(" and ", StringSplitOptions.None);
        var Whole = HomeAssistantEntitySearch.Find(Snapshot.Entities.Where(Entity => Entity.Domain is "light" or "switch" or "fan"), NamedTarget,
            EffectiveArea, Domain is null ? null : [Domain]);
        if (Parts.Length == 1 || Whole.Length > 0) { return Whole; }
        var Compound = new HomeAssistantEntityResolver().Resolve(new(Action switch
        {
            "turn_on" => DirectIntentKind.TurnOn, "turn_off" => DirectIntentKind.TurnOff,
            "set_fan_speed" => DirectIntentKind.SetFanSpeed, _ => DirectIntentKind.SetBrightness
        }, string.Join(" and ", Parts), Brightness, Area, Domain, SpeedPercent), SatelliteArea, Snapshot);
        return Compound.Entities.ToArray();
    }
}
