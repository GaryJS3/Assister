namespace Assister.Intents;

// Execution is separate from catalog metadata. A future integration supplies both.
public interface IIntegrationIntentExecutor
{
    string IntegrationId { get; }
    Task<IntentResult> ExecuteAsync(IntentCandidate Candidate, EntityResolutionResult? Resolution, CancellationToken Token);
}

public sealed class HomeAssistantIntentExecutor(DirectIntentHandler Handler) : IIntegrationIntentExecutor
{
    public string IntegrationId => "home-assistant";
    public Task<IntentResult> ExecuteAsync(IntentCandidate Candidate, EntityResolutionResult? Resolution, CancellationToken Token)
    {
        if (Candidate.Intent is null || Resolution is null) { throw new InvalidOperationException("Home Assistant requires a resolved device action."); }
        return Handler.ExecuteAsync(Candidate.Intent, Resolution, Token);
    }
}

public sealed class AssisterIntentExecutor : IIntegrationIntentExecutor
{
    public string IntegrationId => "assister";
    public Task<IntentResult> ExecuteAsync(IntentCandidate Candidate, EntityResolutionResult? Resolution, CancellationToken Token)
    {
        if (Candidate.Definition.ActionId is not ("assister.reply" or "assister.time" or "assister.date"))
        {
            throw new InvalidOperationException("This local action has no executor.");
        }
        return Task.FromResult(new IntentResult("", "succeeded"));
    }
}

public sealed class IntegrationActionDispatcher(IntentActionRegistry Registry, IEnumerable<IIntegrationIntentExecutor> Executors)
{
    private readonly Dictionary<string, IIntegrationIntentExecutor> ByIntegration = Executors.ToDictionary(Executor => Executor.IntegrationId, StringComparer.Ordinal);
    public bool CanExecute(string ActionId) => ByIntegration.ContainsKey(Registry.Get(ActionId).IntegrationId);
    public Task<IntentResult> ExecuteAsync(IntentCandidate Candidate, EntityResolutionResult? Resolution, CancellationToken Token)
    {
        var Action = Registry.Get(Candidate.Definition.ActionId);
        if (Action.DeviceIntent is { } Kind && Candidate.Intent?.Kind != Kind)
        {
            throw new InvalidOperationException("The resolved intent does not belong to this action.");
        }
        if (!ByIntegration.TryGetValue(Action.IntegrationId, out var Executor)) { throw new InvalidOperationException("The integration has no registered intent executor."); }
        return Executor.ExecuteAsync(Candidate, Resolution, Token);
    }
}
