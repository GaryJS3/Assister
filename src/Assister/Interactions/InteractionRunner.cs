using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;
using Assister.Voice;

namespace Assister.Interactions;

public sealed class InteractionRunner(InteractionStore Store, ClientSignals Signals, IServiceScopeFactory Scopes,
    ILogger<InteractionRunner> Logger, ToneCatalog Tones, IConfiguration Configuration) : BackgroundService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> Active = new();
    private readonly SemaphoreSlim[] Conversations = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
    private void Cue(Guid Id, string Name, string Placement = "immediate")
    {
        if (Tones.Enabled && Store.Get(Id) is { CancelRequested: false, Status: not ("completed" or "failed" or "cancelled") })
            Store.Append(Id, "tone.play", new ToneCue(Name, $"/api/voice/tones/{Name}.wav", DateTimeOffset.UtcNow.AddSeconds(5), Placement));
    }
    private async Task WarnAsync(Guid Id, CancellationToken Token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Configuration.GetValue("Voice:Tones:IssueAfterSeconds", 8), 1, 120)), Token);
            Token.ThrowIfCancellationRequested();
            Cue(Id, "issue");
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
    }
    public void Cancel(Guid Id)
    {
        Store.Cancel(Id);
        Signals.CancelInteraction(Id);
        if (Active.TryGetValue(Id, out var Source))
        {
            try { Source.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        Store.Recover();
        var Tasks = new List<Task>();
        try
        {
            while (!StoppingToken.IsCancellationRequested)
            {
                Tasks.RemoveAll(Task => Task.IsCompleted);
                foreach (var Id in Store.Pending())
                {
                    if (Tasks.Count >= 4) break;
                    var Source = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
                    if (!Active.TryAdd(Id, Source)) { Source.Dispose(); continue; }
                    Tasks.Add(RunAsync(Id, Source));
                }
                await Task.Delay(50, StoppingToken);
            }
        }
        catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(Tasks); }
    }
    private async Task RunAsync(Guid Id, CancellationTokenSource Source)
    {
        SemaphoreSlim? ConversationGate = null;
        var Acquired = false;
        try
        {
            var Item = Store.Get(Id)!;
            if (Item.CancelRequested) Source.Cancel();
            Source.Token.ThrowIfCancellationRequested();
            ConversationGate = Conversations[(uint)Item.ConversationId.GetHashCode() % (uint)Conversations.Length];
            await ConversationGate.WaitAsync(Source.Token);
            Acquired = true;
            await using var Scope = Scopes.CreateAsyncScope();
            var Database = Scope.ServiceProvider.GetRequiredService<AssisterDbContext>();
            // Rich-client conversations have a server-created routing identity; callers cannot impersonate satellites.
            var Satellite = "client-conversation-" + Item.ConversationId.ToString("N");
            if (!await Database.Conversations.AnyAsync(Row => Row.Id == Item.ConversationId, Source.Token))
            {
                Database.Conversations.Add(new() { Id = Item.ConversationId, SatelliteId = Satellite, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                await Database.SaveChangesAsync(Source.Token);
            }
            using var Run = RunTracing.BeginRun(Scope.ServiceProvider.GetRequiredService<RunStore>(), "rich-client", Satellite,
                Conversation: Item.ConversationId, Text: Item.Input);
            Store.Append(Id, "interaction.started", new { runId = RunTracing.RunId }, "running", RunId: RunTracing.RunId);
            var Options = Store.Options(Id);
            using var ToneFeedback = VoiceFeedback.Begin((Name, Token) =>
            {
                Token.ThrowIfCancellationRequested();
                Cue(Id, Name);
                return Task.CompletedTask;
            });
            var Input = Item.Input;
            if (Store.InputAudio(Id) is { } Audio)
            {
                Input = await Scope.ServiceProvider.GetRequiredService<RichSpeech>().TranscribeAsync(Id, Audio, Source.Token);
                RunTracing.Transcript(Input);
            }
            Cue(Id, "confirmed");
            var Created = Store.Events(Id, 0)[0].Data;
            var ClientId = Created.TryGetProperty("clientId", out var Client) && Client.ValueKind == System.Text.Json.JsonValueKind.String ? Client.GetString() : null;
            Store.SelectContext(Id, new("user-request", "user_input", "user", "User request", Input, new { conversationId = Item.ConversationId, suppliedByClientId = ClientId, audioAttachmentId = Options.AudioAttachmentId }, 0));
            using var Feedback = InteractionFeedback.Observe((Type, Data) =>
            {
                if (Data is ContextSelection Selection) Store.SelectContext(Id, Selection);
                else Store.Append(Id, Type, Data);
            });
            var Coordinator = (IStreamingRequestCoordinator)Scope.ServiceProvider.GetRequiredService<IRequestCoordinator>();
            var Started = false;
            Task<RequestResult> Process(UserRequest Request, Func<string, CancellationToken, Task> OnText)
                => Coordinator is Assister.Conversations.ConversationCoordinator Conversations
                    ? Conversations.ProcessClientStreamingAsync(Request, OnText, Source.Token)
                    : Coordinator.ProcessStreamingAsync(Request, OnText, Source.Token);
            using var WarningLifetime = CancellationTokenSource.CreateLinkedTokenSource(Source.Token);
            var Warning = WarnAsync(Id, WarningLifetime.Token);
            RequestResult Result;
            try
            {
                Result = await Process(new(Input, Satellite, ConversationId: Item.ConversationId, Documents: Store.Documents(Id)), (Text, Token) =>
                {
                    Token.ThrowIfCancellationRequested();
                    if (!Started) { Cue(Id, "done"); Store.Append(Id, "response.started", new { }, "responding"); Started = true; }
                    Store.Append(Id, "response.delta", new { text = Text });
                    return Task.CompletedTask;
                });
            }
            finally { WarningLifetime.Cancel(); await Warning; }
            Source.Token.ThrowIfCancellationRequested();
            if (!Started) Cue(Id, "done");
            // The complete authoritative text also corrects any provisional stream on fallback/error paths.
            Store.Append(Id, "response.completed", new { text = Result.Response, spokenText = Result.SpokenResponse }, Response: Result.Response);
            var Failed = Result.Outcome is "failed" or "unavailable" or "invalid-request" or "unmatched";
            if (Failed) Cue(Id, Result.Outcome == "failed" ? "error" : "issue");
            if (Options.Speak) await Scope.ServiceProvider.GetRequiredService<RichSpeech>().SynthesizeAsync(Id,
                Result.SpokenResponse ?? Assister.Voice.VoiceFormatter.Format(Result.Response), Source.Token);
            Source.Token.ThrowIfCancellationRequested();
            Cue(Id, "goodbye", Options.Speak && Store.Audio(Id) is not null ? "after-response-audio" : "immediate");
            Store.Append(Id, Failed ? "interaction.failed" : "interaction.completed",
                new { code = Failed ? Result.Outcome.Replace('-', '_') : null, message = Failed ? Result.Response : null,
                    recoverable = Failed, outcome = Result.Outcome, handledBy = Result.HandledBy }, Failed ? "failed" : "completed");
            Run.Complete(Result.Outcome);
        }
        catch (OperationCanceledException) when (Source.IsCancellationRequested)
        {
            Signals.CancelInteraction(Id);
            Store.Append(Id, "interaction.cancelled", new { }, "cancelled");
        }
        catch (RichSpeechException Error)
        {
            Cue(Id, "error");
            Store.Append(Id, "interaction.failed", new ProtocolError(Error.Message, "Speech input could not be processed.", true), "failed");
        }
        catch (Exception Error)
        {
            Cue(Id, "error");
            Logger.LogWarning("Rich interaction {InteractionId} failed ({FailureType}).", Id, Error.GetType().Name);
            Store.Append(Id, "interaction.failed", new ProtocolError("execution_failed", "The request could not be completed.", true), "failed");
        }
        finally { Signals.CancelInteraction(Id); if (Acquired) ConversationGate!.Release(); Active.TryRemove(Id, out _); Source.Dispose(); }
    }
}
