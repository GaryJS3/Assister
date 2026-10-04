using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Satellites;
using System.Text.RegularExpressions;

namespace Assister.Voice;

public sealed record VoiceSession(Guid Id, string SatelliteId, string? Area, Guid? ConversationId, DateTimeOffset StartedAt);
public sealed record VoiceResult(VoiceSession Session, RequestResult? Request, string Outcome);

public sealed class VoicePipeline(ISpeechToTextProvider Stt, ITextToSpeechProvider Tts, IRequestCoordinator Coordinator,
    SatelliteManager Satellites, IConfiguration Configuration)
{
    public async Task<VoiceResult> RunAsync(ISatelliteConnection Satellite, Guid? ConversationId, CancellationToken CancellationToken)
    {
        var Session = new VoiceSession(Guid.NewGuid(), Satellite.SatelliteId, Satellite.Area, ConversationId, DateTimeOffset.UtcNow);
        if (!Satellites.BeginSession(Satellite.SatelliteId, Session.Id)) { return new(Session, null, "busy"); }
        using var Trace = RunTracing.Start("Voice session", "Receive microphone audio, transcribe, use the shared coordinator, synthesize and play the response.");
        Trace.Detail("sessionId", Session.Id);
        Trace.Detail("satellite", Session.SatelliteId);
        RequestResult? Result = null;
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        Timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await Satellite.SendEventAsync(new("transcribing", SessionId: Session.Id), Timeout.Token);
            var Input = Configuration.GetValue("SatelliteBridge:UseEnergyVad", false)
                ? VoiceActivityDetector.UntilSilenceAsync(Satellite, Configuration.GetValue("SatelliteBridge:VadThreshold", 0.015), Timeout.Token)
                : Satellite.ReceiveAudioAsync(Timeout.Token);
            var Transcript = await Stt.TranscribeAsync(Input,
                new(Configuration["SpeechToText:Language"] ?? "en"), Timeout.Token);
            if (string.IsNullOrWhiteSpace(Transcript.Text))
            {
                Trace.Complete("no-speech");
                return new(Session, null, "no-speech");
            }
            await Satellite.SendEventAsync(new("transcribed", Transcript.Text, Session.Id), Timeout.Token);
            await Satellite.SendEventAsync(new("processing", SessionId: Session.Id), Timeout.Token);
            Result = await Coordinator.ProcessAsync(new(Transcript.Text, Satellite.SatelliteId, Satellite.Area, ConversationId), Timeout.Token);
            Session = Session with { ConversationId = Result.ConversationId };
            Trace.Detail("traceId", Result.TraceId);
            // Preserve text before synthesis so a TTS failure cannot lose the completed response.
            await Satellite.SendEventAsync(new("response", Result.Response, Session.Id, Result.ConversationId), Timeout.Token);
            await Satellite.SendAudioAsync(Tts.SynthesizeAsync(VoiceFormatter.Format(Result.Response),
                new(Configuration["TextToSpeech:Voice"]), Timeout.Token), Timeout.Token);
            await Satellite.SendEventAsync(new("finished", SessionId: Session.Id), Timeout.Token);
            Trace.Complete(Result.Outcome);
            return new(Session, Result, Result.Outcome);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            Trace.Complete("cancelled");
            throw;
        }
        catch (Exception Error) when (Error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidOperationException)
        {
            var Outcome = Result is null ? "stt-failed" : "playback-failed";
            Trace.Complete(Outcome);
            return new(Session, Result, Outcome);
        }
        finally { Satellites.EndSession(Satellite.SatelliteId, Session.Id); }
    }
}

public static partial class VoiceFormatter
{
    [GeneratedRegex(@"(?<value>-?\d+(?:\.\d+)?)\s*°(?<unit>[FC])\b")]
    private static partial Regex Temperature();
    public static string Format(string Text) => Temperature().Replace(Text.Replace("**", "").Replace("`", ""),
        Match => $"{Match.Groups["value"].Value} degrees {(Match.Groups["unit"].Value == "F" ? "Fahrenheit" : "Celsius")}");
}
