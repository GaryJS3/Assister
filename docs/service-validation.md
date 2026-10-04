# Live service validation, October 3, 2026

Checks ran inside the deployed Assister container on 10.44.0.33, using its configured credentials. No devices were controlled and no satellite audio was played.

| Service | From Assister container | Additional Windows check |
| --- | --- | --- |
| Assister | /health HTTP 200 | Not required |
| Home Assistant | Authenticated REST and WebSocket passed | Not required |
| HA metadata | 1965 states, 81 service domains, 3288 entity registry entries, 293 devices, 18 areas | Not required |
| HA events | state_changed subscription accepted; connection closed afterward | Real state change was not triggered |
| Wyoming STT, 10.0.0.43:10300 | describe/info passed, 50 ms | Also passed |
| Wyoming TTS, 10.0.0.43:10200 | Configured voice synthesis passed: 22050 Hz, 16-bit mono, 113152 bytes, 329 ms | Also passed |
| Speech round trip | Recognized: This is a test of the local voice assistant. STT took 1148 ms | Also passed |
| LLM, 10.0.0.41:8006/v1 | Models and completed chat response passed: OK, 2451 ms | Also passed |
| EchoMuse, 10.44.0.33:8768 | Web and setup API HTTP 200; setup complete | Audio transport remains untested |

The configured LLM URL originally omitted port 8006. The responding server identifies as ReInstinct and advertises Qwen3.6-35B-A3B-UD-Q4_K_XL. The probe uses a fixed-length JSON body because this endpoint rejected chunked requests with HTTP 400.

Initial host/container attempts timed out while Windows checks passed. The user corrected a firewall restriction on 10.44.0.33. After that correction, all eight container checks passed with zero failures. Dockhand was redeployed to apply the corrected LLM URL and advertised model name; unrelated environment overrides were preserved.

`tools/Assister.ServiceProbe` is a C# CLI using the same Wyoming providers as Assister. It reads mapped container environment variables and reports bounded, secret-free results. `--speech-only` or `--llm-only` narrows checks. Full checks require the container environment; localhost health refers to container port 8080. Run with `dotnet run --project tools/Assister.ServiceProbe` in an equivalent environment, or publish the probe and run its DLL using the container runtime.

These checks validate external dependencies, not an implemented end-to-end assistant. HA state control, LLM streaming/tool calls, EchoMuse device authentication and audio transport remain untested. The application pipeline and controller adapter are still pending.
