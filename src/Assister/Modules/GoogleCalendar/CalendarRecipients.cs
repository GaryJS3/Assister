using Assister.Interactions;

namespace Assister.Modules.GoogleCalendar;

public sealed record CalendarClientOption(string ClientId, string Owner, string Name);

public sealed class CalendarRecipients(IConfiguration Configuration, ClientSignals? Signals = null)
{
    public bool Available(CalendarRichRecipient Target) => !string.IsNullOrWhiteSpace(Target.ClientId)
        && !string.IsNullOrWhiteSpace(Configuration[$"RichClients:Clients:{Target.ClientId}:Token"])
        && (Configuration[$"RichClients:Clients:{Target.ClientId}:Owner"] ?? Target.ClientId) == Target.Owner;

    public CalendarClientOption[] Clients() => Configuration.GetSection("RichClients:Clients").GetChildren()
        .Where(C => !string.IsNullOrWhiteSpace(C["Token"]))
        .Select(C => new CalendarClientOption(C.Key, C["Owner"] ?? C.Key,
            Signals?.Clients(C["Owner"] ?? C.Key).FirstOrDefault(R => R.ClientId == C.Key)?.DeviceName ?? C.Key))
        .OrderBy(C => C.Owner, StringComparer.Ordinal).ThenBy(C => C.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
