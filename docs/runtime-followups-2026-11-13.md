# Runtime review follow-ups — November 13, 2026

Implemented the four requested follow-ups in order: conversation/playback concurrency,
client read recovery, Calendar isolation, and extensibility. Changes are local; running
service state and hardware acceptance were not checked.

## Conversation and playback

Playback completion now acquires the same satellite lock as request processing, then reloads
the conversation from SQLite before changing context. It records only against the applicable
response run ID and expected device. Voice playback, rich interaction completion, and client
playback acknowledgements pass that ID. A newer turn therefore keeps its references, receipts,
and continuation timestamp when an earlier acknowledgement arrives.

The run ID is stored in existing context JSON, requiring no schema migration. Pre-upgrade
contexts without an ID retain legacy completion support; contexts with an ID require a match.
The native C# completion method's callers must supply `ExpectedRunId` for new responses.
The client HTTP protocol remains unchanged.

Regression coverage uses two DbContexts and a held conversation lock to prove completion
waits, reloads externally updated references, and rejects obsolete, uncorrelated, or wrong-device
completion. The existing long-playback and duplicate-acknowledgement integration tests also pass.

## Client observers

Interaction preflight/snapshot reads, notification polling, and device-request polling share a
read-only retry helper. It retries transport failures, request timeouts, HTTP 408/429 and 5xx
responses up to five times with exponential backoff and jitter. Backoff is cancellable; each
new successful polling cycle resets the budget. Authentication, missing resources, invalid
cursors, malformed JSON, and unsupported protocols continue to fail visibly. Mutation methods
are not routed through the retry helper.

Tests cover eventual notification delivery after a 503, a terminal 401 after a transient error,
and cancellation during recovery. Existing cursor replay and device/client integration tests
exercise the observer paths.

## Calendar isolation

Calendar API responses are capped at 2 MiB and OAuth responses at 64 KiB before JSON parsing.
Response headers are read first, and a linked ten-second deadline covers account coordination,
authentication, headers, and body consumption. Existing pagination limits remain in place.

Account refresh gates are keyed by account, reference-counted, and removed/disposed when the
last user leaves. Same-account requests share the refreshed token while another account can
authenticate independently. Cached tokens for deleted accounts are removed on later access.

The background poller processes up to four calendars concurrently. Satellite recipients are
also processed concurrently, with a scheduler-wide limit of four active delivery operations.
Each recipient gets its own scope and database connection; concurrent sends never share a
DbContext. Existing delivery keys are written only after successful delivery and retain their
at-least-once semantics. Busy destinations remain eligible for a later poll.

Tests demonstrate independent account refresh, shared same-account refresh, rejection of an
oversized page, peer playback while another speaker blocks, and no replay of completed
deliveries. Calendar CRUD, confirmation, recipient selection, agenda and retry tests also pass.

Bounded concurrency still permits queueing when all workers are occupied. Discovery and delivery
remain part of the same poll cycle; this change does not introduce a new durable job subsystem.

## Extensibility

`IToolSelectionPolicy` contributors are resolved through DI. The built-in contributor preserves
existing relevance and memory/weather rules, and an additional contributor can offer its own
registered tools without modifying `ToolLoop`. Selection remains separate from broker schema
validation and action authorization. Tests verify the existing policies and an added tool.

`NativeModuleResult` carries response wording separately from its outcome. Announcement handling
is the first migrated module: the request coordinator consumes explicit outcomes for clarification,
invalid input, unavailable destinations, partial delivery failures, and success. Existing string
methods remain compatibility wrappers. Tests cover typed outcomes and existing announcement
flows. Reminder outcome migration and decomposition of module-specific prompt/completion policy
can proceed through these extension points in later increments.

## Verification

Final checkout: 645 unit/component tests, 81 integration tests, and 20 browser-script tests pass.
Fourteen tests were added in this follow-up; the existing MVP playback test now supplies the
originating response run ID. `git diff --check` passes.

```text
dotnet test Assister.sln --no-restore --verbosity quiet
node --test tests/browser/*.test.cjs
git diff --check
```

Intent compilation caching, notification-driven interaction scheduling, diagnostic lock profiling,
and physical latency/cancellation acceptance remain the next items from the original review.
