# Run audit fixes

The October 26 audit found unintended daily schedules, omitted alarm tools,
unconfirmed device actions, short rich-client context and unnecessary model calls.

- `turn on` remains deterministic; `shut off TARGET` and `shut TARGET off` now use
  the same resolver, authorization and group scope as `turn off`.
- Ordinary clock-time alarm/reminder requests and relative reminders can execute
  without a model. AM/PM ambiguity asks a question before insertion. One-time is
  the default unless recurrence is requested; relative times use request arrival,
  with second precision in storage. Single relative durations from one second to
  365 days are supported; unsupported/compound durations ask for clarification.
- A new `ReminderPolicies` table preserves one-time/recurring kind and importance
  without changing existing reminder rows. Older schedules remain recurring with
  acknowledge importance. Schedule and policy writes are transactional. One-time
  delivery marks the reminder completed instead of scheduling tomorrow. Broadcast
  occurrences complete only after delivery to their snapshotted recipients.
- Reminder APIs expose `oneTime` and `importance`; creation accepts `oneTime:true`
  with an offset-bearing `dueAt`. Legacy dashboard edits preserve policy; the list
  labels one-time reminders. Existing API creation without `oneTime` remains
  recurring for backward compatibility. The dashboard's creation form still
  authors daily/weekly schedules; use speech/text or API for one-time creation.
- Alarm requests persist alarm importance. Client importance remains advisory;
  successful notification storage is not proof of client display or playback.
- Successful device actions and reminder mutations produce confirmations in C#,
  avoiding an extra model round. Partial controls retain their existing safeguards.
- HA command acceptance and state confirmation have separate budgets. Confirmation
  defaults to 15 seconds (`HomeAssistant:ConfirmationTimeoutSeconds`, 0.1–30).
  Traces record requested values, final observed power/speed/step and pending
  entities. Accepted-but-unconfirmed actions return that outcome and are not retried.
  This adds the missing evidence for the fan mismatch; it does not establish its
  original device-side cause or relax state matching.
- Rich-client context defaults to 30 minutes
  (`Conversations:RichClientLifetimeSeconds`, 30–86400). Voice remains 30 seconds.
  Device isolation and five-minute structured action-reference expiry remain.
  Simple state-query clarification selects a candidate directly, including fans.
- Provider failures retain bounded actual finish reason, streaming mode and terminal
  event status. Incomplete responses remain rejected. The original provider's bad
  finish value was not recorded, so its producer-side cause remains unproven.
- Notification capability questions use the actual speaker repeat policy and
  explicitly describe client limitations.

No microphone/post-wake audio changes are included. Regression coverage exercises
the recorded scheduling phrases, missing time, recurrence, alarm importance,
one-time completion, direct power groups, fan clarification, client/voice expiry,
confirmation timeout, provider validation and caller cancellation.

Local validation before upstream integration: 537 unit/component tests, 70
integration tests, and 18 browser tests passed. Deployment evidence is kept in
the local audit directory and verified separately against the committed revision.
