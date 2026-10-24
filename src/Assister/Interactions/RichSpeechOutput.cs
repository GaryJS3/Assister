using System.Threading.Channels;
using Assister.Contracts;
using Assister.Voice;

namespace Assister.Interactions;

// One bounded synthesis worker per opted-in interaction. Audio URLs are persisted independently of events.
public sealed class RichSpeechOutput : IAsyncDisposable
{
    private readonly Channel<string> Text = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
    private readonly SentenceBuffer Sentences = new();
    private readonly CancellationTokenSource Lifetime;
    private readonly CancellationToken Parent;
    private readonly InteractionStore Store;
    private readonly Guid Id;
    private readonly Task Work;
    private bool Finished;
    private bool Disposed;
    public RichSpeechOutput(Guid Id, ITextToSpeechProvider Tts, InteractionStore Store, IConfiguration Configuration, CancellationToken Token)
    {
        this.Id = Id; this.Store = Store; Parent = Token;
        Lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Lifetime.CancelAfter(TimeSpan.FromMinutes(3));
        Store.Append(Id, "tts.started", new { streaming = true, encoding = "wav-segments", maximumPcmBytes = 8 * 1024 * 1024 });
        Work = ConsumeAsync(Tts, Configuration);
    }
    public async Task AppendAsync(string Delta)
    {
        foreach (var Sentence in Sentences.Append(Delta)) await WriteAsync(Sentence);
    }
    private async Task WriteAsync(string Sentence)
    {
        try { await Text.Writer.WriteAsync(VoiceFormatter.Format(Sentence), Lifetime.Token); }
        catch (OperationCanceledException) when (!Parent.IsCancellationRequested && Lifetime.IsCancellationRequested) { }
    }
    public async Task CompleteAsync()
    {
        foreach (var Sentence in Sentences.Append("", Final: true)) await WriteAsync(Sentence);
        Text.Writer.TryComplete();
        await Work;
        Parent.ThrowIfCancellationRequested();
        Finished = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;
        if (!Finished) Lifetime.Cancel();
        Text.Writer.TryComplete();
        try { await Work; } catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        Lifetime.Dispose();
    }
    private async Task ConsumeAsync(ITextToSpeechProvider Tts, IConfiguration Configuration)
    {
        using var Pcm = new MemoryStream();
        using var Segment = new MemoryStream();
        AudioChunk? Format = null;
        var Index = 0;
        void Publish()
        {
            if (Segment.Length == 0 || Format is null) return;
            Lifetime.Token.ThrowIfCancellationRequested();
            if (Index >= 1024) throw new InvalidDataException("Too many speech segments.");
            Store.SaveSegment(Id, Index++, Wave(Segment.ToArray(), Format), Format);
            Segment.SetLength(0);
        }
        try
        {
            await foreach (var Sentence in Text.Reader.ReadAllAsync(Lifetime.Token))
            {
                await foreach (var Chunk in Tts.SynthesizeAsync(Sentence, new(Configuration["TextToSpeech:Voice"]), Lifetime.Token).WithCancellation(Lifetime.Token))
                {
                    if (Chunk.SampleRate is < 8000 or > 96000 || Chunk.SampleWidth is < 1 or > 4 || Chunk.Channels is < 1 or > 2
                        || Chunk.Pcm.Length % (Chunk.SampleWidth * Chunk.Channels) != 0 || Pcm.Length + Chunk.Pcm.Length > 8 * 1024 * 1024)
                        throw new InvalidDataException("Invalid speech audio.");
                    Format ??= Chunk;
                    if (Format.SampleRate != Chunk.SampleRate || Format.SampleWidth != Chunk.SampleWidth || Format.Channels != Chunk.Channels)
                        throw new InvalidDataException("Speech format changed.");
                    Pcm.Write(Chunk.Pcm.Span);
                    var Frame = Format.SampleWidth * Format.Channels;
                    var Target = Math.Min(Format.SampleRate / 2 * Frame, 65536 / Frame * Frame);
                    for (var Offset = 0; Offset < Chunk.Pcm.Length;)
                    {
                        var Count = Math.Min(Target - (int)Segment.Length, Chunk.Pcm.Length - Offset);
                        Segment.Write(Chunk.Pcm.Span.Slice(Offset, Count));
                        Offset += Count;
                        if (Segment.Length == Target) Publish();
                    }
                }
                Publish();
            }
            if (Format is null || Pcm.Length == 0) throw new InvalidDataException("No speech audio.");
            Lifetime.Token.ThrowIfCancellationRequested();
            Store.SaveAudio(Id, Wave(Pcm.ToArray(), Format));
            Store.Append(Id, "tts.completed", new { streaming = true, segmentCount = Index });
        }
        catch (OperationCanceledException) when (Parent.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Lifetime.Cancel();
            Store.Append(Id, "tts.failed", new ProtocolError("tts_failed", "Speech stopped. The text answer is still available.", true));
        }
    }
    private static byte[] Wave(byte[] Pcm, AudioChunk Format)
    {
        using var Output = new MemoryStream();
        using var Writer = new BinaryWriter(Output);
        Writer.Write("RIFF"u8); Writer.Write(Pcm.Length + 36 + (Pcm.Length & 1)); Writer.Write("WAVEfmt "u8); Writer.Write(16);
        Writer.Write((short)1); Writer.Write((short)Format.Channels); Writer.Write(Format.SampleRate);
        Writer.Write(Format.SampleRate * Format.Channels * Format.SampleWidth); Writer.Write((short)(Format.Channels * Format.SampleWidth));
        Writer.Write((short)(Format.SampleWidth * 8)); Writer.Write("data"u8); Writer.Write(Pcm.Length); Writer.Write(Pcm);
        if ((Pcm.Length & 1) != 0) Writer.Write((byte)0);
        return Output.ToArray();
    }
}
