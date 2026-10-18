using System.Net;
using System.Net.Http.Json;
using Assister.Diagnostics;
using Assister.Interactions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assister.IntegrationTests;

public sealed class RunAudioTests
{
    private const string OwnerToken = "run-audio-owner-token-123456789";

    [Fact]
    public async Task VoiceRecordingsHaveRangeSupportPersistAcrossRestartAndExpire()
    {
        var Data = Path.Combine(Path.GetTempPath(), "assister-run-audio-" + Guid.NewGuid());
        var Clock = new TestClock();
        Guid Id;
        await using (var App = new AudioApplication(Data, Clock))
        {
            using var Http = App.CreateClient();
            using (var Run = RunTracing.BeginRun(App.Services.GetRequiredService<RunStore>(), "voice", "speaker"))
            {
                Id = RunTracing.RunId;
                var Audio = App.Services.GetRequiredService<RunAudioStore>();
                await foreach (var _ in Audio.ObserveAsync(Id, "input", Chunks([1, 2]), default)) { }
                await foreach (var _ in Audio.ObserveAsync(Id, "output", Chunks([3, 4]), default)) { }
                Run.Complete("succeeded");
            }
            var Clips = (await Http.GetFromJsonAsync<RunAudioClip[]>($"/api/diagnostics/runs/{Id}/audio"))!;
            Assert.All(Clips, Clip => Assert.Equal("available", Clip.State));
            Assert.Equal(new byte[] { 1, 2 }, (await Http.GetByteArrayAsync(Clips[0].Url!))[44..]);
            using var Request = new HttpRequestMessage(HttpMethod.Get, Clips[1].Url);
            Request.Headers.Range = new(0, 3);
            using var Partial = await Http.SendAsync(Request);
            Assert.Equal(HttpStatusCode.PartialContent, Partial.StatusCode);
            Assert.Equal("audio/wav", Partial.Content.Headers.ContentType!.MediaType);
            Assert.Equal("RIFF"u8.ToArray(), await Partial.Content.ReadAsByteArrayAsync());
            Assert.Equal("no-store", Partial.Headers.CacheControl!.ToString());
            Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"/api/diagnostics/runs/{Id}/audio/invalid")).StatusCode);
        }
        await using var Reopened = new AudioApplication(Data, Clock);
        using var Client = Reopened.CreateClient();
        Assert.Equal(new byte[] { 3, 4 }, (await Client.GetByteArrayAsync($"/api/diagnostics/runs/{Id}/audio/output"))[44..]);
        Clock.Now += TimeSpan.FromHours(25);
        var Expired = (await Client.GetFromJsonAsync<RunAudioClip[]>($"/api/diagnostics/runs/{Id}/audio"))!;
        Assert.All(Expired, Clip => Assert.Equal("expired", Clip.State));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/diagnostics/runs/{Id}/audio/input")).StatusCode);
    }

    [Fact]
    public async Task RichRecordingsRequireOwnerAndInputPcmBecomesPlayableWaveWithoutPlaybackReports()
    {
        var Clock = new TestClock();
        await using var App = new AudioApplication(Path.Combine(Path.GetTempPath(), "assister-run-audio-" + Guid.NewGuid()), Clock);
        using var Http = App.CreateClient();
        var Store = App.Services.GetRequiredService<InteractionStore>();
        var Conversation = Store.CreateConversation("owner");
        var Upload = Store.SaveAttachment("owner", "Voice.pcm", "audio/pcm", "microphone", [1, 2], null);
        var Item = Store.Submit(Conversation.Id, "owner", new("voice", "audio", AudioAttachmentId: Upload.Id, Speak: true));
        Guid Id;
        using (var Run = RunTracing.BeginRun(App.Services.GetRequiredService<RunStore>(), "rich-client"))
        {
            Id = RunTracing.RunId;
            Store.Append(Item.Id, "interaction.started", new { }, "running", RunId: Id);
            Store.SaveAudio(Item.Id, RunAudioStore.Wave(new byte[] { 3, 4 }, 16000, 2, 1));
            Store.Append(Item.Id, "interaction.completed", new { }, "completed");
            Run.Complete("succeeded");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync($"/api/diagnostics/runs/{Id}/audio/input")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync($"/api/client/runs/{Id}/audio")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new("Bearer", "run-audio-other-token-123456789");
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"/api/client/runs/{Id}/audio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"/api/client/runs/{Id}/audio/output")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new("Bearer", OwnerToken);
        var Clips = (await Http.GetFromJsonAsync<RunAudioClip[]>($"/api/client/runs/{Id}/audio"))!;
        Assert.All(Clips, Clip => Assert.Equal("available", Clip.State));
        var Sequence = Store.Get(Item.Id)!.LastSequence;
        var Input = await Http.GetByteArrayAsync(Clips[0].Url!);
        Assert.Equal(16000, BitConverter.ToInt32(Input, 24));
        Assert.Equal(16, BitConverter.ToInt16(Input, 34));
        Assert.Equal(new byte[] { 1, 2 }, Input[44..]);
        Assert.Equal(new byte[] { 3, 4 }, (await Http.GetByteArrayAsync(Clips[1].Url!))[44..]);
        Assert.Equal(Sequence, Store.Get(Item.Id)!.LastSequence);
        Clock.Now += TimeSpan.FromHours(25);
        Store.Prune();
        Assert.All((await Http.GetFromJsonAsync<RunAudioClip[]>($"/api/client/runs/{Id}/audio"))!, Clip => Assert.Equal("expired", Clip.State));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(Clips[0].Url)).StatusCode);
    }

    private static async IAsyncEnumerable<Assister.Contracts.AudioChunk> Chunks(byte[] Pcm)
    { yield return new(Pcm, 16000, 2, 1); await Task.CompletedTask; }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class AudioApplication(string DataPath, TimeProvider Clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder Builder)
        {
            Builder.ConfigureAppConfiguration((_, Config) => Config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assister:DataPath"] = DataPath,
                ["RichClients:Clients:owner:Token"] = OwnerToken,
                ["RichClients:Clients:other:Token"] = "run-audio-other-token-123456789"
            }));
            Builder.ConfigureServices(Services =>
            {
                Services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
                Services.AddSingleton(Clock);
            });
        }
    }
}
