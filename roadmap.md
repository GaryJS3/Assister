# Assister roadmap

Updated: October 4, 2026. Follow the milestone order in [PROJECT.md](PROJECT.md). Use [progress.md](progress.md) for completed work.

## Architectural rules

Use C#, .NET 10 and one ASP.NET Core application with logical modules. Use native C# for Wyoming and SQLite/EF Core for persistence. Prefer deterministic intents over LLM calls. Retrieve only relevant context and expose only relevant tools. Home Assistant remains an external automation/data platform. Separate voice sessions, conversations and trace IDs.

## Next: satellite realignment acceptance

The manager, provider boundary, runtime UI and core physical request path are deployed.
An accurate audible reply and correct LED completion are user-confirmed. Before further
physical checks, finish trace correlation and automated disconnect/reconnect/backoff tests.
Remaining hardware checks: cancellation LEDs, deterministic spoken brightness, standalone
announcement/media duck/resume, ownership conflict/recovery and satellite reboot. Runtime
wake-word editing is unavailable on the installed device firmware; do not invent model IDs.

### Earlier LLM and tool checkpoints

- HA and the initial direct intents are deployed and verified: fresh cache, area metadata, state events, office temperature and authorized light power/brightness/restoration all passed. Continue with milestone 5.
- The shared text coordinator, parser, resolver and action handlers are deployed and tested. Carry this coordinator forward into the voice path; add other controls only through intentional mappings and tests.
- Continue milestone 5: completion/streaming client and function-call assembly are implemented and fake-HTTP tested. Selected tools, validating broker and bounded loop are now implemented locally and connected to unmatched requests. Complete live validation. Keep direct intents independent of LLM availability.
- Recheck HA authentication after the user's credential rotation. Registry metadata currently refreshes on reconnect.
## Remaining milestones

| Order | Deliverable | Acceptance checkpoint |
| --- | --- | --- |
| 4 follow-ups | Add other specified controls intentionally; validate deployment and carry direct routing into voice. | Shared coordinator remains deterministic and uncertain targets never silently execute. |
| 5 | OpenAI-compatible completion/streaming client, bounded tool loop, registry/broker/selector, schema validation and HA search/state/control/history tools. | Fake-client tests plus live completion, streaming and tool-call checks. Validate actions and bound history/results; no arbitrary URLs, SQL or shell execution. |
| 6 | SQLite conversations/turns/tool audit, satellite-aware continuity, context builder, summary and budget handling. | Follow-up uses the right conversation; independent satellites remain separate; old raw tool results stay out of prompts. |
| 7 | Expand the existing manager, isolated ESPHome bridge and authenticated gRPC boundary with persistent identity, normalized capabilities, exclusive voice ownership, runtime configuration, satellite UI and diagnostics. Inspect EchoMuse protocol before selecting an adapter. | Real wake/microphone → STT → shared routing → TTS → physical playback, with verified voice ownership and HA non-voice/media coexistence. TCP/Noise success alone is insufficient. |
| 8 | Native persisted timers and expiry notifications; memory store/search/delete using SQLite FTS. | Restart-safe timers, cancellation, satellite announcements and relevant-only memory retrieval. |
| 9 | LLM sentence streaming, Wyoming streaming-text synthesis, early playback and cancellation/barge-in where supported. | Measured time to first audio, stage durations and cancellation without corrupting the next session. |

## Cross-cutting work

### Milestone 7 incremental acceptance

1. Preserve the working pipeline; document exclusive voice ownership and inspect current contracts.
2. Add persistent logical devices and volatile observed state/capabilities; keep the bridge a protocol adapter.
3. Refresh metadata, capabilities/configuration and ownership on reconnect; bound backoff and event/log buffers.
4. Verify physical wake, microphone, shared routing and acknowledged HTTP audio playback on HA Voice 10.44.65.164.
5. Read/update device-supported wake-word IDs, honor limits, restore desired configuration and display drift.
6. Route timers/test announcements through SatelliteManager; verify duck/resume with HA media and Music Assistant.
7. Expose list/detail runtime configuration and firmware information; link satellite requests to bounded traces.
8. Test duplicate/late events, disconnect cleanup, changing capabilities, ownership conflict/recovery and redaction.
9. Verify the installed HA entity-disable procedure before claiming coexistence; keep the ESPHome integration.

The current local increment and remaining acceptance gaps are recorded in progress.md and
[satellite-architecture.md](docs/satellite-architecture.md). Do not begin streaming/barge-in,
firmware management, discovery or whole-home grouping to satisfy this realignment.

- Wire speech configuration into runtime providers and make status report real connectivity/capability checks as integrations arrive.
- Add trace-correlated stage timings, bounded audit data and a recent-run diagnostics page.
- Keep the text and voice paths on the same coordinator. Retain response text on TTS failure; do not invoke the LLM on STT failure.
- Repair the Dockhand agent's build configuration, restore Git-backed rebuilds and verify revision, image, container and live readiness separately.
- Reuse the C# service probe after endpoint, credential, firewall or deployment changes. Do not log raw secret-bearing API responses.
- Host headroom rechecked: 45 GB free. Preserve other volumes and existing environment overrides during deployment.

## Final MVP verification

Complete all seven acceptance scenarios from PROJECT.md: direct light power/brightness, direct temperature query, bounded comparative history reasoning, conversation follow-up, deterministic controls with the LLM stopped, and exclusion of old raw tool results. These scenarios must ultimately pass through a real satellite. Weather follow-up requires a real weather data source/module; the LLM alone must not invent a forecast.

Do not add vector databases, distributed queues, MCP, arbitrary code execution, cloud sync or a plugin marketplace to this scope. Additional work should serve the specified reliability, latency, context-size and observability priorities.
