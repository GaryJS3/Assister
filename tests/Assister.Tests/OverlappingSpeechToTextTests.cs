using System.Threading.Channels;
using Assister.Contracts;
using Assister.Speech.Wyoming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assister.Tests;

public sealed class OverlappingSpeechToTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeatureSwitchSelectsOverlapOrOriginalProvider(bool Enabled)
    {
        var Services = new ServiceCollection();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SpeechToText:OverlapEnabled"] = Enabled.ToString() }).Build());
        Services.AddAssisterServices();
        using var Provider = Services.BuildServiceProvider();
        var Speech = Provider.GetRequiredService<ISpeechToTextProvider>();
        Assert.Equal(Enabled ? typeof(OverlappingSpeechToTextProvider) : typeof(WyomingSpeechToTextProvider), Speech.GetType());
    }
    [Fact]
    public async Task PreparedTranscriptCannotEscapeBeforeControllerEndsCapture()
    {
        var Input = Channel.CreateUnbounded<AudioChunk>(); var Recognizer = new Recognizer();
        var Work = new OverlappingSpeechToTextProvider(Recognizer, QuietMilliseconds: 160)
            .TranscribeAsync(Input.Reader.ReadAllAsync(), new("en"), default);
        await Prepare(Input, Recognizer);
        Assert.False(Work.IsCompleted);
        Input.Writer.Complete();
        Assert.Equal("prepared", (await Work).Text);
        Assert.Single(Recognizer.Audio);
    }

    [Fact]
    public async Task ResumedSpeechInvalidatesPreparationAndFullRecordingIsRecognizedOnce()
    {
        var Input = Channel.CreateUnbounded<AudioChunk>(); var Recognizer = new Recognizer();
        var Partials = new List<string>();
        var Work = new OverlappingSpeechToTextProvider(Recognizer, QuietMilliseconds: 160)
            .TranscribeStreamingAsync(Input.Reader.ReadAllAsync(), new("en"), (Text, _) => { Partials.Add(Text); return Task.CompletedTask; }, default);
        await Prepare(Input, Recognizer);
        Assert.Empty(Partials);
        await Input.Writer.WriteAsync(Voice());
        Input.Writer.Complete();
        Assert.Equal("complete", (await Work).Text);
        Assert.Equal(2, Recognizer.Audio.Count);
        Assert.Equal(Voice().Pcm.Length * 2 + Silence().Pcm.Length, Recognizer.Audio[1].Length);
        Assert.Equal(Voice().Pcm.ToArray(), Recognizer.Audio[1][^Voice().Pcm.Length..]);
        Assert.Equal(["complete"], Partials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrFailedPreparationFallsBackToCompleteAudio(bool Empty)
    {
        var Input = Channel.CreateUnbounded<AudioChunk>(); var Recognizer = new Recognizer { Empty = Empty, Fail = !Empty };
        var Work = new OverlappingSpeechToTextProvider(Recognizer, QuietMilliseconds: 160).TranscribeAsync(Input.Reader.ReadAllAsync(), new("en"), default);
        await Prepare(Input, Recognizer); Input.Writer.Complete();
        Assert.Equal("complete", (await Work).Text);
        Assert.Equal(2, Recognizer.Audio.Count);
        Assert.Equal(Recognizer.Audio[0], Recognizer.Audio[1]);
    }

    [Fact]
    public async Task CallerCancellationDiscardsPreparedText()
    {
        var Input = Channel.CreateUnbounded<AudioChunk>(); var Recognizer = new Recognizer();
        using var Lifetime = new CancellationTokenSource();
        var Work = new OverlappingSpeechToTextProvider(Recognizer, QuietMilliseconds: 160).TranscribeAsync(Input.Reader.ReadAllAsync(Lifetime.Token), new("en"), Lifetime.Token);
        await Prepare(Input, Recognizer); Lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Work);
        Assert.Single(Recognizer.Audio);
    }

    [Fact]
    public async Task ResumedSpeechCancelsInFlightPreparationBeforeFullRecognition()
    {
        var Input = Channel.CreateUnbounded<AudioChunk>(); var Recognizer = new Recognizer { BlockFirst = true };
        var Work = new OverlappingSpeechToTextProvider(Recognizer, QuietMilliseconds: 160).TranscribeAsync(Input.Reader.ReadAllAsync(), new(), default);
        await Prepare(Input, Recognizer);
        await Input.Writer.WriteAsync(Voice()); Input.Writer.Complete();
        Assert.Equal("complete", (await Work.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        Assert.True(Recognizer.FirstCancelled);
        Assert.Equal(2, Recognizer.Audio.Count);
    }

    [Fact]
    public async Task FinalSpeechShorterThanOneAnalysisFrameStillInvalidatesPreparation()
    {
        var Input = Channel.CreateUnbounded<AudioChunk>(); var Recognizer = new Recognizer();
        var Work = new OverlappingSpeechToTextProvider(Recognizer, QuietMilliseconds: 160).TranscribeAsync(Input.Reader.ReadAllAsync(), new(), default);
        await Prepare(Input, Recognizer);
        await Input.Writer.WriteAsync(new(Voice().Pcm[..320], 16000, 2, 1)); Input.Writer.Complete();
        Assert.Equal("complete", (await Work).Text);
        Assert.Equal(2, Recognizer.Audio.Count);
    }

    [Fact]
    public async Task CaptureFailureCannotCommitAlreadyPreparedText()
    {
        var Input = Channel.CreateUnbounded<AudioChunk>(); var Recognizer = new Recognizer();
        var Work = new OverlappingSpeechToTextProvider(Recognizer, QuietMilliseconds: 160).TranscribeAsync(Input.Reader.ReadAllAsync(), new(), default);
        await Prepare(Input, Recognizer); Input.Writer.Complete(new IOException("Microphone disconnected"));
        await Assert.ThrowsAnyAsync<IOException>(() => Work);
        Assert.Single(Recognizer.Audio);
    }

    [Fact]
    public async Task BufferedAudioAndInitialSilenceDoNotLaunchExtraRecognition()
    {
        var Recognizer = new Recognizer();
        async IAsyncEnumerable<AudioChunk> Audio()
        {
            yield return new(new byte[32000], 16000, 2, 1);
            yield return Voice();
            yield return new(new byte[64000], 16000, 2, 1);
            await Task.CompletedTask;
        }
        await new OverlappingSpeechToTextProvider(Recognizer).TranscribeAsync(Audio(), new(), default);
        Assert.Single(Recognizer.Audio);
        Assert.Equal(32000 + Voice().Pcm.Length + 64000, Recognizer.Audio[0].Length);
    }

    [Fact]
    public async Task InvalidPcmFailsWithoutCallingRecognizer()
    {
        var Recognizer = new Recognizer();
        async IAsyncEnumerable<AudioChunk> Audio() { yield return new(new byte[3], 16000, 2, 1); await Task.CompletedTask; }
        await Assert.ThrowsAsync<InvalidDataException>(() => new OverlappingSpeechToTextProvider(Recognizer).TranscribeAsync(Audio(), new(), default));
        Assert.Empty(Recognizer.Audio);
    }

    [Fact]
    public async Task OtherPcmFormatsPassThroughWithoutSpeculativeBuffering()
    {
        var Recognizer = new Recognizer();
        async IAsyncEnumerable<AudioChunk> Audio()
        {
            yield return new(new byte[48000 * 4], 48000, 2, 2);
            yield return new(new byte[48000 * 4], 48000, 2, 2);
            await Task.CompletedTask;
        }
        await new OverlappingSpeechToTextProvider(Recognizer).TranscribeAsync(Audio(), new(), default);
        Assert.Equal(48000 * 8, Assert.Single(Recognizer.Audio).Length);
    }

    private static async Task Prepare(Channel<AudioChunk> Input, Recognizer Recognizer)
    {
        // Model a paced stream, rather than a buffered upload. 260 ms PCM requires >10 ms arrival time.
        await Task.Delay(30);
        await Input.Writer.WriteAsync(Voice()); await Input.Writer.WriteAsync(Silence());
        await Recognizer.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static AudioChunk Voice()
    {
        var Bytes = new byte[3200]; for (var Offset = 0; Offset < Bytes.Length; Offset += 2) { Bytes[Offset] = 0x00; Bytes[Offset + 1] = 0x10; }
        return new(Bytes, 16000, 2, 1);
    }
    private static AudioChunk Silence() => new(new byte[5120], 16000, 2, 1);

    private sealed class Recognizer : IStreamingSpeechToTextProvider
    {
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Audio { get; } = [];
        public bool Fail, Empty, BlockFirst, FirstCancelled;
        public async Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Chunks, SpeechToTextOptions Options, CancellationToken Token)
        {
            using var Pcm = new MemoryStream(); await foreach (var Chunk in Chunks.WithCancellation(Token)) Pcm.Write(Chunk.Pcm.Span);
            Audio.Add(Pcm.ToArray()); Prepared.TrySetResult();
            if (Audio.Count == 1 && BlockFirst)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, Token); }
                catch (OperationCanceledException) { FirstCancelled = true; throw; }
            }
            if (Audio.Count == 1 && Fail) throw new IOException("Prepared request failed");
            return new(Audio.Count == 1 ? Empty ? "" : "prepared" : "complete", Options.Language);
        }
        public async Task<TranscriptionResult> TranscribeStreamingAsync(IAsyncEnumerable<AudioChunk> Chunks, SpeechToTextOptions Options, Func<string, CancellationToken, Task> Partial, CancellationToken Token)
        { var Result = await TranscribeAsync(Chunks, Options, Token); await Partial(Result.Text, Token); return Result; }
    }
}
