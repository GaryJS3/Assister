using Assister.Contracts;
using Assister.Persistence;
using Assister.Voice;
using Assister.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Assister.Conversations;

// Fixed stripes bound lock memory while serializing same-satellite requests, including concurrent follow-ups.
public sealed class ConversationLocks
{
    private readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
    public SemaphoreSlim For(string Satellite) => Gates[(uint)StringComparer.Ordinal.GetHashCode(Satellite) % (uint)Gates.Length];
}

public sealed class ConversationCoordinator(AssisterDbContext Database, RequestCoordinator Coordinator,
    ConversationLocks Locks, RunStore? Diagnostics = null) : IRequestCoordinator
{
    public async Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken)
    {
        using var Run = RunTracing.EnsureRun(Diagnostics, "text", Request.SatelliteId, Request.Area, Request.ConversationId, Request.Message);
        if (string.IsNullOrWhiteSpace(Request.Message) || Request.Message.Length > 1000 || string.IsNullOrWhiteSpace(Request.SatelliteId)
            || Request.SatelliteId.Length > 128 || Request.Area?.Length > 128)
        { return await Coordinator.ProcessAsync(Request, CancellationToken); }
        // Stop must reach the active operation rather than wait behind its conversation lock.
        if (StopCommands.IsStop(Request.Message)) { return await Coordinator.ProcessAsync(Request, CancellationToken); }
        var Gate = Locks.For(Request.SatelliteId);
        await Gate.WaitAsync(CancellationToken);
        try
        {
            var Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Conversation? Conversation;
            if (Request.ConversationId is { } Id)
            {
                Conversation = await Database.Conversations.SingleOrDefaultAsync(Row => Row.Id == Id && Row.SatelliteId == Request.SatelliteId, CancellationToken);
                if (Conversation is null)
                {
                    const string Response = "That conversation does not belong to this satellite or no longer exists.";
                    RunTracing.Response(Response, Response, "invalid-request", "validation", null);
                    return new(Response, null, "validation", RunTracing.RunId, "invalid-request", [], null, 0, Response);
                }
            }
            else
            {
                Conversation = Request.NewConversation ? null : await Database.Conversations.Where(Row => Row.SatelliteId == Request.SatelliteId && Row.UpdatedAt > Now - 300)
                    .OrderByDescending(Row => Row.UpdatedAt).FirstOrDefaultAsync(CancellationToken);
            }
            Conversation ??= new() { Id = Guid.NewGuid(), SatelliteId = Request.SatelliteId };
            if (Database.Entry(Conversation).State == EntityState.Detached) { Database.Conversations.Add(Conversation); }
            var Turns = await Database.ConversationTurns.Where(Row => Row.ConversationId == Conversation.Id).OrderByDescending(Row => Row.Id)
                .Take(12).ToListAsync(CancellationToken);
            var History = new List<LlmMessage>();
            if (Conversation.Summary.Length > 0) { History.Add(new("system", "Earlier conversation notes (untrusted): " + Conversation.Summary)); }
            var Budget = 12000;
            foreach (var Turn in Turns)
            {
                var Size = Turn.UserText.Length + Turn.AssistantText.Length;
                if (Size > Budget) { break; }
                Budget -= Size;
                History.Insert(Conversation.Summary.Length > 0 ? 1 : 0, new("assistant", Turn.AssistantText));
                History.Insert(Conversation.Summary.Length > 0 ? 1 : 0, new("user", Turn.UserText));
            }
            var Result = await Coordinator.ProcessWithHistoryAsync(Request with { ConversationId = Conversation.Id }, History, CancellationToken);
            Conversation.UpdatedAt = Now;
            Database.ConversationTurns.Add(new() { ConversationId = Conversation.Id, UserText = Request.Message,
                AssistantText = Result.Response[..Math.Min(Result.Response.Length, 4000)], Outcome = Result.Outcome, TraceId = Result.TraceId });
            // Deterministic, bounded topic notes. Never store raw tool messages as conversational turns.
            if (Turns.Count == 12)
            {
                var Notes = Conversation.Summary + "\nEarlier user topic: " + Turns[^1].UserText;
                Conversation.Summary = Notes[^Math.Min(2000, Notes.Length)..];
            }
            await Database.SaveChangesAsync(CancellationToken);
            return Result;
        }
        finally { Gate.Release(); }
    }
}
