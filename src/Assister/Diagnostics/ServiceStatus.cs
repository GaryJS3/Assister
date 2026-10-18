namespace Assister.Diagnostics;

public static class ServiceStatus
{
    public static string Overall(bool DatabaseHealthy, string HomeAssistant, bool CacheStale, IEnumerable<ComponentStatus> Components)
        => !DatabaseHealthy ? "Unavailable"
            : HomeAssistant == "Connected" && !CacheStale && Components.All(Component => Component.Status == "Healthy")
                ? "Healthy" : "Degraded";
}
