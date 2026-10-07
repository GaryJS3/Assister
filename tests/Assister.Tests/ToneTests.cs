using System.Buffers.Binary;
using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Assister.Tests;

public sealed class ToneTests
{
    private static ToneCatalog Catalog() => new(new EnvironmentStub(), new ConfigurationBuilder().Build());

    [Fact]
    public void SuppliedDefinitionsProduceBoundedMonoPcmWithSilentEdges()
    {
        foreach (var Name in ToneCatalog.Names)
        {
            var Definition = Catalog().Read(Name);
            var Chunk = ToneCatalog.Render(Definition);
            var Milliseconds = Definition.Tones.Sum(Note => Note.DurationMs + Note.GapAfterMs) * Definition.RepeatCount
                + (Definition.RepeatCount - 1) * Definition.RepeatGapMs;
            Assert.Equal(Milliseconds * 48 * 2, Chunk.Pcm.Length);
            Assert.Equal(48000, Chunk.SampleRate);
            Assert.Equal(1, Chunk.Channels);
            Assert.Equal(2, Chunk.SampleWidth);
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(Chunk.Pcm.Span));
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(Chunk.Pcm.Span[^2..]));
            Assert.True(Chunk.Pcm.Span.ContainsAnyExcept((byte)0));
        }
    }

    [Fact]
    public void InvalidAndOversizedDefinitionsAreRejected()
    {
        var Definition = Catalog().Read("awake");
        Assert.Throws<InvalidDataException>(() => ToneCatalog.Render(Definition with { RepeatCount = 100 }));
        Assert.Throws<InvalidDataException>(() => ToneCatalog.Render(Definition with { MasterVolume = double.NaN }));
        Assert.Throws<InvalidDataException>(() => ToneCatalog.Render(Definition with { Tones = [Definition.Tones[0] with { Waveform = "unknown" }] }));
        Assert.Throws<ArgumentException>(() => Catalog().Read("../awake"));
    }

    [Theory]
    [InlineData(22050, 1)]
    [InlineData(16000, 2)]
    public void GoodbyeMatchesSpeechFormatWithoutLosingDuration(int Rate, int Channels)
    {
        var Catalog = ToneTests.Catalog();
        var Original = ToneCatalog.Render(Catalog.Read("goodbye"));
        var Converted = Catalog.RenderFor("goodbye", new(new byte[2 * Channels], Rate, 2, Channels));
        Assert.Equal(Rate, Converted.SampleRate);
        Assert.Equal(Channels, Converted.Channels);
        Assert.Equal((int)Math.Ceiling(Original.Pcm.Length / 2.0 * Rate / 48000) * 2 * Channels, Converted.Pcm.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(Converted.Pcm.Span));
    }

    [Fact]
    public async Task EchoMuseToneUsesIndependentPlaybackAndLeavesTurnOpen()
    {
        using var Audio = new VoiceAudioStore();
        var Manager = new SatelliteManager();
        EchoMuseConnection? Device = null;
        JsonElement? Sent = null;
        Device = new("device", "bedroom", "Bedroom", null, (Message, _) =>
        {
            var Json = JsonSerializer.SerializeToElement(Message);
            Sent = Json;
            Assert.Equal("tone", Json.GetProperty("type").GetString());
            Assert.Equal("session", Json.GetProperty("sessionId").GetString());
            Assert.True(Device!.Handle(JsonSerializer.SerializeToElement(new { type = "play_finished", deviceId = "device", sessionId = "session", requestId = Json.GetProperty("requestId").GetString() })));
            return Task.CompletedTask;
        }, Audio, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:PublicUrl"] = "http://localhost" }).Build(), Manager);
        using var Turn = Device.Start("session", "wake", CancellationToken.None)!;
        Device.FeedbackPlaybackSupported = true;
        await ((ITonePlayback)Turn).PlayToneAsync(Catalog().Audio("awake"), CancellationToken.None);
        Assert.False(Turn.ControllerEnded);
        Assert.False(Turn.PipelineFinished);
        Assert.False(Turn.IsCancelled);
        var Id = Guid.Parse(Path.GetFileNameWithoutExtension(new Uri(Sent!.Value.GetProperty("audioUrl").GetString()!).AbsolutePath));
        Assert.Null(Audio.Get(Id));
        Assert.True(Device.Handle(JsonSerializer.SerializeToElement(new { type = "audio_end", deviceId = "device", sessionId = "session", reason = "speech_end" })));
    }

    private sealed class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
