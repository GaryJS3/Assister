# Notification audit — October 26, 2026, 21:10 EDT

Read-only inspection of the live Assister diagnostic API and Dockhand logs for Automation environment 8. Running Assister revision: 8e2e7457be9c1e26519b3cdaab3a477bc683a167; image sha256:a8cde014df6d86ff2aaac91397b3b8fd11ad262b0e4987b31dff35f6398c683e. No playback, acknowledgement, scheduling, settings, or deployment mutations were made. Local unrelated changes were preserved.

## Finding

The extra beeping comes from repeatedly starting acknowledgement voice turns. Each turn runs VoicePipeline's unconditional awake feedback tone. The controller reopens another turn immediately after the previous turn ends, until the acknowledgement window expires. This is separate from replaying the spoken notification.

The latest delivery sequence in controller logs starts at 21:10:04.222. Device-confirmed playback finishes at 21:10:07.023 on G090L91181820LCV. The other devices open their acknowledgement windows after their sequential notification playback. All times below are EDT:

| Device serial | First acknowledgement | Last acknowledgement start | Attempts | Window closes |
| --- | --- | --- | ---: | --- |
| G090L91181820LCV | 21:10:07.027 | 21:10:36.267 | 11 | 21:10:37.026 |
| G090LF1173730WP0 | 21:10:10.528 | 21:10:35.950 | 7 | 21:10:40.527 |
| G090LF1182030WPN | 21:10:13.872 | 21:10:43.818 | 21 | 21:10:43.873 |

These are roughly 30-second acknowledgement windows. The WPN device repeatedly plays 25,920-byte tone audio; many turns last only 1.2–1.4 seconds. Example: session 6aa225f5-a5ff-434c-8595-a0ac0f629b00 starts at 21:10:42.554, plays feedback, ends input at 21:10:43.799, receives turn_error at 21:10:43.812, and is followed by another acknowledgement session at 21:10:43.818.

## Additional failure

Detailed run 3c844df2-bbff-4479-bc24-cc64207468ea records zero PCM bytes, no first-audio timestamp, and terminationReason speech_end. STT fails before producing a transcript. Matching Assister logs report InvalidDataException. Thus the controller signals speech completion without delivering microphone audio for this attempt. The precise throw site is not captured by the exception-type-only logging. Empty audio is rejected by the recording/WAV path, which is consistent with this trace, but a stack trace would be needed to prove the exact throw site.

Other turns return ignored transcripts such as “Subtitles by the Amara.org community.” These are not acknowledgements. Whether those arose from background sound, tone pickup, or recognition hallucination is not established. The trace does not prove the speaker hears its own beep.

## Recommended corrections

1. Suppress ordinary awake/goodbye feedback on acknowledgement-only turns; preserve deliberate acknowledgement confirmation.
2. Keep acknowledgement listening quiet across ignored, empty, and failed recognition attempts; avoid audible feedback on every re-entry and bound rapid retries.
3. Treat zero-byte input as no speech before WAV conversion/STT; close or complete the acknowledgement turn through its protocol rather than returning a generic turn_error.
4. Investigate controller speech_end with zero forwarded PCM; log bounded microphone counters and exception stage/stack information.

Verify in C# with multiple empty and non-acknowledgement turns inside one listening window: no repeated awake playback, no model/tool execution, bounded retry behavior, and a real acknowledgement ends the pending notification. Then deliberately validate one physical notification. This audit makes no implementation changes.

Evidence is saved under .codex-local/notification-audit/: controller.log, controller-full.json, assister-logs.json, satellites.json, events.json, runs.json, failed-turn.json. The controller logs also show similar acknowledgement churn at 21:00; the issue is not unique to the last delivery.
