# Listening to run audio

Select a run in Operations / Run Explorer (`/`). **Listen to this run** has separate
input microphone and output speech players. Playback is manual; reviewing a recording
does not send playback acknowledgements, extend conversation continuity or issue
satellite commands. Playing one recording pauses the other. Trace polling preserves
the players; switching runs stops the previous recording.

## Availability

- New satellite voice runs retain the PCM received by the voice pipeline and the
  synthesized output consumed by the satellite transport, including streaming output.
  The output can include a trailing tone embedded by the transport. These are recordings
  of generated/delivered data, not acoustic proof of what the physical speaker played.
- Rich-client runs reuse existing retained input/output from `interactions.db`. Raw
  16 kHz mono signed 16-bit microphone uploads are wrapped in WAV for browser playback.
  Generated speech is available only when the request asked for speech and TTS succeeded.
- Text-only/debug runs and older satellite traces without captures show **No recording
  captured**. Existing rich-client audio can be played as long as its interaction and
  audio remain retained. Audio already deleted cannot be recovered from trace JSON.
- Expired/evicted recordings, disabled capture and oversized/unsupported recordings
  have explicit states. Interrupted recordings with a retained prefix are marked **Partial**.

## Access and APIs

Satellite audio shares the existing unauthenticated diagnostics surface on the trusted
local network. Rich-client audio keeps owner authentication: sign in at `/chat.html`
in the same browser as the trace page, then use Refresh. Other owners receive 404.
The Sign in to Chat link opens a separate tab to preserve the selected run.

| Route | Purpose |
|---|---|
| `GET /api/diagnostics/runs/{runId}/audio` | Input/output availability for a diagnostic run |
| `GET /api/diagnostics/runs/{runId}/audio/{input\|output}` | Satellite recording as WAV |
| `GET /api/client/runs/{runId}/audio` | Authenticated owner's rich-client availability |
| `GET /api/client/runs/{runId}/audio/{input\|output}` | Authenticated rich-client recording as WAV |

Audio routes support byte ranges for seeking and return `Cache-Control: no-store`.
Missing/expired recordings return 404. Rich-client audio is not exposed by public
diagnostic audio routes, even though the existing run text remains visible there.

## Capture limits and retention

Satellite captures are binary WAV records in `<Assister:DataPath>/run-audio.db`, separate
from redacted diagnostic JSON. Capture observes existing streams without additional
STT/TTS calls. A capture failure or size limit does not interrupt the voice request.
Each direction is limited to 8 MiB of PCM; oversized or changing formats are not retained.

| Configuration | Default | Range |
|---|---|---|
| `Diagnostics:CaptureAudio` | true | Enable/disable new satellite captures |
| `Diagnostics:AudioRetentionHours` | 24 | 1–168 hours |
| `Diagnostics:MaxAudioMiB` | 256 | 16–1024 MiB across satellite WAV data |

Cleanup runs on startup, after capture and every five minutes. Expired audio is not
served even before cleanup. The byte budget evicts oldest audio first. Small metadata
records are bounded by twice `Diagnostics:MaxRuns` and `Diagnostics:RetentionDays`;
an evicted metadata record subsequently appears as not captured. SQLite reuses freed
pages rather than immediately shrinking the database file. Rich-client audio continues
to use its separate [storage retention policy](rich-client-protocol.md#storage-retention).

The local regression suite covers WAV format and padding, retained prefixes, restart,
expiry, byte budgets, both satellite delivery paths, range requests, owner isolation,
and browser player continuity across polling/selection changes. Local browser checks
played separate synthetic input/output tones. These checks do not establish physical
device acceptance or a deployed revision.
