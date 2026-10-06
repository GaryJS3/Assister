# Rich client protocol — text, voice, context and devices

The target clients are web, Android phones/tablets and native Windows apps. The simple web client at `/chat.html` is the reference UI. Backend and native-client scaffolding are C#/.NET 10.

## Implemented

- Version 1 C# interaction/event contracts in `Assister.Contracts`.
- Durable conversations, inputs, interaction snapshots, final responses and ordered events in `interactions.db`, alongside the existing application and diagnostics databases under `Assister:DataPath`.
- Background execution with four workers and a 64-interaction admission limit. Execution continues after HTTP/WebSocket disconnect. Requests within a conversation execute serially.
- Authenticated HTTP submission, retrieval, cancellation, trace inspection and WebSocket streaming/replay.
- Real execution step start/end events from the existing diagnostic boundaries, including intent classification, entity resolution, model rounds and tool execution. Step IDs, parent IDs, kind, status and duration are exposed without raw diagnostic payloads.
- Incremental answer text. A completed response carries the authoritative full text, correcting provisional deltas if execution falls back to an error response. Rich-client fallback text is not voice-formatted; existing satellite speech behavior is preserved.
- A functional browser conversation/history page, live activity, streaming answer, cancellation, trace JSON and refresh/reconnect recovery.
- `Assister.Client`: UI-independent HTTP methods and a WebSocket event iterator with reconnect/replay and duplicate suppression.
- Separate durable context records captured at actual model-request assembly, including conversation messages/summaries, attachments and tool results. Each record identifies the model rounds that consumed it and includes provenance. Direct cached entity answers also record the state/unit used. Round 0 denotes deterministic execution/input rather than a model call.
- Owned attachment uploads/downloads and stable attachment bindings. UTF-8 text/Markdown/JSON enters model context as explicitly untrusted data; image uploads are stored without claiming vision support. Both submission and upload preserve authenticated source-client identity.
- Web file picker, drag/drop, pasted-image upload, attachment downloads and live context counts with expandable inspection. The .NET client includes attachment upload/submission and context retrieval.
- Buffered PCM voice input, real provider partial/final transcription events, optional durable WAV output, independent generation/playback states and replayable audio. The web client captures with AudioWorklet, records at most 30 seconds and exposes recording/transcription/speech feedback.
- Dynamic client capability registration, presence leases, targeted device request/response signaling, structured permission failures, deadlines, cancellation and durable outcomes. Capability-only clients have their own authenticated signaling WebSocket; they need not watch a conversation.

## Local setup

Configure a long random credential through user secrets or environment variables; do not put credentials in source control:

```powershell
$env:RichClients__Clients__web__Token = '<random credential of at least 24 characters>'
$env:RichClients__Clients__web__Owner = 'gary'
dotnet run --project src/Assister --no-launch-profile --urls http://localhost:5080
```

Open `http://localhost:5080/chat.html` and enter the configured credential. Browser login creates an encrypted, HttpOnly, SameSite=Strict session cookie valid for eight hours. Native clients send `Authorization: Bearer <credential>`. Credentials never enter URLs. Use HTTPS/WSS in normal deployments; HTTP is for local development. No clients are authorized when no credentials are configured.

Each `RichClients:Clients:<clientId>:Token` identifies a separate client. Optional `Owner` associates multiple devices with one conversation owner; absent Owner defaults to the client ID. Ownership is configured server-side and cannot be supplied by API callers. Removing/changing a token revokes access when configuration is refreshed, including active streams. Existing unauthenticated Operations/test APIs are unchanged: this increment does not secure the whole application or make the deployment internet-ready.

## HTTP surface

All routes except login require an authenticated client. Browser Origin headers must match the serving origin; native clients can omit Origin. Cookies are scoped to `/api/client`.

| Method | Route under `/api/client` | Behavior |
|---|---|---|
| POST | `/session` | Browser login with `{ "token": "..." }` |
| DELETE | `/session` | Browser sign out |
| GET | `/protocol` | Version, supported features and limits |
| GET/POST | `/conversations` | List/create owned conversations |
| GET | `/conversations/{id}` | Latest 100 interaction snapshots, oldest first |
| POST | `/conversations/{id}/interactions` | `{ "message": "...", "idempotencyKey": "..." }`; returns 202 and interaction immediately |
| GET | `/interactions/{id}` | Durable snapshot and event cursor |
| GET | `/interactions/{id}/events?afterSequence=0` | Up to 256 ordered events; repeat using the last sequence |
| POST | `/interactions/{id}/cancel` | Persist acknowledgement and signal execution cancellation |
| GET | `/interactions/{id}/trace` | Existing redacted diagnostic trace linked through `runId` |
| GET | `/interactions/{id}/context` | Selected context, provenance and model rounds |
| GET | `/interactions/{id}/attachments` | Bound attachment metadata |
| POST | `/attachments?name=notes.txt&source=user` | Raw bytes with an allowed Content-Type; returns metadata and stable ID |
| GET | `/attachments/{id}` | Owned download with nosniff and attachment disposition |
| GET | `/attachments/{id}/metadata` | Owned upload metadata and processing status |
| GET | `/interactions/{id}/audio` | Owned WAV output; supports range requests |
| POST | `/interactions/{id}/playback` | `{ "state": "started", "playbackId": "guid" }` acknowledgement |
| POST | `/clients/register` | Device name, informational client type and canonical capabilities |
| GET | `/clients` | Devices belonging to the owner and their presence |
| DELETE | `/clients/current` | Withdraw current client presence and fail pending requests |
| GET | `/device/requests` | Outstanding requests targeted to the authenticated client |
| GET/WS | `/device/stream` | Ready, heartbeat, device.request and device.resolved signals |
| POST | `/device/responses` | Acknowledge a specific targeted request |
| GET | `/device/responses/{id}` | Retrieve a completed outcome for the targeted client |
| GET/WS | `/interactions/{id}/stream?afterSequence=0` | Replay then continue observing committed events |

Idempotency keys are scoped to the owner. Repeating the same key/input/conversation returns the original interaction; reusing a key for different input returns 409. Input is limited to 1,000 characters. Conversation history and listing pagination beyond 100 items are future work.

Submissions optionally include `attachmentIds`, with at most eight distinct IDs owned by the conversation owner. Idempotency also compares ordered attachment IDs, `audioAttachmentId` and `speak`. Combined extracted attachment text is limited to 12,000 characters. Uploaded text must be valid UTF-8 without NUL bytes, at most 12,000 characters per file. Uploads are limited to 1 MiB each and 100 files per owner. Supported types are text/plain, text/markdown, application/json, image/png, image/jpeg and fixed-format audio/pcm; images receive signature checks and `stored_only` processing status. Submitting stored-only images returns `attachment_processing_unsupported` rather than silently ignoring them. PDF/Office extraction, image decoding/vision, orphan deletion and configurable retention are not implemented yet.

## Voice and playback

Upload raw `audio/pcm` bytes at 16 kHz, mono, signed 16-bit little-endian, then submit `{ "message": "Voice input", "idempotencyKey": "...", "audioAttachmentId": "guid", "speak": true }`. The recording is buffered before submission; this increment does not stream microphone frames to STT while the user is speaking. PCM is limited to 1 MiB (about 32 seconds), and the browser recorder caps itself at 30 seconds even if page timers are delayed. Other codecs and formats must be converted by the native client.

The server emits `stt.started`, genuine `stt.partial` updates when the provider supplies chunks, then persists `stt.final` and replaces the interaction input with the transcript before routing. Empty/overlong transcripts fail without executing the request. Wyoming partial callbacks currently run during transcription finalization after audio upload. Final-only providers still work; no synthetic partials are produced. Native streaming microphone framing remains a future extension.

With `speak: true`, response text and `response.completed` precede `tts.started`. Generated PCM is bounded to 8 MiB, encoded as WAV and stored separately; `tts.audio` references its authenticated download URL, followed by `tts.completed` or `tts.failed`. A speech failure preserves successful text and does not change its outcome. Audio delivery currently waits for full synthesis rather than streaming early chunks. Cancelling during synthesis stops generation and preserves any completed answer text.

Playback is acknowledged by the consuming client, never inferred from generation. Each playback/replay uses a new `playbackId`; transitions are started to completed/stopped/failed, with identical acknowledgements idempotent. A failed attempt may be reported before started. Playback events can be appended after execution completes and do not change that terminal outcome. The default interaction stream closes at execution completion; use `followPlayback=true` (or the .NET observer's `FollowPlayback` option) to keep following post-completion playback, cancelling observation when finished. Browser replay reconstructs audio controls without automatically replaying historical speech; autoplay may require a user gesture.

## Device capabilities and signaling

Register `{ "clientType": "android_tablet", "deviceName": "Tablet", "capabilities": ["location.read", "clipboard.write"] }`. Identity and ownership come from credentials, not this body. Up to 32 canonical dotted capability names are allowed; unknown additive names are accepted. Capabilities can be updated or withdrawn. Registration expires from online presence after 90 seconds without renewal; the web client renews every 30 seconds. An active device signaling socket renews presence. Advertisement indicates support, not permission or authorization.

The trusted server execution layer can call `ClientSignals.RequestAsync` with an interaction, explicit target client, capability, bounded JSON parameters, deadline and cancellation token. It requires an active same-owner interaction and a present capable target. Generic LLM/device tools are not automatically enabled by advertisement. OS actions, permission prompts, camera/sensor access and platform UI remain the native client's responsibility.

Device requests are persisted in `client-signals.db`, have stable request IDs and deadlines (100 ms to two minutes), and are delivered only to the target client through pending retrieval or its signaling stream. Interaction progress receives request/outcome metadata without raw device parameters/results. Target response JSON is limited to 8 KiB; structured permission errors are normal outcomes. Wrong-target responses, expired responses and conflicting duplicates are rejected; identical completed responses are accepted idempotently. Results can be retrieved only by the target. Cancellation, explicit disconnect and capability withdrawal close outstanding requests. Restart closes unfinished requests with `server_restarted` rather than redelivering them.

The device signaling stream replays still-pending requests with the same IDs after reconnect and sends `device.resolved` when they leave the pending set. A native client must persist an execution ledger before performing actions and avoid repeating a claimed/completed action on replay, including uncertain results after a client crash. Stable IDs alone cannot provide exactly-once external effects. The .NET observer suppresses duplicate deliveries during its lifetime; it never executes device actions itself.

`context.added/updated` events carry summary metadata and model-round usage. Full inspected content is separately retrieved and passes credential redaction, with explicit truncation after 4,096 characters. Context captures selected model messages, not raw upstream tool retrieval. Failed or aborted interactions may have selected context without a completed answer. Direct intents do not consume attachment text merely because a file was attached; only context actually used by execution appears in the context view.

## Event delivery

Sequences start at 1 and increase within each interaction. Sequence allocation, event append and associated snapshot changes commit in one SQLite transaction. Delivery is at least once; deduplicate by interaction ID and sequence. Ignore unknown additive event types.

On WebSocket connection, `subscription.ready` announces protocol version, requested cursor and a high-water sequence. It means the subscription is established, not that replay is complete. Both missed and subsequent events are read from the same persisted ordered log, with a 50 ms polling interval; no separate replay/live queues can race. Slow socket sends have a five-second deadline and disconnect for replay. Active streams emit a heartbeat every 15 seconds. Client-to-server commands use HTTP; this first stream endpoint is read-only and subscribes to one interaction.

Clients render `response.delta` immediately, replace their answer with `response.completed.data.text`, and distinguish that from terminal `interaction.completed/failed/cancelled`. Rendering state and its cursor must be saved together; the browser instead reconstructs them from persisted history after refresh. Persisted cursors outside the current stream return an error.

`interaction.started` supplies a separate diagnostic `runId`; the interaction ID is allocated before execution. `step.started/completed/failed` include stable step IDs and parent relationships. Provider reasoning uses its own correlated events. Existing tool-capable rounds still hold prose until validated; permitted final answer rounds stream immediately.

### Thinking and step details

The capability response advertises additive features `reasoning.stream` and `execution.details`. Clients feature-detect these names and ignore unknown event types. The adapter accepts explicit provider `reasoning_content` or `reasoning` fields (preferring `reasoning_content`) separately from answer `content`. It never infers reasoning from answer text. `LanguageModel:EnableThinking=false` disables capture; true enables the provider extension. With the setting absent, provider defaults apply and explicitly returned reasoning is accepted. Unsupported providers emit no reasoning events.

Each segment uses the stable LanguageModel `stepId` and one-based `modelRound`. Started precedes deltas; completed occurs once before the model step ends, with status `completed`, `failed`, or `cancelled`. Provider timeout is failed; interaction cancellation is cancelled. Restart recovery closes persisted open segments as failed before the terminal interaction event.

Canonical event examples, inside the existing sequence/eventId envelope:

```json
{"type":"reasoning.started","data":{"stepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","modelRound":1}}
{"type":"reasoning.delta","data":{"stepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","modelRound":1,"text":"Checking available tools. ","truncated":false}}
{"type":"reasoning.completed","data":{"stepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","modelRound":1,"status":"completed","truncated":false}}
{"type":"step.updated","data":{"stepId":"ee0a1cbc-2962-48cb-9183-a4bfa30e17e","parentStepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","kind":"ToolCall","input":{"query":"office"},"output":null,"inputTruncated":false,"outputTruncated":false}}
{"type":"step.updated","data":{"stepId":"ee0a1cbc-2962-48cb-9183-a4bfa30e17e","parentStepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","kind":"ToolCall","input":{"query":"office"},"output":{"entities":[]},"inputTruncated":false,"outputTruncated":false}}
```

Append reasoning deltas only to the thinking segment. Collapse thinking when `response.completed` arrives if desired, but retain it for reopening. Reasoning never enters answer deltas, speech, or conversation history. Replay reasoning and step updates with the existing cursor and duplicate suppression rules; replay returns original event IDs. Step updates replace the input/output state for their step, arrive when payloads are captured before the terminal step event, and use the same sanitized values as authenticated trace inspection.

Redaction and diagnostic capture settings apply before persistence and delivery. Reasoning retains at most 32,768 source characters per round, with the existing 4,096 character string limit per emitted payload and shared 131,072 byte detailed run budget. Streaming retains trailing credential context (at least 32 characters, or the longest configured secret) across provider fragments and emits safe word boundaries. Short thoughts and trailing fragments flush on completion, failure, or cancellation. Truncation flags indicate lost content; disabled payload capture omits reasoning text.

Tests cover provider fragments and disabled thinking, ordered multiple rounds, redaction and truncation, persisted reopen and cursor replay, repeated delivery, cancellation, and restart recovery.

### Correlated ordinary model output

The additive feature `model.output.stream` advertises `model.output.started`, `model.output.delta`, and `model.output.completed`. These carry ordinary provider `content` for every model round, including tool-capable rounds whose prose is withheld from the answer. They use the same stable `stepId` and one-based `modelRound` as reasoning events, but their text is separate from reasoning and from the user-facing answer. Render it as provisional step detail; it does not confirm an action or authorize tool execution. Keep rendering the answer from `response.delta` and authoritative `response.completed`.

```json
{"type":"model.output.started","data":{"stepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","modelRound":1}}
{"type":"model.output.delta","data":{"stepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","modelRound":1,"text":"I will check the office sensor. ","truncated":false}}
{"type":"model.output.completed","data":{"stepId":"842c1fa1-620d-4798-9f6e-942581f0bd13","modelRound":1,"status":"completed","truncated":false}}
```

The stream consumes provider text fragments as they arrive, before the completed model response or tool execution. It shares reasoning's redaction buffer, capture settings, per-round source limit, and detailed run budget. Safe prefixes stream incrementally; short text and trailing credential context flush when the round finishes. This buffering means events need not match provider token boundaries. Non-streaming model calls emit available output after completion. Rounds without ordinary text emit no model-output segment. Completion statuses are `completed`, `failed`, or `cancelled`; restart recovery closes open segments. Events persist after completion and use existing replay cursors and duplicate suppression.

Tests assert ordinary tool-round output arrives before model completion and tool execution, remains separate from answer and reasoning, and correlates across rounds. Persistence, duplicate replay, cancellation, failure, and restart tests apply to both streams.

Cancellation is best effort and cannot undo commands already accepted by a device. Pending work reaches cancelled; completed work is immutable. Restart marks all unfinished interactions failed with `server_restarted`, including unclaimed submissions, rather than silently repeating controls. Automatic retry/regenerate is not implemented.

## Native client use

```csharp
using Assister.Client;
using System.Net.Http.Headers;

using var http = new HttpClient { BaseAddress = new Uri("https://assister.example/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
var client = new AssisterClient(http);
var conversation = await client.CreateConversationAsync(cancellationToken);
var interaction = await client.SubmitAsync(conversation.Id, "How warm is the office?",
    Guid.NewGuid().ToString(), cancellationToken);
await foreach (var item in client.ObserveAsync(interaction.Id, Token: cancellationToken))
{
    // Update native UI from semantic events; dispatch to the UI thread as appropriate.
}
```

Keep the same idempotency key if a submission must be retried after an uncertain network result. Disposing/cancelling observation does not cancel the interaction; call `CancelAsync` explicitly. Caller owns HttpClient and credential refresh. Use a base URL ending in `/`. Streaming microphone/early-audio transports remain future work.

The SDK also supports optional connection-state callbacks, `UploadAttachmentAsync` for PCM, submission options for `AudioAttachmentId`/`Speak`, `DownloadAudioAsync`, `ReportPlaybackAsync`, `RegisterAsync`, `ObserveDeviceRequestsAsync`, `RespondAsync` and `GetDeviceOutcomeAsync`. Register capabilities before opening the device observer. Both observers check the v1 readiness message and ignore unknown additive event types. Dispose returned audio streams after use. Connection-state callbacks are transport feedback; execution and playback remain separate semantic events.

## Verification and remaining work

Local C# tests exercise SQLite reopen, concurrent sequence allocation, idempotency conflicts, interrupted-work recovery, early deltas, WebSocket disconnect/replay, cancellation, browser login/logout, origin checks, ownership and two devices sharing an owner. A fake streaming execution backend avoids performing real Home Assistant controls. This is protocol verification, not live LLM/STT/TTS or physical-device acceptance.

The real coordinator is also tested with unavailable Home Assistant, proving intent-classification feedback and authoritative response delivery without issuing a device control. An isolated local server completed a native timer request through the new HTTP endpoints and returned events plus its diagnostic trace. Attachment integration uses real coordinators and a capturing fake model to verify that uploaded content reaches model assembly and produces persisted provenance. Storage tests verify context reopen, redaction and bounded inspection; tool-loop tests verify round-specific message/tool provenance. Voice tests cover partial/final transcripts, text-before-TTS ordering, WAV output, TTS failure, synthesis cancellation and playback idempotency. A fake Wyoming TCP server verifies actual provider chunk callbacks. Device tests cover native/server WebSocket delivery, targeting, success and permission errors, withdrawal, timeout, cancellation and restart recovery. The full solution has 233 passing C# tests. Three Node recorder tests verify 48/44.1 kHz conversion, PCM sample encoding and the recording limit. Physical microphone/speaker and real device permissions remain unverified; browser visual verification was unavailable because tab creation was restricted in the sidebar session. No deployment was performed.

Next increments:

1. Streaming microphone framing and early TTS audio delivery, with live-provider and physical-browser acceptance.
2. Execution tools that deliberately consume device capabilities, plus platform-specific permissions and durable execution ledgers in future native clients.
3. Vision-capable image processing and additional document formats, plus attachment cleanup/retention.
4. Conversation/event pagination, retention and expired-cursor snapshot recovery.
5. Richer protocol negotiation, multiplexed subscriptions, SDK connection-state signaling, retry/regenerate with side-effect policy and whole-application authentication hardening.

SQLite schema is currently additive startup SQL, matching the diagnostics store pattern; future protocol schema changes need explicit versioned upgrades. Protocol event retention is not enabled yet. Detailed trace retains its existing independent limits and expiry.

Run one server replica: active execution cancellation and client presence are process-local, while interactions, events, audio and device outcomes persist in SQLite. Audio and device-outcome retention are not enabled yet.
