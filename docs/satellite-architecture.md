# Satellite boundary and acceptance

Assister owns voice; ESPHome owns hardware; the Python sidecar translates protocols;
SatelliteManager owns endpoints/capabilities/output; VoicePipeline owns spoken interaction;
the shared coordinator owns semantics; HA is an external tool and media backend.

## Source investigation, October 4, 2026

- [ESPHome voice_assistant subscription](https://github.com/esphome/esphome/blob/dev/esphome/components/voice_assistant/voice_assistant.cpp): `client_subscription` rejects a second API client and logs `Multiple API Clients attempting to connect to Voice Assistant`. It does not send a positive subscription acknowledgment.
- [aioesphomeapi 46.6.0](https://github.com/esphome/aioesphomeapi/blob/v46.6.0/aioesphomeapi/client.py): subscription dispatch installs callbacks; device voice requests confirm which client receives voice. Configuration read/write returns device-reported model IDs. Log callback removal alone does not stop device log traffic; request `LOG_LEVEL_NONE` as well.
- [HA ESPHome Assist Satellite](https://github.com/home-assistant/core/blob/dev/homeassistant/components/esphome/assist_satellite.py): `async_added_to_hass` subscribes and registers unsubscribe on removal. This supports testing entity disable rather than removing the integration. These sources describe upstream behavior, not verification of the installed HA version.

125 decodes inside the adapter as voice assistant, API audio, timers, announce,
start conversation and multiple microphone channels; raw speaker is absent. Actual
announcement/media output support also requires a discovered supported media-player path.
The existing HA Voice response remains device-fetched HTTP FLAC on its announcement pipeline.

## HA coexistence procedure to verify

1. Record installed HA and device firmware/API versions, ESPHome entity states, active wake-word IDs and media state.
2. In HA Settings → Devices & services → Entities, locate this device's `assist_satellite` entity. Disable that entity (do not merely hide it, change its pipeline, or mute the device).
3. Reload the device's ESPHome integration if necessary so entity removal executes unsubscribe. Keep the integration enabled, its media_player and normal entities enabled. Do not delete the device as the first solution.
4. Retry voice subscription from Assister only after releasing HA's subscriber. Check for explicit conflict and then confirm ownership with a real wake/button request. Unknown is not a pass. Verify HA Assist did not run.
5. Confirm HA's ESPHome integration remains connected, normal entity updates arrive, media_player playback and supported media-player announcements work, and Music Assistant remains usable.
6. With normal music playing, invoke Assister and confirm response/announcement completion and music resumption. Media transitions ANNOUNCING → PLAYING/PAUSED/IDLE can acknowledge the announcement without waiting for long-form media to end.

This procedure is a candidate supported by source inspection, not yet a verified recipe for
the installed HA deployment. HA `assist_satellite.announce` is not promised while Assister owns
voice. A successful second encrypted API connection is not coexistence acceptance.

## Runtime and deployment

The existing environment satellite is imported once into SQLite. Assister's registration and
area are authoritative; provider registration cannot override room policy. CRUD accepts only
identity, manual hostname/IP, area, provider and enabled policy. Connected identity/policy changes
are rejected until the provider is disconnected. Runtime state/events stay volatile. Manual
registration does not provision a container or move encryption keys into Assister.

For multiple devices, provision separate thin bridge instances with unique registered IDs and
separate externally supplied `ESPHOME_ENCRYPTION_KEY_FILE` paths or Dockhand secret values.
Keep the bridge token private; it authenticates a trusted provider link, not an end-user API.
Do not paste keys into satellite registration or diagnostic pages. Existing unauthenticated
diagnostic/API deployment exposure remains; use the existing trusted network boundary.

Reconnect retries back off from 5 to 60 seconds; ownership conflict does not disconnect or
automatically resubscribe. After releasing another subscriber, use the explicit retry action.
Ownership is Unknown until an incoming device voice request confirms it, or a brief error-log
lease observes rejection. Firmware without that log cannot reliably distinguish Unknown from
Conflict without an actual activation. No ownership stealing or absence-of-error inference.

Wake-word writes are validated on both sides and read back. Desired state persists and drift
is shown; mismatched readback cannot create an endless write loop. Desired changes reconcile
after ownership confirmation. The selected STT stream stays 16 kHz mono S16; the source channel
count travels in gRPC and multiple-channel capability remains visible. ESPHome's microphone
callbacks lack upstream session IDs, so correlation can only be attached at the adapter boundary.

Audio uses random GUID bearer URLs, 2-minute TTL, at most 16 objects/8 MiB each and periodic
30-second expiry cleanup. No filesystem path is exposed. Playback has its own opaque ID;
wrong playback/session acknowledgments cannot complete a different request. Legacy voice
acknowledgments without playback ID are accepted only with matching transport session.

Device warning logs require an explicit 60-second lease and are turned off on expiry/stop;
raw logs are sanitized in the bridge and again in C#. Satellite history retains 100 events
per ID in memory. Existing request traces keep bounded sanitized semantic stages, durations,
transcripts, routing/tool/model details and raw/spoken responses. Audio is not diagnostic data.

## Acceptance ledger

| Scenario | Evidence for this realignment |
| --- | --- |
| Existing button/microphone → temperature → physical response | Prior deployment/user confirmation in progress.md; not a new wake test |
| Authenticated gRPC microphone/WAV and duplicate/late event guards | Automated C# integration tests |
| Connection success vs voice conflict; wake proves ownership | Fake ESPHome/gRPC adapter tests |
| Capabilities refresh and wake configuration/drift | Automated manager/configuration and adapter tests |
| Announcements unsupported/busy/failing | Fake provider tests |
| Two API clients, HA entity/media retention | Pending physical verification |
| Real wake → deterministic living-room brightness → TTS/playback; no LLM | Pending physical verification |
| Supported wake-word change/new phrase/disabled old phrase/restoration | Pending physical verification |
| Music/announcement duck/resume and Music Assistant | Pending physical verification |
| Physical ownership conflict/release/retry and satellite reboot | Pending physical verification |

Do not mark Milestone 7 acceptance complete until the pending physical rows pass. No firmware
management, discovery, mixer, music library or streaming/barge-in is included.
