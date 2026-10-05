# Live service validation, October 3, 2026

## Latest production checkpoint — October 4, 2026

Assister and ESPHome bridge were built from exact committed revision
`d51ca906c453490a1f1e6b413f535fedacb13982` and redeployed through Dockhand after Git sync.
Running revision labels match. App image digest:
`sha256:647484fe8d14cac99e881560c7f872056e488fba9f870b86393853f5ad4af5d8`.
Bridge image digest: `sha256:d02a48740527dce721532e4ea9cf4174fd43f9454f56f2fd2ae637946da45341`.
EchoMuse was redeployed with runtime `d19959628585c20a2fa2bc9b767ae7ca540e9e4d`.
Assister's 28 environment overrides and EchoMuse stack configuration were unchanged;
existing data mounts were preserved. All 196 tests and dashboard JavaScript syntax pass.

`/health` returned Healthy. `POST /api/homeassistant/cache/rebuild` completed with `isStale=false`,
1,967 entities and 623 received state events at readback. This validates a live full reload,
not a 15-minute elapsed schedule or an actual HA registry edit. Those paths have automated tests.
Kitchen and MBedroom returned Online with Assister ownership and no last error after restart.
HA Voice remains Offline, so overall satellite readiness must not be described as complete.

The preceding `fa78a75` live text checks verified office/kitchen discovery and naming without
controls or failed tools, and explicit on/off clarification for "Both of them?". Independent
cached readback showed office lights still on and kitchen still off. See
[device-interactions.md](device-interactions.md) for trace IDs and the actual earlier kitchen
control audit. The user confirmed microphone reopening after assistant questions with the
EchoMuse continuation update; new spoken control acceptance for these device fixes is pending.

## Historical validation records

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

## Direct and LLM control confirmation, October 4, 2026

The C# probe now supports `--controls --light light.authorized_entity` for both deterministic
and model-mediated control. It captures a durable credential-free backup, tests power and
brightness, checks matching LLM control traces and independent HA state/member/cache results,
and restores original settings in finally. This mode performs actual device controls.

On deployed revision `7e7f08a`, all seven control requests passed for
`light.living_room_lights`, including the exact transcript "Set living room light to 100."
and LLM dim-to-40%, fully-bright and switch-off requests. Every LLM action had one matching
successful ha_control trace; every requested state/brightness was confirmed independently.
The probe restored original on/255 settings. Backup:
`/data/live-light-backup-20261004181316.json`. Direct timings were 246-358 ms; LLM timings
were 9.34-10.14 seconds. 92 C# tests pass. A new spoken satellite control remains a separate
physical acceptance check; shared coordinator execution is verified.

Both action paths now wait for HA state readback rather than treating HTTP service acceptance
as completion. LLM controls require current-request search/control tool execution; older
confirmation replies cannot authorize reporting a new action as successful. Failed/unexecuted
control returns a failed response instead of passing through model success prose.

## Multi-target brightness and cancellation, October 4, 2026

Deployed application revision: `0fbc4a707f6ce668b6fcf72f6496a711fcba722e`.
Dockhand Git stack 10 synced to `0fbc4a7`. The image was built from that exact Git archive
on Automation because Dockhand builds remain disabled. Dockhand recreated only Assister,
using the local image without a registry pull. Running image:
`sha256:db28c6ac97f13fa016642eea0624131182799390a316b5a7c537b4599198caad`.
Environment fingerprints and mounts match the pre-deployment snapshot. ESPHome bridge container
and image remain unchanged at `ed8d918`. /health is Healthy; read-only authenticated HA REST,
WebSocket and registry checks pass. All 125 automated C# tests pass (110 unit, 15 integration).

A temporary C# probe ran inside the production application container, using its existing
environment without exposing credentials. It exercised the shared coordinator via production
HTTP APIs, queried HA states independently, and captured a durable recovery record before
controls. It reported zero failures:

| Check | Production result |
| --- | --- |
| Both light groups to 40% | Direct intent; both HA states confirmed; trace `85edb09f-9e3e-4903-9a3f-3196eb8f9ff0` |
| Both light groups to 100% | Direct intent; both HA states confirmed; trace `1d7a5646-a901-4464-893d-f96bc29ceef9` |
| Missing second target | `not-found`; existing light states unchanged |
| Original settings | Living Room Lights on/128, Kitchen Main Lights on/194 restored and independently verified |
| Stop during real MBedroom announcement | Playback-start event followed by deterministic `please stop`, cancellation event and released session |
| Next MBedroom announcement | Playback-start and completion acknowledgments; runtime returned idle with no error |

The tested command was "Can you set both the living room light and Kitchen Main Lights to
40?" (also 100). Targets were `light.living_room_lights` and `light.kitchen_main_lights`;
neither required HA area assignment. The shorter "kitchen light" remains unresolved and
requires an alias or the full name. Missing/ambiguous targets never trigger partial control.
Recovery record: `/data/production-two-light-backup-20261004220703.json`.

Announcement playback began at 18:07:06 EDT, cancellation was recorded immediately afterward,
and the next announcement completed at 18:07:08 EDT. Stop used the text API with MBedroom's
satellite ID, proving deterministic routing and real controller playback cancellation.
These tests do not prove microphone recognition, human-audible interruption or LED cleanup.
Capture/model-processing/synthesis cancellation has automated coverage; those voice stages
were not physically exercised by this production probe. Wake during speech remains deferred.
