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
        if (Matches(Message, @"\b(?:yesterday|ago|earlier|historical|history|so far|has been|have been|was|were|did|gotten|(?:last|this) (?:week|month|morning|afternoon|evening|night))\b")
            || Matches(Message, @"\b(?:highest|lowest|maximum|minimum|average|total|how much)\b")
                && !Matches(Message, @"\b(?:will|forecast|expected|tomorrow|tonight|next)\b"))
            return WeatherRequestKind.History;
        if (Matches(Message, @"\b(?:now|currently|current|right now|is it raining|weather station)\b")) return WeatherRequestKind.Current;
        if (Matches(Message, @"\b(?:forecast|will|expected|tomorrow|tonight|later|upcoming|next|this weekend)\b")
            || FollowUp && Matches(Message, @"\b(?:sunday|monday|tuesday|wednesday|thursday|friday|saturday)\b"))
            return WeatherRequestKind.Forecast;
        return WeatherRequestKind.Current;
    }
}
