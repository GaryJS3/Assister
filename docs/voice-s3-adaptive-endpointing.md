# S3 adaptive turn completion

Status: C# implementation and evaluation complete; early-cutoff quality acceptance
has not passed. Production rollout uses **shadow mode**, which records proposed
decisions while the existing controller retains capture completion. No live
endpoint-latency gain is claimed while shadow mode is selected.

## Capture ownership and implementation

Assister's existing fallback uses a 900 ms quiet window. Installed EchoMuse runtime
`7be7df32b1481c93dfeb98c5dd43b54ee7d8973c` instead uses its controller's speech gate
and clock: 1000 ms since the last detected speech, with a 2500 ms grace from first
speech. Device/provider termination can also end capture. Changing the C# fallback
alone cannot change those EchoMuse decisions. ESPHome remains offline; manually
ended rich-client recordings retain their existing push-to-talk behavior.

The new C# policy combines DC-corrected energy/noise-floor calibration with a CPU
Smart Turn v3.2 ONNX classifier. It evaluates after 300 ms quiet, accepts completion
at probability >= 0.8, extends a confidently incomplete pause (<= 0.2) to 1600 ms,
and falls back to 900 ms for ambiguous/model-failure cases. Speech shorter than
120 ms cannot open the semantic gate by itself. No-speech and duration bounds are
5 and 20 seconds of audio. Quiet durations and confidence thresholds are bounded,
configurable options under `Voice:Endpointing`.

Audio is evaluated in 20 ms frames independent of transport chunk boundaries.
Only the last eight seconds are retained for the classifier. Inference runs off
the capture path with one shared inference slot, a 400 ms analysis deadline and
bounded cleanup. Resumed speech invalidates/cancels the old score. Startup warmup
avoids paying initial model loading on the first pause. Missing/corrupt models,
model errors, saturation, cancellation and incomplete results preserve fallback.
Unsupported formats pass through unchanged in shadow/provider mode; active local
semantic capture requires 16 kHz mono S16 input. Shadow never configures a provider
or sends an input-end command.

A small adapter is implemented and tested separately in EchoMuse commit
`150644c2388f7ba9c9d8c8a4b3f288bbc3955192`, branch `codex/adaptive-endpointing`:
[controller change](https://github.com/GaryJS3/EchoMuse/commit/150644c2388f7ba9c9d8c8a4b3f288bbc3955192).
It advertises optional `adaptiveEndpointing`, accepts a bounded per-turn quiet
limit, and acknowledges an explicit input-end request. Ownership, device, session,
phase and request IDs are validated; controller-side speech-byte offsets reject
requests superseded by newly detected speech. Feedback cannot be endpointed.
The C# side waits for the provider's real `audio_end` after acceptance. Old
controllers keep their original behavior. **This controller adapter is not deployed**
because shadow evaluation does not require it and active quality acceptance failed.

## Model and preprocessing verification

Model: [Pipecat Smart Turn](https://huggingface.co/pipecat-ai/smart-turn-v3),
`smart-turn-v3.2-cpu.onnx`, revision
`f766f81d3cfdf7737ac64aad813d91bbfd56bf93`, SHA256
`2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f`.
The Docker build pins and checks the asset hash and includes its BSD-2-Clause
license. Application inference and feature extraction are C#; the upstream
reference was used only for numerical validation, and the existing Python
controller receives only the small protocol adapter above.

The extractor follows the [upstream inference recipe](https://github.com/pipecat-ai/smart-turn/blob/main/inference.py):
16 kHz PCM; last-eight-second truncation/left-zero-padding; full-window mean and
variance normalization; periodic 400-sample Hann window, 160-sample hop, centered
reflection padding; 80 Slaney-normalized mel filters; log10 floor, eight-decade
clipping and Whisper scaling. Output shape is [1,80,800].

Against the independently installed upstream `WhisperFeatureExtractor` on a real
speech clip, maximum absolute feature error was 1.9431e-5 and mean error 9.93e-8.
A checked-in synthetic golden fixture tests 99 reference values across frequency
bins, padding, speech boundaries and quiet frames. Silence, resampling-independent
frame handling and bounded context are also tested. See
[model provenance](voice-s3-model-provenance.json).

## Labeled quality evaluation

[Raw scores and CPU measurements](voice-s3-model-evaluation.json) cover 100 English
clips: the first 50 complete and 50 incomplete examples among the first 500 rows
of [the public v3.1 test dataset](https://huggingface.co/datasets/pipecat-ai/smart-turn-data-v3.1-test),
revision `22b4ec796d222da74beac14245603fba7653ac3f`. IDs, component dataset and PCM
hashes are recorded. Clips were decoded to 16 kHz mono S16 with FFmpeg and given
300 ms additional quiet. The sample contains natural and synthetic speech and is
not proven independent of the model's training data. It is a reproducible sanity
set, not a validated household cutoff rate or an independent model benchmark.

| Dataset label | Samples | Complete score >= 0.8 | Incomplete score <= 0.2 |
| --- | ---: | ---: | ---: |
| Complete | 50 | 48 | 0 |
| Incomplete | 50 | **3** | 39 |

The three incomplete false-complete scores were approximately 0.933, 0.954 and
0.980. Changing a threshold after seeing those examples would require a fresh
independent evaluation. The initial 25-clip subset missed these failures; the
larger set is the relevant quality finding.

Warm workstation median model-call wall time was 121.63 ms (nearest-rank p95
137.03 ms). Feature and inference medians were 62.20 and 65.52 ms respectively;
median process CPU consumption was 218.75 ms per call. These are separate medians,
not additive components. Single ORT thread settings do not imply that all FFT/
process work consumes only one core. Cold loading is reported separately.

## Paced before/after timing

[Recorded replay](voice-s3-paced-replay.json) sends actual public PCM at 20 ms
intervals, paced to a monotonic clock, through the original C# VAD and the new
C# capture wrapper/classifier. Mode order alternates. The model is warmed first.
This is a fallback-policy comparison, not the installed EchoMuse clock, device
speech gate, STT, model routing, response synthesis or acoustic playback.

Eight clips (four of each label) were replayed once per policy. One complete clip
caused an early-energy termination marker under both policies and is excluded
from endpoint-delay comparisons. These markers use RMS estimates of the last
voiced frame, not human word alignments, so they are not verified human speech
cutoffs. The remaining three complete pairs averaged:

- Conservative fallback: **906.40 ms** from the final delivered voiced frame.
- Adaptive policy: **395.78 ms**, **510.61 ms / 56.3% sooner**.

For four incomplete clips, mean quiet-to-end increased from 899.22 to 1336.73 ms.
Three received the intended 1600 ms hold. The other incomplete clip ended early
with a semantic-complete decision. A dedicated repeat records this explicitly:
[incomplete replay failure](voice-s3-replay-incomplete-case.json). Its measured
pause-to-end was 922.46 -> 531.77 ms, an undesirable early decision. The same
clip's score on a longer quiet tail was incomplete: pause-length sensitivity is
another reason static full-clip accuracy cannot establish safe live cutoffs.

The replay's two policies have the same count of early-energy markers (one each)
but that does not satisfy semantic-quality acceptance. Both the 3/50 labeled
errors and the explicit incomplete replay failure remain open. The measured
half-second saving is a potential benefit of active mode, not an enabled live gain.

## Tests and rollout

Tests cover confidence/ambiguous/error fallback, incomplete holds, resumed-speech
stale results, noise/DC/clicks, short-speech gating, malformed PCM, frame/chunk
boundaries, eight-second bounds, no-speech/duration limits, independent golden
features, shadow transparency, unsupported formats, cancellation/bounded cleanup,
remote acknowledgement ordering, provider capability refusal, and exact
request/device/session matching. The fake controller regression suite includes
shadow mode with tones and streaming playback. Controller tests cover protocol
ownership, limits, feedback guards, stale speech and closed-phase rejection.

Application defaults remain `Voice:Endpointing:Enabled=false` and `Shadow=true`.
The Compose switches are `VOICE_ADAPTIVE_ENDPOINTING_ENABLED` and
`VOICE_ADAPTIVE_ENDPOINTING_SHADOW`. Shadow deployment selects both true.
Provider diagnostics report `endpointingPolicy`; voice run traces record proposed
endpoint time, decision, model cost/failures, stale scores and acknowledgement
state. Push-to-talk/manual capture is not implicitly converted to automatic VAD.

Active enablement requires resolving the failure cases and validating on fresh
complete/incomplete speech, real pauses/background noise and actual household
microphones. Physical EchoMuse timing and ESPHome hardware acceptance remain open.
