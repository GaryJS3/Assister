# Voice latency measurements

S0 implementation started October 23, 2026, from application revision
`b1eb4189dd431331224f6ad5b96c03ba35a319a6`. Changes and benchmark sources are in
the working tree; the running server has not been updated by this work.

## Reproduce

```powershell
dotnet build tools/Assister.VoiceBench -c Release
dotnet run --project tools/Assister.VoiceBench -c Release --no-build -- 20 > docs/voice-baseline-offline.json
dotnet run --project tools/Assister.VoiceBench -c Release --no-build -- --live http://10.44.0.33:8081 10 > docs/voice-baseline-live-text.json
dotnet test Assister.sln -c Release
node --test tests/browser/*.test.cjs
```

The offline command uses the actual `VoicePipeline` with simulated STT, request
coordination, TTS and transport. It performs no device actions or network calls.
It runs a warmup per scenario/mode, rotates mode order, and saves every sample,
median and nearest-rank p95. The same per-sentence generation/synthesis work is
used in all modes. Direct controls are simulated; the tool scenario holds text
until the simulated tool-capable round is complete. Tones are disabled explicitly.
This measures orchestration overlap and buffering under known artificial delays,
not inference performance or physical playback. Twenty samples per cell provide
an initial comparison, not a reliable production tail distribution.

The live command submits three fixed read-only questions in fresh conversations
and records client round-trip and server routing times, outcomes and run IDs.
It intentionally excludes actual control scenarios. It creates diagnostic and
conversation records. Failed outcomes stay in raw results and are excluded from
successful latency summaries. Process/cache warmth is uncontrolled; this is a
text-only baseline without STT, TTS or playback. A successful outcome does not
independently validate factual answer quality or that a particular tool was used.

## Observability

Voice runs now add a `Voice latency` diagnostic step with monotonic server
milestones: capture completion, final transcript, first speakable coordinator
text, routing completion, first nonempty synthesized PCM, transport completion
and pipeline finish. First observation wins, including concurrent output.
Early failure/cancellation leaves unobserved milestones absent. These offsets
share a clock and can be subtracted; they exclude audio capture duration when
using capture completion as the origin.

First speakable text is the coordinator callback, which can occur after model
text has been held for tool validation. It is not necessarily first model token.
Capture completion is not actual speech end: it includes provider endpointing,
transport buffering and trailing silence. PCM duration is already recorded
separately in microphone/STT traces. Actual speech end, endpoint decision,
first delivered audio and first audible playback remain explicitly unknown in
the new aggregate; transport completion is not an inferred playback start.
Existing individual playback acknowledgements remain available in their own
trace steps. No device/server clock synchronization is assumed.

## Client capability audit

| Path | Current boundary before playback | Evidence and limitation |
| --- | --- | --- |
| Rich browser / SDK | Complete response, complete TTS WAV, audio URL | `InteractionRunner`, `RichSpeech`, protocol `tts.audio`; explicit playback acknowledgements; S1 required for early audio |
| EchoMuse | Complete WAV, URL to controller | `EchoMuseConnection.Turn.SendAudioAsync`; matching completion acknowledgement; no established incremental playback |
| ESPHome bridge | Complete audio URL through bridge | `BridgeTransportService`; playback-started/finished events; no established incremental playback |
| Generic streaming satellite | PCM enumeration when `SupportsStreamingPlayback` is true | Pipeline can overlap synthesis/generation; only fake transport verified here |
| Tone-capable satellite with tones enabled | Buffered coordinator branch | Current pipeline bypasses generation streaming for this case; benchmark numbers do not apply to this path |

## Initial controlled results

Twenty measured samples per scenario/mode, after one warmup per cell. Median
first useful **simulated** playback in milliseconds after capture completion:

| Scenario | Buffered generation/delivery | Overlap, buffered delivery | Overlap, streaming delivery | Buffered → streaming reduction |
| --- | ---: | ---: | ---: | ---: |
| Simulated direct control | 95.10 | 94.85 | 94.61 | 0.5% |
| Short knowledge | 352.05 | 305.17 | 192.48 | 45.3% |
| Tool-capable round held for validation | 480.76 | 477.34 | 431.76 | 10.2% |
| Long reply | 675.09 | 529.48 | 192.58 | 71.5% |

Long-reply p95 first simulated playback: 678.71 ms buffered versus 194.14 ms
streaming. Median total completion: 691.24 ms versus 546.37 ms (21.0% lower).
These are comparisons of existing pipeline modes, not a new production speedup.
Raw samples and complete p95/completion tables are in
[voice-baseline-offline.json](voice-baseline-offline.json).

## Initial live text baseline

Running model settings observed after measurement: `Qwen3.6-35B-A3B-UD-Q4_K_XL`,
temperature 0.2, maximum 1024 tokens, thinking disabled, timeout 30 seconds.
Runtime revision, model backend version/hardware and cache state were not verified.
Ten fresh conversations per scenario, serial requests:

| Scenario | Successful requests | Server median | Server p95 |
| --- | ---: | ---: | ---: |
| Moon phases, one sentence | 10/10 | 3749.05 ms | 3918.95 ms |
| Rain formation, five sentences | 0/10 | unavailable | unavailable |
| Office temperature, read-only | 10/10 | 18087.41 ms | 21079.51 ms |

The rain request is a reliability finding, not a fast response. An inspected run
required `ha_search` for `rain`, then failed in model round two with
`InvalidOperationException`.
The trace does not establish the precise provider failure cause. This route needs
investigation before using rain explanations as a successful long-answer latency
comparison. No runtime settings were changed to make the benchmark pass.

The original live stdout export included build output and was discarded during
report processing. The checked-in live baseline was recovered from all thirty
retained diagnostic runs, and reports server run duration only. Individual client
round-trip samples are unavailable. Future commands above separate building from
JSON output. Sanitized per-stage durations/status/failure categories are preserved
in [voice-baseline-live-stages.json](voice-baseline-live-stages.json); prompts,
answers, tool arguments/results and credentials are excluded.

Validation: Release solution build succeeded with no warnings/errors; 387 unit /
component tests plus 52 integration tests passed, along with 10 browser tests.

## Remaining S0 acceptance

Physical first useful playback, real speech-end/endpoint timing and client clock
uncertainty need capture/device instrumentation. Pauses, interrupted real speech,
cold versus warm inference, model/backend/hardware identity and action completion
must be measured independently. Automated cancellation, synthesis failure,
incomplete-model-output, sentence splitting and legacy buffering regressions are
covered by existing tests. These do not prove physical interruption behavior.

S1 should reuse these scenarios and add actual rich-client chunk playback tests.
No speedup feature or deployed latency improvement is claimed by S0.
