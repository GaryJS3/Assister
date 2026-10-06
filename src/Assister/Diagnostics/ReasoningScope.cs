using System.Text.Json;
using Assister.Contracts;

namespace Assister.Diagnostics;

public sealed class ReasoningScope(RunTracing.TraceStep Step, DiagnosticSanitizer Sanitizer, int ModelRound, CancellationToken Token = default) : IDisposable
{
    private readonly System.Text.StringBuilder Pending = new();
    private int Remaining = 32768;
    private bool Started, Finished, Truncated;
    public void Delta(string Text)
    {
        if (Finished || Text.Length == 0)
            return;
        if (!Started)
        {
            Started = true;
            InteractionFeedback.Emit("reasoning.started", new
            {
                stepId = Step.Id,
                modelRound = ModelRound
            });
        }
        var Count = Math.Min(Remaining, Text.Length);
        Pending.Append(Text.AsSpan(0, Count));
        Remaining -= Count;
        if (Count < Text.Length)
        {
            Truncated = true;
            // Drop an unconfirmed trailing fragment at the source limit rather than expose a partial credential.
            Pending.Length = Sanitizer.StreamPrefixLength(Pending.ToString());
        }
        var Value = Pending.ToString();
        var End = Sanitizer.StreamPrefixLength(Value) - 1;
        if (End >= 0)
        {
            Publish(Value[..(End + 1)]);
            Pending.Remove(0, End + 1);
        }
    }
    private void Publish(string Text)
    {
        var Payload = Step.ReasoningPayload(Text);
        var Value = Payload.Value is { ValueKind: JsonValueKind.String } Safe ? Safe.GetString() : "";
        Truncated |= Payload.Truncated;
        if (!string.IsNullOrEmpty(Value))
            InteractionFeedback.Emit("reasoning.delta", new
            {
                stepId = Step.Id,
                modelRound = ModelRound,
                text = Value,
                truncated = Truncated
            });
    }
    public void Complete(string Status)
    {
        if (Finished)
            return;
        Finished = true;
        if (!Started)
            return;
        Publish(Pending.ToString());
        InteractionFeedback.Emit("reasoning.completed", new
        {
            stepId = Step.Id,
            modelRound = ModelRound,
            status = Status,
            truncated = Truncated
        });
    }
    public void Dispose() => Complete(Token.IsCancellationRequested ? "cancelled" : "failed");
}