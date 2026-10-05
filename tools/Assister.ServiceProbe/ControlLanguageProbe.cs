using Assister.Llm;
using Assister.Tools;
using Microsoft.Extensions.Configuration;

// Read-only: calls the language model interpreter, never Home Assistant controls.
public static class ControlLanguageProbe
{
    public static async Task<int> RunAsync()
    {
        var Configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        using var Http = new HttpClient();
        var Model = new OpenAiCompatibleLanguageModel(Http, Configuration);
        var Cases = new (string Message, ControlRequest? Expected)[]
        {
            ("Hey Jarvis, can you turn the kitchen lights on 100%?", new("set_brightness", "kitchen lights", 100)),
            ("I'd like some light in the kitchen, make it as bright as possible.", new("set_brightness", "kitchen lights", 100)),
            ("Could you get the kitchen lights going?", new("turn_on", "kitchen lights")),
            ("What lights are in the kitchen?", null),
            ("Don't turn the kitchen lights on", null),
            ("Turn on the kitchen lights when I get home", null),
            ("Tell me how to turn the kitchen lights on", null),
            ("If I say turn off the kitchen lights, what happens?", null)
        };
        var Failures = 0;
        foreach (var (Message, Expected) in Cases)
        {
            var Actual = await SemanticControlRequest.InterpretAsync(Model, Message, CancellationToken.None);
            // The target may preserve the room in its noun phrase or explicit area.
            var Passed = Expected is null ? Actual is null : Actual is not null && Actual.Action == Expected.Action
                && Actual.Brightness == Expected.Brightness
                && (Actual.Target.Contains("kitchen", StringComparison.OrdinalIgnoreCase) || Actual.Area?.Equals("kitchen", StringComparison.OrdinalIgnoreCase) == true)
                && (Actual.Target.Contains("light", StringComparison.OrdinalIgnoreCase)
                    || Actual.Action == "set_brightness" && Actual.Target.Equals("kitchen", StringComparison.OrdinalIgnoreCase));
            if (!Passed) { Failures++; }
            Console.WriteLine($"{(Passed ? "PASS" : "FAIL")}: {Message} => {Actual?.ToString() ?? "no control"}");
        }
        return Failures == 0 ? 0 : 1;
    }
}
