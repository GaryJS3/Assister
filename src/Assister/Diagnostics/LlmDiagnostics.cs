using Assister.Contracts;

namespace Assister.Diagnostics;

public static class LlmDiagnostics
{
    public static RunTracing.TraceStep Start(LlmRequest Request, IConfiguration Configuration, string Name)
    {
        var Step = RunTracing.Start("LanguageModel", Name, "Generate a response using the offered tools and conversation messages.");
        Step.Metadata(new { model = Configuration["LanguageModel:Model"],
            temperature = Math.Clamp(Configuration.GetValue("LanguageModel:Temperature", 0.2), 0, 2),
            maximumTokens = Math.Clamp(Configuration.GetValue("LanguageModel:MaxTokens", 500), 1, 4096),
            toolChoice = Request.ToolChoice, toolsOffered = Request.Tools?.Select(Tool => Tool.Function.Name).ToArray() ?? [],
            messageCount = Request.Messages.Count, enableThinking = Configuration.GetValue<bool?>("LanguageModel:EnableThinking") });
        Step.Input(new { messages = Request.Messages.Select(Message => new
        {
            Message.Role, Message.Content, Message.ToolCallId,
            toolCalls = Message.ToolCalls?.Select(Call => new { Call.Id, name = Call.Function.Name, arguments = DiagnosticSanitizer.ParseJson(Call.Function.Arguments) }),
            toolResult = Message.Role == "tool" && Message.Content is not null ? DiagnosticSanitizer.ParseJson(Message.Content) : null
        }) });
        return Step;
    }
    public static void Output(RunTracing.TraceStep Step, LlmResponse Response)
    {
        Step.Output(new { assistantContent = Response.Content, Response.FinishReason,
            toolCalls = Response.ToolCalls.Select(Call => new { toolCallId = Call.Id, name = Call.Function.Name,
                arguments = DiagnosticSanitizer.ParseJson(Call.Function.Arguments) }) });
        Step.Finish(Response.ToolCalls.Count > 0 ? "tool-call" : "succeeded");
    }
}
