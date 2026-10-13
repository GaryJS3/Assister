# Reminders, first increment

Reminders use the shared voice/text coordinator without calling an LLM. Try:

- "Remind me to take my vitamin every night at 8"
- "Remind me to take out the trash Sunday night at 6"
- "Remind me to stretch every day at 8 AM"
- "List my reminders"
- "Cancel reminder take my vitamin"

Daily and weekly reminders recur until cancelled. A bare weekday means every week.
Night/evening imply PM; morning implies AM. Other ambiguous 12-hour times require
the user to repeat the complete request with AM/PM. Follow-up slot filling is not implemented.
Cancellation matches the complete reminder text or the ID shown by listing; ambiguous names
require the ID. Management is scoped to the originating satellite, with at most 100 active reminders.

Schedules capture Assister:TimeZone (default America/New_York). They retain local clock time
across daylight-saving changes. Nonexistent times move to the first valid minute; repeated
times use the later occurrence once per local day.

SQLite persists reminders across restarts. The scheduler announces through SatelliteManager
on the originating satellite. Busy, disconnected, or unsupported satellites defer delivery.
Missed occurrences coalesce into one announcement, then advance to the next future occurrence.
Delivery is at least once: a crash after playback but before the database update may repeat it.
Cancellation prevents future scheduling but cannot retract playback already underway.

Future increments: one-time/relative reminders, snooze and acknowledgement, edit/pause,
a management screen, explicit delivery selection (all-satellite announcement, notifications),
and recorded-command replay. Broadcast needs per-target delivery tracking to avoid repeating
successful targets when another target is offline. Recorded commands should use the shared
request coordinator with explicit stored authorization and execution receipts.
External calendar synchronization is deferred; it should be an adapter over schedules rather
than replacing local reminder storage.

## Dashboard management

Open Reminders in the shared navigation (/reminders.html) to view active reminders across
all satellites, add new ones, edit their message/schedule/target/time zone, or remove them.
Registered satellites are suggested; a manual satellite ID also works. The page uses
GET/POST /api/reminders and PUT/DELETE /api/reminders/{id}. GET supports an offset for
100-item pages. Removal cancels the stored reminder rather than deleting its history.
Message-only edits preserve the next due time; schedule edits compute a new future occurrence.

## AI tools

The model can use reminder_create, reminder_get, reminder_edit, and reminder_cancel.
Tools are offered only when the current request contains scheduling vocabulary:
schedule/scheduling, remind/reminder, calendar, or calander (including common plural forms).
Follow-up edits should include a word such as "reminder"; unrelated requests receive no reminder tools.
All tool operations are scoped to the current satellite. Get returns up to ten active
reminders per page and their stable IDs. Edit accepts only the fields to change; omitted
fields stay unchanged. weekday -1 means daily; 0 through 6 mean Sunday through Saturday.
Creation defaults to the configured time zone. Unsupported native reminder phrasing can
fall through to the AI, while native supported phrases continue to bypass the model.
The tool broker validates arguments, audits results, and bounds execution and response size.
