# Assister

Local voice orchestration in C# and .NET 10. Deterministic intents precede LLM routing; context and tools are selected per request.

## Current implementation

Milestone 1: ASP.NET Core, EF Core SQLite startup migrations, structured console logs, health/status endpoints, Docker packaging, and persistence/API tests. Speech, Home Assistant, intents, LLM, satellites, timers and memory are pending.

## Development

Run `dotnet test Assister.sln` and `dotnet run --project src/Assister`.
The database defaults to `data/assister.db`. Environment variables override ASP.NET configuration. Never commit credentials.

## Docker / Dockhand

Build with `docker compose build`; start with `docker compose up -d`. The container runs as `app`, listens on 8080, and persists SQLite under `/data` in a named volume. Startup migrations run before serving requests. Run one replica.

Target: Dockhand **Automation (8)**, host `auto@10.44.0.33`. Git stack: https://github.com/GaryJS3/Assister, branch `main`, compose file `compose.yaml`. Set secrets through Dockhand environment overrides. STT, TTS, LLM and Home Assistant remain external.

`/health` checks the local database; `/api/status` reports degradation while integrations are unconfigured. Neither endpoint exposes configuration or secrets.
