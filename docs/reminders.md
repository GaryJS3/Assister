# Scheduler and reminders

Supported native reminder phrases use the shared voice/text coordinator without an LLM. Unsupported creation phrasing can fall through to the AI when available. Try:

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

Future increments: one-time/relative reminders, snooze and acknowledgement, pause/resume,
explicit delivery selection (all-satellite announcement, notifications),
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

## Tool contract and completion checks

| Tool | Arguments |
| --- | --- |
| reminder_get | Optional id or offset; returns ten active reminders per page, next_offset, and default_time_zone |
| reminder_create | Required message, hour, minute, weekday; optional time_zone |
| reminder_edit | Required id; optional message, hour, minute, weekday, time_zone; at least one change required |
| reminder_cancel | Required id |

For tools, weekday -1 means daily; 0 through 6 mean Sunday through Saturday.
HTTP uses weekday null for daily. Tools always use Context.Request.SatelliteId;
the AI cannot select another satellite through arguments. Resolve IDs through get,
and clarify ambiguous matches. Tool strings have the broker's general 256-character
limit; dashboard/native messages permit up to 500 characters.

ReminderTool.Relevant gates on the current request, case-insensitively:
schedule/schedules/scheduled/scheduling, remind/reminds/reminded/reminding,
reminder/reminders, calendar/calendars, and calander/calanders.
The tool also checks relevance during execution. "Move that reminder to 9 PM"
is eligible; "Move it to 9 PM" alone is not. Calendar vocabulary enables tools
but does not provide external calendar integration.

ReminderTool.RequestedMutation routing
recognizes create/add/set/schedule/remind me, edit/change/move/update/reschedule/adjust,
and cancel/delete/remove/stop. Pending mutation rounds offer reminder_get plus the
requested mutation with required tool choice. Completion requires a tool result with
status created, updated, or cancelled. If the model skips execution, one corrective
round is allowed before an explicit unconfirmed-change response. A missing detail
can still produce a clarification question.

Tool descriptions and the system prompt restrict mutations to current user requests;
keyword/action routing is not a complete semantic authorization parser. Attempt tracking
blocks duplicate mutations within a request, not repeated user turns. The broker audits
calls and applies its existing schema, 12-second timeout, and output bounds.

## HTTP contract and operating limits

| Endpoint | Behavior |
| --- | --- |
| GET /api/reminders?offset=0 | All active reminders, ordered by due time/ID; 100 per page, plus defaultTimeZone |
| POST /api/reminders | Create; returns 201 and ID |
| PUT /api/reminders/{id} | Replace editable fields of an active reminder; returns 204 |
| DELETE /api/reminders/{id} | Cancel; returns 204 |

POST/PUT body example:

    {
      "message": "Take my vitamin",
      "satelliteId": "bedroom",
      "hour": 20,
      "minute": 0,
      "weekday": null,
      "timeZone": "America/New_York"
    }

PUT requires the full input shape; the dashboard supplies it. An omitted/null timeZone
uses the configured default. Message-only or target-only edits preserve the due time;
schedule changes compute a future occurrence. Invalid fields return 400; missing active
records return 404; exceeding 100 active reminders on a satellite returns 409.
Unknown/offline satellite IDs are accepted and defer delivery until a capable device connects.
These follow the existing trusted-network management API model; they are not a newly
authenticated public API or a per-user access boundary.

The scheduler checks every second, selects one due reminder per connected satellite per
tick, and delivers in batches of up to four. Explicit playback dismissal consumes the
occurrence, as with timers. Run one application replica; there is no distributed lease.
Native, HTTP, and tool writes use separate parameterized SQL paths; HTTP/tool validation
and row mapping are shared. Keep limits and recurrence consistent when extending them.

## Code map for future sessions

| File | Responsibility |
| --- | --- |
| src/Assister/Modules/Reminders/ReminderIntentHandler.cs | Native parser, ReminderSchedule.Next, and DST policy |
| src/Assister/Modules/Reminders/ReminderScheduler.cs | Due checks, announcements, retry/advance |
| src/Assister/Modules/Reminders/ReminderEndpoints.cs | HTTP CRUD, shared validation/row mapping |
| src/Assister/Persistence/Migrations/Reminders.cs | SQLite schema/index |
| src/Assister/Tools/ReminderTool.cs | Schemas, gating/mutation detection, scoped operations |
| src/Assister/Llm/ToolLoop.cs | Tool selection and confirmed-mutation enforcement |
| src/Assister/Voice/RequestCoordinator.cs | Native-first routing and AI fallback |
| src/Assister/Program.cs | Registration and endpoint mapping |
| src/Assister/wwwroot/reminders.html, reminders.js, reminders.css | Management UI |
| src/Assister/wwwroot/app-shell.js | Navigation |
| tests/Assister.Tests/ReminderTests.cs | Parser, recurrence/DST, scope, restart/delivery |
| tests/Assister.Tests/ReminderToolTests.cs | Gating, broker CRUD/audit/scope, false-completion regression |
| tests/Assister.IntegrationTests/ReminderDashboardTests.cs | HTTP CRUD, validation, restart persistence |

## Deployment and verification — October 12, 2026

Runtime revision: 691874134e93acb4d759dc8004748f46cbfb173b.
Feature commits: bb02c8b (scheduler/dashboard), 88cfb83 (AI tools),
6918741 (confirmed reminder execution).

391 automated tests passed: 352 unit/component and 39 integration.
Browser verification covered add/edit/remove. Real model requests created a daily
reminder, retrieved it, edited its time, and cancelled it. Persisted state and correlated
tool traces verified the operations, not just model replies.

Live verification on 88cfb83 caught cancellation prose without tool execution.
The completion guard and regression test in 6918741 fixed that case. The deployed
recheck recorded reminder_cancel with status cancelled and zero active test reminders.
Tests used unconnected satellite IDs, so no household announcements were played.
Physical scheduled reminder announcement acceptance remains unverified.

Dockhand Automation (8), Git stack assister (10), synced to 6918741.
An exact Git archive was built over SSH because Dockhand build-on-deploy remains
disabled; only assister-assister-1 was updated through Dockhand.
Running image digest:
sha256:6a89d758a8136e0661b7986c0c8dfafd7b2e05c9373be655000f65a1d443e107.
The image revision label matched the full runtime revision above. Health was Healthy,
with zero restarts, unchanged environment, and preserved assister_assister-data volume.
ESPHome bridge and EchoMuse container IDs were unchanged.

## All-satellite reminder delivery

The reminder editor's **Announce on** selector starts with **All satellites**, followed by individual satellite names. Edit an existing reminder, choose its destination, and save; changing only its destination preserves the next scheduled time. The API represents all satellites as `satelliteId: "all"`. AI reminder create/edit tools accept `all_satellites: true`; omitting it on edit preserves the existing destination.

Each broadcast occurrence snapshots enabled registered satellites. Busy or offline destinations wait for retry, while successful destinations are recorded in SQLite and are not repeated on subsequent ticks or after a restart. The recurrence advances after every recipient completes. Delivery remains at least once if the process stops between playback completion and recording that completion.
