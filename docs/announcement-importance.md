# Announcement importance

`POST /api/satellites/{id}/announcement` accepts `message` and optional `importance`:

| Importance | Playback |
| --- | --- |
| `notify` (default) | Existing announcement |
| `high-importance` | Three opening cues, speech, then one repeat, followed by goodbye |
| `acknowledge` | Announcement every 10 minutes until dismissed; request a 30-second acknowledgement window after playback |
| `alarm` | Three opening cues and speech; repeat 10 seconds after playback until dismissed |

The satellite Playback panel and `satellite_announce` tool accept the same levels.
Direct commands use a level after the verb, for example `announce alarm to Kitchen: Check the oven`.
The destination picker and deferred delivery preserve the importance.
Expired timers default to `alarm`. Reminders, including all-satellite broadcasts,
default to `acknowledge`. Their repeat-until-dismissed state remains memory-only.

Acknowledgements match whole words anywhere in the transcript: acknowledge, acknowledged,
stop, cancel, heard, dismiss, dismissed, got it, understood. An acknowledgement dismisses
the outstanding alert on the receiving satellite. The Stop endpoint also dismisses it.
One repeating alert is allowed per satellite; another repeating alert fails until the first
is dismissed. Other notify announcements may still play between repetitions.
Alerts are held in memory and do not survive service restarts. A busy/disconnected satellite
is retried when available; delivery errors retry after 10 seconds.

## Microphone integration

`IAcknowledgementListener` is the provider contract for opening the microphone after an
announcement. It must initiate a voice activation with no wake word, stop microphone input
at the requested deadline, and permit another activation within the remaining window after
unrelated speech. Assister accepts only acknowledgement transcripts during that window;
unrelated speech cannot invoke the coordinator, tools, or home controls. Explicit wake-word
activations can still issue normal commands or acknowledgements.

EchoMuse implements this contract when its controller advertises `acknowledgementListening`
in the voice hello. Both the Assister adapter and EchoMuse controller must be updated.
After playback it opens the microphone for up to 30 seconds (`acknowledge`) or the 10-second
gap before an `alarm` repeats. Silence and unrelated speech start another capture within
the same deadline. An accepted acknowledgement ends the window; timeout, Stop, mute and
disconnect also close it and restore the ordinary wake-word stream. A new announcement
waits for the controller to confirm that the previous listening window has closed.

Each input carries an acknowledgement request ID. That input remains acknowledgement-only
even if its transcript arrives after the server's window expires or the alert was dismissed
elsewhere. Unrelated speech receives a terminal acknowledgement result without invoking
normal routing or setting the controller's global cancellation flag.

Older EchoMuse controllers and the ESPHome adapter retain wake-word acknowledgement
between playbacks, with `Acknowledgement microphone unavailable` in device diagnostics.
Announcements currently own playback exclusively, so a wake-word turn cannot interrupt
an announcement in progress. Physical microphone behavior still requires live validation.
