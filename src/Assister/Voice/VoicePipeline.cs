using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Satellites;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

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
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        if (!Satellites.BeginSession(Satellite.SatelliteId, Session.Id, Timeout)) { Run.Complete("busy"); return new(Session, null, "busy"); }
        Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.CapturingAudio);
        RequestResult? Result = null;
        var Phase = "stt-failed";
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
            if (StopCommands.IsStop(Transcript.Text))
            {
                await Satellites.StopAsync(Satellite.SatelliteId, Timeout.Token);
                Timeout.Token.ThrowIfCancellationRequested();
            }
            Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Routing);
            await Satellite.SendEventAsync(new("transcribed", Transcript.Text, Session.Id), Timeout.Token);
            await Satellite.SendEventAsync(new("processing", SessionId: Session.Id), Timeout.Token);
            Phase = "processing-failed";
            if (Configuration.GetValue("Voice:StreamingEnabled", false) && Coordinator is IStreamingRequestCoordinator Streaming)
            {
                var Text = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
                var Sentences = new SentenceBuffer();
                var DeliveredText = new System.Text.StringBuilder();
                using var OutputLifetime = CancellationTokenSource.CreateLinkedTokenSource(Timeout.Token);
                var ResponseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var OutputPhase = "playback-failed";
                var FirstAudioAtOutput = (DateTimeOffset?)null;
                var Output = DeliverAsync();
                try
                {
                    Result = await Streaming.ProcessStreamingAsync(new(Transcript.Text, Satellite.SatelliteId, Satellite.Area, ConversationId),
                        async (Delta, Token) =>
                        {
                            Token.ThrowIfCancellationRequested();
                            if (DeliveredText.Length < 16000) { DeliveredText.Append(Delta.AsSpan(0, Math.Min(Delta.Length, 16000 - DeliveredText.Length))); }
                            foreach (var Sentence in Sentences.Append(Delta))
                            {
                                try { await Text.Writer.WriteAsync(VoiceFormatter.Format(Sentence), OutputLifetime.Token); }
                                catch (OperationCanceledException) when (!Timeout.IsCancellationRequested && OutputLifetime.IsCancellationRequested) { break; }
                            }
                        }, Timeout.Token);
                    Session = Session with { ConversationId = Result.ConversationId };
                    RunTracing.Response(Result.Response, Result.SpokenResponse ?? VoiceFormatter.Format(Result.Response), Result.Outcome, Result.HandledBy, Result.ConversationId);
                    await Satellite.SendEventAsync(new("response", Result.Response, Session.Id, Result.ConversationId), Timeout.Token);
                    if (Result.Outcome != "succeeded" && Result.HandledBy == "language-model"
                        && VoiceFormatter.Format(DeliveredText.ToString()).Trim() != (Result.SpokenResponse ?? VoiceFormatter.Format(Result.Response)).Trim())
                    {
                        // Discard partial generation on WAV providers; streaming providers must stop on cancellation.
                        Phase = "processing-failed";
                        throw new InvalidOperationException("Incomplete model output cannot finish playback.");
                    }
                    foreach (var Sentence in Sentences.Append("", Final: true))
                    {
                        if (OutputLifetime.IsCancellationRequested) { break; }
                        try { await Text.Writer.WriteAsync(VoiceFormatter.Format(Sentence), OutputLifetime.Token); }
                        catch (OperationCanceledException) when (!Timeout.IsCancellationRequested && OutputLifetime.IsCancellationRequested) { break; }
                    }
                    Text.Writer.TryComplete();
                    ResponseReady.TrySetResult();
                    try { await Output; }
                    catch { Phase = OutputPhase; throw; }
                    using var Latency = RunTracing.Start("Playback", "Streaming latency", "Measure first synthesized audio independently of device playback acknowledgement.");
                    Latency.Metadata(new { streaming = true, timeToFirstAudioMilliseconds = FirstAudioAtOutput is { } First ? (double?)(First - Session.StartedAt).TotalMilliseconds : null,
                        earlyPlaybackSupported = Satellite is IStreamingAudioPlayback { SupportsStreamingPlayback: true } });
                    Latency.Complete();
                    await Satellite.SendEventAsync(new("finished", SessionId: Session.Id), Timeout.Token);
                    Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Complete);
                    Run.Complete(Result.Outcome);
                    return new(Session, Result, Result.Outcome);
                }
                finally
                {
                    OutputLifetime.Cancel();
                    Text.Writer.TryComplete();
                    ResponseReady.TrySetCanceled();
                    try { await Output; } catch (Exception) { /* Observed by the main await, or superseded by routing/cancellation. */ }
                }
                async Task DeliverAsync()
                {
                    try
                    {
                        using var Playback = RunTracing.Start("Playback", "Streaming delivery", "Overlap sentence synthesis with response generation; the provider controls audio buffering.");
                        await Satellite.SendAudioAsync(Audio(OutputLifetime.Token), OutputLifetime.Token);
                        Playback.Complete();
                    }
                    catch { OutputLifetime.Cancel(); throw; }
                }
                async IAsyncEnumerable<AudioChunk> Audio([EnumeratorCancellation] CancellationToken Token)
                {
                    OutputPhase = "tts-failed";
                    using var Synthesis = RunTracing.Start("TextToSpeech", "Streaming speech synthesis", "Synthesize bounded sentences as model text becomes safe to speak.");
                    var Voice = new TextToSpeechOptions(Configuration["TextToSpeech:Voice"]);
                    var InputText = Text.Reader.ReadAllAsync(Token);
                    var Chunks = Tts is IStreamingTextToSpeechProvider StreamingTts
                        ? StreamingTts.SynthesizeStreamAsync(InputText, Voice, Token) : SynthesizeSentences(InputText, Voice, Token);
                    await foreach (var Chunk in Chunks.WithCancellation(Token))
                    {
                        if (FirstAudioAtOutput is null) { Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Synthesizing); }
                        FirstAudioAtOutput ??= DateTimeOffset.UtcNow;
                        yield return Chunk;
                    }
                    Synthesis.Complete();
                    OutputPhase = "playback-failed";
                    Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.WaitingForPlayback);
                    await ResponseReady.Task.WaitAsync(Token);
                }
                async IAsyncEnumerable<AudioChunk> SynthesizeSentences(IAsyncEnumerable<string> InputText, TextToSpeechOptions Voice,
                    [EnumeratorCancellation] CancellationToken Token)
                {
                    await foreach (var Sentence in InputText.WithCancellation(Token))
                        await foreach (var Chunk in Tts.SynthesizeAsync(Sentence, Voice, Token).WithCancellation(Token)) { yield return Chunk; }
                }
            }
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
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested || Satellites.IsStopRequested(Session.Id))
        {
            Run.Complete("cancelled");
            Satellites.Stage(Satellite.SatelliteId, Session.Id, VoiceSessionState.Cancelled);
            if (CancellationToken.IsCancellationRequested) { throw; }
            return new(Session, Result, "cancelled");
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
    [GeneratedRegex(@"\bmph\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MilesPerHour();
    public static string Format(string Text) => MilesPerHour().Replace(
        Temperature().Replace(Text.Replace("**", "").Replace("`", ""),
            Match => $"{Match.Groups["value"].Value} degrees {(Match.Groups["unit"].Value == "F" ? "Fahrenheit" : "Celsius")}"),
        "miles per hour");
}
