# Assister

Local voice orchestration in C# and .NET 10. Deterministic intents precede LLM routing; context and tools are selected per request.

## Current implementation

Milestone 1 is implemented and deployed: ASP.NET Core, EF Core SQLite startup migrations, structured console logs, health/status endpoints, Docker packaging, and persistence/API tests.

Milestone 2 includes native C# Wyoming framing, bounded message parsing, describe/info capability checks, STT and streaming audio TTS providers, and fake TCP-server tests. These providers are not yet wired into a voice pipeline. Home Assistant, intents, LLM, conversations, satellites, timers and memory are pending. See PROJECT.md for the full brief and acceptance criteria.

## Development

Run `dotnet test Assister.sln` and `dotnet run --project src/Assister`.
The database defaults to `data/assister.db`. Environment variables override ASP.NET configuration. Never commit credentials.

## Docker / Dockhand

Integration settings are listed in `.env.example` and mapped in `compose.yaml`. In Dockhand, add overrides using the names from `.env.example`: `STT_HOST`, `STT_PORT`, `STT_LANGUAGE`, `TTS_HOST`, `TTS_PORT`, `TTS_VOICE`, and `LLM_MODEL`, alongside the HA/LLM URL and credential settings. Mark credentials as secret. `ESPHOME_*` settings reserve configuration for the upcoming bridge; they are not consumed yet. Speech settings likewise prepare deployment configuration; the providers are not yet connected to the application pipeline. Adding settings does not activate these integrations.

Build with `docker compose build`; start with `docker compose up -d`. The container runs as `app`, listens on 8080, and persists SQLite under `/data` in a named volume. Startup migrations run before serving requests. Run one replica.

Target: Dockhand **Automation (8)**, host `auto@10.44.0.33`. Git stack: https://github.com/GaryJS3/Assister, branch `main`, compose file `compose.yaml`. Set secrets through Dockhand environment overrides. STT, TTS, LLM and Home Assistant remain external.

`/health` checks the local database; `/api/status` reports degradation while integrations are unconfigured. Neither endpoint exposes configuration or secrets.

The initial live deployment publishes port **8081** because 8080 is occupied. Dockhand Git stack ID is **10**. The agent currently fails image builds with `mkdir /root/.docker: read-only file system`. Its build-on-deploy setting is temporarily disabled; images were built over SSH, with service creation performed by Dockhand. Future Git syncs alone will not rebuild the image until the agent configuration is repaired or an image is built separately.
