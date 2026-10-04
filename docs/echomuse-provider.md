# EchoMuse external voice provider

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
Wake-word writes, volume/mute writes and media commands are not advertised through this
initial adapter because those REST write contracts have not been validated.

Each activation has a separate bounded channel and cancellation lifetime. The input format
is 16 kHz mono signed 16-bit PCM; malformed, overflowing or over-20-second audio is rejected.
The whole turn is bounded at 120 seconds. Provider endpointing bypasses the ESPHome energy
VAD. No-speech input never reaches the request coordinator; cancel/disconnect terminates
the matching operation. Device ID plus session ID guards audio and voice playback; device
ID plus request ID guards announcements. Old events cannot complete a new turn.

Voice replies use `turn_response` with an opaque HTTP WAV URL. C# resamples speech to
48 kHz mono PCM. Explicit `play_finished` acknowledgment is required; `play_failed` preserves
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
