namespace Assister.Voice;

public sealed class SmartTurnWarmup(IConfiguration Configuration, ITurnCompletionClassifier Classifier,
    ILogger<SmartTurnWarmup> Logger) : IHostedService
{
    public async Task StartAsync(CancellationToken Token)
    {
        if (!Configuration.GetValue("Voice:Endpointing:Enabled", false)) return;
        try
        {
            await Classifier.ScoreAsync(ReadOnlyMemory<short>.Empty, Token);
            Logger.LogInformation("Semantic endpoint model ready; shadow mode: {Shadow}.", Configuration.GetValue("Voice:Endpointing:Shadow", true));
        }
        catch (Exception Error) when (!Token.IsCancellationRequested && Error is not OutOfMemoryException)
        { Logger.LogWarning("Semantic endpoint model unavailable ({FailureType}); capture will use its conservative fallback.", Error.GetType().Name); }
    }
    public Task StopAsync(CancellationToken Token) => Task.CompletedTask;
}
