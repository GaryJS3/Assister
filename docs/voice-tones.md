# Voice feedback tones

The nine version-1 definitions in `tones/` are rendered in C# as 48 kHz mono
16-bit PCM. Edit the JSON using `misc/tone-designer.html` or a text editor.
Files are read at playback time, so edits do not require restarting. Published
builds include the definitions; set `Voice:Tones:Path` to an absolute directory
for externally maintained definitions (environment variable `Voice__Tones__Path`).

| Tone | Trigger |
| --- | --- |
| Awake | Accepted voice activation, before transcription |
| Confirmed | Nonempty transcript, before routing |
| Intent-Match | Shared coordinator matches one deterministic intent |
| AI Think | Before calling the language model |
| AI Thought | Language model completes successfully |
| Issue | Routing takes over eight seconds, or returns a recoverable outcome |
| Done | Response is ready for speech |
| Goodbye | Spoken response finishes with no EchoMuse follow-up requested, or recognition returns no speech |
| Error | Failed request or pipeline failure |

`Voice:Tones:Enabled` defaults to true. Set it to false to disable feedback.
`Voice:Tones:IssueAfterSeconds` defaults to 8 (1–120 seconds). Slow-request Issue
plays once. Spoken stop commands cancel without routing or a confirmation tone.
Tone playback failure is recorded and disables further tones for that turn;
the spoken response can still proceed. Feedback has a five-second delivery
deadline, so definitions intended for satellites should stay shorter than that.

Tones use a distinct `ITonePlayback` path, never a synthetic voice response.
ESPHome receives `tone-ready` through the bridge and uses media announcements
without the voice-assistant announcement call that could finish the turn.
Deploy the updated bridge alongside Assister. EchoMuse uses independently
correlated `tone` requests with both a session ID and a playback request ID.
The controller advertises `feedbackPlayback` in its hello; older controllers
continue without tones. The fork validates session ownership, serializes feedback,
keeps the microphone running, and excludes cue audio from recognition.
Stopping just a failed cue does not cancel the voice turn. Goodbye is appended
to the spoken response audio in the matching PCM format, and omitted for
follow-up questions. Physical capture/playback behavior still needs field testing.

With tones enabled on a supporting satellite, response generation buffers before
speech delivery to keep model/response cues ordered. Disabling tones restores the
configured streaming path. Future concurrent streaming feedback needs a provider
mixing/queue contract. No rich-client event subscription is added yet.

`GET /api/voice/tones` lists names; `GET /api/voice/tones/{name}.wav` renders a
preview. `POST /api/satellites/{id}/tones/{name}` plays a tone as an independent
announcement on an idle satellite and waits for playback acknowledgment; busy
or unavailable satellites return 409. These endpoints follow the existing voice
audio and satellite operations API boundaries.

Rich-client interaction streams advertise `audio.tones` and emit durable
`tone.play` events containing `name`, `url`, `expiresAt` and `placement`. The
server emits Confirmed after input is ready, Intent-Match/AI Think/AI Thought
from the shared coordinator, Done before the first response text, Issue for a
slow request or recoverable failure, Error for execution/transcription failure,
and Goodbye before the terminal event. Tone generation follows
`Voice:Tones:Enabled` and the slow warning follows `Voice:Tones:IssueAfterSeconds`.
Awake remains a local microphone-ready cue: uploaded audio does not tell the
server when a client started listening.

Clients deduplicate by event sequence, skip expired cues and never autoplay
historical events when rebuilding a conversation. Immediate cues expire after
five seconds. A fresh `after-response-audio` cue is accepted within that window
and held until the corresponding response finishes playing; it must be cleared
on cancellation, stopped playback, sign-out or conversation change. It does not
mean that speech has already been heard. The web client opts into playback with
"Speak answers and play tones", serializes immediate cues ahead of speech, and
does not replay Goodbye on later manual speech replays. Browser autoplay policies
may suppress cues. Native clients receive the same events through `ObserveAsync`,
deserialize their data as `ToneCue`, and fetch WAV audio using `DownloadToneAsync`.

Definitions support sine, square, triangle and sawtooth; note volumes and master
volume are multiplied. Attack and release are linear, capped at half the note
duration, matching the designer. Gaps contain silence; repetitions include their
configured gap. Validation bounds file size, note count, frequency, volume,
repetitions and total duration (15 seconds).

## ESPHome bridge deployment, October 7, 2026

Bridge-only revision `d6d4608c853597237ea571a55ec9ce5e91d6444f` was built from its
Git archive and deployed through Dockhand on Automation (8). Running image digest
`sha256:732d457416d17a3322e5e808a89404eb2555627c463c059c7cb3da7bf9986b97`
matches the built image. The installed adapter file hash matches the archived
source. All 13 adapter tests pass, including correlated tone playback during
capture without ending the voice run. Environment fingerprints and mounts were
preserved; the Assister container was unchanged. Bridge process is running with
zero restarts, but HA Voice at `10.44.65.164:6053` is unreachable (`SocketAPIError`,
connect error 113), so physical tone acceptance remains pending. The Assister
tone implementation is deployed separately from this bridge-only verification.

## EchoMuse compatibility

The controller fork requires the session-scoped feedback update alongside this
Assister implementation. Its announcement path rejects active voice turns and
stops/restarts the microphone, so voice feedback never uses that path. The new
tone callback reuses device playback completion while leaving microphone and
cancellation state intact. A cue that fails or is stopped flushes only its speaker
audio; an explicit whole-turn stop still cancels capture and response generation.
The controller bounds feedback to three seconds of audio and five seconds for
fetch/playback. All supplied cues fit these limits. Duplicate request IDs and
foreign, stale, cancelled, speaking or already-responded sessions are rejected.

Validation: 281 C# unit tests, 33 C# integration tests, 165 EchoMuse backend and
deployment tests, and 13 ESPHome adapter tests pass. Tests cover microphone audio
exclusion during cues, active-turn playback, old-controller compatibility,
simultaneous devices, cancellation, response-format resampling and Goodbye in
the final audio object. Field acceptance still requires hearing a real wake,
command, response and follow-up.
