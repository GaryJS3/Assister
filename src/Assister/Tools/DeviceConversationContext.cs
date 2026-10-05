using System.Text.RegularExpressions;
using Assister.Intents;
using Assister.Modules.HomeAssistant;

namespace Assister.Tools;

public sealed record DeviceReference(string EntityId, string Name);
public sealed record ControlReceipt(string Action, string[] EntityIds, DateTimeOffset CompletedAt, int? BrightnessPercent = null);
public sealed record ControlAttempt(string Action, string[] EntityIds, string Outcome, DateTimeOffset AttemptedAt, int? BrightnessPercent = null);
public sealed class DeviceConversationContext
{
    public DeviceReference[] References { get; set; } = [];
    public ControlRequest? Pending { get; set; }
    public bool AwaitingAction { get; set; }
    public ControlReceipt? LastCompleted { get; set; }
    public ControlAttempt? LastAttempted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public void Expire()
    {
        if (UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-5)) { References = []; Pending = null; AwaitingAction = false; }
    }
}

// Authorization is independent of the model. History/prose can describe a previous
// action but cannot authorize it. Only a current command or an answer to a bounded,
// structured pending command supplies an action.
public sealed record ControlRequest(string Action, string Target, int? Brightness = null, string? Area = null)
{
    private static Match Match(string Text, string Pattern) => Regex.Match(Text, Pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool IsSelection(string Text) => Match(LanguageParser.Noun(Text),
        @"^(?:both(?: of them)?|all(?: of them)?|them|it|yes|that one)$").Success;
    public static bool IsReference(string Text) => Match(LanguageParser.Noun(Text),
        @"^(?:both(?: of them)?|all(?: of them)?|them|it|those(?: lights)?|these(?: lights)?|that one|yes)$").Success;

    public static ControlRequest? Parse(string Text, DeviceConversationContext? Conversation = null)
    {
        Conversation?.Expire();
        var Normalized = LanguageParser.Normalize(Text);
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
            if (Parsed is { Kind: DirectIntentKind.TurnOn or DirectIntentKind.TurnOff or DirectIntentKind.SetBrightness })
                return new(Parsed.Kind == DirectIntentKind.TurnOn ? "turn_on" : Parsed.Kind == DirectIntentKind.TurnOff ? "turn_off" : "set_brightness",
                    Parsed.Target, Parsed.BrightnessPercent, Parsed.ExplicitArea);
            var Power = Match(Command, @"^(?:switch|put) (?<target>.+?) (?:back )?(?<power>on|off)$");
            if (!Power.Success) { Power = Match(Command, @"^turn (?<target>.+?) back (?<power>on|off)$"); }
            if (Power.Success) { return new("turn_" + Power.Groups["power"].Value, LanguageParser.Noun(Power.Groups["target"].Value)); }
            var Brightness = Match(Command, @"^(?:dim|brighten|set|make|bring|adjust) (?<target>.+?) (?:to )?(?<percent>\d{1,3})\s*(?:percent|%)?$");
            if (Brightness.Success) { return new("set_brightness", LanguageParser.Noun(Brightness.Groups["target"].Value), int.Parse(Brightness.Groups["percent"].Value)); }
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
            if (References.Length == 0 || Target is "it" or "that one" or "yes" && References.Length != 1) { return []; }
            if (Target.StartsWith("both", StringComparison.Ordinal) && References.Length != 2) { return []; }
            var Resolved = References.Select(Reference => Snapshot.Entities.FirstOrDefault(Entity => Entity.EntityId == Reference.EntityId))
                .OfType<HomeAssistantEntity>().Where(Entity => Entity.Domain is "light" or "switch").ToArray();
            return Resolved.Length == References.Length ? Resolved : [];
        }
        var NamedTarget = Regex.Replace(Target, @"^(?:both|all)\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var Domain = HomeAssistantEntitySearch.Words(NamedTarget).Contains("light") || Action == "set_brightness" ? "light"
            : HomeAssistantEntitySearch.Words(Target).Contains("switch") ? "switch" : null;
        var Generic = Domain is not null && HomeAssistantEntitySearch.Words(NamedTarget).SequenceEqual([Domain]);
        var EffectiveArea = Area ?? (Generic ? SatelliteArea : null);
        if (Generic && EffectiveArea is null && !Target.StartsWith("all ", StringComparison.Ordinal)) { return []; }
        var Parts = NamedTarget.Split(" and ", StringSplitOptions.None);
        var Whole = HomeAssistantEntitySearch.Find(Snapshot.Entities.Where(Entity => Entity.Domain is "light" or "switch"), NamedTarget,
            EffectiveArea, Domain is null ? null : [Domain]);
        if (Parts.Length == 1 || Whole.Length > 0) { return Whole; }
        var Groups = Parts.Select(Part => HomeAssistantEntitySearch.Find(Snapshot.Entities.Where(Entity => Entity.Domain is "light" or "switch"), Part,
            EffectiveArea, Domain is null ? null : [Domain])).ToArray();
        return Groups.All(Group => Group.Length == 1) ? Groups.SelectMany(Group => Group).DistinctBy(Entity => Entity.EntityId).ToArray() : [];
    }
}
