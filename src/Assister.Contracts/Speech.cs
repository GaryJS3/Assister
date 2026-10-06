namespace Assister.Contracts;

public sealed record AudioChunk(ReadOnlyMemory<byte> Pcm, int SampleRate, int SampleWidth, int Channels);
public sealed record SpeechToTextOptions(string Language = "en");
public sealed record TextToSpeechOptions(string? Voice = null);
public sealed record TranscriptionResult(string Text, string? Language);

public interface ISpeechToTextProvider
{
    Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> Audio,
        SpeechToTextOptions Options, CancellationToken CancellationToken);
}

public interface ITextToSpeechProvider
{
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text,
        TextToSpeechOptions Options, CancellationToken CancellationToken);
}

public interface IStreamingTextToSpeechProvider : ITextToSpeechProvider
{
    IAsyncEnumerable<AudioChunk> SynthesizeStreamAsync(IAsyncEnumerable<string> Text,
        TextToSpeechOptions Options, CancellationToken CancellationToken);
}

public interface IStreamingSpeechToTextProvider : ISpeechToTextProvider
{
    Task<TranscriptionResult> TranscribeStreamingAsync(IAsyncEnumerable<AudioChunk> Audio, SpeechToTextOptions Options,
        Func<string, CancellationToken, Task> OnPartial, CancellationToken CancellationToken);
}
