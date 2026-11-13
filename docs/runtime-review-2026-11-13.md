# Runtime and architecture review — November 13, 2026

Follow-up: the first four remaining findings below have now received local implementation and
regression coverage. See [follow-up implementation and verification](runtime-followups-2026-11-13.md)
for the resulting behavior, incremental extensibility scope, and current test counts. The findings
below preserve the evidence and recommendations from the initial review.

Assister's existing single-process C# architecture remains appropriate. The main opportunities
are repeated work on request paths, independent work sharing a serial queue, and module policies
accumulating inside central coordinators. Four focused improvements are implemented in this
checkout; the remaining findings below are ordered by their expected value.

## Scope and evidence

Reviewed the project goals and roadmap alongside service registration, shared request/conversation
routing, intent matching, LLM/tool selection and authorization, HA caching and transport, Wyoming
framing, voice/satellite delivery, timer/reminder/calendar scheduling, interaction persistence,
diagnostics, the client SDK, and browser playback tests. The ESPHome bridge received a static
boundary/queue review. This is an architecture and critical-path review, not an exhaustive proof
of every branch or a production load test.

Baseline: 623 unit/component and 78 integration tests passed. After changes: **634 unit/component,
78 integration, and 20 browser-script tests passed**. No skipped tests. `git diff --check` passed.
Tests use local fixtures/fake providers; no deployment, physical playback acceptance, real device
controls, or live service performance measurement was performed.

## Implemented improvements

| Area | Finding | Change and verification |
| --- | --- | --- |
| HA cache | Every `Snapshot()` rebuilt every entity and all three registry maps while holding the cache lock. Tools, resolution, and confirmation polling repeatedly paid this cost. | Registry maps persist until reload; state events resolve only the changed entity. Readers reuse a read-only entity list until the next change. Tests cover snapshot immutability, registry lifetime, removal, concurrent readers, refresh/reconnect, and stale status. |
| Announcement queue | A busy destination occupied the only consumer until its two-minute deadline; a slow playback blocked later destinations too. | Up to four independent deliveries, one per destination in enqueue order. Busy destinations consume no workers. A shared admission limit bounds accepted queued, deferred, and active work to 100. Tests cover busy/slow peers, ordering, capacity, expiry, concurrency, and cancellation/shutdown. |
| Wyoming framing | The reader's buffering comment was inaccurate: its one-byte header reads went directly to `NetworkStream`. | An 8 KiB read-only buffer preserves prefetched data and payload bytes. Writes remain on the raw stream for concurrent streaming TTS. Tests cover coalesced events, large payload boundaries, fragmented input, truncation, and provider/streaming integration. |
| Rich-client polling | `InteractionRunner` polls pending work every 50 ms; `Status` filtering and creation-time ordering lacked a matching index. | Added `(Status, CreatedAt)` index through the existing idempotent store initialization. Upgrade test preserves existing queued work and verifies indexed searches without a temporary ordering tree. |

Source: `HomeAssistantStateCache.cs`, `AnnouncementQueue.cs`, `WyomingEventReader.cs`, and
`InteractionStore.cs`. No new dependency, service, environment setting, or destructive schema
change is required. The index adds a small write/storage cost and is created at startup.
The HA cache retains joined entity objects between requests; this exchanges retained memory
for much lower temporary allocation. Queued announcements remain in-memory and do not survive
restart; persisted timer/reminder/notification delivery retains its separate existing semantics.

## Controlled cache measurements

Release build, Windows, .NET SDK 10.0.302; 1,000 entities, 1,000 devices, one area, one alias per
entity. Each scenario used 100 warm-up operations followed by 1,000 measured operations, repeated
five times. Values below are median elapsed totals and allocated bytes from the median-time run.
The baseline cache source was extracted from commit `8c27748` into a separate namespace and run
in the same process as the revised cache. No network, database, inference, or physical audio is
included. Microbenchmark order and JIT/GC behavior limit generalization.

| Scenario, 1,000 operations | Before | After | Allocated before | Allocated after |
| --- | ---: | ---: | ---: | ---: |
| Repeated snapshot, unchanged state | 994.94 ms | 0.0545 ms | 1,074,744,040 bytes | 48,040 bytes |
| One state event followed by snapshot | 941.28 ms | 3.85 ms | 1,074,792,040 bytes | 8,840,040 bytes |

Raw samples: [runtime-review-2026-11-13-bench.json](runtime-review-2026-11-13-bench.json).
The local comparison harness is retained in `.codex-local/runtime-review-bench/` and can be run
with `dotnet run --project .codex-local/runtime-review-bench/RuntimeReviewBench.csproj -c Release`.
That ignored harness includes the extracted baseline source and is a local artifact, not part
of the tracked benchmark tooling. These measurements demonstrate reduced cache work; they do
not establish a corresponding end-to-end voice speedup.

The Wyoming regression test also proves 100 coalesced short frames plus EOF require at most two
underlying reads, rather than a separate underlying read for every header byte. Actual socket
read counts depend on fragmentation and arrival timing.

## Remaining findings and recommended order

### 1. Serialize playback completion with conversation updates

**High priority, concurrency risk identified by inspection.**
`Conversations/ConversationCoordinator.cs:127` reads and rewrites the entire `DeviceContextJson`
in `MarkResponseFinishedAsync` without the conversation lock used by `ProcessCoreAsync`.
`Interactions/InteractionEndpoints.cs:112` invokes this through a separate HTTP request/scope.
A playback acknowledgement overlapping a new turn can therefore save older context over newer
references/receipts, or have its completion timestamp overwritten. A delayed acknowledgement
also has no originating-turn check in this method.

Next: reproduce the overlap with two DbContexts and controlled barriers, then use the same
serialization boundary, reload tracked state after acquiring it, and correlate completion to
the applicable response. Verify newer context survives and obsolete playback cannot extend the
wrong turn. This review does not claim an observed production data-loss incident.

### 2. Recover SDK observers from transient HTTP failures

**Medium priority, client reliability.** `Assister.Client/AssisterClient.cs` reconnects after
WebSocket failures, but the HTTP preflight, snapshot refresh, device-request polling, and
notification polling can throw outside that recovery path. A transient HTTP outage can terminate
an observer even though it is designed to resume a durable event cursor.

Next: share a bounded, cancellable read-retry policy with jitter. Treat credential revocation,
missing resources, protocol incompatibility, and invalid cursors as terminal. Preserve sequence
deduplication and never automatically replay state-changing requests. Test transient HTTP
failure followed by recovery, cancellation during backoff, and terminal authentication failures.

### 3. Bound and isolate Calendar I/O and delivery

**Medium priority, reliability and scaling.** `Modules/GoogleCalendar/GoogleCalendarClient.cs:43`
buffers responses without an application byte limit before parsing. Pagination caps do not cap
the size of an individual page. Its single token gate also spans authentication network I/O for
all accounts. `CalendarScheduler.cs:24` and `:151` process calendars and satellite recipients
serially; slow playback can delay later calendars beyond their five-minute grace window.

Next: bounded response streams, per-account token refresh coordination with lifecycle cleanup,
and a bounded delivery scheduler backed by the existing durable delivery keys. Separate event
discovery from physical playback. Keep scoped SQLite operations serialized and retain the
current deletion-confirmation and at-least-once delivery rules. Verify slow/erroring accounts
and speakers do not starve healthy ones. The immediate announcement queue fix above does not
change Calendar's separate scheduler.

### 4. Move module policy out of the central coordinators

**Medium priority, extensibility.** `Llm/ToolLoop.cs:35` selects tools through name prefixes and
module-specific conditions; its prompt and completion rules also contain module-specific logic.
`Voice/RequestCoordinator.cs:66` derives outcomes by searching English response text. Changing
wording or adding a module can therefore alter routing/status or require central policy edits.

Next: introduce typed native-module results (`Handled`, `Outcome`, response, structured details)
and small registered policy contributors for tool relevance and prompt guidance. Keep action
authorization, result validation, tool limits, and confirmed execution in the broker/coordinator.
Use the existing `IIntentActionProvider` / `IIntegrationIntentExecutor` separation as the model.
Migrate one module at a time with policy contract tests; a plugin marketplace or microservice
split is unnecessary for the project goals.

### 5. Cache validated intent templates and remove unnecessary idle polling

**Medium priority, speed as configuration/history grows.** `Intents/IntentEngine.cs:89` creates
regex objects for every enabled template on every match. `IntentStore.DefinitionsAsync` also
loads/deserializes definitions each time. `InteractionRunner.cs:59` polls SQLite even when idle.

Next: benchmark a populated intent catalog, then cache immutable compiled definitions by an
explicit catalog revision. Invalidate on successful save/delete and bound cache size. Keep
matching ambiguity and regex timeouts unchanged. Wake the interaction runner on durable enqueue
and completion, with periodic reconciliation so missed notifications cannot strand work. The
new SQL index reduces the present polling cost without changing scheduling semantics.

### 6. Keep diagnostic disk work off shared tracing locks

**Lower priority until profiled.** `Diagnostics/RunTracing.cs:321` onward uses one lock for active
traces and completed-history reads/writes. History listing deserializes up to 1,000 summaries;
finish writes/prunes SQLite under this synchronization boundary. A busy dashboard or slow disk
can delay unrelated tracing activity.

Next: measure lock hold time and dashboard polling under concurrent turns. Prefer lightweight
in-memory summaries and short lock scopes; any background persistence needs a bounded queue,
explicit overflow policy, and shutdown flushing. Do not silently drop protocol events or
diagnostic data while refactoring.

## Project goals

- Advanced the reliability/latency priorities with isolated announcement delivery, lower cache
  allocation/lock work, and indexed pending-work queries.
- Completed the project's intended buffered native C# Wyoming read path while retaining framing
  limits and streaming read/write separation.
- Preserved deterministic routing, action authorization, bounded LLM context/tool execution,
  protocol-independent satellite contracts, and the single C# application.
- Reconciled the roadmap with implemented one-time/relative reminders, broadcast/acknowledgement,
  Google Calendar integration, and the separate S0–S6 voice plan. These existing features were
  not newly implemented by this review.
- Physical first-useful-audio timing, hardware cancellation/media coexistence, deployment health,
  and consolidated spoken MVP acceptance remain separate validation work. S3 active endpointing
  still needs its quality/CPU acceptance; speculative S4 work should follow that evidence.

## Validation commands

```text
dotnet test Assister.sln --no-restore --verbosity quiet
node --test tests/browser/*.test.cjs
git diff --check
```

Eleven new regression tests cover the changed behavior. Existing calendar/notification work
was preserved; no commit or deployment was performed by this review.
