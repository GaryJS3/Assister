using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assister.Interactions;

public sealed class InteractionRunner(InteractionStore Store, ClientSignals Signals, IServiceScopeFactory Scopes,
    ILogger<InteractionRunner> Logger) : BackgroundService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> Active = new();
    private readonly SemaphoreSlim[] Conversations = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
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
            var Input = Item.Input;
            if (Store.InputAudio(Id) is { } Audio)
            {
                Input = await Scope.ServiceProvider.GetRequiredService<RichSpeech>().TranscribeAsync(Id, Audio, Source.Token);
                RunTracing.Transcript(Input);
            }
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
            var Result = await Process(new(Input, Satellite, ConversationId: Item.ConversationId, Documents: Store.Documents(Id)), (Text, Token) =>
            {
                Token.ThrowIfCancellationRequested();
                if (!Started) { Store.Append(Id, "response.started", new { }, "responding"); Started = true; }
                Store.Append(Id, "response.delta", new { text = Text });
                return Task.CompletedTask;
            });
            Source.Token.ThrowIfCancellationRequested();
            // The complete authoritative text also corrects any provisional stream on fallback/error paths.
            Store.Append(Id, "response.completed", new { text = Result.Response, spokenText = Result.SpokenResponse }, Response: Result.Response);
            var Failed = Result.Outcome is "failed" or "unavailable" or "invalid-request" or "unmatched";
            if (Options.Speak) await Scope.ServiceProvider.GetRequiredService<RichSpeech>().SynthesizeAsync(Id,
                Result.SpokenResponse ?? Assister.Voice.VoiceFormatter.Format(Result.Response), Source.Token);
            Source.Token.ThrowIfCancellationRequested();
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
            Store.Append(Id, "interaction.failed", new ProtocolError(Error.Message, "Speech input could not be processed.", true), "failed");
        }
        catch (Exception Error)
        {
            Logger.LogWarning("Rich interaction {InteractionId} failed ({FailureType}).", Id, Error.GetType().Name);
            Store.Append(Id, "interaction.failed", new ProtocolError("execution_failed", "The request could not be completed.", true), "failed");
        }
        finally { Signals.CancelInteraction(Id); if (Acquired) ConversationGate!.Release(); Active.TryRemove(Id, out _); Source.Dispose(); }
    }
}
