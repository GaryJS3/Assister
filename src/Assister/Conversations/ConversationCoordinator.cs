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
    ConversationLocks Locks, RunStore? Diagnostics = null, TimeProvider? Clock = null) : IStreamingRequestCoordinator
{
    private DateTimeOffset UtcNow => (Clock ?? TimeProvider.System).GetUtcNow();
    public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken CancellationToken)
        => ProcessCoreAsync(Request, null, CancellationToken);

    public Task<RequestResult> ProcessStreamingAsync(UserRequest Request, Func<string, CancellationToken, Task> OnText, CancellationToken CancellationToken)
        => ProcessCoreAsync(Request, OnText, CancellationToken);

    public Task<RequestResult> ProcessClientStreamingAsync(UserRequest Request, Func<string, CancellationToken, Task> OnText, CancellationToken CancellationToken)
        => ProcessCoreAsync(Request, OnText, CancellationToken, AuthoritativeText: true);

    private async Task<RequestResult> ProcessCoreAsync(UserRequest Request, Func<string, CancellationToken, Task>? OnText, CancellationToken CancellationToken, bool AuthoritativeText = false)
    {
        var Arrived = UtcNow;
        var ArrivedAt = Arrived.ToUnixTimeSeconds();
        var SourceDevice = Request.DeviceId ?? Request.SatelliteId;
        bool CanContinue(Conversation Candidate)
        {
            try
            {
                var Context = JsonSerializer.Deserialize<DeviceConversationContext>(Candidate.DeviceContextJson);
                var Finished = Context?.ResponseFinishedAt ?? DateTimeOffset.FromUnixTimeSeconds(Candidate.UpdatedAt);
                return (Context?.SourceDeviceId ?? Candidate.SatelliteId) == SourceDevice
                    && Arrived >= Finished && Arrived - Finished <= TimeSpan.FromSeconds(30);
            }
            catch (JsonException) { return false; }
        }
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
            var Now = ArrivedAt;
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
                if (Request.NewConversation || !CanContinue(Conversation)) Conversation = null;
            }
            else
            {
                Conversation = null;
            }
            if (Conversation is null && !Request.NewConversation)
            {
                var Candidates = await Database.Conversations.Where(Row => Row.SatelliteId == Request.SatelliteId && Row.UpdatedAt >= Now - 31)
                    .OrderByDescending(Row => Row.UpdatedAt).Take(32).ToListAsync(CancellationToken);
                Conversation = Candidates.FirstOrDefault(CanContinue);
            }
            Conversation ??= new() { Id = Guid.NewGuid(), SatelliteId = Request.SatelliteId };
            if (Database.Entry(Conversation).State == EntityState.Detached) { Database.Conversations.Add(Conversation); }
            var Turns = await Database.ConversationTurns.Where(Row => Row.ConversationId == Conversation.Id)
                .OrderByDescending(Row => Row.Id).Take(12).ToListAsync(CancellationToken);
            var History = new List<LlmMessage>();
            var Budget = 12000;
            foreach (var Turn in Turns)
            {
                var Size = Turn.UserText.Length + Turn.AssistantText.Length;
                if (Size > Budget) break;
                Budget -= Size;
                History.Insert(0, new("assistant", Turn.AssistantText));
                History.Insert(0, new("user", Turn.UserText));
            }
            var Emitted = false;
            DeviceConversationContext DeviceContext;
            try { DeviceContext = JsonSerializer.Deserialize<DeviceConversationContext>(Conversation.DeviceContextJson) ?? new(); }
            catch (JsonException) { DeviceContext = new(); }
            DeviceContext.Expire();
            DeviceContext.SourceDeviceId = SourceDevice;
            async Task Deliver(string Text, CancellationToken Token)
            {
                Emitted = true;
                await OnText!(Text, Token);
            }
            var Result = await Coordinator.ProcessWithHistoryAsync(Request with { ConversationId = Conversation.Id }, History, CancellationToken,
                OnText is null ? null : Deliver, DeviceContext);
            if (OnText is not null && !Emitted) { await OnText(AuthoritativeText ? Result.Response : Result.SpokenResponse ?? VoiceFormatter.Format(Result.Response), CancellationToken); }
            DeviceContext.ResponseFinishedAt = UtcNow;
            Conversation.UpdatedAt = DeviceContext.ResponseFinishedAt.Value.ToUnixTimeSeconds();
            Conversation.DeviceContextJson = JsonSerializer.Serialize(DeviceContext);
            Database.ConversationTurns.Add(new() { ConversationId = Conversation.Id, UserText = Request.Message,
                AssistantText = Result.Response[..Math.Min(Result.Response.Length, 4000)], Outcome = Result.Outcome, TraceId = Result.TraceId });
            await Database.SaveChangesAsync(CancellationToken);
            return Result;
        }
        finally { Gate.Release(); }
    }

    public async Task MarkResponseFinishedAsync(Guid ConversationId, CancellationToken Token, string? ExpectedDeviceId = null)
    {
        var Conversation = await Database.Conversations.FindAsync([ConversationId], Token);
        if (Conversation is null) return;
        var Context = JsonSerializer.Deserialize<DeviceConversationContext>(Conversation.DeviceContextJson) ?? new();
        if (ExpectedDeviceId is not null && Context.SourceDeviceId != ExpectedDeviceId) return;
        Context.ResponseFinishedAt = UtcNow;
        Conversation.UpdatedAt = UtcNow.ToUnixTimeSeconds();
        Conversation.DeviceContextJson = JsonSerializer.Serialize(Context);
        await Database.SaveChangesAsync(Token);
    }
}
