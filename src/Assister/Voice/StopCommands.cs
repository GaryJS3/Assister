using Assister.Intents;

namespace Assister.Voice;

public static class StopCommands
{
    public static bool IsStop(string Text)
    {
        var Normalized = LanguageParser.Normalize(Text);
        if (Normalized.StartsWith("please ", StringComparison.Ordinal)) { Normalized = Normalized[7..]; }
        return Normalized is "stop" or "cancel" or "stop talking" or "stop speaking" or "cancel that";
    }
}
