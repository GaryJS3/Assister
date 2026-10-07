using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Assister.Intents;

public sealed record IntentCandidate(IntentDefinition Definition, IntentMatch? Intent, string Pattern);
public sealed record IntentDecision(string Status, string Normalized, IntentCandidate[] Candidates, string Reason)
{
    public IntentCandidate? Match => Status == "matched" ? Candidates.FirstOrDefault() : null;
}

public interface IIntentEngine
{
    Task<IntentDecision> MatchAsync(string Text, CancellationToken Token);
}

public sealed class IntentEngine(IntentStore Store, IntentClassifier Native, IntentActionRegistry Registry) : IIntentEngine
{
    public async Task<IntentDecision> MatchAsync(string Text, CancellationToken Token) =>
        IntentMatching.Match(Text, await Store.DefinitionsAsync(Token), Native, Registry);
}

public sealed record CompiledIntentTemplate(Regex Pattern, HashSet<string> Slots, string? TargetDomain);

public static class IntentTemplate
{
    public static CompiledIntentTemplate Compile(string Template)
    {
        if (string.IsNullOrWhiteSpace(Template) || Template.Length > 256) { throw new ArgumentException("Each phrase must have 1 to 256 characters."); }
        var Text = IntentClassifier.Normalize(Template);
        var Pattern = new StringBuilder("^");
        var Slots = new HashSet<string>();
        string? Domain = null;
        var Index = 0;
        foreach (Match Slot in Regex.Matches(Text, @"\{(?<name>target|brightness|speed|area)(?::(?<type>light|switch|fan|entity|percent|area))?\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
        {
            var Literal = Text[Index..Slot.Index];
            if (Literal.Contains('{') || Literal.Contains('}')) { throw new ArgumentException("Unknown slot. Use target, brightness or area."); }
            Pattern.Append(Regex.Escape(Literal));
            var Name = Slot.Groups["name"].Value;
            var Type = Slot.Groups["type"].Value;
            if (!Slots.Add(Name)) { throw new ArgumentException("A phrase cannot repeat the same slot."); }
            if (Index > 0 && Slot.Index == Index) { throw new ArgumentException("Separate slots with literal words."); }
            if (Name is "brightness" or "speed" && Type is not ("" or "percent")
                || Name == "area" && Type is not ("" or "area")
                || Name == "target" && Type is not ("" or "light" or "switch" or "fan" or "entity")) { throw new ArgumentException("The slot type does not match its name."); }
            if (Name == "target" && Type is "light" or "switch" or "fan") { Domain = Type; }
            Pattern.Append(Name is "brightness" or "speed" ? $@"(?<{Name}>-?\d{{1,9}})" : $"(?<{Name}>.{{1,256}}?)");
            Index = Slot.Index + Slot.Length;
        }
        var Tail = Text[Index..];
        if (Tail.Contains('{') || Tail.Contains('}')) { throw new ArgumentException("Unknown or unclosed slot."); }
        if (Slots.Count > 0 && string.IsNullOrWhiteSpace(Regex.Replace(Text, @"\{[^}]*\}", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))))
        {
            throw new ArgumentException("Phrases need literal words, not only slots.");
        }
        Pattern.Append(Regex.Escape(Tail)).Append('$');
        return new(new Regex(Pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), Slots, Domain);
    }
}

public static class IntentMatching
{
    public static IntentDecision Match(string Text, IReadOnlyList<IntentDefinition> Definitions, IntentClassifier Native, IntentActionRegistry? Registry = null)
    {
        if (string.IsNullOrWhiteSpace(Text) || Text.Length > 1000) { return new("invalid-request", "", [], "Enter a request of 1 to 1000 characters."); }
        var Normalized = IntentClassifier.Normalize(Text);
        if (Assister.Modules.Timers.TimerIntentHandler.Recognizes(Text))
        {
            return new("timer-route", Normalized, [], "The existing native timer module handles this before the intent engine. Use Debug Chat to exercise timers.");
        }
        var Candidates = new List<IntentCandidate>();
        var NativeMatch = Native.Classify(Text);
        if (NativeMatch is not null && Definitions.SingleOrDefault(Row => Row.Id == IntentCatalog.NativeId(NativeMatch.Kind) && Row.Enabled) is { } BuiltIn)
        {
            Candidates.Add(new(BuiltIn, NativeMatch, "Native C# rule: " + NativeMatch.MatchedRule));
        }
        foreach (var Definition in Definitions.Where(Row => Row.Enabled))
        {
            foreach (var Template in Definition.Patterns)
            {
                var Compiled = IntentTemplate.Compile(Template);
                var Match = Compiled.Pattern.Match(Normalized);
                if (!Match.Success) { continue; }
                var Target = Compiled.Slots.Contains("target") ? LanguageParser.Noun(Match.Groups["target"].Value) : Definition.Target;
                var Area = Compiled.Slots.Contains("area") ? LanguageParser.Noun(Match.Groups["area"].Value) : Definition.Area;
                int? Brightness = Compiled.Slots.Contains("brightness") ? int.Parse(Match.Groups["brightness"].Value, CultureInfo.InvariantCulture) : Definition.Brightness;
                int? Speed = Compiled.Slots.Contains("speed") ? int.Parse(Match.Groups["speed"].Value, CultureInfo.InvariantCulture) : Definition.SpeedPercent;
                var Intent = (Registry ?? IntentActionRegistry.Default).Get(Definition.ActionId).DeviceIntent is { } Kind
                    ? new IntentMatch(Kind, Kind == DirectIntentKind.QueryTemperature ? "temperature" : Target ?? "", Kind == DirectIntentKind.SetFanSpeed ? null : Brightness,
                        Area, Compiled.TargetDomain, Speed) : null;
                // Two patterns in one definition may extract different slots; retain conflicts.
                var Equivalent = Candidates.FindIndex(Item => Item.Definition.Id == Definition.Id
                    && Item.Intent is { } Prior && Intent is not null && (Prior with { TargetDomain = null }) == (Intent with { TargetDomain = null })
                    && (Prior.TargetDomain is null || Intent.TargetDomain is null || Prior.TargetDomain == Intent.TargetDomain));
                if (Equivalent >= 0)
                {
                    Candidates[Equivalent] = new(Definition, Intent! with { TargetDomain = Intent!.TargetDomain ?? Candidates[Equivalent].Intent!.TargetDomain }, Template);
                }
                else if (!Candidates.Any(Item => Item.Definition.Id == Definition.Id && Item.Intent == Intent)) { Candidates.Add(new(Definition, Intent, Template)); }
            }
        }
        if (Candidates.Count == 0) { return new("unmatched", Normalized, [], "No enabled deterministic rule matched; the normal pipeline may use the LLM."); }
        // Multiple matching definitions are a visible conflict, never a priority-based silent action.
        if (Candidates.Count > 1) { return new("ambiguous", Normalized, Candidates.ToArray(), "More than one intent or slot extraction matches. Narrow the phrases before executing."); }
        if (Candidates[0].Intent is { Kind: DirectIntentKind.SetBrightness, BrightnessPercent: not (>= 0 and <= 100) })
        {
            return new("invalid-request", Normalized, Candidates.ToArray(), "Brightness must be between 0 and 100 percent.");
        }
        if (Candidates[0].Intent is { Kind: DirectIntentKind.SetFanSpeed, SpeedPercent: not (>= 0 and <= 100) })
        { return new("invalid-request", Normalized, Candidates.ToArray(), "Fan speed must be between 0 and 100 percent."); }
        return new("matched", Normalized, Candidates.ToArray(), "One enabled deterministic rule matched.");
    }
}

public static class IntentResponses
{
    public static string Render(IntentCandidate Candidate, string Response, IConfiguration Configuration, DateTimeOffset? Now = null)
    {
        var Time = Now ?? DateTimeOffset.UtcNow;
        if (Candidate.Definition.ActionId is "assister.time" or "assister.date" || Candidate.Definition.Response.Contains("{time}", StringComparison.Ordinal) || Candidate.Definition.Response.Contains("{date}", StringComparison.Ordinal))
        {
            Time = TimeZoneInfo.ConvertTime(Time, TimeZoneInfo.FindSystemTimeZoneById(Configuration["Assister:TimeZone"] ?? "America/New_York"));
        }
        if (Candidate.Definition.ActionId == "assister.time") { Response = $"It is {Time.ToString("h:mm tt", CultureInfo.InvariantCulture)}."; }
        if (Candidate.Definition.ActionId == "assister.date") { Response = $"Today is {Time.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)}."; }
        return Regex.Replace(Candidate.Definition.Response, @"\{(?<slot>response|target|brightness|speed|area|time|date)\}", Match => Match.Groups["slot"].Value switch
        {
            "response" => Response, "target" => Candidate.Intent?.Target ?? "", "brightness" => Candidate.Intent?.BrightnessPercent?.ToString(CultureInfo.InvariantCulture) ?? "",
            "speed" => Candidate.Intent?.SpeedPercent?.ToString(CultureInfo.InvariantCulture) ?? "",
            "area" => Candidate.Intent?.ExplicitArea ?? "", "time" => Time.ToString("h:mm tt", CultureInfo.InvariantCulture),
            _ => Time.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
        }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
}
