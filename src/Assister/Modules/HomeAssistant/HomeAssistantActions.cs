using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Assister.Modules.HomeAssistant;

public enum HomeAssistantAction
{
    TurnOn,
    TurnOff,
    SetBrightness
}

public sealed record HomeAssistantControl(HomeAssistantAction Action, IReadOnlyList<string> EntityIds, int? BrightnessPercent = null);

public interface IHomeAssistantClient
{
    Task ControlAsync(HomeAssistantControl Control, CancellationToken CancellationToken);
}

public sealed class HomeAssistantActionClient(HttpClient Http, IConfiguration Configuration,
    HomeAssistantClient Connection, HomeAssistantStateCache Cache) : IHomeAssistantClient
{
    public async Task ControlAsync(HomeAssistantControl Control, CancellationToken CancellationToken)
    {
        var Snapshot = Cache.Snapshot();
        if (Connection.Status != "Connected" || Snapshot.IsStale)
        {
            throw new InvalidOperationException("Home Assistant is unavailable; no command was sent.");
        }

        if (Control.EntityIds.Count is < 1 or > 64 || Control.EntityIds.Distinct().Count() != Control.EntityIds.Count)
        {
            throw new InvalidOperationException("The control target is invalid.");
        }

        var Targets = Control.EntityIds.Select(Id => Snapshot.Entities.SingleOrDefault(Entity => Entity.EntityId == Id)
            ?? throw new InvalidOperationException("The control target no longer exists.")).ToArray();
        var Domain = Targets[0].Domain;
        if (Domain is not ("light" or "switch") || Targets.Any(Entity => Entity.Domain != Domain || Entity.IsUnavailable))
        {
            throw new InvalidOperationException("The control target is unsupported or unavailable.");
        }

        var Service = Control.Action switch
        {
            HomeAssistantAction.TurnOn when Control.BrightnessPercent is null => "turn_on",
            HomeAssistantAction.TurnOff when Control.BrightnessPercent is null => "turn_off",
            HomeAssistantAction.SetBrightness when Domain == "light" && Control.BrightnessPercent is >= 0 and <= 100
                && Targets.All(Entity => Entity.SupportsBrightness) => Control.BrightnessPercent == 0 ? "turn_off" : "turn_on",
            _ => throw new InvalidOperationException("The requested control is unsupported.")
        };
        if (Snapshot.Services.ValueKind != JsonValueKind.Object || !Snapshot.Services.TryGetProperty(Domain, out var Services)
            || !Services.TryGetProperty(Service, out _))
        {
            throw new InvalidOperationException("Home Assistant does not advertise this action.");
        }

        var Data = new Dictionary<string, object> { ["entity_id"] = Control.EntityIds };
        if (Control.Action == HomeAssistantAction.SetBrightness && Control.BrightnessPercent > 0)
        {
            Data["brightness_pct"] = Control.BrightnessPercent.Value;
        }

        using var Request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(Configuration["HomeAssistant:Url"]!), $"api/services/{Domain}/{Service}"));
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration["HomeAssistant:Token"]);
        Request.Content = new StringContent(JsonSerializer.Serialize(Data), Encoding.UTF8, "application/json");
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        Timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // State-changing calls are never retried: a lost response does not prove the action failed.
        using var Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, Timeout.Token);
        if (!Response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Home Assistant rejected the command.");
        }
    }
}
