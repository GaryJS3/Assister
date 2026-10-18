using Assister.Diagnostics;

namespace Assister.Tests;

public sealed class ServiceStatusTests
{
    [Theory]
    [InlineData(true, "Connected", false, "Healthy", "Healthy")]
    [InlineData(false, "Connected", false, "Healthy", "Unavailable")]
    [InlineData(true, "Disconnected", false, "Healthy", "Degraded")]
    [InlineData(true, "NotConfigured", true, "NotConfigured", "Degraded")]
    [InlineData(true, "Connected", true, "Healthy", "Degraded")]
    [InlineData(true, "Connected", false, "Checking", "Degraded")]
    [InlineData(true, "Connected", false, "Unavailable", "Degraded")]
    public void OverallReflectsDatabaseConnectivityCacheAndDependencies(bool DatabaseHealthy, string HomeAssistant,
        bool CacheStale, string Dependency, string Expected)
        => Assert.Equal(Expected, ServiceStatus.Overall(DatabaseHealthy, HomeAssistant, CacheStale, [new("LLM", Dependency, "test")]));
}
