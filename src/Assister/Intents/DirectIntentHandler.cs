using Assister.Modules.HomeAssistant;
using Assister.Contracts;

namespace Assister.Intents;

public sealed record IntentResult(string Response, string Outcome);

public sealed class DirectIntentHandler(IHomeAssistantClient HomeAssistant, HomeAssistantStateCache? Cache = null)
{
    public async Task<IntentResult> ExecuteAsync(IntentMatch Intent, EntityResolutionResult Resolution, CancellationToken CancellationToken)
    {
        var Entities = Resolution.Entities;
        if (Entities.Any(Entity => Entity.IsUnavailable)) { return new("That device is unavailable.", "unavailable"); }
        if (Intent.Kind is DirectIntentKind.QueryState or DirectIntentKind.QueryTemperature)
        {
            var Entity = Entities.Single();
            var Value = Entity.State.GetProperty("state").GetString();
            if (Entity.Domain == "fan")
            {
                var Speed = Entity.FanSpeedPercent is { } Percent ? $" at {Percent:0.##} percent speed" : "; its speed is not reported";
                return new($"{Entity.Name} is {Value}{Speed}.", "succeeded");
            }
            var Unit = Entity.State.GetProperty("attributes").TryGetProperty("unit_of_measurement", out var UnitValue) ? UnitValue.GetString() : null;
            InteractionFeedback.Emit("context.selected", new ContextSelection("entity-" + Entity.EntityId, "entity_state", "home_assistant", Entity.Name,
                System.Text.Json.JsonSerializer.Serialize(new { state = Value, unit = Unit }), new { entityId = Entity.EntityId }, 0));
            Unit = Unit switch { "°F" => "degrees Fahrenheit", "°C" => "degrees Celsius", _ => Unit };
            return new($"{Entity.Name} is {Value}{(Unit is null ? "" : " " + Unit)}.", "succeeded");
        }

        if (Intent.Kind == DirectIntentKind.SetBrightness && Entities.Any(Entity => !Entity.SupportsBrightness))
        {
            return new("One of those lights does not support brightness control.", "unsupported");
        }

        var Action = Intent.Kind switch
        {
            DirectIntentKind.SetFanSpeed => HomeAssistantAction.SetFanSpeed,
            DirectIntentKind.TurnOn => HomeAssistantAction.TurnOn,
            DirectIntentKind.TurnOff => HomeAssistantAction.TurnOff,
            DirectIntentKind.SetBrightness => HomeAssistantAction.SetBrightness,
            _ => throw new InvalidOperationException("Unsupported direct intent.")
        };
        if (Intent.Kind == DirectIntentKind.SetFanSpeed && Entities.Any(Entity => !Entity.SupportsFanSpeed))
        {
            return new("That fan does not support percentage speed control.", "unsupported");
        }
        await HomeAssistant.ControlAsync(new(Action, Entities.Select(Entity => Entity.EntityId).ToArray(), Intent.BrightnessPercent, Intent.SpeedPercent), CancellationToken);
        if (Intent.Kind == DirectIntentKind.SetFanSpeed)
        {
            var Confirmed = Cache?.Snapshot().Entities.Where(Entity => Entities.Any(Target => Target.EntityId == Entity.EntityId)).ToArray();
            return new(Confirmed is { Length: > 0 } && Confirmed.All(Entity => Entity.FanSpeedPercent is not null)
                ? string.Join(" ", Confirmed.Select(Entity => $"{Entity.Name} is set to {Entity.FanSpeedPercent:0.##} percent speed."))
                : "Fan speed command completed.", "succeeded");
        }
        return new(Intent.Kind == DirectIntentKind.SetBrightness ? $"Set to {Intent.BrightnessPercent} percent." : "Done.", "succeeded");
    }
}
