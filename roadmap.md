# Assister roadmap

Updated: October 3, 2026. Follow the milestone order in [PROJECT.md](PROJECT.md). Use [progress.md](progress.md) for completed work.

## Architectural rules

Use C#, .NET 10 and one ASP.NET Core application with logical modules. Use native C# for Wyoming and SQLite/EF Core for persistence. Prefer deterministic intents over LLM calls. Retrieve only relevant context and expose only relevant tools. Home Assistant remains an external automation/data platform. Separate voice sessions, conversations and trace IDs.

## Next: Home Assistant integration

- Implement an authenticated persistent WebSocket client with cancellation and reconnect/backoff.
- Load states, services and entity/device/area registries; build the local cache and entity index.
- Subscribe to state changes; retain cached states as stale during disconnects and block state-changing operations until connectivity returns.
- Test authentication, registry joins, cache updates, failures and reconnection with fakes, then verify live reads/subscriptions. Recheck credentials after rotation.

## Remaining milestones

| Order | Deliverable | Acceptance checkpoint |
| --- | --- | --- |
| 4 | Separate language parsing, intent classification, slot extraction, area-aware entity resolution and action execution. Start with on/off, brightness and basic state queries; add the other specified controls intentionally. | Shared `POST /api/test/message` path; ambiguity never silently controls an uncertain target. Deterministic controls work while the LLM is offline. |
| 5 | OpenAI-compatible completion/streaming client, bounded tool loop, registry/broker/selector, schema validation and HA search/state/control/history tools. | Fake-client tests plus live completion, streaming and tool-call checks. Validate actions and bound history/results; no arbitrary URLs, SQL or shell execution. |
| 6 | SQLite conversations/turns/tool audit, satellite-aware continuity, context builder, summary and budget handling. | Follow-up uses the right conversation; independent satellites remain separate; old raw tool results stay out of prompts. |
| 7 | Satellite abstraction/manager, voice session lifecycle and coordinator. Inspect EchoMuse controller APIs and protocol before choosing its adapter. Add optional isolated ESPHome bridge only if a device requires it. | Real microphone → STT → shared routing → response formatting → TTS → playback. Confirm controller authentication and device capabilities; web reachability alone is insufficient. |
| 8 | Native persisted timers and expiry notifications; memory store/search/delete using SQLite FTS. | Restart-safe timers, cancellation, satellite announcements and relevant-only memory retrieval. |
| 9 | LLM sentence streaming, Wyoming streaming-text synthesis, early playback and cancellation/barge-in where supported. | Measured time to first audio, stage durations and cancellation without corrupting the next session. |

## Cross-cutting work

- Wire speech configuration into runtime providers and make status report real connectivity/capability checks as integrations arrive.
- Add trace-correlated stage timings, bounded audit data and a recent-run diagnostics page.
- Keep the text and voice paths on the same coordinator. Retain response text on TTS failure; do not invoke the LLM on STT failure.
- Repair the Dockhand agent's build configuration, restore Git-backed rebuilds and verify revision, image, container and live readiness separately.
- Reuse the C# service probe after endpoint, credential, firewall or deployment changes. Do not log raw secret-bearing API responses.

## Final MVP verification

Complete all seven acceptance scenarios from PROJECT.md: direct light power/brightness, direct temperature query, bounded comparative history reasoning, conversation follow-up, deterministic controls with the LLM stopped, and exclusion of old raw tool results. These scenarios must ultimately pass through a real satellite. Weather follow-up requires a real weather data source/module; the LLM alone must not invent a forecast.

Do not add vector databases, distributed queues, MCP, arbitrary code execution, cloud sync or a plugin marketplace to this scope. Additional work should serve the specified reliability, latency, context-size and observability priorities.
