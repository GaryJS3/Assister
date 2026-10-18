namespace Assister.Interactions;

public sealed class InteractionRetentionService(InteractionStore Store, ILogger<InteractionRetentionService> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken Token)
    {
        while (!Token.IsCancellationRequested)
        {
            try { Store.Prune(); }
            catch (Exception Error) { Logger.LogWarning(Error, "Rich-client history cleanup failed; cleanup will be retried."); }
            await Task.Delay(TimeSpan.FromMinutes(5), Token);
        }
    }
}
