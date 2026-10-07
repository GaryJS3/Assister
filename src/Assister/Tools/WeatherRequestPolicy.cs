using System.Text.RegularExpressions;
using Assister.Contracts;

namespace Assister.Tools;

public enum WeatherRequestKind { None, Current, History, Forecast }

public static class WeatherRequestPolicy
{
    private static bool Matches(string Text, string Pattern) => Regex.IsMatch(Text, Pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static WeatherRequestKind Classify(string Message, IReadOnlyList<LlmMessage> History)
    {
        const string Topic = @"\b(?:weather|forecast|wind|gusts?|rain(?:ing|fall)?|precipitation|humidity|outdoor temperature|outside temperature)\b";
        var FollowUp = Matches(Message, @"^\s*(?:what about|and|how about)\b")
            && History.Where(Item => Item.Role == "user").TakeLast(2).Any(Item => Matches(Item.Content ?? "", Topic));
        if (!Matches(Message, Topic) && !FollowUp) return WeatherRequestKind.None;
        // Observations take precedence over future keywords, including after a forecast conversation.
        if (Matches(Message, @"\b(?:yesterday|ago|earlier|historical|history|so far|been|was|were|did|gotten|last (?:week|month|morning|afternoon|evening|night))\b"))
            return WeatherRequestKind.History;
        var Future = Matches(Message, @"\b(?:forecast|will|expected|tomorrow|tonight|later|upcoming|next|this weekend)\b")
            || Matches(Message, @"\b(?:look like|outlook)\b") && Matches(Message, @"\bthis (?:week|month|morning|afternoon|evening|night)\b")
            || FollowUp && Matches(Message, @"\b(?:sunday|monday|tuesday|wednesday|thursday|friday|saturday)\b");
        if (Matches(Message, @"\bthis (?:week|month|morning|afternoon|evening|night)\b") && !Future
            || Matches(Message, @"\b(?:highest|lowest|maximum|minimum|average|total|how much)\b")
                && !Future)
            return WeatherRequestKind.History;
        if (Matches(Message, @"\b(?:now|currently|current|right now|is it raining|weather station)\b")) return WeatherRequestKind.Current;
        if (Future) return WeatherRequestKind.Forecast;
        return WeatherRequestKind.Current;
    }
}
