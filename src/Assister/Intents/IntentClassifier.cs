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

public sealed record IntentMatch(DirectIntentKind Kind, string Target, int? BrightnessPercent = null, string? ExplicitArea = null)
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

public sealed class IntentClassifier
{
    public IntentMatch? Classify(string Message)
    {
        var Text = Normalize(Message);

        var Match = Pattern(Text, @"^turn (?<target>.+) (?<power>on|off)$");
        if (!Match.Success) { Match = Pattern(Text, @"^turn (?<power>on|off) (?<target>.+)$"); }
        if (Match.Success)
        {
            return Slots(Match.Groups["power"].Value == "on" ? DirectIntentKind.TurnOn : DirectIntentKind.TurnOff, Match.Groups["target"].Value);
        }

        Match = Pattern(Text, @"^set (?<target>.+) to (?<percent>-?\d{1,9})\s*(?:percent|%)$");
        if (Match.Success)
        {
            return Slots(DirectIntentKind.SetBrightness, Match.Groups["target"].Value, int.Parse(Match.Groups["percent"].Value));
        }

        Match = Pattern(Text, @"^(?:what is|what's) the temperature (?:in|of) (?<area>.+)$");
        if (Match.Success) { return new(DirectIntentKind.QueryTemperature, "temperature", ExplicitArea: LanguageParser.Noun(Match.Groups["area"].Value)); }
        if (Text is "what is the temperature" or "what's the temperature") { return new(DirectIntentKind.QueryTemperature, "temperature"); }

        Match = Pattern(Text, @"^(?:what is|what's) the (?:state|status) of (?<target>.+)$");
        if (!Match.Success) { Match = Pattern(Text, @"^is (?<target>.+) (?:on|off)$"); }
        return Match.Success ? Slots(DirectIntentKind.QueryState, Match.Groups["target"].Value) : null;
    }

    public static string Normalize(string Message)
    {
        var Text = LanguageParser.Normalize(Message);
        if (Text.StartsWith("please ", StringComparison.Ordinal)) { Text = Text[7..]; }
        if (Text.EndsWith(" please", StringComparison.Ordinal)) { Text = Text[..^7]; }
        return Text;
    }

    private static Match Pattern(string Text, string Pattern) => Regex.Match(Text, Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static IntentMatch Slots(DirectIntentKind Kind, string Target, int? Percent = null)
    {
        Target = LanguageParser.Noun(Target);
        var AreaMatch = Pattern(Target, @"^(?<target>.+) in (?<area>.+)$");
        return AreaMatch.Success
            ? new(Kind, LanguageParser.Noun(AreaMatch.Groups["target"].Value), Percent, LanguageParser.Noun(AreaMatch.Groups["area"].Value))
            : new(Kind, Target, Percent);
    }
}
