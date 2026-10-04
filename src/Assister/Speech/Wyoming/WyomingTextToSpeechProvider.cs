using Assister.Diagnostics;
using Assister.Contracts;
using System.Runtime.CompilerServices;

namespace Assister.Speech.Wyoming;

public sealed class WyomingTextToSpeechProvider(WyomingEndpoint Endpoint) : ITextToSpeechProvider
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string Text,
        TextToSpeechOptions Options, [EnumeratorCancellation] CancellationToken CancellationToken)
    {
        using var Trace = RunTracing.CurrentKind == "TextToSpeech" ? null : RunTracing.Start("TextToSpeech", "Text to speech", "Invoke the Wyoming synthesis provider.");
        Trace?.Input(new { spokenText = Text, voice = Options.Voice });
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        Timeout.CancelAfter(TimeSpan.FromSeconds(Endpoint.TimeoutSeconds));
        var Token = Timeout.Token;
        await using var Connection = await WyomingConnection.ConnectAsync(Endpoint, Token);
        var Info = await Connection.DescribeAsync(Token);
        if (!Info.Supports("tts"))
        {
            throw new InvalidOperationException("Wyoming endpoint does not advertise speech synthesis.");
        }
        var Data = new Dictionary<string, object?> { ["text"] = Text };
        if (Options.Voice is not null)
        {
            Data["voice"] = new { name = Options.Voice };
        }
        await Connection.Writer.WriteAsync(WyomingEvent.Create("synthesize", Data), Token);
        var Rate = 0;
        var Width = 0;
        var Channels = 0;
        while (await Connection.Reader.ReadAsync(Token) is { } Event)
        {
            if (Event.Type == "audio-start")
            {
                Rate = Event.Data.GetProperty("rate").GetInt32();
                Width = Event.Data.GetProperty("width").GetInt32();
                Channels = Event.Data.GetProperty("channels").GetInt32();
                if (Rate <= 0 || Width is < 1 or > 4 || Channels <= 0)
                {
                    throw new InvalidDataException("Invalid Wyoming audio format.");
                }
            }
            else if (Event.Type == "audio-chunk")
            {
                if (Rate <= 0 || Event.Payload.Length % (Width * Channels) != 0)
                {
                    throw new InvalidDataException("Invalid Wyoming audio chunk.");
                }
                yield return new(Event.Payload, Rate, Width, Channels);
            }
            else if (Event.Type == "audio-stop")
            {
                if (Rate <= 0)
                {
                    throw new InvalidDataException("Wyoming audio stopped before start.");
                }
                Trace?.Output(new { encoding = "PCM", sampleRate = Rate, sampleWidth = Width, channels = Channels });
                Trace?.Complete();
                yield break;
            }
            else if (Event.Type == "error")
            {
                throw new InvalidOperationException("Wyoming synthesis failed.");
            }
        }
        throw new EndOfStreamException("Wyoming connection ended before audio-stop.");
    }
}
