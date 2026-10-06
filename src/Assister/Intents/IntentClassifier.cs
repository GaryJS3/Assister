using System.Text.RegularExpressions;

namespace Assister.Intents;

public enum DirectIntentKind
{
    TurnOn,
    TurnOff,
    SetBrightness,
    QueryState,
    QueryTemperature
}

public sealed record IntentMatch(DirectIntentKind Kind, string Target, int? BrightnessPercent = null, string? ExplicitArea = null, string? TargetDomain = null)
{
    public string MatchedRule => Kind switch
    {
        DirectIntentKind.TurnOn or DirectIntentKind.TurnOff => "TurnPower",
        DirectIntentKind.SetBrightness => "SetBrightnessPercent",
        DirectIntentKind.QueryTemperature => "QueryTemperature",
        _ => "QueryState"
    };
}

public static class LanguageParser
{
    public static string Normalize(string Text) => Regex.Replace(Text.Trim().TrimEnd('.', '?', '!').ToLowerInvariant(), @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public static string Noun(string Text)
    {
        var Value = Normalize(Text);
        return Value.StartsWith("the ", StringComparison.Ordinal) ? Value[4..] : Value;
    }
}

public sealed class IntentClassifier : IIntentEngine
{
    public Task<IntentDecision> MatchAsync(string Text, CancellationToken Token) => Task.FromResult(IntentMatching.Match(Text, IntentCatalog.BuiltIns, this));
    public IntentMatch? Classify(string Message)
    {
        var Text = Normalize(Message);

        var Match = Pattern(Text, @"^(?:turn|switch) (?:(?<target>.+?) on|on (?<target>.+?)) (?:to |at )?(?<percent>-?\d{1,9})\s*(?:percent|%)?$");
        if (Match.Success) { return Slots(DirectIntentKind.SetBrightness, Match.Groups["target"].Value, int.Parse(Match.Groups["percent"].Value)); }

        Match = Pattern(Text, @"^turn (?<target>.+) (?<power>on|off)$");
        if (!Match.Success) { Match = Pattern(Text, @"^turn (?<power>on|off) (?<target>.+)$"); }
        if (Match.Success)
        {
            return Slots(Match.Groups["power"].Value == "on" ? DirectIntentKind.TurnOn : DirectIntentKind.TurnOff, Match.Groups["target"].Value);
        }

        Match = Pattern(Text, @"^(?:set|turn) (?<target>.+) (?:to|at) (?<percent>-?\d{1,9})\s*(?:percent|%)?$");
        if (Match.Success)
        {
            var Target = Match.Groups["target"].Value;
            // A bare number is brightness only for a light target, not a thermostat or other device.
            if (Text.EndsWith("percent", StringComparison.Ordinal) || Text.EndsWith('%')
                || Pattern(Target, @"\b(?:light|lights|lamp|lamps)\b").Success || Target.StartsWith("light.", StringComparison.Ordinal))
            {
                return Slots(DirectIntentKind.SetBrightness, Target, int.Parse(Match.Groups["percent"].Value));
            }
        }

        Match = Pattern(Text, @"^(?:what is|what's) the temperature (?:in|of) (?<area>.+)$");
        if (Match.Success) { return new(DirectIntentKind.QueryTemperature, "temperature", ExplicitArea: LanguageParser.Noun(Match.Groups["area"].Value)); }
        if (Text is "what is the temperature" or "what's the temperature") { return new(DirectIntentKind.QueryTemperature, "temperature"); }

        Match = Pattern(Text, @"^(?:what is|what's) the (?:state|status) of (?<target>.+)$");
        if (!Match.Success) { Match = Pattern(Text, @"^is (?<target>.+) (?:on|off)$"); }
        if (!Match.Success) { return null; }
        var StateTarget = Match.Groups["target"].Value;
        // The native state handler reads exactly one entity. Group questions need
        // search/list reasoning; do not turn their scope into a device name or HA area.
        // Explicit IDs remain single-entity references even when their names are plural.
        if (!Pattern(StateTarget, @"^[a-z_]+\.[a-z0-9_]+$").Success
            && Pattern(StateTarget, @"\b(?:all|both|every|each|lights|lamps|switches|devices|sensors)\b|\band\b|\b(?:in|throughout|across) (?:the |my |our )?(?:(?:whole|entire) )?(?:house|home)\b").Success)
        {
            return null;
        }
        return Slots(DirectIntentKind.QueryState, StateTarget);
    }

    public static string Normalize(string Message)
    {
        var Text = LanguageParser.Normalize(Message);
        Text = Regex.Replace(Text, @"^(?:(?:hey|okay|ok)\s+)?(?:jarvis|assister)[,\s]+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        string Previous;
        do
        {
            Previous = Text;
            Text = Regex.Replace(Text, @"^(?:can you|could you|would you|will you|please)\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        } while (Text != Previous);
        if (Text.EndsWith(" please", StringComparison.Ordinal)) { Text = Text[..^7]; }
        return Text;
    }

    private static Match Pattern(string Text, string Pattern) => Regex.Match(Text, Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static IntentMatch Slots(DirectIntentKind Kind, string Target, int? Percent = null)
    {
        Target = LanguageParser.Noun(Target);
        if (Target.StartsWith("both ", StringComparison.Ordinal)) { Target = LanguageParser.Noun(Target[5..]); }
        var AreaMatch = Pattern(Target, @"^(?<target>.+) in (?<area>.+)$");
        return AreaMatch.Success
            ? new(Kind, LanguageParser.Noun(AreaMatch.Groups["target"].Value), Percent, LanguageParser.Noun(AreaMatch.Groups["area"].Value))
            : new(Kind, Target, Percent);
    }
}
