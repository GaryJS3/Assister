# Assister progress

Updated: October 4, 2026. This file records completed work and verification; [roadmap.md](roadmap.md) records planned work. [PROJECT.md](PROJECT.md) contains durable architectural decisions.

## Current state

Deployed multi-target brightness correction: live trace eef525e6-5556-4809-b672-44abbe774b1c
contained "Can you set both the living room light and kitchen light to 100?". The polite
prefix prevented direct matching; fallback model searches assumed HA areas and failed.
Direct normalization now accepts can/could/would-you prefixes and "both"; brightness lists
resolve every named target independently before one bounded, deduplicated control. Missing
or ambiguous members prevent all controls, and whole names containing "and" remain intact.
The exact transcript and these safety cases are regression-tested. Production API controls
and independent HA state checks pass; new spoken multi-target acceptance remains pending.

Deployed stop/cancel increment: the satellite Stop API and UI cancel active capture, processing,
synthesis and playback through a shared operation lifetime. Exact spoken stop/cancel phrases are
silent deterministic commands, bypassing LLM/TTS and conversation locks. EchoMuse stop uses the
matching session/request ID, suppresses duplicate terminal commands, removes cancelled audio and
ignores stale playback events. Immediate next-turn recovery covers completed and cancelled turns.
Explicitly dismissed timer announcements are consumed to prevent replay. Device button cancellation
during reply playback is regression-tested. Production announcement stop and subsequent playback
pass; physical LED/audible-stop verification of this revision remains pending. Interruption by
speech requires a new device microphone turn. The user verified wake-and-stop during thinking
on the earlier deployment; wake during speech is unavailable and deferred by agreement.

October 4 deployment and production acceptance: source revision
`0fbc4a707f6ce668b6fcf72f6496a711fcba722e` was committed, pushed and synced through Dockhand.
An image built from that exact Git archive replaced only the Assister container through
Dockhand; running image ID and revision match. Environment fingerprints and mounts are preserved.
The ESPHome bridge container/image remain unchanged at `ed8d918`. All 125 C# tests pass
(110 unit, 15 integration). /health is Healthy; HA REST/WebSocket/registry probes pass, with
fresh cache and both EchoMuse satellites online. The previously offline HA Voice remains offline.

Production controls set `light.living_room_lights` and `light.kitchen_main_lights` together to
40% and 100% through direct intents, with independent HA state readback and no model rounds.
Traces: `85edb09f-9e3e-4903-9a3f-3196eb8f9ff0` and
`1d7a5646-a901-4464-893d-f96bc29ceef9`. A missing second target prevented partial changes.
Original on/128 and on/194 settings respectively were restored and verified. Recovery backup:
`/data/production-two-light-backup-20261004220703.json`. The actual kitchen group is named
"Kitchen Main Lights" and has no HA area assignment; "kitchen light" still needs an alias.

MBedroom's real announcement reported playback started, then deterministic `please stop`
through the production text API cancelled it and released the session. The next announcement
completed with controller acknowledgment. Final runtime is Online, Assister output idle,
without an active session or last error. These checks do not establish microphone recognition,
audible interruption or LED behavior; see [service-validation.md](docs/service-validation.md).

The integration foundation now registers Home Assistant and Assister with qualified action IDs, declared inputs and integration-specific execution dispatch. `/integrations.html` lists actions and connection status; the intent editor/inspector/confirmation show ownership explicitly. Existing JSON definitions receive an idempotent upgrade with a startup backup checkpoint. All 107 tests pass (93 unit, 14 integration), including upgrade and dispatch guards. Additional services, connection editing and dynamic plugin installation remain future work; see [integrations.md](docs/integrations.md).

October 4 integration acceptance: source revision `26f69719c1fa7abadf0df641b5836b042bfd4061` is deployed and healthy. Home Assistant reports Connected with five actions; Assister reports Available with three. All seven existing definitions retained their names, patterns, responses and enabled states. The startup database backup `assister.db.before-20261004202608.db` was created. Five saved recognition tests pass; the exact living-room brightness preview resolves the qualified Home Assistant action and correct entity. Native time and a read-only Home Assistant state request succeed through normal routing. The deployed integration page and new intent selectors were checked in the browser. Both containers retain environment/mounts; the bridge image is unchanged.

The intent workbench is implemented at `/intents.html`: persisted definition/example management, shared runtime matching, draft inspection, explicit execution with stale-plan rejection, conflict detection and recent LLM request candidates. All 104 automated tests pass (91 unit, 13 integration). Browser checks cover custom intent creation/editing, inspection, saved tests and explicit Reply execution. See [intent-workbench.md](docs/intent-workbench.md) for grammar and Home Assistant reference/license boundaries. Live deployment acceptance is recorded separately below.

October 4 intent workbench acceptance: deployed source revision `bc4673934268081dc10bbbc75da3872f6b0ebb18` is healthy. Live inspection resolves the exact brightness regression to `light.living_room_lights`, 100 percent, without sending a command. All five seeded recognition tests pass; the fictional `light.desk` example correctly reports missing-device resolution separately. Temporary custom Reply creation, catalog persistence, normal request routing and deletion passed; native time also routes directly. Production browser inspection passed. App environment/data mounts are preserved; bridge image/environment remain unchanged and its mount list remains empty. Physical control was not repeated for this workbench increment; earlier direct/LLM control acceptance remains the evidence for actual device commands.

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

## Satellite diagnostics and hardening without device interaction

Implemented and tested locally after the physical reply confirmation: request traces retain
activation/wake word, provider arrival timestamp, transport/session identity and dispatch
latency. Microphone traces include first audio arrival and source-channel metadata; bounded
satellite microphone/playback events carry request trace IDs. Ownership/media events include
meaningful observed values, and the satellite UI links correlated events directly to traces.
The UI explains unavailable runtime wake-word configuration. Terminal exception results now
include their transport session ID; repeated playback-start events are ignored.

A new in-process gRPC test disconnects during capture, verifies cancellation and session
cleanup, reconnects and verifies refreshed capabilities/unknown ownership. It also verifies
secret redaction in activation traces, event history and the satellite API. A fake adapter test
verifies retry delays 5/10/20/40/60/60 seconds and that cancellation stops retries. Strengthened
existing transport tests verify ordered activation stages, durations, request/event correlation,
and duplicate playback-start suppression. Validation: 63 unit tests, 9 C# integration tests
and 12 adapter tests pass (84 total), JavaScript syntax and git diff --check pass.
No device playback, ownership changes, restarts or physical tests were performed for this
increment. These changes are not deployed. A final read-only image check found Assister at
f47804f (the separately committed timing-bar UI work) and the bridge at ed8d918; both were preserved.

## Forked EchoMuse external voice integration

Implemented native C# external voice v1 provider for the user's deployed fork: one authenticated
controller WebSocket, authenticated approved-device inventory, persistent logical EchoMuse
satellites, independent bounded turn queues and exact device/session correlation. It uses the
existing Wyoming STT/TTS and shared coordinator, respects controller endpointing, suppresses
no-speech responses, cancels matching work and refreshes inventory/re-authenticates on reconnect.
Standalone announcements/stop use request/device pairs through SatelliteManager. Output is
opaque device-reachable 48 kHz mono PCM WAV, retained through acknowledged playback and then
removed. Configuration credentials remain outside Git. Runtime writes not yet verified through
the REST API remain unsupported; normal observed volume/mute/wake information is displayed.

Local validation: 69 unit tests and 10 C# integration tests pass. New tests cover shared-pipeline
voice delivery, stale/wrong IDs, cancellation/no speech, distinct standalone playback, disconnect,
invalid audio, and a real in-process authenticated WebSocket multiplexing two devices with the
same session ID. Existing 12 bridge adapter tests remain applicable (91 total). Live encrypted
HA Voice transport is preserved. At this implementation checkpoint deployment and physical
EchoMuse acceptance were pending; the subsequent verified results are recorded below.

EchoMuse deployment verified: Assister image revision a784345 is running through Dockhand;
HA Voice bridge image is unchanged at ed8d918. Non-EchoMuse environment fingerprints and all
mounts match the pre-deployment snapshot. Dockhand's cached Compose definition required adding
the three provider settings; normal enable/user settings are in the existing environment file,
while the password is a masked Dockhand secret value. No credentials were committed.
Authenticated external WebSocket hello and approved inventory succeeded. Kitchen and MBedroom
are online and OwnedByAssister; Kitchen remains muted, MBedroom unmuted. Restart during image
update released/reacquired the single backend connection successfully. /health is Healthy.

The first speaker test exposed integer multiplication overflow while resampling longer TTS;
a784345 fixes it and adds a five-second duration/sample-value regression test. Validation now
passes 70 unit, 10 C# integration and 12 bridge adapter tests (92 total). The repeated live
MBedroom announcement returned HTTP 200 in 3.565 seconds with controller play_started and
play_finished acknowledgment, then returned output to idle with zero active sessions. The
controller fetched Assister's opaque HTTP WAV directly. At that point, audible confirmation and
actual EchoMuse wake/microphone → shared routing → reply were pending; see acceptance below.

HA Voice was already unreachable before this deployment (bridge logs at 15:53 UTC onward show
Native API network unreachable; app deployment began later). Its configuration, image and
volume were preserved. No HA integrations, device mute states or controller firmware were changed.

Live MBedroom wake/request verified on 2026-10-04 at 12:24:57 EDT: device-reported
`hey_jarvis_v0.1` activation, 16 kHz mono microphone capture, Wyoming STT transcript
"What time is it?", shared coordinator language-model response "It is 12:25 PM EDT.",
Wyoming TTS, opaque HTTP WAV delivery and matching controller playback completion all
succeeded. Trace `8f213ddb-ee7b-456d-b23c-6757a19087df` took 8.348 seconds including
capture and playback; post-input STT finalization was 1.044 seconds, model generation
0.544 seconds and TTS 0.305 seconds. Runtime returned to Complete/idle with no active
voice session or error. The user explicitly confirmed hearing the reply and LEDs returning
to idle afterward. This completes the initial MBedroom physical wake/request/reply acceptance.
Deterministic HA control, physical cancellation and media coexistence remain acceptance work.

Reboot/reconnect verification: the user identified the spinning-LED startup issue as line
endings in the EchoMuse fork scripts, with no Assister fix required. Assister recorded the
MBedroom disconnect at 12:26:35 EDT and reconnect at 12:48:20 EDT on 2026-10-04. A subsequent
voice request at 13:09:37 EDT completed microphone/STT/shared routing/TTS/playback in 7.677
seconds without errors (trace `0d56b136-3c42-41d2-a86f-2ba10876a780`). Live verification
found Online, OwnedByAssister, observed firmware v2.17.0, idle output and no active session
or last error; Assister health is Healthy. Re-ran all 70 unit and 10 C# integration tests:
80 passed. Reconnect and post-reconnect voice processing are verified; this reboot occurred
outside an active request, so mid-request disconnect acceptance remains separate.

## Verified-control correction - October 4, 2026

The live MBedroom trace ae373758-6d2c-450d-82f6-a4fe27c3fefe transcribed
"Set living room light to 100." but neither matched a direct intent nor selected ha_control.
The model searched/read entities and invented successful control without any service call.
The living-room light group also lacks an HA area assignment.

Implemented: bare light brightness numbers and turn-to-percent direct commands; full named
entity resolution before inferred room filtering; imperative LLM control selection; strongest
search matches with equal-score ambiguity retained; compact brightness/capability data; and
code enforcement that LLM control replies require a successful ha_control result. Failed or
missing control cannot become a successful model response. The shared HA action client now
reads back power/brightness for targets and nested light-group members within a bounded timeout,
without retrying mutations. Both direct and LLM paths use this confirmation.

Local validation: 81 unit and 10 integration tests pass (91 C# tests). Regression coverage includes
the exact transcript, unnamed-area light group, missing/rejected LLM control, read-only tool
selection, ranked search ambiguity and accepted-service/no-state-change behavior. The C# probe
adds --controls --light to verify direct and LLM actions, matching tool traces, independent HA
state/member reads and cache delivery, with durable original-state backup and restoration.
Deployment and live control verification are pending at this implementation checkpoint.

First deployment/live check: exact bare-number request and direct off/on/50%/100% controls
passed independent HA state/cache checks. The LLM still emitted prose without using offered
tools; the new guard correctly returned failed, and the probe restored the original on/255
state. Follow-up requires search/control tool use until a confirmed mutation, then disables
further tool calls for the final reply. Control rounds offer only the relevant search/control
schemas, with search required before control is offered. Live revalidation remains pending.

Required-tool replay isolated a model/history interaction: repeated direct-control confirmation
phrases in the probe conversation caused the server to return HTTP 400 because the model did
not emit the required call. A fresh request succeeded, and replaying the same history with an
explicit current-request execution instruction produced ha_search. The control user message
now emphasizes that earlier confirmations are previous actions and that this request needs
new search/control execution. A phase-order/history regression test brings C# validation to
82 unit plus 10 integration tests (92 total). No model service was modified.

Final live verification: application revision 7e7f08a is deployed through Dockhand and its
running image label matches 7e7f08aeb42d0d7d6c24886433b3b954b5c6f6bd. /health is Healthy.
All environment fingerprints and mounts match the pre-change snapshot. ESPHome bridge image
remains ed8d918. The --controls probe passed with zero failures against light.living_room_lights:
direct off/on/50% and the exact "Set living room light to 100." transcript; LLM "Dim Living Room
Lights to 40 percent", "Could you make Living Room Lights fully bright?", and "Could you switch
Living Room Lights off?". Each LLM request had exactly one successful matching ha_control trace.
Independent HA state/member reads and the subscribed cache confirmed every requested result.
Direct controls took 246-358 ms; LLM controls took 9.34-10.14 seconds including search/control
rounds and final reply. The original on/brightness-255 setting was restored and verified;
backup is /data/live-light-backup-20261004181316.json. Subscriber delivered 1834 events during
the run, including unrelated HA activity. 92 C# tests pass and git diff --check passes.
These checks use the shared text/voice coordinator through the test API; a new microphone/
spoken control request after this deployment has not yet been physically confirmed.
