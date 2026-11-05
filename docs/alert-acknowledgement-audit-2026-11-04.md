# Phone acknowledgment and repeating vitamin reminder

Live audit, November 4, 2026 (America/New_York):

- Phone run `9b4686aa-f215-42a1-9ebe-43ebe1b886c9` at 23:34:30 sent
  `acknowledge alerts`; run `ee96d7af-3a23-4f71-b7d4-e5a05b17d3f2`
  at 23:36:39 sent `acknowledge`. Both fell through to the model and made
  no tool calls. Their succeeded status described generation, not acknowledgment.
- MBedroom (`echomuse-G090LF1182030WPN`) completed announcements at
  23:33:54 and 23:43:58, matching the ten-minute acknowledgment repeat interval.
- The live SQLite ledger identified the current broadcast as reminder
  `566b6a3a-59e3-4506-a070-80a783dfa553`, `take my vitamins`, due at
  21:00 November 4. Its next scheduled occurrence remains 21:00 November 5.
- A direct request to MBedroom at 23:46:54 returned the deterministic
  `satellite-acknowledgement` handler, run
  `249505de-a0d6-4bdf-92e4-53595b90265f`. The satellite event recorded
  `Announcement acknowledged`. A subsequent read-only database check found
  all four broadcast recipients marked delivered, including the offline recipient.

The old handler checked pending announcements by the requesting satellite ID.
Authenticated phone conversations have server-created `client-conversation-...`
routes, which cannot match a physical speaker's pending announcement.

Authenticated rich-client routes now handle whole commands such as `acknowledge`,
`acknowledge alerts`, and `dismiss all satellite alerts` deterministically.
They acknowledge current pending satellite announcements across the shared home,
using the same shared-satellite authority already available to rich-client
announcements. A route must resolve to an authenticated conversation owner;
a caller-supplied prefix alone grants no authority. Physical voice requests keep
their existing local scope. Explicit target phrases and unrelated reminder
cancellation requests do not match this global command grammar.

Acknowledgment preserves schedules and future occurrences. Empty pending state
returns an explicit no-pending-alerts response without invoking the model.
Rich-client notification receipts remain a separate contract.

Validation: 621 unit/component tests and 76 integration tests passed, including
authenticated phone submissions for both failed phrases, repeated empty-state
requests, strict command matching, and independent pending announcements.
