# Assister

Local voice orchestration in C# and .NET 10. Deterministic intents precede LLM routing; context and tools are selected per request.

## Current implementation

Milestone 1 is implemented and deployed: ASP.NET Core, EF Core SQLite startup migrations, structured console logs, health/status endpoints, Docker packaging, and persistence/API tests.

Milestone 2 includes native C# Wyoming framing, bounded message parsing, describe/info capability checks, STT and streaming audio TTS providers, and fake TCP-server tests. These providers are not yet wired into a voice pipeline.

Home Assistant now has a persistent authenticated connection, state subscriptions, registry/area joins, and reconnecting cache. Direct intents support light/switch on/off, light brightness and cached temperature/state queries through a shared request coordinator. Ambiguous targets require clarification; stale HA connectivity blocks actions. LLM, persisted conversations, satellites, timers and memory remain pending. See PROJECT.md for the full brief and acceptance criteria.

Milestone 5 has started: an injectable native C# `ILanguageModel` client supports OpenAI-compatible completions, SSE text streaming and function-call assembly. Requests use fixed content lengths; redirects are disabled. Request/response sizes, tool counts and duration are bounded, and incomplete responses cannot publish completed tool calls. This client is registered but not routed from text requests yet; the validating broker and selected HA tools are the next step. Wire-format behavior follows the [official function-calling documentation](https://developers.openai.com/api/docs/guides/function-calling).

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

Responses include `response`, `handledBy`, `outcome`, resolved `entityIds`, confidence, trace ID and total duration. Invalid requests return HTTP 400. Conversations are not created or persisted yet; a supplied conversation ID is only echoed. Voice integration will use the same coordinator. No LLM client is invoked on this path.

`GET /api/homeassistant/entities?query=office&limit=10` returns a bounded cache search with names, areas and state values. `/api/status` reports connection status, freshness and received state-event counts. Raw attributes and credentials are excluded. These development APIs are intended for the trusted local network; complex authentication is outside the MVP scope.

## Development

Live service checks and their limits are recorded in [docs/service-validation.md](docs/service-validation.md). The C# `tools/Assister.ServiceProbe` CLI runs read-only dependency checks using the container environment, including a synthetic TTS-to-STT round trip and a short LLM completion. `--ha-only` narrows read-only checks. The explicit `--direct-intents --light light.authorized_entity` mode performs real controls and restores original member states/brightness; use it only with an authorized target.

Run `dotnet test Assister.sln` and `dotnet run --project src/Assister`.
The database defaults to `data/assister.db`. Environment variables override ASP.NET configuration. Never commit credentials.

## Docker / Dockhand

`ECHOMUSE_CONTROLLER_URL` configures the EchoMuse controller address (currently `http://10.44.0.33:8768`). This is separate from a device's ESPHome Native API address. The controller adapter is not implemented yet; this setting reserves its configuration.

Integration settings are listed in `.env.example` and mapped in `compose.yaml`. In Dockhand, add overrides using the names from `.env.example`: `STT_HOST`, `STT_PORT`, `STT_LANGUAGE`, `TTS_HOST`, `TTS_PORT`, `TTS_VOICE`, and `LLM_MODEL`, alongside the HA/LLM URL and credential settings. Mark credentials as secret. `ESPHOME_*` settings reserve configuration for the upcoming bridge; they are not consumed yet. Speech settings likewise prepare deployment configuration; the providers are not yet connected to the application pipeline. Adding settings does not activate these integrations.

Build with `docker compose build`; start with `docker compose up -d`. The container runs as `app`, listens on 8080, and persists SQLite under `/data` in a named volume. Startup migrations run before serving requests. Run one replica.

Target: Dockhand **Automation (8)**, host `auto@10.44.0.33`. Git stack: https://github.com/GaryJS3/Assister, branch `main`, compose file `compose.yaml`. Set secrets through Dockhand environment overrides. STT, TTS, LLM and Home Assistant remain external.

`/health` checks the local database; `/api/status` reports degradation while integrations are unconfigured. Neither endpoint exposes configuration or secrets.

The initial live deployment publishes port **8081** because 8080 is occupied. Dockhand Git stack ID is **10**. The agent currently fails image builds with `mkdir /root/.docker: read-only file system`. Its build-on-deploy setting is temporarily disabled; images were built over SSH, with service creation performed by Dockhand. Future Git syncs alone will not rebuild the image until the agent configuration is repaired or an image is built separately.
