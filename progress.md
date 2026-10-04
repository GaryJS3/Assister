# Assister progress

Updated: October 3, 2026. This file records completed work and verification; [roadmap.md](roadmap.md) records planned work. [PROJECT.md](PROJECT.md) is the original specification.

## Current state

The service skeleton is deployed and healthy. Native C# Wyoming speech providers are implemented and tested. External service checks pass, but Assister does not yet orchestrate an end-to-end conversation or control Home Assistant.

| Milestone | Status | Evidence / remaining work |
| --- | --- | --- |
| 1. Application skeleton | Complete | .NET 10 ASP.NET Core, contracts project, EF Core SQLite migration, structured logs, Docker/Compose, health/status APIs, unit/integration projects; container runs as non-root with persistent data. |
| 2. Wyoming | Core implementation complete | Buffered framing with partial reads, bounded lengths, JSON data merging, payloads, describe/info, STT, TTS audio streaming and transcript events. Fake TCP tests and real TTS/STT round trip pass. Providers are not yet wired into the pipeline. |
| 3. Home Assistant | Dependency verified; implementation pending | Authenticated REST/WebSocket, state/service/registry reads and event subscription passed through the probe. Persistent client, cache, index and reconnect logic are pending. |
| 4. Direct intents | Pending | Parser, resolver, handlers and shared text test endpoint. |
| 5. LLM and tools | Dependency verified; implementation pending | Model discovery and completion passed. Compatible client, streaming, broker, selection and HA tools are pending. |
| 6. Conversations | Pending | Persistence, continuity and bounded context. |
| 7. Satellites | Controller API verified; implementation pending | EchoMuse web/setup APIs passed. Controller transport, physical-device audio and lifecycle are untested. ESPHome Native API settings remain reserved. |
| 8. Timers and memory | Pending | Persistent timers, announcements, SQLite FTS and memory tools. |
| 9. Streaming latency | Pending | Sentence-boundary synthesis, early playback and barge-in. |

## Verified baseline

- Nine automated tests pass: framing validation, fragmented/multiple frames, truncated payloads, fake STT/TTS sequences, persistent database migrations and startup API behavior.
- Eight live checks pass from the deployed container following the user's firewall correction: health, HA REST, HA WebSocket/registries/events, STT discovery, TTS synthesis, speech round trip, LLM models/completion and EchoMuse web/setup APIs.
- The speech round trip recognized “This is a test of the local voice assistant.”
- A repeatable C# probe is available in `tools/Assister.ServiceProbe`. See [service-validation.md](docs/service-validation.md) for timings, counts and test limits.

These checks do not prove HA control, real state-event delivery, LLM streaming/tool execution or satellite microphone/playback behavior. Those need validation as their integrations are built.

## Deployment and follow-ups

- Code/probe baseline: `51db96e`. The running application image is labeled `a840aa6`; later commits added deployment settings and the separate probe, without rebuilding the main image.
- Dockhand-managed deployment is running. Its image build fails with `mkdir /root/.docker: read-only file system`; build-on-deploy is temporarily disabled. Image creation currently requires a separate build before Dockhand deployment.
- Corrected the model URL to include its service port and selected the model advertised by that endpoint. The server requires fixed-length JSON request bodies; chunked requests returned HTTP 400.
- HA credential rotation is planned by the user after a deployment response exposed it in tool output. Rotation and post-rotation authentication have not been verified. Never print entire deployment responses; select only non-secret status fields.

## Update convention

Update this file when a milestone changes or a meaningful check completes. Record what is implemented, what is deployed, and what was actually tested separately. Keep credentials out of every document.
