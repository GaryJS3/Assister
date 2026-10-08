using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Assister.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assister.IntegrationTests;

public sealed class RichClientTests
{
    private const string Token = "local-client-test-credential-123456789";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerToneCuesAreOrderedReplayableAndRespectDisable(bool Enabled)
    {
        using var Base = new TestApplication(); Base.Coordinator.Release.TrySetResult();
        using var App = Base.WithWebHostBuilder(Builder => Builder.ConfigureAppConfiguration((_, Config) =>
            Config.AddInMemoryCollection(new Dictionary<string, string?> { ["Voice:Tones:Enabled"] = Enabled.ToString() })));
        using var Http = App.CreateClient(); Http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        var Client = new Assister.Client.AssisterClient(Http);
        var Conversation = await Client.CreateConversationAsync();
        var Item = await Client.SubmitAsync(Conversation.Id, "Hello", "tone-cues");
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await WaitTerminal(Http, Item.Id, Deadline.Token);
        var Events = (await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{Item.Id}/events"))!;
        var Cues = Events.Where(Event => Event.Type == "tone.play").ToArray();
        if (!Enabled) { Assert.Empty(Cues); return; }
        Assert.Equal(new[] { "confirmed", "ai-think", "ai-thought", "done", "goodbye" },
            Cues.Select(Event => Event.Data.Deserialize<ToneCue>(Json)!.Name));
        Assert.True(Cues.Single(Event => Event.Data.GetProperty("name").GetString() == "done").Sequence < Events.First(Event => Event.Type == "response.delta").Sequence);
        Assert.True(Cues.Last().Sequence < Events.Single(Event => Event.Type == "interaction.completed").Sequence);
        foreach (var Event in Cues)
        {
            var Cue = Event.Data.Deserialize<ToneCue>(Json)!;
            Assert.Equal(Event.Timestamp.AddSeconds(5).ToUnixTimeSeconds(), Cue.ExpiresAt.ToUnixTimeSeconds());
            Assert.Equal("immediate", Cue.Placement);
            using var Audio = await Client.DownloadToneAsync(Cue.Name, Deadline.Token);
            Assert.True(Audio.CanRead);
        }
        var Ws = App.Server.CreateWebSocketClient(); Ws.ConfigureRequest = Request => Request.Headers.Authorization = "Bearer " + Token;
        var Observer = new Assister.Client.AssisterClient(Http, (Uri, Token) => Ws.ConnectAsync(Uri, Token));
        var Replayed = new List<InteractionEvent>();
        await foreach (var Event in Observer.ObserveAsync(Item.Id, Token: Deadline.Token)) Replayed.Add(Event);
        Assert.Equal(Cues.Select(Event => Event.EventId), Replayed.Where(Event => Event.Type == "tone.play").Select(Event => Event.EventId));
    }
    [Fact]
    public async Task SlowInteractionIssuesOneCueAndCancellationStopsFurtherCues()
    {
        using var Base = new TestApplication();
        using var App = Base.WithWebHostBuilder(Builder => Builder.ConfigureAppConfiguration((_, Config) =>
            Config.AddInMemoryCollection(new Dictionary<string, string?> { ["Voice:Tones:IssueAfterSeconds"] = "1" })));
        using var Http = App.CreateClient(); Http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        var Client = new Assister.Client.AssisterClient(Http);
        var Conversation = await Client.CreateConversationAsync();
        var Item = await Client.SubmitAsync(Conversation.Id, "wait", "tone-cancel");
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        InteractionEvent[] Events;
        do
        {
            await Task.Delay(50, Deadline.Token);
            Events = (await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{Item.Id}/events", Deadline.Token))!;
        } while (!Events.Any(Event => Event.Type == "tone.play" && Event.Data.GetProperty("name").GetString() == "issue"));
        await Client.CancelAsync(Item.Id, Deadline.Token);
        await WaitTerminal(Http, Item.Id, Deadline.Token);
        Events = (await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{Item.Id}/events", Deadline.Token))!;
        Assert.Single(Events, Event => Event.Type == "tone.play" && Event.Data.GetProperty("name").GetString() == "issue");
        Assert.DoesNotContain(Events, Event => Event.Type == "tone.play" && Event.Data.GetProperty("name").GetString() is "error" or "goodbye");
        Assert.Equal("interaction.cancelled", Events.Last().Type);
    }
    [Fact]
    public async Task VoiceTranscriptTtsAndPlaybackHaveIndependentDurableStates()
    {
        var Stt = new FakeStt(); var Tts = new FakeTts();
        using var Base = new TestApplication(); Base.Coordinator.Release.TrySetResult();
        using var App = Base.WithWebHostBuilder(Builder => Builder.ConfigureServices(Services =>
        { Services.AddSingleton<ISpeechToTextProvider>(Stt); Services.AddSingleton<ITextToSpeechProvider>(Tts); }));
        using var Http = App.CreateClient(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var Client = new Assister.Client.AssisterClient(Http);
        var Conversation = await Client.CreateConversationAsync();
        var Audio = await Client.UploadAttachmentAsync(new byte[3200], "voice.pcm", "audio/pcm");
        var Request = new SubmitInteraction("Voice input", "voice-key", AudioAttachmentId: Audio.Id, Speak: true);
        var Item = await Client.SubmitAsync(Conversation.Id, Request);
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var Final = await WaitTerminal(Http, Item.Id, Deadline.Token);
        Assert.Equal("completed", Final.Status); Assert.Equal("What time is it?", Final.Input);
        Assert.Equal("What time is it?", Base.Coordinator.LastRequest!.Message);
        var Events = (await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{Item.Id}/events"))!;
        Assert.Contains(Events, Event => Event.Type == "stt.partial"); Assert.Contains(Events, Event => Event.Type == "stt.final");
        Assert.True(Events.Single(Event => Event.Type == "response.completed").Sequence < Events.Single(Event => Event.Type == "tts.started").Sequence);
        Assert.Contains(Events, Event => Event.Type == "tts.completed");
        Assert.Equal("after-response-audio", Events.Single(Event => Event.Type == "tone.play" && Event.Data.GetProperty("name").GetString() == "goodbye").Data.GetProperty("placement").GetString());
        using var Wave = await Client.DownloadAudioAsync(Item.Id); using var Bytes = new MemoryStream(); await Wave.CopyToAsync(Bytes);
        Assert.True(Bytes.ToArray().AsSpan().StartsWith("RIFF"u8));
        Assert.Equal(Item.Id, (await Client.SubmitAsync(Conversation.Id, Request)).Id); // Transcript updates do not break submission idempotency.
        var Playback = Guid.NewGuid();
        var Ws = App.Server.CreateWebSocketClient(); Ws.ConfigureRequest = Request => Request.Headers.Authorization = "Bearer " + Token;
        var Following = new Assister.Client.AssisterClient(Http, (Uri, Token) => Ws.ConnectAsync(Uri, Token));
        await using (var Observer = Following.ObserveAsync(Item.Id, Final.LastSequence, Deadline.Token, FollowPlayback: true).GetAsyncEnumerator())
        {
            var Next = Observer.MoveNextAsync().AsTask();
            await Client.ReportPlaybackAsync(Item.Id, new("started", Playback));
            Assert.True(await Next); Assert.Equal("playback.started", Observer.Current.Type);
        }
        await Client.ReportPlaybackAsync(Item.Id, new("completed", Playback));
        var Sequence = (await Client.GetAsync(Item.Id))!.LastSequence;
        await Client.ReportPlaybackAsync(Item.Id, new("completed", Playback));
        Assert.Equal(Sequence, (await Client.GetAsync(Item.Id))!.LastSequence);
        Assert.Equal("completed", (await Client.GetAsync(Item.Id))!.Status);
        Tts.Fail = true;
        var NoSpeech = await Client.SubmitAsync(Conversation.Id, Request with { IdempotencyKey = "tts-fails" });
        Assert.Equal("completed", (await WaitTerminal(Http, NoSpeech.Id, Deadline.Token)).Status);
        Assert.Equal("Hello world", (await Client.GetAsync(NoSpeech.Id))!.Response);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"/api/client/interactions/{NoSpeech.Id}/audio")).StatusCode);
        Assert.Contains((await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{NoSpeech.Id}/events"))!, Event => Event.Type == "tts.failed");
        Assert.Contains((await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{NoSpeech.Id}/events"))!, Event => Event.Type == "tone.play" && Event.Data.GetProperty("name").GetString() == "issue");
        var Calls = Base.Coordinator.Calls; Stt.Fail = true;
        var NoTranscript = await Client.SubmitAsync(Conversation.Id, Request with { IdempotencyKey = "stt-fails" });
        Assert.Equal("failed", (await WaitTerminal(Http, NoTranscript.Id, Deadline.Token)).Status);
        Assert.Equal(Calls, Base.Coordinator.Calls);
        Assert.Contains((await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{NoTranscript.Id}/events"))!, Event => Event.Type == "tone.play" && Event.Data.GetProperty("name").GetString() == "error");
        Stt.Fail = false; Tts.Fail = false; Tts.Block = true;
        var StopSpeech = await Client.SubmitAsync(Conversation.Id, Request with { IdempotencyKey = "cancel-tts" });
        await Tts.Started.Task.WaitAsync(Deadline.Token); await Client.CancelAsync(StopSpeech.Id);
        Assert.Equal("cancelled", (await WaitTerminal(Http, StopSpeech.Id, Deadline.Token)).Status);
        Assert.Equal("Hello world", (await Client.GetAsync(StopSpeech.Id))!.Response);
    }
    [Fact]
    public async Task DeviceRequestsAreTargetedPermissionAwareAndCancelledOrExpired()
    {
        using var App = new TestApplication(); using var Http = App.CreateClient();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var Client = new Assister.Client.AssisterClient(Http); var Conversation = await Client.CreateConversationAsync();
        var Interaction = await Client.SubmitAsync(Conversation.Id, "wait", "device-interaction");
        using var TabletHttp = App.CreateClient(); TabletHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tablet-client-test-credential-123456789");
        var Tablet = new Assister.Client.AssisterClient(TabletHttp);
        await Tablet.RegisterAsync(new("android_tablet", "Local test tablet", ["location.read"]));
        var Signals = App.Services.GetRequiredService<Assister.Interactions.ClientSignals>();
        var Parameters = JsonSerializer.SerializeToElement(new { });
        var Pending = Signals.RequestAsync(Interaction.Id, "tablet", "location.read", Parameters, TimeSpan.FromSeconds(5), CancellationToken.None);
        var Request = Assert.Single((await Tablet.GetDeviceRequestsAsync())!);
        var Ws = App.Server.CreateWebSocketClient(); Ws.ConfigureRequest = Request => Request.Headers.Authorization = "Bearer tablet-client-test-credential-123456789";
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var Socket = await Ws.ConnectAsync(new Uri("ws://localhost/api/client/device/stream"), Deadline.Token);
        Assert.Equal("connection.ready", (await Receive(Socket, Deadline.Token)).GetProperty("type").GetString());
        var Signal = await Receive(Socket, Deadline.Token);
        Assert.Equal("device.request", Signal.GetProperty("type").GetString());
        Assert.Equal(Request.RequestId, Signal.GetProperty("data").GetProperty("requestId").GetGuid());
        var Native = new Assister.Client.AssisterClient(TabletHttp, (Uri, Token) => Ws.ConnectAsync(Uri, Token));
        await using (var Observer = Native.ObserveDeviceRequestsAsync(Deadline.Token).GetAsyncEnumerator())
        { Assert.True(await Observer.MoveNextAsync()); Assert.Equal(Request.RequestId, Observer.Current.RequestId); }
        Assert.Empty((await Client.GetDeviceRequestsAsync())!);
        var Denied = new DeviceResponse(Request.RequestId, false, Error: new("permission_required", "Location permission is not granted."));
        Assert.Equal(HttpStatusCode.Conflict, (await Http.PostAsJsonAsync("/api/client/device/responses", Denied)).StatusCode);
        await Tablet.RespondAsync(Denied); Assert.Equal("permission_required", (await Pending).Error!.Code);
        await Tablet.RespondAsync(Denied); Assert.Empty((await Tablet.GetDeviceRequestsAsync())!);
        var Successful = Signals.RequestAsync(Interaction.Id, "tablet", "location.read", Parameters, TimeSpan.FromSeconds(5), CancellationToken.None);
        var SuccessRequest = Assert.Single((await Tablet.GetDeviceRequestsAsync())!);
        await Tablet.RespondAsync(new(SuccessRequest.RequestId, true, JsonSerializer.SerializeToElement(new { latitude = 51.5 })));
        Assert.True((await Successful).Success);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"/api/client/device/responses/{SuccessRequest.RequestId}")).StatusCode);
        var Saved = (await TabletHttp.GetFromJsonAsync<DeviceResponse>($"/api/client/device/responses/{SuccessRequest.RequestId}"))!;
        Assert.Equal(51.5, Saved.Result!.Value.GetProperty("latitude").GetDouble());
        var Progress = await Http.GetStringAsync($"/api/client/interactions/{Interaction.Id}/events");
        Assert.DoesNotContain("51.5", Progress);
        var Withdrawn = Signals.RequestAsync(Interaction.Id, "tablet", "location.read", Parameters, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Tablet.RegisterAsync(new("android_tablet", "Local test tablet", []));
        Assert.Equal("device_unavailable", (await Withdrawn).Error!.Code);
        await Tablet.RegisterAsync(new("android_tablet", "Local test tablet", ["location.read"]));
        var Expired = await Signals.RequestAsync(Interaction.Id, "tablet", "location.read", Parameters, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("device_timeout", Expired.Error!.Code);
        var Cancelled = Signals.RequestAsync(Interaction.Id, "tablet", "location.read", Parameters, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Client.CancelAsync(Interaction.Id); Assert.Equal("cancelled", (await Cancelled).Error!.Code);
        Assert.Empty((await Tablet.GetDeviceRequestsAsync())!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Signals.RequestAsync(Interaction.Id, "other", "location.read", Parameters, TimeSpan.FromSeconds(1), CancellationToken.None));
    }
    [Fact]
    public async Task TextAttachmentReachesRealModelBoundaryAndContextHasProvenance()
    {
        var Model = new CapturingModel();
        using var App = new TestApplication(UseFake: false).WithWebHostBuilder(Builder => Builder.ConfigureServices(Services => Services.AddSingleton<ILanguageModel>(Model)));
        using var Http = App.CreateClient();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var Client = new Assister.Client.AssisterClient(Http);
        var Attachment = await Client.UploadAttachmentAsync(System.Text.Encoding.UTF8.GetBytes("The meeting starts at 4 PM."), "notes.txt", "text/plain");
        var Conversation = await Client.CreateConversationAsync();
        var Item = await Client.SubmitAsync(Conversation.Id, new SubmitInteraction("Summarize the attached document.", "with-file", [Attachment.Id]));
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", (await WaitTerminal(Http, Item.Id, Deadline.Token)).Status);
        Assert.Contains(Model.Request!.Messages, Message => Message.Content?.Contains("The meeting starts at 4 PM.") == true);
        var Context = (await Client.GetContextAsync(Item.Id))!;
        var Document = Assert.Single(Context, Record => Record.Type == "attachment");
        Assert.Equal(Attachment.Id, Document.Provenance.GetProperty("attachmentId").GetGuid());
        Assert.Equal("local", Document.Provenance.GetProperty("suppliedByClientId").GetString());
        Assert.Equal(new[] { 1 }, Document.ModelRounds);
        Assert.Contains("The meeting starts at 4 PM.", Document.Content);
        Assert.Single((await Client.GetAttachmentsAsync(Item.Id))!);
        var Events = (await Http.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{Item.Id}/events"))!;
        Assert.Contains(Events, Event => Event.Type == "context.added");
        var Changed = await Http.PostAsJsonAsync($"/api/client/conversations/{Conversation.Id}/interactions", new SubmitInteraction("Summarize the attached document.", "with-file"));
        Assert.Equal(HttpStatusCode.Conflict, Changed.StatusCode);
        using var Other = App.CreateClient();
        Other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "other-client-test-credential-123456789");
        Assert.Equal(HttpStatusCode.NotFound, (await Other.GetAsync($"/api/client/attachments/{Attachment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Other.GetAsync($"/api/client/interactions/{Item.Id}/context")).StatusCode);
        var OtherConversation = (await (await Other.PostAsync("/api/client/conversations", null)).Content.ReadFromJsonAsync<ClientConversation>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await Other.PostAsJsonAsync($"/api/client/conversations/{OtherConversation.Id}/interactions", new SubmitInteraction("Summarize", "steal", [Attachment.Id]))).StatusCode);
    }
    [Fact]
    public async Task UploadLimitsAndUnsupportedImageProcessingAreExplicit()
    {
        using var App = new TestApplication();
        using var Http = App.CreateClient();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        async Task<HttpResponseMessage> Upload(byte[] Bytes, string Type)
        {
            using var Content = new ByteArrayContent(Bytes); Content.Headers.ContentType = new(Type);
            return await Http.PostAsync("/api/client/attachments?name=test.bin&source=clipboard", Content);
        }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(new byte[1048577], "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload([255, 255], "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload([1, 2, 3], "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload([1], "application/pdf")).StatusCode);
        var Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jP1sAAAAASUVORK5CYII=");
        var Image = (await (await Upload(Png, "image/png")).Content.ReadFromJsonAsync<ClientAttachment>())!;
        Assert.Equal("stored_only", Image.Processing);
        var Download = await Http.GetAsync($"/api/client/attachments/{Image.Id}");
        Assert.Equal(Png, await Download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", Download.Headers.GetValues("X-Content-Type-Options").Single());
        var Conversation = (await (await Http.PostAsync("/api/client/conversations", null)).Content.ReadFromJsonAsync<ClientConversation>())!;
        var Rejected = await Http.PostAsJsonAsync($"/api/client/conversations/{Conversation.Id}/interactions", new SubmitInteraction("Describe the image", "image", [Image.Id]));
        Assert.Equal(HttpStatusCode.Conflict, Rejected.StatusCode);
        Assert.Equal("attachment_processing_unsupported", (await Rejected.Content.ReadFromJsonAsync<ProtocolError>())!.Code);
    }
    [Fact]
    public async Task RealCoordinatorEmitsIntentFeedbackAndPreservesAuthoritativeText()
    {
        using var App = new TestApplication(UseFake: false);
        using var Client = App.CreateClient();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var Conversation = (await (await Client.PostAsync("/api/client/conversations", null)).Content.ReadFromJsonAsync<ClientConversation>())!;
        var Item = (await (await Client.PostAsJsonAsync($"/api/client/conversations/{Conversation.Id}/interactions", new SubmitInteraction("turn light.protocol_missing on", "real-intent"))).Content.ReadFromJsonAsync<InteractionSnapshot>())!;
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var Final = await WaitTerminal(Client, Item.Id, Deadline.Token);
        Assert.Equal("failed", Final.Status); // An unconfigured HA connection must never issue a control.
        var Events = (await Client.GetFromJsonAsync<InteractionEvent[]>($"/api/client/interactions/{Item.Id}/events"))!;
        Assert.Contains(Events, Event => Event.Type == "step.started" && Event.Data.GetProperty("kind").GetString() == "IntentClassification");
        Assert.Contains(Events, Event => Event.Type == "tone.play" && Event.Data.GetProperty("name").GetString() == "intent-match");
        Assert.Equal(Final.Response, Events.Single(Event => Event.Type == "response.completed").Data.GetProperty("text").GetString());
        Assert.Equal(Final.Response, string.Concat(Events.Where(Event => Event.Type == "response.delta").Select(Event => Event.Data.GetProperty("text").GetString())));
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/client/interactions/{Item.Id}/trace")).StatusCode);
    }
    [Fact]
    public async Task NativeClientCanConsumeProtocolAndRelatedDevicesShareHistory()
    {
        using var App = new TestApplication();
        using var Http = App.CreateClient();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var Ws = App.Server.CreateWebSocketClient();
        Ws.ConfigureRequest = Request => Request.Headers.Authorization = "Bearer " + Token;
        var Client = new Assister.Client.AssisterClient(Http, (Uri, Token) => Ws.ConnectAsync(Uri, Token));
        var Conversation = await Client.CreateConversationAsync();
        var Item = await Client.SubmitAsync(Conversation.Id, "native client", "native-key");
        App.Coordinator.Release.TrySetResult();
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var Events = new List<InteractionEvent>();
        await foreach (var Event in Client.ObserveAsync(Item.Id, Token: Deadline.Token)) Events.Add(Event);
        Assert.Equal("Hello world", string.Concat(Events.Where(Event => Event.Type == "response.delta").Select(Event => Event.Data.GetProperty("text").GetString())));
        Assert.Equal("interaction.completed", Events.Last().Type);
        Assert.Equal(Enumerable.Range(1, Events.Count).Select(Value => (long)Value), Events.Select(Event => Event.Sequence));
        using var Tablet = App.CreateClient();
        Tablet.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tablet-client-test-credential-123456789");
        Assert.Equal(HttpStatusCode.OK, (await Tablet.GetAsync($"/api/client/conversations/{Conversation.Id}")).StatusCode);
        var Count = 0;
        await foreach (var Event in Client.ObserveAsync(Item.Id, Events.Last().Sequence, Deadline.Token)) Count++;
        Assert.Equal(0, Count);
    }
    [Fact]
    public async Task StreamsBeforeCompletionReplaysAfterDisconnectAndCancels()
    {
        using var App = new TestApplication();
        using var Client = App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/client/conversations")).StatusCode);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var Conversation = await (await Client.PostAsync("/api/client/conversations", null)).Content.ReadFromJsonAsync<ClientConversation>();
        var Request = new SubmitInteraction("stream please", "first");
        var Submitted = await Client.PostAsJsonAsync($"/api/client/conversations/{Conversation!.Id}/interactions", Request);
        Assert.Equal(HttpStatusCode.Accepted, Submitted.StatusCode);
        var Item = (await Submitted.Content.ReadFromJsonAsync<InteractionSnapshot>())!;
        var Duplicate = await (await Client.PostAsJsonAsync($"/api/client/conversations/{Conversation.Id}/interactions", Request)).Content.ReadFromJsonAsync<InteractionSnapshot>();
        Assert.Equal(Item.Id, Duplicate!.Id);
        var Ws = App.Server.CreateWebSocketClient();
        Ws.ConfigureRequest = Request => Request.Headers.Authorization = "Bearer " + Token;
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        long Cursor = 0;
        using (var Socket = await Ws.ConnectAsync(new Uri($"ws://localhost/api/client/interactions/{Item.Id}/stream"), Timeout.Token))
        {
            while (true)
            {
                var Event = await Receive(Socket, Timeout.Token);
                if (Event.TryGetProperty("sequence", out var Sequence)) Cursor = Sequence.GetInt64();
                if (Event.GetProperty("type").GetString() == "response.delta") break;
            }
            var Snapshot = await Client.GetFromJsonAsync<InteractionSnapshot>($"/api/client/interactions/{Item.Id}");
            Assert.Equal("responding", Snapshot!.Status);
            // Dispose disconnects this observer while server execution remains alive.
        }
        App.Coordinator.Release.TrySetResult();
        var Final = await WaitTerminal(Client, Item.Id, Timeout.Token);
        Assert.Equal("completed", Final.Status);
        Assert.Equal("Hello world", Final.Response);
        using (var Socket = await Ws.ConnectAsync(new Uri($"ws://localhost/api/client/interactions/{Item.Id}/stream?afterSequence={Cursor}"), Timeout.Token))
        {
            var Events = new List<InteractionEvent>();
            while (true)
            {
                var Event = await Receive(Socket, Timeout.Token);
                if (!Event.TryGetProperty("sequence", out _)) continue;
                Events.Add(Event.Deserialize<InteractionEvent>(Json)!);
                if (Event.GetProperty("type").GetString() == "interaction.completed") break;
            }
            Assert.Equal(Cursor + 1, Events[0].Sequence);
            Assert.Contains(Events, Event => Event.Type == "response.completed");
        }
        var Cancel = (await (await Client.PostAsJsonAsync($"/api/client/conversations/{Conversation.Id}/interactions", new SubmitInteraction("wait", "second"))).Content.ReadFromJsonAsync<InteractionSnapshot>())!;
        await Client.PostAsync($"/api/client/interactions/{Cancel.Id}/cancel", null);
        Assert.Equal("cancelled", (await WaitTerminal(Client, Cancel.Id, Timeout.Token)).Status);
        using var Other = App.CreateClient();
        Other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "other-client-test-credential-123456789");
        Assert.Equal(HttpStatusCode.NotFound, (await Other.GetAsync($"/api/client/interactions/{Item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Other.GetAsync($"/api/client/interactions/{Item.Id}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Other.GetAsync($"/api/client/conversations/{Conversation.Id}")).StatusCode);
        Client.DefaultRequestHeaders.Add("Origin", "https://hostile.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.PostAsync("/api/client/conversations", null)).StatusCode);
    }
    [Fact]
    public async Task BrowserSessionAndFunctionalPageWork()
    {
        using var App = new TestApplication();
        using var Client = App.CreateClient();
        Assert.Contains("Ask. Watch it happen.", await Client.GetStringAsync("/chat.html"));
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/api/client/session", new { token = Token })).StatusCode);
        var Protocol = await Client.GetFromJsonAsync<JsonElement>("/api/client/protocol");
        Assert.Equal(1, Protocol.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync("/api/client/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/client/protocol")).StatusCode);
    }
    private static async Task<InteractionSnapshot> WaitTerminal(HttpClient Client, Guid Id, CancellationToken Token)
    {
        while (true)
        {
            var Item = (await Client.GetFromJsonAsync<InteractionSnapshot>($"/api/client/interactions/{Id}", Token))!;
            if (Item.Status is "completed" or "cancelled" or "failed") return Item;
            await Task.Delay(20, Token);
        }
    }
    private static async Task<JsonElement> Receive(WebSocket Socket, CancellationToken Token)
    {
        using var Stream = new MemoryStream();
        var Buffer = new byte[4096];
        WebSocketReceiveResult Result;
        do { Result = await Socket.ReceiveAsync(Buffer, Token); Assert.NotEqual(WebSocketMessageType.Close, Result.MessageType); Stream.Write(Buffer, 0, Result.Count); } while (!Result.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(Stream.ToArray());
    }
    private sealed class TestApplication(bool UseFake = true) : WebApplicationFactory<Program>
    {
        public FakeCoordinator Coordinator { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-rich-" + Guid.NewGuid()),
                ["RichClients:Clients:local:Token"] = Token,
                ["RichClients:Clients:tablet:Token"] = "tablet-client-test-credential-123456789",
                ["RichClients:Clients:tablet:Owner"] = "local",
                ["RichClients:Clients:other:Token"] = "other-client-test-credential-123456789"
            }));
            if (UseFake) Builder.ConfigureServices(Services => Services.AddScoped<IRequestCoordinator>(_ => Coordinator));
        }
    }
    private sealed class CapturingModel : ILanguageModel
    {
        public LlmRequest? Request { get; private set; }
        public Task<LlmResponse> CompleteAsync(LlmRequest Input, CancellationToken Token)
        { Request = Input; return Task.FromResult(new LlmResponse("The meeting starts at 4 PM.", [], "stop")); }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { yield return new(Completed: await CompleteAsync(Input, Token)); }
    }
    private sealed class FakeCoordinator : IStreamingRequestCoordinator
    {
        public UserRequest? LastRequest { get; private set; }
        public int Calls { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RequestResult> ProcessAsync(UserRequest Request, CancellationToken Token) => ProcessStreamingAsync(Request, (_, _) => Task.CompletedTask, Token);
        public async Task<RequestResult> ProcessStreamingAsync(UserRequest Request, Func<string, CancellationToken, Task> OnText, CancellationToken Token)
        {
            LastRequest = Request; Calls++;
            InteractionFeedback.Emit("step.started", new { stepId = Guid.NewGuid(), label = "Running local test model" });
            await Assister.Voice.VoiceFeedback.EmitAsync("ai-think", Token);
            await Assister.Voice.VoiceFeedback.EmitAsync("ai-thought", Token);
            await OnText("Hello", Token);
            if (Request.Message == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, Token);
            else await Release.Task.WaitAsync(Token);
            await OnText(" world", Token);
            return new("Hello world", Request.ConversationId, "test-model", Guid.NewGuid(), "succeeded", [], null, 0);
        }
    }
    private sealed class FakeStt : IStreamingSpeechToTextProvider
    {
        public bool Fail { get; set; }
        public Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, CancellationToken Token)
            => TranscribeStreamingAsync(Audio, Options, (_, _) => Task.CompletedTask, Token);
        public async Task<TranscriptionResult> TranscribeStreamingAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options, Func<string, CancellationToken, Task> OnPartial, CancellationToken Token)
        {
            await foreach (var Chunk in Audio.WithCancellation(Token)) Assert.Equal(16000, Chunk.SampleRate);
            if (Fail) throw new IOException("Unavailable test STT");
            await OnPartial("What time", Token); return new("What time is it?", "en");
        }
    }
    private sealed class FakeTts : ITextToSpeechProvider
    {
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        {
            await Task.Yield(); if (Fail) throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, Token); }
            yield return new(new byte[3200], 16000, 2, 1);
        }
    }
}
