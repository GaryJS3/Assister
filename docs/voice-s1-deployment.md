# S1 production deployment and live measurements

Verified October 23, 2026 EDT (October 24 UTC). Running application revision:
`205a490006f816edc9a2a40de6bff24ae9b68917`.

## Deployment identity and preservation

- Dockhand Git stack 10, `assister`, Automation environment 8.
- Git stack `lastCommit=205a490`, `syncStatus=synced`, no sync error.
- Running image: `sha256:a65daf4f16b91d123272d8cc5229b84b02896189dd78c1750e602a5ead7639bf`.
- Container: `edfaecfe53fd97e4be4a5ddb598093d97b10cd7421478f56b3849e32878de866`.
- Image built from an exact Git archive of the committed, pushed revision and
  labeled with its full revision. Dockhand performed the service recreation.
  Automatic Dockhand builds remain disabled; only the application image was built.
- Full environment fingerprint matches the original container. The
  `assister_assister-data` volume is still mounted at `/data`.
- ESPHome bridge container and image are unchanged; no satellite controls or
  announcements were issued. Tests created conversations/diagnostic records only.
- `/health` and `/api/status` are Healthy. Application restart count remains zero.
- Original application image retained as `assister-assister:rollback-b1eb418`.

Dockhand's deploy-stream response incorrectly reported `success:false, error:"No result"`.
This was not treated as deployment evidence: subsequent Git state, container ID,
image digest/revision label, health, environment/mount fingerprints and live
requests independently established the running state. No environment overrides
were rewritten to work around the response.

Before/after snapshots: [before](voice-s1-deploy-before.json),
[final runtime](voice-s1-deploy-after.json). These snapshots contain hashes and
operational identifiers, not environment credential values.

## Changes discovered by live browser testing

The initial S1 application release was `f992e38`. Actual browser testing on the
private HTTP address exposed an existing `crypto.randomUUID()` secure-context
restriction that prevented request submission. Revision `49e259f` added a UUID v4
fallback using `crypto.getRandomValues`, and moved ID creation inside the submit
error handler. A regression test covers private HTTP origins.

Real speech then played successfully, starting before synthesis completed. The
retained WAV event overwrote the browser's `Speaking` label with `Audio ready`
while audio continued. Revision `205a490` preserves the live streaming status;
its browser regression test and actual deployed browser both passed.

## Live timing comparison

The C# service probe submitted read-only Moon-phase questions in fresh
conversations to the actual deployed model and TTS provider. No audio was played
by the timing probe and no playback acknowledgements were fabricated. Each mode
was tested three times per scenario, alternating mode order. These are preliminary
samples, not production tail-latency estimates. Model output length and cache
state were not fixed, so small differences include normal inference variance.

The final comparison ran without concurrent inference probes. It measures from
HTTP submission to the first authenticated, downloaded, validated playable WAV.
Microphone capture, endpointing, STT, browser decoding, tones and physical output
are excluded. The configured model is `Qwen3.6-35B-A3B-UD-Q4_K_XL`, temperature
0.2, maximum 1024 tokens, thinking disabled. TTS output is mono 22,050 Hz / 16 bit.

| Scenario | Buffered median first playable audio | Streaming median | Change |
| --- | ---: | ---: | ---: |
| One-sentence knowledge answer | 5064.93 ms | 4770.47 ms | 294.46 ms / 5.8% sooner |
| Four-sentence knowledge answer | 6798.56 ms | 5909.52 ms | 889.04 ms / 13.1% sooner |

Nearest-rank p95 with only three observations is effectively the maximum:
short replies 5220.80 → 4834.14 ms, long replies 6906.42 → 6170.46 ms.

After response-text completion, median first playable audio for long replies
improved from 1678.37 to 713.40 ms. However, median full generation/synthesis
completion increased from 6798.58 to 7562.80 ms (764.22 ms / 11.2% later).
Earlier first speech is the benefit; full synthesis throughput did not improve
in this live sample. Per-sentence provider calls and varying generated answers
are potential contributors and were not independently isolated.

Every streaming recording reconstructed to the same PCM byte count as its
segments, with ordered indices, a matching final segment count and no TTS failure.
All 12 final requests passed. For these requests, **no segment preceded complete
response text**: the existing tool-capable-round speech policy held model output
until validation. Streaming reduced synthesis/output buffering; the synthetic
73.5% first-audio gain does not transfer directly to this deployed model path.

Before deployment the six buffered baseline requests passed, with medians
4938.73 ms (short) and 6505.45 ms (long). The alternating comparison on the final
runtime is more useful for evaluating the mode difference than comparing against
earlier requests with different inference/cache conditions.

Evidence:
[pre-deployment baseline](voice-s1-predeploy-live.json),
[final alternating comparison](voice-s1-postdeploy-live.json),
[model settings](voice-s1-live-model-settings.json).
An initial post-deployment smoke comparison also passed all 12 requests; its
first sample overlapped a dependency inference check and is retained separately
in [smoke results](voice-s1-postdeploy-smoke.json).

## Browser, cancellation and regression checks

- Actual in-app browser using production TTS: started and completed streamed
  playback with no duplicate retained-WAV autoplay. In the first complete browser
  run, the client scheduling acknowledgement preceded `tts.completed` by
  **1369.38 ms**. These are server receipt times for client acknowledgements,
  not independently measured acoustic onset or synchronized device clocks.
- Final revision: browser showed `Speaking` while the recording was already
  available; **Stop speech** changed it to `Playback stopped`, with matching
  playback started/stopped events. Reloaded history remained silent.
- Real server cancellation after the first audio segment: terminal status
  `cancelled`, observed in **44.79 ms** after the cancellation request began;
  **zero segments after `interaction.cancel_requested`**. This measures server
  cancellation, not physical-device stop latency.
- Live dependency checks passed: database health, HA authenticated REST,
  HA WebSocket/registries, STT capability, real TTS synthesis, synthetic TTS-to-STT
  recognition, model listing/completion and EchoMuse controller web/setup API.
- Release suite: **449 .NET tests** (391 unit/component, 58 integration),
  **18 browser-script tests**, and clean whitespace checks.

One observer-only repeat run disconnected while draining the last request's
audio around terminal stream closure. The server completed the request and
remained healthy with zero restarts. The probe was corrected to request
`followPlayback=true` and dispose its subscription after consuming the terminal
event. The final 12-request comparison completed cleanly. This does not establish
that every possible network disconnect is eliminated; reconnect/replay support
remains part of the client contract. [Observer finding](voice-s1-postdeploy-observer-finding.json).

Browser and cancellation evidence:
[completed browser run](voice-s1-live-browser-events.json),
[final browser stop](voice-s1-live-browser-stop-events.json),
[server cancellation](voice-s1-live-cancellation.json),
[Speaking screenshot](voice-s1-live-speaking.png),
[Stop screenshot](voice-s1-live-stopped.png).

## Scope and remaining work

Web rich-client streaming is deployed and verified with real provider audio and
browser lifecycle events. Refresh an already open client to load the new script.
Native application players retain buffered fallback unless they implement the
segment contract. EchoMuse/ESPHome playback remains buffered and is S2 work.
Human-audible intelligibility, end-of-actual-speech to first useful sound, client
clock uncertainty and physical satellite playback/stop acceptance are still open.
Follow-up performance work should address model latency and provider throughput
without bypassing the validated speech/tool policy.
