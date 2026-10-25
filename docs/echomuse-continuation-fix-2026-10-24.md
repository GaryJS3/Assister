# EchoMuse clarification continuation race

The clarification run `af751ccb-7639-4a4c-ad14-1d36a4ee5a75` played
“Which device do you mean: Main Lights or Sink Light?” successfully, but did not
accept an answer without another wake word.

Controller logs establish the handoff failure (October 24, 2026 EDT):

- 20:01:01.923: original external session completed successfully.
- 20:01:01.927: controller logged continuation and started transport session
  `e111ddd5-1d7f-4fec-a92b-b7a6f4297116`.
- 20:01:01.928: that session ended with `backend_cancel`.

Assister's socket reader called synchronous `EchoMuseConnection.Start` while
the previous voice pipeline still held the satellite session during post-playback
cleanup/conversation persistence. Admission returned null and the provider sent
`turn_cancel`. Reopening the microphone was requested correctly; successful
playback alone did not prove that the continuation was accepted.

The provider now uses asynchronous admission. Only a controller-finished turn
with successful playback acknowledgement qualifies for a cleanup wait. A release
signal is completed when the provider finishes processing that turn, after the
pipeline releases its satellite session. Admission waits at most five seconds,
honors connection cancellation, then applies the existing busy checks. Since
successful playback and controller completion are already received, the previous
turn needs no more socket input during this bounded wait. Other overlapping
requests retain immediate rejection.

Regression coverage holds a satellite session busy after controller completion,
proves the previous admission path rejects it, then verifies the continuation
waits until release and starts without a wake word. Failed/cancelled playback and
stale-session isolation are covered. The real in-process WebSocket provider test
also submits continuation immediately after completion without waiting for idle,
with tones both enabled and disabled.

Validation: 394 unit/component tests and 58 integration tests passed in Release.
Physical microphone acceptance still requires a new spoken clarification and
answer on the intended satellite after deployment.
