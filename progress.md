# Assister progress

Updated: October 4, 2026. This file records completed work and verification; [roadmap.md](roadmap.md) records planned work. [PROJECT.md](PROJECT.md) contains durable architectural decisions.

## Current state

The existing deployment has user-confirmed button/microphone input, office temperature responses, acknowledged physical playback and a timer announcement (see October 4 evidence below). The local satellite realignment extends that implementation; exclusive wake ownership and HA media coexistence acceptance are separate and are not yet established.

| Milestone | Status | Evidence / remaining work |
| --- | --- | --- |
| 1. Application skeleton | Complete | .NET 10 ASP.NET Core, contracts project, EF Core SQLite migration, structured logs, Docker/Compose, health/status APIs, unit/integration projects; container runs as non-root with persistent data. |
| 2. Wyoming | Core implementation complete | Buffered framing with partial reads, bounded lengths, JSON data merging, payloads, describe/info, STT, TTS audio streaming and transcript events. Fake TCP tests and real TTS/STT round trip pass. Providers are not yet wired into the pipeline. |
| 3. Home Assistant | Core implementation deployed and verified | Authenticated REST/WebSocket, state/service/registry reads and event subscription passed through the probe. Persistent C# client, state cache, registry joins/entity index, startup event replay, bounded messages and reconnect/backoff are deployed and verified with a fresh 1965-entity cache and delivered state events. |
| 4. Direct intents | Core implementation deployed and verified | Separate parser/classifier/slots, conservative area/name/alias/ID resolver, validated light/switch on/off, brightness and cached temperature/state handlers; shared `POST /api/test/message`. Fake/HTTP tests pass. Live off/on, 50% brightness, cache updates and restoration passed for the user-selected `light.living_room_lights`; office temperature query passed. |
| 5. LLM and tools | Implemented locally; live validation in progress | Fixed-schema broker, selected HA tools, bounded tool loop and unmatched routing; optional Qwen thinking setting and bounded history summaries. |
| 6. Conversations | Implemented locally | SQLite turns, satellite ownership checks, five-minute continuity, bounded recent turns/topic notes; raw tool results excluded. |
| 7. Satellites | Existing physical pipeline verified; management realignment implemented locally | Shared C# coordinator, authenticated gRPC, HA Voice microphone/temperature/playback and timer acknowledgment have prior live evidence. New ownership/capability/configuration/UI hardening has local automated evidence; wake/media coexistence acceptance remains pending. |
| 8. Timers and memory | Implemented locally | Restart-safe timers, deferred announcements, SQLite FTS memory and compact persisted tool audit. |
| 9. Streaming latency | Pending | Sentence-boundary synthesis, early playback and barge-in. |

## Verified baseline

- Milestone 5 client increment: 47 automated tests pass overall. Fake HTTP checks cover configured routing and fixed-length JSON, selected tool serialization, completion tool calls, byte-fragmented UTF-8/SSE, text-only completion, tool argument assembly, malformed/truncated/oversized responses, cancellation and sanitized HTTP failures. The existing direct endpoint still passes with an unreachable model URL. No text fallback or tool execution has been enabled by this increment.

- Thirty-five automated tests pass: framing validation, fragmented/multiple frames, truncated payloads, fake STT/TTS sequences, persistent database migrations, startup API behavior, HA authentication rejection, registry joins, state updates/removal, startup event replay and disconnect/reconnect cache reload, deterministic classification/resolution, ambiguity, plural area controls, stale/invalid/unsupported targets, fixed service routes and HTTP endpoint behavior.
- Eight live checks pass from the deployed container following the user's firewall correction: health, HA REST, HA WebSocket/registries/events, STT discovery, TTS synthesis, speech round trip, LLM models/completion and EchoMuse web/setup APIs.
- The speech round trip recognized “This is a test of the local voice assistant.”
- A repeatable C# probe is available in `tools/Assister.ServiceProbe`. See [service-validation.md](docs/service-validation.md) for timings, counts and test limits.

The original dependency checks alone did not prove HA control or real state-event delivery; both are now verified by the direct-intent checks below. LLM streaming/tool execution and satellite microphone/playback remain untested.

## Deployment and follow-ups

- Application/probe revision: `1b76012b66dba2dd752e8ca9ad1845eaaef90280`. Dockhand Git stack synced `1b76012`; the rebuilt running image carries that exact revision label. Image digest: `sha256:bebaf94696c1ba6a3714131c8e895046a0236170f16766f98bf3c733038d6341`. Container is running as `app`, with the original volume and environment fingerprint preserved.
- Dockhand-managed deployment is running. Its image build fails with `mkdir /root/.docker: read-only file system`; build-on-deploy is temporarily disabled. Image creation currently requires a separate build before Dockhand deployment.
- Corrected the model URL to include its service port and selected the model advertised by that endpoint. The server requires fixed-length JSON request bodies; chunked requests returned HTTP 400.
- HA credential rotation is planned by the user after a deployment response exposed it in tool output. Rotation and post-rotation authentication have not been verified. Never print entire deployment responses; select only non-secret status fields.

## Update convention

Update this file when a milestone changes or a meaningful check completes. Record what is implemented, what is deployed, and what was actually tested separately. Keep credentials out of every document.

## Latest MVP verification

HA runs as a hosted persistent WebSocket connection when URL/token are configured. Status includes cache freshness and received state-event counts. Disconnect retains stale states; reconnect authenticates and reloads snapshots. Registry metadata refreshes on reconnect. Message/event buffers are bounded and logs omit remote payloads and exception messages.

The shared `IRequestCoordinator` handles text requests without an LLM dependency. Explicit plural area controls can target multiple lights; uncertain singular requests require clarification. HA service names are fixed mappings, target/domain/capability/service validation runs before dispatch, stale connectivity blocks mutations, redirects are disabled, and state-changing requests are not retried. Conversation IDs are only echoed until milestone 6.

Validation: 35 tests pass and `git diff --check` passes. The exact Git archive was built over SSH and deployed through Dockhand. Health/database and HA connection/cache checks passed. The C# live probe successfully tested the explicitly authorized light and restored its original off state. Direct control request timings were 126.9 ms (off), 38.5 ms (on) and 39.7 ms (50% brightness); HA state and subscribed cache were confirmed at each step. The probe observed 55 state events during the run (not all events were necessarily from the test light). An office temperature query resolved the correct area sensor at confidence 1.0 in 15.7 ms. Live ambiguous office-light input returned clarification without a control. HA credential rotation, dynamic registry subscriptions, other control categories, and voice integration remain pending.

The host filesystem filled during build/probe staging. Removed only unused probe platform binaries and this build's identified cache records; installed a Linux-only framework-dependent probe. Last disk check showed approximately 126 MB available on the 15 GB root filesystem. More capacity or approved broader cleanup is needed before further builds. No other containers or volumes were changed.

The direct coordinator has no LLM dependency and the HTTP test uses an unreachable model URL. The live model server was not stopped. Complete satellite acceptance and persisted conversation behavior remain pending.


## MVP implementation increment - October 3, 2026

Latency acceptance update: deployed revision `cd104c6` ended real microphone capture on silence after 3.36 seconds of audio. STT including capture took 4.42 seconds; the office-temperature direct request took 51 ms, and playback completed. The user confirmed the delay is much better. All 58 automated tests pass. The full session duration includes the entire spoken response.

October 4 update: revision `f96b8ea` is deployed in Assister and the bridge. HA Voice button input, office temperature resolution, synthesis and physical spoken playback are confirmed by the user and correlated traces. Playback uses the announcement media player and 48 kHz mono FLAC; successful turns now wait for a playing-to-idle acknowledgment. A timer announcement also completed with an acknowledgment. The persistent volume and HA/STT/TTS/model environment fingerprint were preserved. Speech capture currently takes 16–21 seconds in observed turns; endpointing is the next correction. Forecast/history acceptance, spoken controls and streaming/barge-in remain pending. Earlier deployment notes below are historical.

56 automated tests pass, including native timer restart persistence, FTS search/delete, selected-tool validation and loop limits, satellite conversation separation, STT/TTS failure handling, and authenticated gRPC microphone input/WAV delivery. Deployment and spoken acceptance are still pending for this increment.

HA Voice 0a587e at 10.44.65.164 authenticated through the bridge and advertised voice feature flags 125. This was a read-only capability inspection. The key stays outside Git and is stored as a Dockhand secret. EchoMuse's installed controller supports only HA as its backend, so the HA Voice device is the initial adapter target. Host disk space was rechecked: 45 GB free, resolving the previous disk blocker.

Live isolated text validation passed the office temperature query, but model requests sometimes finished with length at 500 tokens and were correctly rejected. A direct function-call contract check passed. The voice deployment now explicitly disables Qwen thinking and raises its bounded token cap to 1024; that change still needs live loop validation. No shared model service was stopped.

## Satellite realignment - October 4, 2026

Implemented locally: additive normalized gRPC metadata/capabilities/configuration/ownership and
playback identity, persisted satellite registration/desired wake words, volatile observed state,
bounded event history, capability-gated runtime APIs and Satellites list/detail page. Existing
encrypted bridge, mono microphone PCM, Wyoming, shared coordinator, FLAC/HTTP response and
announcement playback are preserved. Timers now use SatelliteManager.AnnounceAsync.

Wake-word validation covers reported models, active count, drift and readback. Firmware values
remain read-only. Ownership stays Unknown until device voice activation confirms it; a short
error-log subscription detects explicit other-client conflict. Conflict is passive with manual
retry after release. Reconnect delay grows from 5 to 60 seconds. Diagnostic warning logs use
60-second leases, bounded sanitized events, and explicit device log disable.

Duplicate starts and late audio/playback acknowledgments cannot replace/finish another session.
Cancellation now sends a terminal result to release bridge busy state and RUN_END to reset
hardware feedback; bridge shutdown also ends feedback while the native connection is available.
Synthesis failures retain completed text and now report tts-failed separately from playback-failed.
Audio objects have opaque identifiers, count/size bounds, 2-minute TTL and periodic cleanup.

Local validation: 63 unit tests, 8 C# integration tests and 9 isolated adapter tests pass (80 total).
Tests cover exclusive ownership conflict/recovery, wake proof, model read/update, normalized mask
125, multi-channel metadata, announcement completion when music resumes, playback failure,
cancellation/next-session independence, duplicate/late events, persistence/drift, unsupported
actions and redacted events. JavaScript syntax checks and git diff --check pass. The actual
local browser renders list/detail and disables offline unsupported playback controls.

During baseline live inspection, a wake activation reached Assister microphone/STT capture,
then cancelled after 12.3 seconds without transcript. The user reported LEDs remaining active
after cancellation; the old bridge had no terminal cancellation acknowledgment. A targeted
Dockhand bridge restart completed to clear that state. This is evidence of the old deployment,
not verification of the new reset code. New physical wake/playback, wake-word changes, HA entity
disable/media coexistence, conflict/reboot and LED-reset acceptance remain pending. See the
acceptance ledger in docs/satellite-architecture.md. No Milestone 9 work was added.

Deployment follow-up: revision 5c4880c was built and deployed through Dockhand with credentials
and mounts preserved. On Home Assistant 2026.9.4, disabling only the ESPHome Assist Satellite
entity before enabling/reloading its ESPHome entry retains normal entities and media_player.
Both Native API clients connected; an actual Okay Nabu activation confirmed Assister ownership.
The user heard the intentional Home Assistant media test. Assister's overlapping announcement
completion timed out, so duck/resume and successful announcement acceptance are NOT established.
The installed ESPHome 2026.5.2 firmware reports no configurable wake-word models (maximum zero),
despite physical wake detection. Runtime model changes are therefore unavailable on this firmware.
Follow-up fix uses native announcement acknowledgment after proven voice ownership, retains the
observed wake word across button activations, and disables unavailable wake configuration.
Validation: 71 C# tests and 11 adapter tests pass (82 total). Physical follow-up remains pending.
Revision ed8d918 deployed through Dockhand: both running image revision labels match the exact
commit; /health is Healthy, satellite is Online and playback IDLE. Environment fingerprints and
/data volume match pre-deployment values. No overlapping test audio is being generated while
awaiting the user's next physical request. This is readiness evidence, not a successful spoken
request or LED-reset acceptance.
Live request after ed8d918: trace 9e2f69f3-f9f0-4a03-83a8-30012d282ccc completed successfully
at 01:47 local. Transcript "What time is it?" traversed STT, shared coordinator/LLM, TTS,
FLAC HTTP delivery and native device playback acknowledgment. Total 9.642 seconds; STT 3.788,
LLM 0.711, TTS 0.493, device playback 4.073. Runtime returned playback IDLE, no active session,
no error, with voice ownership OwnedByAssister. The user confirmed that the LED responded
correctly and the audible reply was accurate. Successful request/playback/LED completion is
physically verified. Cancellation LED cleanup is a separate check; deterministic brightness,
media duck/resume, conflict and reboot remain pending.
