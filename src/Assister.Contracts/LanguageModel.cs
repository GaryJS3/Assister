using System.Text.Json;

namespace Assister.Contracts;

public sealed record LlmFunction(string Name, string Arguments);
public sealed record LlmToolCall(string Id, LlmFunction Function, string Type = "function");
public sealed record LlmMessage(string Role, string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null, string? ToolCallId = null);
public sealed record LlmToolFunction(string Name, string Description, JsonElement Parameters);
public sealed record LlmTool(LlmToolFunction Function, string Type = "function");
public sealed record LlmRequest(IReadOnlyList<LlmMessage> Messages, IReadOnlyList<LlmTool>? Tools = null,
    string ToolChoice = "auto");
public sealed record LlmResponse(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, string FinishReason);
// Text deltas may be consumed immediately; tool calls are only published in the completed response.
public sealed record LlmStreamEvent(string? TextDelta = null, LlmResponse? Completed = null);

public interface ILanguageModel
{
    Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken CancellationToken);
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request, CancellationToken CancellationToken);
}
