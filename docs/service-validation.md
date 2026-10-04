# Live service validation, October 3, 2026

Initial dependency checks ran inside the deployed Assister container on 10.44.0.33, using its configured credentials. Those initial checks did not control devices or play satellite audio. The subsequent authorized control checks are recorded below.

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

These checks validate external dependencies, not an implemented end-to-end assistant. LLM streaming/tool calls, EchoMuse device authentication and audio transport remain untested. The text coordinator and HA controls are now verified below; the complete voice pipeline and controller adapter remain pending.

## Home Assistant and direct intents, October 3, 2026

Application revision `1b76012b66dba2dd752e8ca9ad1845eaaef90280` was built from its exact Git archive and deployed by Dockhand to Automation (8), stack `assister`. The running image revision label and digest were independently inspected. Original container environment fingerprint and named volume `assister_assister-data` were preserved; the service runs as `app`, with zero restarts after startup.

| Check | Result |
| --- | --- |
| Health/database | Healthy, HTTP 200 |
| Persistent HA client | Connected; fresh cache of 1965 entities |
| Live metadata | 3288 registry entities, 293 devices, 18 areas; Office device/entity area join verified |
| User-authorized light | `light.living_room_lights`; original state off |
| Turn off via shared coordinator | Passed; 126.9 ms; HA state and cache confirmed |
| Turn on via shared coordinator | Passed; 38.5 ms; HA state and cache confirmed |
| Set 50% brightness via shared coordinator | Passed; 39.7 ms; actual brightness and cache confirmed |
| State events | Persistent subscriber received updates; 55 events during control run, including unrelated HA activity |
| Restoration | Original off state restored and verified; original HA brightness attribute was null |
| Office temperature | Correct area sensor resolved at confidence 1.0; 78.746 degrees Fahrenheit, 15.7 ms |
| Ambiguous office light | Clarification returned with no resolved targets or control |
| Automated suite | 35 tests passed; `git diff --check` passed |

The C# probe supports `--ha-only` for read-only HA checks. `--direct-intents --light light.authorized_entity` explicitly performs real power/brightness controls. It captures the selected entity's leaf-member states/brightness before changing anything, saves a credential-free recovery record under `/data`, and restores settings in a finally block. This run's record is `/data/live-light-backup-20261004012300.json`. Controls use Assister's `/api/test/message`; direct HA REST reads independently confirm actual state. Restoration uses fixed HA light services to restore exact captured brightness values.

These direct controls do not invoke an LLM client. The separate model server was not stopped; physical satellite acceptance, conversation persistence and LLM tools remain pending. HA reconnect/stale behavior is fake-tested; a forced live HA outage was not performed. Credential rotation remains unverified.

The host previously ran out of general disk space while staging the probe. It has since been expanded to 61 GB; the playback deployment recheck showed 43 GB free.

## Physical HA Voice acceptance

Assister and the bridge are deployed at revision `f96b8ea`. Home Assistant's ESPHome integration for HA Voice 0a587e was disabled by the user so the Assister bridge owns voice input. The user confirmed button-triggered temperature questions now receive spoken results. Correlated traces confirm office temperature direct routing, Wyoming synthesis and device playing-to-idle completion. The device also fetched and played a test FLAC through its native media-player API. Audio is encoded in C# through FFmpeg as 48 kHz mono 16-bit FLAC; independent FFprobe validation confirmed those parameters.

Revision `cd104c6` corrects speech endpointing. The user reported a much better response delay. The correlated office-temperature trace captured 3.36 seconds of audio and ended on silence; total STT including capture took 4.42 seconds, compared with earlier 16–21-second STT stages. Direct routing took 51 ms and playback completed successfully. The 15.52-second total session includes the entire spoken answer. The 58-test suite passes. This does not yet establish all MVP acceptance scenarios or streaming/barge-in.
