using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Tools;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class SatelliteVolumeTests
{
    [Theory]
    [InlineData("{\"percent\":50}", "0.5")]
    [InlineData("{\"delta_percent\":10}", "0.6")]
    [InlineData("{\"delta_percent\":-100}", "0")]
    public async Task ToolTargetsOnlyCurrentDeviceAndDoesNotRepeat(string Arguments, string Expected)
    {
        var Manager = new SatelliteManager();
        var Current = new Speaker("current");
        var Other = new Speaker("other");
        Manager.Register(Current); Manager.Register(Other);
        Manager.Update("current", State => State with { CurrentVolume = .5, Capabilities = new() { VolumeControl = true } });
        var Tool = new SatelliteVolumeTool(Manager);
        var Context = new ToolExecutionContext(new("adjust my volume", "current"), []);
        var Result = await Tool.ExecuteAsync(Json(Arguments), Context, default);
        Assert.Contains("sent", Result);
        Assert.Equal(Expected, Assert.Single(Current.Events).Text);
        Assert.Empty(Other.Events);
        Assert.Contains("error", await Tool.ExecuteAsync(Json(Arguments), Context, default));
        Assert.Single(Current.Events);
    }

    [Fact]
    public async Task MissingUnknownAndUnsupportedDevicesDoNotSend()
    {
        var Manager = new SatelliteManager();
        var Tool = new SatelliteVolumeTool(Manager);
        var Context = new ToolExecutionContext(new("volume", "current"), []);
        Assert.Contains("error", await Tool.ExecuteAsync(Json("{\"percent\":30}"), Context, default));
        var Speaker = new Speaker("current"); Manager.Register(Speaker);
        Assert.Contains("error", await Tool.ExecuteAsync(Json("{\"percent\":30}"), Context, default));
        Manager.Update("current", State => State with { Capabilities = new() { VolumeControl = true } });
        Assert.Contains("unknown", await Tool.ExecuteAsync(Json("{\"delta_percent\":10}"), Context, default));
        Assert.Contains("error", await Tool.ExecuteAsync(Json("{}"), Context, default));
        Assert.Empty(Speaker.Events);
    }

    [Fact]
    public async Task EchoMuseWaitsForMatchingAcknowledgement()
    {
        using var Audio = new VoiceAudioStore();
        var Manager = new SatelliteManager();
        var Sent = new TaskCompletionSource<JsonElement>();
        var Device = new EchoMuseConnection("echo", "current", "Echo", null, (Message, _) =>
        { Sent.SetResult(JsonSerializer.SerializeToElement(Message)); return Task.CompletedTask; }, Audio, new ConfigurationBuilder().Build(), Manager);
        var Work = Device.SendEventAsync(new("set-volume", "0.5"), default);
        var Message = await Sent.Task;
        Assert.Equal("set_volume", Message.GetProperty("type").GetString());
        Assert.False(Work.IsCompleted);
        Assert.False(Device.Handle(JsonSerializer.SerializeToElement(new { type = "volume_result", deviceId = "other", requestId = Message.GetProperty("requestId").GetString(), status = "sent" })));
        Assert.True(Device.Handle(JsonSerializer.SerializeToElement(new { type = "volume_result", deviceId = "echo", requestId = Message.GetProperty("requestId").GetString(), status = "sent" })));
        await Work;
    }

    private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);
    [Theory]
    [InlineData("{\"percent\":101}")]
    [InlineData("{\"percent\":-1}")]
    [InlineData("{\"percent\":1.5}")]
    [InlineData("{\"percent\":50,\"satelliteId\":\"other\"}")]
    public async Task InvalidLevelsAndTargetOverridesAreRejected(string Arguments)
    {
        var Tool = new SatelliteVolumeTool(new SatelliteManager());
        await Assert.ThrowsAsync<InvalidDataException>(() => Tool.ExecuteAsync(Json(Arguments), new(new("volume"), []), default));
    }
    private sealed class Speaker(string Id) : ISatelliteConnection
    {
        public string SatelliteId => Id;
        public string Name => Id;
        public string? Area => null;
        public List<SatelliteEvent> Events { get; } = [];
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { Events.Add(Event); return Task.CompletedTask; }
        public IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(CancellationToken Token) => throw new NotSupportedException();
        public Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) => throw new NotSupportedException();
    }
}
