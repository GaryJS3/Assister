# EchoMuse external voice provider

October 4 deployment: controller runtime `d19959628585c20a2fa2bc9b767ae7ca540e9e4d`
includes microphone continuation; the user confirmed that follow-up questions reopen the mic.
Assister/bridge revision `d51ca90` and the controller were redeployed; Kitchen and MBedroom
returned Online with Assister ownership and no last error. The preexisting HA Voice device
remains Offline. Controller runtime/data mounts and stack configuration were preserved.

The deployed controller fork's protocol is distinct from ESPHome Native API. Assister uses
one authenticated C# WebSocket for the entire controller, with existing native Wyoming and
the shared request coordinator. No Python is added to the Assister service.

Enable `ECHOMUSE_ENABLED`, set `ECHOMUSE_CONTROLLER_URL`, and supply `ECHOMUSE_USERNAME`
and secret `ECHOMUSE_PASSWORD` through deployment configuration. `ASSISTER_PUBLIC_URL`
must be reachable from the controller host. No credential belongs in SQLite or Git.

Login at `/api/auth/login`, authenticate the `/api/voice` upgrade with its token, and require
hello protocol version 1 with the external backend selected. No registration message is
sent. Read continuously while independent request tasks process microphone audio. Every
reconnect logs in again; delays grow from 5 to 60 seconds. HTTP 409 produces a visible voice
ownership conflict and a 60-second retry interval. Errors log types, never remote bodies.

Authenticated `/api/devices` inventory imports approved logical identities as
`echomuse-{deviceId}`. Existing name/area/enabled policy remains authoritative. Runtime device
connectivity, firmware, selected wake model, volume and mute refresh every 15 seconds.
Controllers advertising `volumeControl: true` in the voice hello support volume writes.
Assister sends `set_volume` with `deviceId`, a unique `requestId` and a normalized
`volume` (0–1); `volume_result` acknowledges `sent` or `rejected`. The controller
uses its codec conversion and output-mute handling. A sent acknowledgement confirms
dispatch, not physical volume; inventory remains the source of reported volume.
Older controllers continue to report volume control as unsupported. Wake-word and
mute writes remain unsupported by this adapter.

Direct commands include “volume 5” (0–10) and “set the volume to 50 percent”.
The LLM's `satellite_set_volume` tool accepts either `percent` or `delta_percent`
and always targets the request's current satellite. Relative changes require known
reported volume. The tool rejects repeat volume attempts within the same request.
Both Assister and the EchoMuse controller change must be deployed to enable this.

Each activation has a separate bounded channel and cancellation lifetime. The input format
is 16 kHz mono signed 16-bit PCM; malformed, overflowing or over-20-second audio is rejected.
The whole turn is bounded at 120 seconds. Provider endpointing bypasses the ESPHome energy
VAD. No-speech input never reaches the request coordinator; cancel/disconnect terminates
the matching operation. Device ID plus session ID guards audio and voice playback; device
ID plus request ID guards announcements. Old events cannot complete a new turn.

Voice replies use `turn_response` with an opaque HTTP WAV URL. C# resamples speech to
48 kHz mono PCM. Responses containing a question mark request `continueConversation`;
the controller reopens its microphone through its existing continuation loop after
successful playback. Each follow-up has a new transport session and uses the same
satellite conversation. This requires the corresponding EchoMuse external-backend
update; older controllers ignore the flag. Failed or cancelled playback never continues.
Explicit `play_finished` acknowledgment is required; `play_failed` preserves
the completed textual response and failed trace. Audio is removed after playback/cleanup,
with the existing two-minute TTL as a safety net. Announcements use `play`/`stop` and are
routed by SatelliteManager, including existing timer notifications. No music controller,
software mixer, firmware flashing or HA integration changes are included.

Fake-provider and real in-process WebSocket tests cover shared routing, concurrent device
turns with the same session ID, wrong/stale IDs, cancellation/no speech, WAV delivery,
standalone stop and cleanup. A live MBedroom Hey Jarvis request on 2026-10-04 verified
microphone → STT → shared coordinator → TTS → HTTP WAV → controller playback completion
in 8.348 seconds, returning the session to idle without errors (trace
`8f213ddb-ee7b-456d-b23c-6757a19087df`). The user confirmed hearing the reply and LEDs
returning to idle afterward. Initial MBedroom physical wake/request/reply acceptance passed.
Deterministic HA control, physical cancellation and media coexistence remain acceptance work.

MBedroom also recovered after a physical power cycle and a user-reported EchoMuse script
line-ending correction. Assister recorded disconnect/reconnect on 2026-10-04 and a later
successful voice trace (`0d56b136-3c42-41d2-a86f-2ba10876a780`, 7.677 seconds), then reported
Online/OwnedByAssister with idle output and no active session or error. No Assister code
change was needed. This verifies reconnect and subsequent voice processing; physical
disconnect during an active request remains untested.
