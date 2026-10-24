using System.Runtime.CompilerServices;
using Assister.Contracts;
using Assister.Interactions;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class RichSpeechOutputTests
{
    [Theory]
    [InlineData("format")]
    [InlineData("oversize")]
    [InlineData("frame")]
    public async Task InvalidStreamingPcmFailsWithoutPublishingACompleteRecording(string Failure)
    {
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-stream-invalid-" + Guid.NewGuid()) }).Build();
        using var Store = new InteractionStore(Config);
        var Item = Store.Submit(Store.CreateConversation("test").Id, "test", new("test", "test", Speak: true, StreamSpeech: true));
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var Output = new RichSpeechOutput(Item.Id, new InvalidTts(Failure), Store, Config, Deadline.Token);
        await Output.AppendAsync("First sentence. Another sentence. ");
        await Output.CompleteAsync();
        var Events = Store.Events(Item.Id, 0);
        Assert.Single(Events, Event => Event.Type == "tts.failed");
        Assert.DoesNotContain(Events, Event => Event.Type is "tts.audio" or "tts.completed");
        Assert.Null(Store.Audio(Item.Id));
    }
    [Fact]
    public async Task SynthesisFailureReleasesAFullTextQueueAndPreservesRecordingBound()
    {
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Assister:DataPath"] = Path.Combine(Path.GetTempPath(), "assister-stream-pressure-" + Guid.NewGuid()) }).Build();
        using var Store = new InteractionStore(Config);
        var Item = Store.Submit(Store.CreateConversation("test").Id, "test", new("test", "test"));
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var Tts = new BlockedFailureTts();
        await using var Output = new RichSpeechOutput(Item.Id, Tts, Store, Config, Deadline.Token);
        var Producer = Output.AppendAsync(string.Concat(Enumerable.Repeat("A complete sentence. ", 40)));
        await Tts.Started.Task.WaitAsync(Deadline.Token);
        Assert.False(Producer.IsCompleted);
        Tts.Release.TrySetResult();
        await Producer.WaitAsync(Deadline.Token);
        await Output.CompleteAsync().WaitAsync(Deadline.Token);
        Assert.Single(Store.Events(Item.Id, 0), Event => Event.Type == "tts.failed");
    }
    private sealed class InvalidTts(string Failure) : ITextToSpeechProvider
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken Token)
        {
            await Task.Yield();
            if (Failure == "oversize") { yield return new(new byte[8 * 1024 * 1024 + 2], 16000, 2, 1); yield break; }
            if (Failure == "frame") { yield return new(new byte[3], 16000, 2, 1); yield break; }
            yield return new(new byte[320], 16000, 2, 1);
            yield return new(new byte[320], 22050, 2, 1);
        }
    }
    private sealed class BlockedFailureTts : ITextToSpeechProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text, TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken Token)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(Token); throw new IOException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
