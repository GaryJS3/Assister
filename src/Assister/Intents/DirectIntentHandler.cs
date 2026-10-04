using Assister.Modules.HomeAssistant;

namespace Assister.Intents;

public sealed record IntentResult(string Response, string Outcome);

public sealed class DirectIntentHandler(IHomeAssistantClient HomeAssistant)
{
    public async Task<IntentResult> ExecuteAsync(IntentMatch Intent, EntityResolutionResult Resolution, CancellationToken CancellationToken)
    {
        var Entities = Resolution.Entities;
        if (Entities.Any(Entity => Entity.IsUnavailable)) { return new("That device is unavailable.", "unavailable"); }
        if (Intent.Kind is DirectIntentKind.QueryState or DirectIntentKind.QueryTemperature)
        {
            var Entity = Entities.Single();
            var Value = Entity.State.GetProperty("state").GetString();
            var Unit = Entity.State.GetProperty("attributes").TryGetProperty("unit_of_measurement", out var UnitValue) ? UnitValue.GetString() : null;
            Unit = Unit switch { "°F" => "degrees Fahrenheit", "°C" => "degrees Celsius", _ => Unit };
            return new($"{Entity.Name} is {Value}{(Unit is null ? "" : " " + Unit)}.", "succeeded");
        }

        if (Intent.Kind == DirectIntentKind.SetBrightness && Entities.Any(Entity => !Entity.SupportsBrightness))
        {
            return new("One of those lights does not support brightness control.", "unsupported");
        }

        var Action = Intent.Kind switch
        {
            DirectIntentKind.TurnOn => HomeAssistantAction.TurnOn,
            DirectIntentKind.TurnOff => HomeAssistantAction.TurnOff,
            DirectIntentKind.SetBrightness => HomeAssistantAction.SetBrightness,
            _ => throw new InvalidOperationException("Unsupported direct intent.")
        };
        await HomeAssistant.ControlAsync(new(Action, Entities.Select(Entity => Entity.EntityId).ToArray(), Intent.BrightnessPercent), CancellationToken);
        return new(Intent.Kind == DirectIntentKind.SetBrightness ? $"Set to {Intent.BrightnessPercent} percent." : "Done.", "succeeded");
    }
}
