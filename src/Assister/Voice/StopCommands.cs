using Assister.Intents;

namespace Assister.Voice;

public static class StopCommands
{
    public static bool IsStop(string Text)
    {
        var Normalized = LanguageParser.Normalize(Text);
        if (Normalized.StartsWith("please ", StringComparison.Ordinal)) { Normalized = Normalized[7..]; }
        return System.Text.RegularExpressions.Regex.IsMatch(Normalized,
            @"^(?:(?:what|okay|ok|actually|never mind)[?.,!]?\s+)?(?:please\s+)?(?:stop|cancel|canceled|cancelled|stop talking|stop speaking|cancel that)(?:[, ]+please)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
