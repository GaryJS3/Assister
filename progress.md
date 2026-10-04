# Assister progress

Updated: October 3, 2026. This file records completed work and verification; [roadmap.md](roadmap.md) records planned work. [PROJECT.md](PROJECT.md) is the original specification.

## Current state

The service skeleton is deployed and healthy. Native C# Wyoming speech providers are implemented and tested. External service checks pass. The deployed shared text coordinator now implements deterministic HA controls and queries; end-to-end voice and conversations remain pending.

| Milestone | Status | Evidence / remaining work |
| --- | --- | --- |
| 1. Application skeleton | Complete | .NET 10 ASP.NET Core, contracts project, EF Core SQLite migration, structured logs, Docker/Compose, health/status APIs, unit/integration projects; container runs as non-root with persistent data. |
| 2. Wyoming | Core implementation complete | Buffered framing with partial reads, bounded lengths, JSON data merging, payloads, describe/info, STT, TTS audio streaming and transcript events. Fake TCP tests and real TTS/STT round trip pass. Providers are not yet wired into the pipeline. |
| 3. Home Assistant | Core implementation deployed and verified | Authenticated REST/WebSocket, state/service/registry reads and event subscription passed through the probe. Persistent C# client, state cache, registry joins/entity index, startup event replay, bounded messages and reconnect/backoff are deployed and verified with a fresh 1965-entity cache and delivered state events. |
| 4. Direct intents | Core implementation deployed and verified | Separate parser/classifier/slots, conservative area/name/alias/ID resolver, validated light/switch on/off, brightness and cached temperature/state handlers; shared `POST /api/test/message`. Fake/HTTP tests pass. Live off/on, 50% brightness, cache updates and restoration passed for the user-selected `light.living_room_lights`; office temperature query passed. |
| 5. LLM and tools | Dependency verified; implementation pending | Model discovery and completion passed. Compatible client, streaming, broker, selection and HA tools are pending. |
| 6. Conversations | Pending | Persistence, continuity and bounded context. |
| 7. Satellites | Controller API verified; implementation pending | EchoMuse web/setup APIs passed. Controller transport, physical-device audio and lifecycle are untested. ESPHome Native API settings remain reserved. |
| 8. Timers and memory | Pending | Persistent timers, announcements, SQLite FTS and memory tools. |
| 9. Streaming latency | Pending | Sentence-boundary synthesis, early playback and barge-in. |

## Verified baseline

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
