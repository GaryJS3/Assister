using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Satellites;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Assister.Voice;

public sealed record VoiceSession(Guid Id, string SatelliteId, string? Area, Guid? ConversationId, DateTimeOffset StartedAt);
public sealed record VoiceResult(VoiceSession Session, RequestResult? Request, string Outcome);

public sealed class VoicePipeline(ISpeechToTextProvider Stt, ITextToSpeechProvider Tts, IRequestCoordinator Coordinator,
    SatelliteManager Satellites, IConfiguration Configuration, RunStore? Diagnostics = null)
{
    public async Task<VoiceResult> RunAsync(ISatelliteConnection Satellite, Guid? ConversationId, CancellationToken CancellationToken)
    {
        var Session = new VoiceSession(Guid.NewGuid(), Satellite.SatelliteId, Satellite.Area, ConversationId, DateTimeOffset.UtcNow);
        using var Run = RunTracing.BeginRun(Diagnostics ?? new RunStore(), "voice", Satellite.SatelliteId, Satellite.Area, Session.Id, ConversationId);
        using (var Activation = RunTracing.Start("Input", "Voice activation", "Create a voice session from a provider activation; a missing wake word may indicate a button or another device trigger."))
        {
            var Observed = (Satellite as IVoiceActivationContext)?.Activation;
            Activation.Metadata(new { voiceSessionId = Session.Id, ConversationId,
                transportSessionId = Observed?.TransportSessionId, wakeWord = Observed?.WakeWord,
                activationReceivedAt = Observed?.ReceivedAt, sessionCreatedAt = Session.StartedAt,
                dispatchLatencyMilliseconds = Observed is null ? (double?)null : Math.Max(0, (Session.StartedAt - Observed.ReceivedAt).TotalMilliseconds) });
            Activation.Complete();
            Satellites.Record(Satellite.SatelliteId, "Voice session created", Observed?.WakeWord, Session.Id);
        }
        if (!Satellites.BeginSession(Satellite.SatelliteId, Session.Id)) { Run.Complete("busy"); return new(Session, null, "busy"); }
        Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.CapturingAudio);
        RequestResult? Result = null;
        var Phase = "stt-failed";
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        Timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await Satellite.SendEventAsync(new("transcribing", SessionId: Session.Id), Timeout.Token);
            var Input = Configuration.GetValue("SatelliteBridge:UseEnergyVad", false) && Satellite is not IProviderEndpointing { OwnsEndpointing: true }
                ? VoiceActivityDetector.UntilSilenceAsync(Satellite, Configuration.GetValue("SatelliteBridge:VadThreshold", 0.015), Timeout.Token)
                : Satellite.ReceiveAudioAsync(Timeout.Token);
            DateTimeOffset? AudioEnded = null;
            DateTimeOffset? FirstAudioAt = null;
            long PcmBytes = 0;
            double AudioMilliseconds = 0;
            AudioChunk? Format = null;
            async IAsyncEnumerable<AudioChunk> ObserveAudio([EnumeratorCancellation] CancellationToken Token)
            {
                using var Capture = RunTracing.Start("Input", "Microphone audio", "Receive PCM microphone audio; only format, byte count and duration are retained.");
                Capture.Metadata(new { Session.SatelliteId, Session.Area, voiceSessionId = Session.Id, Session.ConversationId });
                await foreach (var Chunk in Input.WithCancellation(Token))
                {
                    if (FirstAudioAt is null)
                    {
                        FirstAudioAt = DateTimeOffset.UtcNow;
                        Satellites.Record(Satellite.SatelliteId, "Microphone started", SessionId: Session.Id);
                    }
                    Format ??= Chunk;
                    PcmBytes += Chunk.Pcm.Length;
                    if (Chunk.SampleRate > 0 && Chunk.Channels > 0 && Chunk.SampleWidth > 0)
                        AudioMilliseconds += 1000.0 * Chunk.Pcm.Length / (Chunk.SampleRate * Chunk.Channels * Chunk.SampleWidth);
                    yield return Chunk;
                }
                AudioEnded = DateTimeOffset.UtcNow;
                Satellites.Record(Satellite.SatelliteId, "Microphone stopped", SessionId: Session.Id);
                Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Transcribing);
                Capture.Output(new { encoding = "PCM", Format?.SampleRate, Format?.Channels, Format?.SampleWidth,
                    pcmByteCount = PcmBytes, audioDurationMilliseconds = AudioMilliseconds, firstAudioReceivedAt = FirstAudioAt,
                    sourceChannels = Satellites.State(Satellite.SatelliteId).MicrophoneSourceChannels, audioInputCompletedAt = AudioEnded });
                Capture.Complete();
            }
            TranscriptionResult Transcript;
            using (var Recognition = RunTracing.Start("SpeechToText", "Speech to text", "Stream microphone input to STT and measure finalization separately from audio capture."))
            {
                var Language = Configuration["SpeechToText:Language"] ?? "en";
                Recognition.Input(new { languageRequested = Language });
                Transcript = await Stt.TranscribeAsync(ObserveAudio(Timeout.Token), new(Language), Timeout.Token);
                var Arrived = DateTimeOffset.UtcNow;
                Recognition.Output(new { transcript = Transcript.Text, returnedLanguage = Transcript.Language });
                Recognition.Metadata(new { audioDurationMilliseconds = AudioMilliseconds, audioInputCompletedAt = AudioEnded,
                    finalTranscriptAt = Arrived, postAudioLatencyMilliseconds = AudioEnded is { } End ? (double?)(Arrived - End).TotalMilliseconds : null });
                Recognition.Complete();
                RunTracing.Transcript(Transcript.Text);
            }
            if (string.IsNullOrWhiteSpace(Transcript.Text)) { Run.Complete("no-speech"); return new(Session, null, "no-speech"); }
            Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Routing);
            await Satellite.SendEventAsync(new("transcribed", Transcript.Text, Session.Id), Timeout.Token);
            await Satellite.SendEventAsync(new("processing", SessionId: Session.Id), Timeout.Token);
            Phase = "processing-failed";
            Result = await Coordinator.ProcessAsync(new(Transcript.Text, Satellite.SatelliteId, Satellite.Area, ConversationId), Timeout.Token);
            Session = Session with { ConversationId = Result.ConversationId };
            Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Synthesizing, Result.ConversationId);
            var Spoken = Result.SpokenResponse ?? VoiceFormatter.Format(Result.Response);
            RunTracing.Response(Result.Response, Spoken, Result.Outcome, Result.HandledBy, Result.ConversationId);
            await Satellite.SendEventAsync(new("response", Result.Response, Session.Id, Result.ConversationId), Timeout.Token);
            Phase = "playback-failed";
            async IAsyncEnumerable<AudioChunk> Synthesize([EnumeratorCancellation] CancellationToken Token)
            {
                using var Synthesis = RunTracing.Start("TextToSpeech", "Text to speech", "Synthesize the exact voice-formatted response; audio bytes are not stored in diagnostics.");
                Phase = "tts-failed";
                var Voice = Configuration["TextToSpeech:Voice"];
                Synthesis.Input(new { spokenText = Spoken, voice = Voice });
                var Started = DateTimeOffset.UtcNow;
                double? FirstAudio = null;
                AudioChunk? OutputFormat = null;
                long Bytes = 0;
                double Duration = 0;
                await foreach (var Chunk in Tts.SynthesizeAsync(Spoken, new(Voice), Token).WithCancellation(Token))
                {
                    FirstAudio ??= (DateTimeOffset.UtcNow - Started).TotalMilliseconds;
                    OutputFormat ??= Chunk;
                    Bytes += Chunk.Pcm.Length;
                    if (Chunk.SampleRate > 0 && Chunk.Channels > 0 && Chunk.SampleWidth > 0)
                        Duration += 1000.0 * Chunk.Pcm.Length / (Chunk.SampleRate * Chunk.Channels * Chunk.SampleWidth);
                    yield return Chunk;
                }
                Synthesis.Output(new { encoding = "PCM", OutputFormat?.SampleRate, OutputFormat?.SampleWidth, OutputFormat?.Channels,
                    pcmByteCount = Bytes, audioDurationMilliseconds = Duration, timeToFirstAudioMilliseconds = FirstAudio });
                Synthesis.Complete();
                Phase = "playback-failed";
                Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.WaitingForPlayback);
            }
            using (var Playback = RunTracing.Start("Playback", "Playback delivery", "Deliver synthesized audio and wait for satellite playback completion."))
            {
                await Satellite.SendAudioAsync(Synthesize(Timeout.Token), Timeout.Token);
                Playback.Output(new { deliveryCompleted = true });
                Playback.Complete();
            }
            await Satellite.SendEventAsync(new("finished", SessionId: Session.Id), Timeout.Token);
            Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Complete);
            Run.Complete(Result.Outcome);
            return new(Session, Result, Result.Outcome);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            Run.Complete("cancelled");
            Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Cancelled);
            throw;
        }
        catch (Exception Error) when (Error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidOperationException)
        {
            using var Failure = RunTracing.Start("Error", "Pipeline failure", "The pipeline stopped before completion.");
            Failure.Metadata(new { failureCategory = Error.GetType().Name, phase = Phase });
            Failure.Complete("failed");
            // Keep the existing response-preservation behavior on synthesis/playback failures.
            Run.Complete(Phase);
            Satellites.Stage(Satellite.SatelliteId, Session.Id, Phase switch
            {
                "stt-failed" => VoiceSessionState.STTFailed,
                "tts-failed" => VoiceSessionState.TTSFailed,
                "processing-failed" => VoiceSessionState.RoutingFailed,
                _ => VoiceSessionState.PlaybackFailed
            });
            return new(Session, Result, Phase);
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
