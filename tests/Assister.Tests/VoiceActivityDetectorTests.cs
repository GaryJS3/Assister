using System.Buffers.Binary;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Voice;

namespace Assister.Tests;

public sealed class VoiceActivityDetectorTests
{
    [Theory]
    [InlineData(12000, 0, 2000)]
    [InlineData(0, 2000, 8000)]
    public async Task DcOffsetAndSteadyBackgroundDoNotKeepSpeechCaptureOpen(int Offset, int Noise, int Speech)
    {
        var Satellite = new Input(Offset, Noise, Speech);
        var Count = 0;
        await foreach (var _ in VoiceActivityDetector.UntilSilenceAsync(Satellite, 0.015, CancellationToken.None)) { Count++; }
        Assert.InRange(Count, 22, 24); // 0.3s background, 1s speech, about 0.9s endpoint silence.
        Assert.True(Satellite.Ended);
    }
    private sealed class Input(int Offset, int Noise, int Speech) : ISatelliteConnection
    {
        public string SatelliteId => "test";
        public string Name => "test";
        public string? Area => null;
        public bool Ended { get; private set; }
        public async IAsyncEnumerable<AudioChunk> ReceiveAudioAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken Token)
        {
            await Task.Yield();
            for (var Index = 0; Index < 200; Index++)
            {
                var Pcm = new byte[3200];
                var Amplitude = Index is >= 3 and < 13 ? Speech : Noise;
                for (var Sample = 0; Sample < 1600; Sample++)
                { BinaryPrimitives.WriteInt16LittleEndian(Pcm.AsSpan(Sample * 2), (short)(Offset + (Sample % 2 == 0 ? Amplitude : -Amplitude))); }
                yield return new(Pcm, 16000, 2, 1);
            }
        }
        public Task SendEventAsync(SatelliteEvent Event, CancellationToken Token) { Ended |= Event.Type == "end-of-speech"; return Task.CompletedTask; }
        public Task SendAudioAsync(IAsyncEnumerable<AudioChunk> Audio, CancellationToken Token) => throw new NotSupportedException();
    }
}
