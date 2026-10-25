# STT delay investigation and measured candidate

October 24, 2026 EDT. Production Assister remains on `814dbdd`; the overlap
candidate is implemented but disabled and has not been deployed.

## Cause

Assister uses Wyoming Faster Whisper at `10.0.0.43:10300` on the Tesla P4.
The running container is LinuxServer Faster Whisper v3.5.0-ls59, configured for
Whisper large-v3, CUDA, int8_float32 and beam size 1. Its installed handler buffers
the input and calls the batch recognizer only after `audio-stop`. Assister sends
that stop immediately after enumerating capture; there is no fixed one-second
sleep in its transcription client.

Correlated examples from the original audit:

| Input recording | Capture-end to recognizer start | Recognizer processing | Observed post-capture delay |
| --- | ---: | ---: | ---: |
| Kitchen singular command, Oct 23 23:07 | 61 ms | 939 ms | 1,000 ms |
| Kitchen plural command, Oct 23 23:07 | 68 ms | 1,414 ms | 1,482 ms |
| Kitchen off command, Oct 23 23:07 | 54 ms | 1,031 ms | 1,085 ms |

These timestamps were correlated between the running recognizer logs and
Assister traces. Most of the measured delay is batch recognition after capture,
not an application timer. The application already specifies English, beam size
is already one, and the running recognizer is using the GPU.

## Experiments

All experiments replayed retained PCM into STT only. No coordinator, Home
Assistant control, physical playback or microphone ownership change was involved.
Original transcripts are agreement references, not independent human ground
truth. Punctuation/case and common 10/15 numeral spellings are normalized.

| Comparison | Baseline median | Candidate median | Outcome |
| --- | ---: | ---: | --- |
| Small model through Speaches, default VAD | 986 ms | 506 ms | Rejected: changed words, including Wait to Peace |
| Small model, VAD off | 991 ms | 478 ms | Wait recovered, but another question still changed |
| Turbo model, VAD off | 990 ms | 955 ms | Little gain on this hardware; another question changed |
| Existing model, overlap after 240 ms quiet | 989 ms | 569 ms | Rejected setting: observed p95 rose 1,066 to 1,298 ms |
| Existing model, overlap after 400 ms quiet | 1,007 ms | 448 ms | Rejected setting: observed p95 rose 1,094 to 1,431 ms |
| Conservative overlap, tuning set | 995 ms | 800 ms | 20% lower median; p95 1,050 to 1,064 ms |
| Conservative overlap, held-out audit recordings | 1,072 ms | 1,056 ms | Essentially unchanged; p95 1,113 to 1,112 ms |

The final candidate requires 800 ms of low-energy audio after clear speech.
Across the combined eleven recordings, two measured replays per mode, median
post-capture delay was **999 to 858 ms (14% lower)**. Observed p95 was
**1,108 to 1,102 ms**, essentially unchanged. Both baseline and candidate matched
the reference on **22/22 measured replays**. The held-out set has three original
audited commands, six measured requests per mode; it was not used to choose the
quiet window. The gain depends strongly on the recording's quiet tail.

Warmup: one replay per clip/mode, discarded. Mode order rotates across clips and
repetitions; requests are serial. Final overlap comparisons replay 100 ms PCM
chunks at their audio rate, including retained silence. Timings use a client
monotonic clock and include the remaining network/provider work after source
enumeration ends. Model-download and first-load times are outside measured
samples. Percentiles use nearest rank; these small samples are not a production
tail estimate. Replay cadence is not an independent physical-speech-end measure.

Sanitized per-request results, including rejected experiments:
[stt-benchmarks-2026-10-24.json](stt-benchmarks-2026-10-24.json).
Original trace timings: [stt-original-live-timings.json](stt-original-live-timings.json).
Raw audio, local audio paths and transcript text are not committed.

## Implementation

`OverlappingSpeechToTextProvider` prepares one recognition request on the unchanged
Wyoming model while the capture owner continues listening. It never changes the
capture endpoint, publishes prepared partials, routes commands or commits a
transcript before capture ends. Resumed speech invalidates preparation, including
a final fragment shorter than an analysis frame. The complete recording is then
recognized normally. Empty/failed preparation also falls back. At most one
preparation plus one final recognition runs per turn.

Clear speech arms preparation at RMS 0.015; the quiet/resumption detector uses
the more sensitive RMS 0.003 threshold. A paced-arrival guard avoids speculative
requests for ordinary buffered uploads. Non-mono-16-kHz-PCM16 inputs pass straight
to the original provider. Source cancellation/failure discards the prepared
result. Audio buffering is bounded to thirty seconds for the analyzed format.

Configuration is in C# and appsettings JSON; no compose or server model settings
were changed:

- `SpeechToText:OverlapEnabled=false` (default; original path)
- `SpeechToText:OverlapQuietMilliseconds=800`
- `SpeechToText:OverlapQuietThreshold=0.003`

An `STT overlap` diagnostic step records preparation time, capture completion,
post-capture duration, result reuse and invalidation by resumed speech.

## Validation and rollout decision

Release suite: **429 unit/component plus 58 integration tests passed**. Coverage
includes no early transcript, complete input on resumed speech, cancellation of
in-flight preparation, capture failure, empty/failed preparation, partial-frame
speech, buffered-input bypass, format passthrough and the feature switch.

The candidate remains disabled. It helps some recent recordings but delivered
negligible improvement on the held-out audited commands. Shorter quiet windows
had worse tail latency. Multi-satellite contention, whispered/soft resumed speech
and physical first-useful-response latency have not been validated. Preparation
can add GPU work when it is discarded; it does not make model inference itself
faster. There is not enough evidence here to claim a general replacement for the
one-second recognizer delay or enable this globally.

For repeatable local comparisons, create a private JSON manifest containing
`wyomingHost`, `wyomingPort`, `httpBaseUrl`, `models` (empty for Wyoming-only),
`overlap: true`, `overlapQuietMilliseconds: 800`, and `clips` with `name`, WAV
`path`, and reference transcript. Then run:

```text
dotnet build tools/Assister.VoiceBench -c Release
dotnet tools/Assister.VoiceBench/bin/Release/net10.0/Assister.VoiceBench.dll --stt manifest.json 2 --paced
```

`--stt-analyze manifest.json` reports energy and quiet-tail statistics without
calling STT. The benchmark uses mono 16 kHz PCM16 WAVs. Test audio never enters
the control pipeline.

## Shared-service cleanup

Speaches' installed unload route held its model-manager lock while invoking an
unload callback that reacquired the same lock. Its request deadlocked during
cleanup of the temporary Turbo model. Speaches was restarted through Dockhand;
its original small-model preload, loaded-model list, health and a real
transcription were verified afterward. Its configuration/image/mounts were not
changed. Turbo files remain cached on disk but are no longer resident. The
Wyoming container and Assister STT configuration were not changed or restarted.

Model reference for the exploratory Turbo conversion:
[deepdml/faster-whisper-large-v3-turbo-ct2](https://huggingface.co/deepdml/faster-whisper-large-v3-turbo-ct2).
