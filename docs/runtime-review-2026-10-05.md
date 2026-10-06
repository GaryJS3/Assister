# Runtime review — October 5, 2026

Read-only production investigation while awaiting the two timer announcements. No services,
device settings or temperature routing were changed. Measurements are individual observations,
not benchmarks. Timings include speaking/capture and completed reply playback.

## Playback wake interruption

Run `e6db9cb9-1987-4413-8332-45d58641d2b3` created a timer, then its confirmation playback
was cancelled. EchoMuse logs at 00:10:18 EDT identify `barge_in`, with consecutive scores
0.277/0.118 against playback threshold 0.10. A new microphone session immediately opened and
ended at 00:10:23 with `no_speech_timeout`. This is distinct from deliberate follow-up listening.
The logs establish the trigger, not whether a person actually spoke a wake word.

The running controller's `/app/em_barge.py` requires two consecutive playback scores over
the configured threshold; `/app/em_db.py` defines a 0.25 default. At 0.25 the recorded pair
would not fire because 0.118 is below threshold. The thinking-phase threshold in these logs
was approximately 0.78. Warm-up gating was present, but the detection fired once warm-up ended.

Next experiment: after expiry acceptance, record the current device setting, test playback
threshold 0.25, and compare ordinary confirmations with intentional spoken wake interruption.
Do not claim this will eliminate all false wakes. Keep wake detection while idle and thinking
unchanged. Controller settings were not changed in this review.

## Latency

| Trace/request | Capture plus STT | Model rounds | HA tools | Playback | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| `4dcd5b8d` kitchen discovery | 4.38 s | 2.77 + 2.37 s | 0.042 s | 2.88 s | 12.82 s |
| `2ce020db` power reading | 6.89 s | 2.59 + 3.00 + 2.57 s | 0.077 + 0.029 s | 2.76 s | 18.24 s |

STT post-audio finalization was 1.05 and 1.16 seconds respectively. Model generation dominates
the post-transcription delay; HA tool execution does not. Remaining time includes synthesis,
formatting and orchestration, some of which overlap. Do not add nested synthesis/delivery
spans as independent durations: those spans also wait for response generation.

`ToolLoop` buffers tool-capable rounds until completion. Text can reach sentence synthesis
immediately only when no tools are offered or tool choice is `none`, and requested control
has been confirmed. General discovery/state rounds retain tools, so final prose can remain
buffered even when it ultimately contains no calls. Installed transport reports
`earlyPlaybackSupported=false`; generation/synthesis overlap is not early device playback.

Next bounded work:

- Measure repeated read-only requests with the same context before optimizing model round count.
- Consider a verified final-answer phase after sufficient read results, with tests for requests
  needing additional tools. Do not force it immediately after every search.
- Investigate STT endpoint/finalization latency separately from the time a user spends speaking.
- Preserve source validation, current-request authorization, and buffering of unvalidated prose.
- Leave office-temperature routing unchanged, as requested.

## Acceptance and persistence

User confirmed spoken brightness, clarification/follow-ups and cancellation/recovery.
Power traces confirm both office lights and the kitchen/living-room groups, with HA state
confirmation and successful controller playback acknowledgments. Updated Kitchen, Office and
Living Room metadata is visible; the import event mechanism was not isolated in this review.

`c48745f` deployed through Dockhand after 207 tests passed. Before/after SQLite snapshots passed
integrity checks. Both active timer IDs and deadlines were unchanged:

- `b480d7b2-d2fa-4dcf-a232-31b64ed76aae`: interrupted creation attempt.
- `51bce7fe-dca4-40aa-93b4-9aee3dc644f3`: successful retry.

The native production query returned both timers after deployment. Voice run
`5 October 00:18:16 EDT` then completed the spoken remaining-time query successfully.
Timers belong to the requesting satellite; no timers were cancelled or rescheduled by this review.
Expiry announcements remain pending; persistence alone does not establish successful delivery.
