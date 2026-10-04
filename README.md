# Assister

Local voice orchestration in C# and .NET 10. Deterministic intents precede LLM routing; context and tools are selected per request.

## Current implementation

Milestone 1 is implemented and deployed: ASP.NET Core, EF Core SQLite startup migrations, structured console logs, health/status endpoints, Docker packaging, and persistence/API tests.

Milestone 2 includes native C# Wyoming framing, bounded message parsing, describe/info capability checks, STT and streaming audio TTS providers, and fake TCP-server tests. These providers are not yet wired into a voice pipeline.

Home Assistant has a persistent authenticated connection, state subscriptions, registry/area joins, and reconnecting cache. Direct intents support light/switch on/off, light brightness and cached temperature/state queries through a shared request coordinator. Ambiguous targets require clarification; stale HA connectivity blocks actions. Unmatched requests use selected tools and a bounded LLM loop. Conversations, timers, FTS memory and compact tool audit records persist in SQLite. See PROJECT.md for the acceptance criteria.

An injectable native C# `ILanguageModel` client supports OpenAI-compatible completions, SSE text streaming and function-call assembly. The broker validates fixed schemas, selected tools, timeouts and result sizes. HA tools provide compact search/state, conservative light/switch control and filtered numeric history summaries. History is limited to seven days and a 1 MiB upstream response; summaries identify sample means rather than time-weighted means. The loop permits at most eight iterations and four calls per iteration; repeated model controls are blocked. Direct intents do not call the model. Wire-format behavior follows the [official function-calling documentation](https://developers.openai.com/api/docs/guides/function-calling).

Configure `LanguageModel:BaseUrl` (including `/v1`), `Model` and optional `ApiKey`. Optional `Temperature`, `MaxTokens` and `TimeoutSeconds` default to 0.2, 500 and 30; tokens are capped at 4096 and timeout at 120 seconds. Each request/response is limited to 256 KiB, with at most 64 messages and 16 tools. Streaming requires a finish reason and `[DONE]`; truncated or filtered responses fail explicitly. Tool arguments remain untrusted strings for broker validation.

## Text requests

`POST /api/test/message` accepts `message`, `satelliteId` (defaults to `test`), `area` and optional `conversationId`:

```json
{
  "message": "set the kitchen lights to 50 percent",
  "satelliteId": "test",
  "area": "kitchen",
  "conversationId": null
}
```

Try `turn the kitchen light off`, `what is the temperature in the office` or `what is the state of light.desk`. An explicitly plural area request targets all matching lights in that area (up to 64); a singular request with several candidates asks for clarification. Brightness must be 0–100 percent; zero turns the light off. Unsupported brightness devices are rejected.

Responses include `response`, `handledBy`, `outcome`, resolved `entityIds`, confidence, trace ID and total duration. Invalid requests return HTTP 400. Requests reuse a satellite's conversation within five minutes, or accept an explicit conversation ID belonging to that satellite. Prompts contain at most twelve recent turns within a 12,000-character budget and bounded earlier topic notes. Raw tool results are never persisted as conversational turns. Unknown and cross-satellite conversation IDs are rejected.

`set a tea timer for 5 minutes` and `cancel the tea timer` use native persisted timers without the LLM. Up to twenty timers per satellite are supported, from one second to 24 hours. Expired timers wait for a connected, idle satellite; announcement delivery is at least once, so a crash immediately after playback can repeat it. The memory tools store short facts on explicit remember requests, search SQLite FTS5 for relevant facts, and delete on explicit forget requests. Tool audits retain the most recent 5,000 metadata entries, excluding raw arguments/results and credentials.

Weather requests use Home Assistant's `weather.get_forecasts` service. Set `WEATHER_ENTITY_ID` if several weather providers exist; a sole available provider is selected automatically. Unsupported night/hourly forecasts fail explicitly rather than inventing data.

`GET /api/homeassistant/entities?query=office&limit=10` returns a bounded cache search with names, areas and state values. `/api/status` reports connection status, freshness and received state-event counts. Raw attributes and credentials are excluded. These development APIs are intended for the trusted local network; complex authentication is outside the MVP scope.

## Development

Live service checks and their limits are recorded in [docs/service-validation.md](docs/service-validation.md). The C# `tools/Assister.ServiceProbe` CLI runs read-only dependency checks using the container environment, including a synthetic TTS-to-STT round trip and a short LLM completion. `--ha-only` narrows read-only checks. The explicit `--direct-intents --light light.authorized_entity` mode performs real controls and restores original member states/brightness; use it only with an authorized target.

Run `dotnet test Assister.sln` and `dotnet run --project src/Assister`.
The database defaults to `data/assister.db`. Environment variables override ASP.NET configuration. Never commit credentials.

## Docker / Dockhand

`ECHOMUSE_CONTROLLER_URL` reserves the EchoMuse controller address. Inspection of the installed controller confirmed its only voice backend is Home Assistant; it has no forwarding API for Assister. The initial physical target is HA Voice `0a587e`, using ESPHome Native API through the optional isolated bridge.

Integration settings are listed in `.env.example` and mapped in `compose.yaml`. Mark credentials as secret in Dockhand. To enable voice, set `COMPOSE_PROFILES=voice`, `SATELLITE_BRIDGE_ENABLED=true`, a random `SATELLITE_BRIDGE_TOKEN`, `ESPHOME_HOST`, `ESPHOME_ENCRYPTION_KEY`, `ESPHOME_SATELLITE_ID`, name/area and `ASSISTER_PUBLIC_URL` reachable from the device. The key is passed only to the bridge. The bridge uses encrypted ESPHome connections and a token-authenticated gRPC link on internal port 8082; it owns no intent, STT, LLM, conversation or timer logic. The C# pipeline uses bounded microphone queues and configurable energy VAD, then Wyoming STT, shared routing, Wyoming TTS and a short-lived WAV playback URL. STT failure never invokes routing; TTS failure preserves response text. Physical playback and cancellation still require device verification.

Compose configures Qwen/llama.cpp with `LLM_ENABLE_THINKING=false` and `LLM_MAX_TOKENS=1024` to avoid incomplete voice replies. The client extension is optional outside Compose: leave `LanguageModel:EnableThinking` unset for servers that do not support `chat_template_kwargs`. Before applying pending migrations to an existing database, startup creates an online SQLite backup next to it.

Build with `docker compose build`; start with `docker compose up -d`. The container runs as `app`, listens on 8080, and persists SQLite under `/data` in a named volume. Startup migrations run before serving requests. Run one replica. A framework-dependent C# service probe is also packaged under `/probe` in the application image. `--mvp-readonly` exercises temperature, comparative history and weather follow-up through text; it does not perform device controls or prove spoken acceptance.

Target: Dockhand **Automation (8)**, host `auto@10.44.0.33`. Git stack: https://github.com/GaryJS3/Assister, branch `main`, compose file `compose.yaml`. Set secrets through Dockhand environment overrides. STT, TTS, LLM and Home Assistant remain external.

`/health` checks the local database; `/api/status` reports degradation while integrations are unconfigured. Neither endpoint exposes configuration or secrets.

The initial live deployment publishes port **8081** because 8080 is occupied. Dockhand Git stack ID is **10**. The agent currently fails image builds with `mkdir /root/.docker: read-only file system`. Its build-on-deploy setting is temporarily disabled; images were built over SSH, with service creation performed by Dockhand. Future Git syncs alone will not rebuild the image until the agent configuration is repaired or an image is built separately.

## Troubleshooting dashboard

The unauthenticated dashboard at `/` shows component health and the latest 200 runs, with searchable request text, trace identifiers, responses, outcomes, decision reasons, and per-step elapsed time. It refreshes every three seconds. Traces are held in memory (up to 256 spans per run, individual detail values capped at 16 KiB) and reset on restart. Anyone who can reach the service can read request text and responses.

`/api/diagnostics/runs` lists traces; `/api/diagnostics/runs/{id}` returns one trace. `/api/diagnostics/health` combines live database connectivity, Home Assistant connection/cache status, and cached external probes. External probes run every 15 seconds with a three-second timeout: the LLM `/models` endpoint and Wyoming `describe` capabilities. Configure `SpeechToText:Host` / `Port` (default 10300) and `TextToSpeech:Host` / `Port` (default 10200). LLM settings are the same as the provider's existing settings. Capability health does not prove successful inference, transcription, or synthesis; the UI states this distinction.

Direct-intent classification, entity resolution, execution, Home Assistant service calls, LLM completions/streams, broker, STT, TTS and voice sessions emit application spans. Provider spans join an active run through .NET activity context or appear as standalone runs when invoked without one. LLM traces include model, message count, finish reason, response, and proposed tool calls; recorded reasons describe application routing, not hidden model reasoning. Interrupted or failed spans remain visible even when an exception prevents a normal response. Token-to-sentence streaming synthesis and streaming device playback remain follow-up work; the first bridge path buffers bounded WAV audio.
