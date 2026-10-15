using Assister.Contracts;
using Assister.Satellites;
using Assister.Tools;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Assister.Voice;

namespace Assister.Tests;

public sealed class AnnouncementTests
{
    [Fact]
    public async Task AnnouncementIncludesHelloSpeechAndGoodbyeInOnePlayback()
    {
        var Tones = new ToneCatalog(new ToneEnvironment(), new ConfigurationBuilder().Build());
        var Manager = new SatelliteManager(Tones: Tones);
        var Speaker = new Speaker("test", "Test");
        Manager.Register(Speaker);
        Manager.Update("test", State => State with { Capabilities = new() { AnnouncementPlayback = true } });
        Assert.True(await Manager.AnnounceAsync("test", "Hello", new Speech(), default));
        Assert.Equal(3, Speaker.Audio.Count);
        Assert.Equal(Tones.RenderFor("awake", Speaker.Audio[1]).Pcm.ToArray(), Speaker.Audio[0].Pcm.ToArray());
        Assert.Equal(2, Speaker.Audio[1].Pcm.Length);
        Assert.Equal(Tones.RenderFor("goodbye", Speaker.Audio[1]).Pcm.ToArray(), Speaker.Audio[2].Pcm.ToArray());
    }

    private sealed class ToneEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Assister";
        public string ContentRootPath { get; set; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    [Fact]
    public async Task RequestingSatelliteQueuesUntilVoiceSessionEnds()
    {
        var (Manager, Kitchen, _) = Setup();
        using var Services = new ServiceCollection().AddTransient<ITextToSpeechProvider, Speech>().BuildServiceProvider();
        using var Queue = new AnnouncementQueue(Manager, Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AnnouncementQueue>.Instance);
        var Session = Guid.NewGuid();
        Manager.BeginSession("kitchen", Session);
        await Queue.StartAsync(default);
        try
        {
            var Handler = new AnnouncementHandler(Manager, new Speech(), Queue);
            var Response = await Handler.TryHandleAsync(new("announce to Kitchen: Hello", "kitchen"), new(), default);
            Assert.Contains("Queued", Response);
            Assert.Empty(Kitchen.Events);
            Manager.EndSession("kitchen", Session);
            await Kitchen.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Hello", Kitchen.Events.Single(Event => Event.Type == "announcement").Text);
        }
        finally { await Queue.StopAsync(default); }
    }

    [Fact]
    public async Task MissingDestinationListsAllFirstAndSelectionKeepsMessage()
    {
        var (Manager, Kitchen, Office) = Setup();
        var Handler = new AnnouncementHandler(Manager, new Speech());
        var Context = new DeviceConversationContext();
        var Prompt = await Handler.TryHandleAsync(new("announce Dinner is ready", "client"), Context, default);
        Assert.Equal("Which satellite should receive the announcement?\n1. All satellites\n2. Kitchen\n3. Office", Prompt);
        Assert.Empty(Kitchen.Events);
        Assert.Empty(Office.Events);
        Assert.Contains("Announced on Kitchen", await Handler.TryHandleAsync(new("2", "client"), Context, default));
        Assert.Equal("Dinner is ready", Kitchen.Events.Single(Event => Event.Type == "announcement").Text);
        Assert.Empty(Office.Events);
        Assert.Null(Context.Announcement);
    }

    [Theory]
    [InlineData("announce to Kitchen: Hello", false)]
    [InlineData("broadcast to all satellites Hello", true)]
    [InlineData("announce on Kitchen that Hello", false)]
    [InlineData("announce Hello to Kitchen", false)]
    public async Task ExplicitDestinationsDeliver(string Command, bool All)
    {
        var (Manager, Kitchen, Office) = Setup();
        var Handler = new AnnouncementHandler(Manager, new Speech());
        Assert.Contains("Announced", await Handler.TryHandleAsync(new(Command, "client"), new(), default));
        Assert.Equal("Hello", Kitchen.Events.Single(Event => Event.Type == "announcement").Text);
        Assert.Equal(All, Office.Events.Any(Event => Event.Type == "announcement"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolumeRestoresAfterPlaybackIncludingFailure(bool Fail)
    {
        var (Manager, Kitchen, _) = Setup();
        Kitchen.Fail = Fail;
        var Task = Manager.AnnounceAsync("kitchen", "Hello", new Speech(), default);
        if (Fail) await Assert.ThrowsAsync<IOException>(() => Task);
        else Assert.True(await Task);
        Assert.Equal(new[] { "set-volume", "announcement", "set-volume" }, Kitchen.Events.Select(Event => Event.Type));
        Assert.Equal("0.9", Kitchen.Events[0].Text);
        Assert.Equal("0.35", Kitchen.Events[^1].Text);
        Assert.False(Manager.IsBusy("kitchen"));
    }

    [Fact]
    public async Task UnknownVolumeAndBusyDevicesReportFailureWithoutClaimingSuccess()
    {
        var (Manager, Kitchen, _) = Setup();
        Manager.Update("kitchen", State => State with { CurrentVolume = null });
        Manager.BeginSession("office", Guid.NewGuid());
        var Response = await new AnnouncementHandler(Manager, new Speech()).ExecuteAsync("Hello", "all", new(), default);
        Assert.DoesNotContain("Announced", Response);
        Assert.Contains("Could not announce", Response);
        Assert.Empty(Kitchen.Events);
    }

    [Fact]
    public async Task ToolUsesSamePickerAndCannotRepeat()
    {
        var (Manager, Kitchen, _) = Setup();
        var Tool = new SatelliteAnnouncementTool(new(Manager, new Speech()));
        var Context = new ToolExecutionContext(new("Please make an announcement", "client"), [], Conversation: new());
        var Arguments = JsonSerializer.SerializeToElement(new { message = "Hello" });
        Assert.Contains("All satellites", await Tool.ExecuteAsync(Arguments, Context, default));
        Assert.NotNull(Context.Conversation!.Announcement);
        Assert.Contains("error", await Tool.ExecuteAsync(Arguments, Context, default));
        Assert.Empty(Kitchen.Events);
    }

    [Fact]
    public async Task UnrelatedTurnAndExpiredSelectionCannotResurrectAnnouncement()
    {
        var (Manager, Kitchen, _) = Setup();
        var Handler = new AnnouncementHandler(Manager, new Speech());
        var Context = new DeviceConversationContext();
        await Handler.TryHandleAsync(new("announce Hello", "client"), Context, default);
        Assert.Null(await Handler.TryHandleAsync(new("what time is it", "client"), Context, default));
        Assert.Null(await Handler.TryHandleAsync(new("Kitchen", "client"), Context, default));
        Context.Announcement = new("Hello", ["kitchen"], DateTimeOffset.UtcNow.AddMinutes(-6));
        Assert.Null(await Handler.TryHandleAsync(new("2", "client"), Context, default));
        Assert.Empty(Kitchen.Events);
    }

    private static (SatelliteManager, Speaker, Speaker) Setup()
    {
        var Manager = new SatelliteManager();
        var Kitchen = new Speaker("kitchen", "Kitchen");
        var Office = new Speaker("office", "Office");
        foreach (var Speaker in new[] { Kitchen, Office })
        {
            Manager.Register(Speaker);
            Manager.Update(Speaker.SatelliteId, State => State with { CurrentVolume = .35,
                Capabilities = new() { AnnouncementPlayback = true, VolumeControl = true } });
        }
        return (Manager, Kitchen, Office);
    }
    private sealed class Speech : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        { await Task.Yield(); yield return new(new byte[2], 16000, 2, 1); }
    }
    private sealed class Speaker(string Id, string Label) : ISatelliteConnection
    {
        public string SatelliteId => Id;
        public string Name => Label;
        public string? Area => null;
        public bool Fail { get; set; }
        public List<SatelliteEvent> Events { get; } = [];
        public List<AudioChunk> Audio { get; } = [];
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public async Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Chunks, CancellationToken Token)
        { await foreach (var Chunk in Chunks.WithCancellation(Token)) { Audio.Add(Chunk); } if (Fail) throw new IOException(); }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token)
        {
            Events.Add(Event);
            if (Event.Type == "set-volume" && Event.Text == "0.35") Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
