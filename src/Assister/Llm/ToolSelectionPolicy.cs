using Assister.Tools;

namespace Assister.Llm;

public sealed record ToolSelectionContext(string Message, bool CalendarRequest, bool Memory, WeatherRequestKind Weather);

// Contributors choose relevance only. The broker continues to enforce authorization and schemas.
public interface IToolSelectionPolicy
{
    bool Includes(string Name, ToolSelectionContext Context);
}

public sealed class BuiltInToolSelectionPolicy : IToolSelectionPolicy
{
    public bool Includes(string Name, ToolSelectionContext Context) => Name.StartsWith("ha_", StringComparison.Ordinal)
            || Name.StartsWith("calendar_", StringComparison.Ordinal) && Context.CalendarRequest
            || Name is "satellite_set_volume" or "satellite_announce"
            || Name == "chat_history" || (Name.StartsWith("reminder_", StringComparison.Ordinal) && !Context.CalendarRequest && ReminderTool.Relevant(Context.Message))
            || Name == "memory_search" && Context.Memory
            || Name == "memory_store" && MemoryAuthorization.CanStore(Context.Message)
            || Name == "memory_delete" && MemoryAuthorization.CanDelete(Context.Message)
            || Name == "weather_forecast" && Context.Weather == WeatherRequestKind.Forecast;
}
