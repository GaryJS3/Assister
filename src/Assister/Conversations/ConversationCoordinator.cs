using Assister.Contracts;
using Assister.Persistence;
using Assister.Voice;
using Assister.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Assister.Tools;
using System.Text.Json;

namespace Assister.Conversations;

// Fixed stripes bound lock memory while serializing same-satellite requests, including concurrent follow-ups.
public sealed class ConversationLocks
{
    private readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
    public SemaphoreSlim For(string Satellite) => Gates[(uint)StringComparer.Ordinal.GetHashCode(Satellite) % (uint)Gates.Length];
}

public sealed class ConversationCoordinator(AssisterDbContext Database, RequestCoordinator Coordinator,
    ConversationLocks Locks, RunStore? Diagnostics = null) : IStreamingRequestCoordinator
{
    public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken)
        => ProcessCoreAsync(Request, null, CancellationToken);

    public Task<RequestResult> ProcessStreamingAsync(UserRequest Request, Func<string, CancellationToken, Task> OnText, CancellationToken CancellationToken)
        => ProcessCoreAsync(Request, OnText, CancellationToken);

    public Task<RequestResult> ProcessClientStreamingAsync(UserRequest Request, Func<string, CancellationToken, Task> OnText, CancellationToken CancellationToken)
        => ProcessCoreAsync(Request, OnText, CancellationToken, AuthoritativeText: true);

    private async Task<RequestResult> ProcessCoreAsync(UserRequest Request, Func<string, CancellationToken, Task>? OnText, CancellationToken CancellationToken, bool AuthoritativeText = false)
    {
        using var Run = RunTracing.EnsureRun(Diagnostics, "text", Request.SatelliteId, Request.Area, Request.ConversationId, Request.Message);
        if (string.IsNullOrWhiteSpace(Request.Message) || Request.Message.Length > 1000 || string.IsNullOrWhiteSpace(Request.SatelliteId)
            || Request.SatelliteId.Length > 128 || Request.Area?.Length > 128 || !RequestInputLimits.ValidDocuments(Request))
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
            var Emitted = false;
            DeviceConversationContext DeviceContext;
            try { DeviceContext = JsonSerializer.Deserialize<DeviceConversationContext>(Conversation.DeviceContextJson) ?? new(); }
            catch (JsonException) { DeviceContext = new(); }
            DeviceContext.Expire();
            async Task Deliver(string Text, CancellationToken Token)
            {
                Emitted = true;
                await OnText!(Text, Token);
            }
            var Result = await Coordinator.ProcessWithHistoryAsync(Request with { ConversationId = Conversation.Id }, [], CancellationToken,
                OnText is null ? null : Deliver, DeviceContext);
            if (OnText is not null && !Emitted) { await OnText(AuthoritativeText ? Result.Response : Result.SpokenResponse ?? VoiceFormatter.Format(Result.Response), CancellationToken); }
            Conversation.UpdatedAt = Now;
            Conversation.DeviceContextJson = JsonSerializer.Serialize(DeviceContext);
            Database.ConversationTurns.Add(new() { ConversationId = Conversation.Id, UserText = Request.Message,
                AssistantText = Result.Response[..Math.Min(Result.Response.Length, 4000)], Outcome = Result.Outcome, TraceId = Result.TraceId });
            await Database.SaveChangesAsync(CancellationToken);
            return Result;
        }
        finally { Gate.Release(); }
    }
}
