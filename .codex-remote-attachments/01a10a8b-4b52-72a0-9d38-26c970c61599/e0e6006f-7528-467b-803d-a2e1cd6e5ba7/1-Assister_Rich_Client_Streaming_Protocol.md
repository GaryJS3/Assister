# Assister Rich Client & Streaming Interaction Protocol

## Status

**Goal / Feature Specification**

This document defines the Assister-side architecture and protocol for rich interactive clients.

The primary targets are:

1. The built-in **web client**
2. A future **Android client**

The Android application itself is **out of scope** for this project. This document only defines the Assister features, APIs, protocol behavior, capability model, and server-side expectations required to support such an application later.

---

## 1. Summary

Assister should expose a common, persistent, streaming interaction model that can be consumed by multiple clients.

The web client should become the reference implementation.

A future Android client should use the same interaction protocol while also being able to advertise additional device-specific capabilities such as:

- Microphone
- Speaker
- Display
- Notifications
- Location
- Clipboard
- Camera
- Screen context
- Android intents / app launching

The core principle is:

> The Assister server owns conversations, interactions, execution, tools, context, traces, responses, and persistence. Clients render and interact with that state.

The WebSocket is a live transport, not the source of truth.

---

## 2. Goals

### 2.1 Shared client protocol

Create one protocol that supports:

- Browser/web UI
- Future Android app
- Potential future desktop clients
- Debug/trace interfaces
- Other trusted Assister clients

Clients should not need direct knowledge of:

- Qwen
- Specific LLM backends
- Home Assistant internals
- Agent implementation details
- Tool implementation details
- VLM implementation details
- STT/TTS implementation details

Clients consume normalized Assister interaction events and structured state.

---

### 2.2 Rich browser client

The web client should support:

- Typed conversations
- Voice input
- Visible STT transcription
- Attachments
- Image attachments
- Screenshot/pasted-image attachments
- Drag-and-drop files
- Streaming execution progress
- Streaming final response text
- Optional TTS playback
- Conversation history
- Context inspection
- Tool/agent trace inspection
- Cancellation
- Retry / regenerate
- Reconnection and replay after refresh/disconnect

---

### 2.3 Future Android integration

The backend must support a future Android client that can:

- Act as a normal Assister conversation client
- Stream microphone audio
- Receive TTS/audio
- Display STT text
- Display progress
- Display final text response
- Display context used
- Attach photos
- Attach screenshots
- Attach files
- Receive and expose device capability requests
- Advertise Android-specific capabilities dynamically

The Android app implementation is not part of this feature.

---

## 3. Non-Goals

This project does **not** include:

- Building the Android app
- Android UI implementation
- `VoiceInteractionService` implementation
- Wake-word implementation on Android
- Android permission handling
- Android notification implementation
- Android intent execution logic
- OEM-specific Android integrations

This project only creates the Assister-side support needed for such features.

---

# 4. Core Data Model

The system should distinguish between:

```text
Conversation
    |
    +-- Messages
    |
    +-- Interactions
            |
            +-- Input
            |     +-- typed text
            |     +-- STT
            |     +-- audio
            |     +-- attachments
            |
            +-- Execution
            |     +-- steps
            |     +-- agents
            |     +-- tools
            |
            +-- Context
            |     +-- conversation context
            |     +-- entities
            |     +-- tool-derived context
            |     +-- memories
            |     +-- attachments
            |
            +-- Response
            |     +-- streaming text
            |     +-- final text
            |     +-- TTS
            |
            +-- Trace
                  +-- raw execution events
                  +-- timings
                  +-- errors
                  +-- tool requests/results
```

---

## 5. Conversation

A conversation represents an ongoing user/assistant thread.

Suggested fields:

```json
{
  "id": "conv_...",
  "title": "Office temperature",
  "createdAt": "2026-10-05T01:00:00-04:00",
  "updatedAt": "2026-10-05T01:04:12-04:00",
  "clientMetadata": {},
  "metadata": {}
}
```

A conversation contains zero or more interactions.

---

## 6. Interaction

An interaction represents one user request and the resulting Assister execution.

Example:

```json
{
  "id": "int_...",
  "conversationId": "conv_...",
  "status": "running",
  "createdAt": "...",
  "completedAt": null,
  "input": {},
  "response": {},
  "context": [],
  "events": []
}
```

Suggested statuses:

- `created`
- `receiving_input`
- `transcribing`
- `running`
- `responding`
- `completed`
- `cancelled`
- `failed`

---

# 7. Transport

## 7.1 HTTP / HTTPS

Use normal HTTP APIs for:

- Authentication
- Conversation listing
- Conversation retrieval
- Interaction retrieval
- Attachment upload
- Historical event retrieval
- Context inspection
- Trace inspection
- Retry/regenerate actions
- Cancellation
- Capability registration where appropriate

---

## 7.2 WebSocket

Use WebSocket for:

- Live STT updates
- Execution progress
- Tool activity
- Agent activity
- Context additions
- Response streaming
- TTS lifecycle events
- Interaction completion/failure
- Device requests
- Presence / reconnect state if needed

The WebSocket must **not** be the only copy of events.

Important state must be persisted server-side.

---

# 8. Event Stream

Every live interaction should be represented by ordered events.

Every event should include:

```json
{
  "sequence": 123,
  "eventId": "evt_...",
  "interactionId": "int_...",
  "conversationId": "conv_...",
  "timestamp": "2026-10-05T01:04:12.123-04:00",
  "type": "tool.completed",
  "data": {}
}
```

`sequence` must be monotonically increasing within an interaction.

This allows clients to reconnect and request:

```text
events after sequence 123
```

---

# 9. Event Types

The exact set may evolve, but the protocol should use generic semantic events rather than UI-specific commands.

Do **not** create events such as:

- `show_spinner`
- `make_green_checkmark`
- `set_status_label`

The client determines presentation.

---

## 9.1 Interaction events

```text
interaction.created
interaction.started
interaction.cancel_requested
interaction.cancelled
interaction.completed
interaction.failed
```

---

## 9.2 STT events

```text
stt.started
stt.partial
stt.final
stt.failed
```

Example:

```json
{
  "type": "stt.partial",
  "data": {
    "text": "How hot did the office..."
  }
}
```

---

## 9.3 Step events

Steps represent user-readable execution progress.

```text
step.started
step.progress
step.completed
step.failed
```

Example:

```json
{
  "type": "step.started",
  "data": {
    "stepId": "step_12",
    "label": "Checking office temperature history"
  }
}
```

Steps may have parent/child relationships.

```json
{
  "stepId": "step_12",
  "parentStepId": "step_5"
}
```

This is important for concurrent or nested execution.

---

## 9.4 Tool events

```text
tool.started
tool.progress
tool.completed
tool.failed
```

Normal event payloads should contain safe summarized data.

Detailed raw tool parameters/results belong in trace storage and should only be included in normal live events when appropriate.

Example:

```json
{
  "type": "tool.completed",
  "data": {
    "toolCallId": "tool_13",
    "tool": "get_history",
    "summary": "Retrieved 288 temperature samples",
    "durationMs": 301
  }
}
```

---

## 9.5 Agent events

```text
agent.started
agent.progress
agent.completed
agent.failed
```

Agents may run concurrently.

Example:

```text
Understanding request
|
+-- Home Assistant agent
|   +-- Retrieved temperature history
|
+-- Log agent
|   +-- Searching HVAC logs
|
+-- Research agent
    +-- Found equipment documentation
```

The protocol must not assume execution is linear.

---

## 9.6 Context events

```text
context.added
context.updated
context.removed
```

These events indicate information that becomes part of the interaction's usable context.

---

## 9.7 Response events

```text
response.started
response.delta
response.completed
response.failed
```

Example:

```json
{
  "type": "response.delta",
  "data": {
    "text": "The office peaked at "
  }
}
```

followed by:

```json
{
  "type": "response.delta",
  "data": {
    "text": "78.4°F around 3:20 PM."
  }
}
```

Clients must be able to render a response incrementally.

---

## 9.8 TTS events

```text
tts.started
tts.audio
tts.completed
tts.failed
```

The exact audio transport can be separate from the event transport if more efficient.

The event stream should still indicate TTS state.

---

# 10. Persistence and Replay

All important interaction events must be persisted.

A client refresh must not destroy the execution trace.

Required behavior:

1. Client connects.
2. Client subscribes to an interaction.
3. Client receives live events.
4. Connection drops.
5. Server continues processing.
6. Client reconnects.
7. Client provides last received sequence number.
8. Server sends missed persisted events.
9. Client resumes live streaming.

Example:

```text
Client last sequence: 123

Server replays:
124
125
126
...

Then resumes live events.
```

---

# 11. Context Model

Context is separate from trace.

## Trace answers:

> What did Assister do?

## Context answers:

> What information contributed to the answer?

These must not be treated as the same thing.

For example:

```text
Tool retrieves 10,000 log records
        |
Small filtering agent examines them
        |
6 relevant records selected
        |
Only those 6 records enter final model context
```

The trace can show that the 10,000 records were retrieved.

The context view should show the 6 records actually used.

---

## 11.1 Suggested context record

```json
{
  "id": "ctx_...",
  "interactionId": "int_...",
  "type": "entity_history",
  "source": "home_assistant",
  "name": "Office temperature history",
  "summary": "288 samples covering the previous 24 hours",
  "provenance": {
    "entityId": "sensor.office_temperature",
    "toolCallId": "tool_13"
  },
  "inspectable": true
}
```

---

## 11.2 Suggested context types

Examples:

```text
attachment
conversation_message
conversation_summary
entity
entity_state
entity_history
tool_result
agent_result
memory
document
file
web_result
image
user_location
device_context
screen_context
```

The design should be extensible.

---

# 12. Trace Model

A detailed trace should be available for debugging.

Normal users should not be forced to view raw trace information.

The trace may contain:

- Exact event timing
- Model calls
- Agent calls
- Tool calls
- Tool parameters
- Tool outputs
- Filtering stages
- Prompt/context assembly metadata
- Errors
- Retry behavior
- Token counts when available
- Model names
- Execution duration
- Cancellation information

Example:

```text
14:32:10.114 interaction.started

14:32:10.182 model.request
               model: qwen-...

14:32:10.491 model.tool_call
               get_history({
                   "entity_id": "sensor.office_temperature",
                   "start": "..."
               })

14:32:10.793 tool.result
               duration: 301 ms
               records: 288

14:32:10.802 model.request

14:32:11.127 response.delta
```

---

# 13. Normal Progress vs Debug Trace

Assister should expose two levels of execution information.

## 13.1 User progress

Human-readable:

```text
Checking Home Assistant...
Found 288 temperature readings
Calculating maximum...
Preparing response...
```

This is what should normally appear in web and Android interfaces.

---

## 13.2 Debug trace

Technical:

```text
tool.started
tool.completed
model.request
agent.started
agent.completed
context.added
response.delta
```

with detailed timing and raw data available through inspection.

The same underlying execution can produce both views.

---

# 14. Attachments

An interaction should support zero or more attachments.

Supported initial types should include:

- Image
- Screenshot
- File
- Text snippet
- Audio

Suggested metadata:

```json
{
  "id": "att_...",
  "type": "image",
  "mimeType": "image/png",
  "name": "Screenshot.png",
  "size": 823441,
  "source": "user",
  "createdAt": "..."
}
```

---

## 14.1 Attachment lifecycle

Suggested flow:

```text
Client uploads attachment
        |
Server stores attachment
        |
Attachment associated with pending interaction
        |
User submits request
        |
Attachment enters context as appropriate
```

---

## 14.2 Web attachment methods

The web client should support:

- File picker
- Drag/drop
- Paste image from clipboard
- Paste screenshot from clipboard
- Paste text
- Optional browser microphone/audio upload

---

## 14.3 Future Android attachment methods

The protocol should support future client sources such as:

- Take photo
- Select photo
- Select file
- Current screenshot
- Shared content from Android share sheet
- Foreground screen context

The server should not depend on the Android-specific acquisition method.

---

# 15. Web Client Requirements

The web client should become the reference implementation for the rich client protocol.

---

## 15.1 Main conversation view

The UI should support:

```text
+------------------------------------------------------------+
| Assister                                 Conversation       |
+------------------------------------------+-----------------+
|                                          | Activity        |
| You                                      |                 |
| How hot did the office get today?        | Checking HA     |
|                                          | Found samples   |
| Assister                                 | Complete        |
| The office peaked at 78.4°F at 3:20 PM. |                 |
|                                          | Context         |
|                                          | Office temp     |
|                                          | Conversation    |
|                                          | Tool result     |
|                                          |                 |
|                                          | Full trace      |
+------------------------------------------+-----------------+
| Attach   Ask Assister...              Mic / Send           |
+------------------------------------------------------------+
```

The exact layout is not mandatory.

The capabilities are.

---

## 15.2 Input

Support:

- Text
- Microphone
- Attachments
- Images
- Clipboard paste
- Drag/drop

---

## 15.3 STT display

While speaking:

- Display partial transcription
- Replace/update with newer partial text
- Mark final transcription when complete
- Allow user to see exactly what Assister heard

Optional future behavior:

- Allow user to edit transcription before execution
- Configurable immediate-send vs review-before-send

---

## 15.4 Progress display

While running:

Display user-readable step/agent/tool progress.

Examples:

```text
Understanding request
Checking Home Assistant
Retrieved office temperature history
Searching HVAC logs
Preparing response
```

Concurrent work should be representable as nested or parallel items.

---

## 15.5 Streaming final response

The final response text should appear as it is generated.

Do not wait for the complete response before displaying it.

---

## 15.6 TTS

The web client should optionally:

- Request TTS
- Automatically play TTS if enabled
- Allow replay of TTS
- Display text regardless of TTS state

Text remains authoritative.

---

## 15.7 Context inspection

Each response should provide a way to inspect context used.

Example:

```text
Context used
------------------------------------------------

USER PROVIDED
  Screenshot.png                       View

HOME ASSISTANT
  sensor.office_temperature            View
  24-hour history                      View

ASSISTER
  4 previous conversation messages     View

TOOLS
  get_history(...)                     Inspect
```

Context inspection should expose provenance.

---

## 15.8 Trace inspection

Every completed or running interaction should provide a trace/debug view.

Suggested controls:

- Normal
- Detailed
- Raw JSON

Raw JSON is particularly useful during development.

---

# 16. Client Capability Model

Clients should be able to advertise capabilities.

Example:

```json
{
  "clientId": "client_...",
  "clientType": "web",
  "capabilities": [
    "text.input",
    "text.output",
    "audio.input",
    "audio.output",
    "attachments.image",
    "attachments.file"
  ]
}
```

A future Android device might advertise:

```json
{
  "clientId": "client_...",
  "clientType": "android",
  "deviceName": "Gary's Pixel",
  "capabilities": [
    "text.input",
    "text.output",
    "audio.input",
    "audio.output",
    "display",
    "attachments.image",
    "attachments.file",
    "camera",
    "screenshot",
    "notifications",
    "location",
    "clipboard",
    "screen_context",
    "intent_launch"
  ]
}
```

Capabilities must be treated as dynamic.

The server must not assume every Android client has every capability enabled.

---

# 17. Capability Names

Suggested naming convention:

```text
text.input
text.output

audio.input
audio.output

attachments.image
attachments.file

camera.capture

screenshot.capture
screen_context.read

notifications.show

location.read

clipboard.read
clipboard.write

intent.launch

device.info
```

Future capabilities can be added without changing the core protocol.

---

# 18. Device Capability Requests

The protocol should support Assister requesting an operation from a capable client.

Example:

```json
{
  "type": "device.request",
  "data": {
    "requestId": "req_...",
    "capability": "location.read",
    "parameters": {}
  }
}
```

Client response:

```json
{
  "type": "device.response",
  "data": {
    "requestId": "req_...",
    "success": true,
    "result": {
      "latitude": 0,
      "longitude": 0
    }
  }
}
```

Sensitive data in this example is illustrative only.

---

## 18.1 Permission failures

A client may advertise capability support while currently lacking runtime permission.

Example:

```json
{
  "type": "device.response",
  "data": {
    "requestId": "req_...",
    "success": false,
    "error": {
      "code": "permission_required",
      "message": "Location permission is not currently granted."
    }
  }
}
```

The server must handle this normally.

It must not assume a capability is always executable.

---

# 19. Future Android Client Contract

Again, the Android application itself is out of scope.

The Assister backend should support these future Android workflows.

---

## 19.1 Voice request

```text
Android client
    |
    +-- starts interaction
    +-- streams microphone/audio
    |
Assister
    |
    +-- STT partial events
    +-- STT final
    +-- execution progress
    +-- response streaming
    +-- TTS
```

The client should be able to display:

- Current STT
- Current execution status
- Final response
- Context used

---

## 19.2 Compact assistant mode

A future Android app may show a compact system-assistant overlay.

Assister should send the same event stream used by the full client.

No special "compact mode protocol" should be required.

The Android client decides how much data to render.

---

## 19.3 Full conversation mode

A future Android application may expose a complete chat interface equivalent to the web interface.

The same APIs should support:

- Conversation history
- Attachments
- Context
- Trace
- Retry
- Cancel
- TTS replay

---

## 19.4 Android-specific context

The protocol should permit Android to provide context records such as:

```text
current screenshot
foreground app context
selected text
shared URL
shared image
location
device state
```

These should enter Assister using the same generic context/attachment models.

---

# 20. Satellite / Provider Relationship

A future Android client may also participate in Assister's provider/satellite model.

Conceptually:

```text
Assister
    |
    +-- SatelliteManager
            |
            +-- ESPHomeProvider
            +-- EchoMuseProvider
            +-- AndroidProvider
```

The provider represents device connectivity and capability exposure.

The rich interaction protocol represents user-facing conversation/execution.

These concepts may overlap but should not be tightly coupled.

An Android client can be both:

1. A rich conversation client
2. A capability-providing device/satellite

---

# 21. Authentication

Clients must authenticate.

The protocol should support secure long-lived clients without putting credentials into URLs.

Minimum requirements:

- HTTPS/WSS in normal deployment
- Authenticated WebSocket sessions
- Revocable client/session credentials
- Per-client identity
- Server-side authorization checks
- No assumption that LAN access implies trust

Future support may include:

- OIDC
- Device authorization flow
- API tokens
- Refresh tokens
- Client certificates

Do not hard-code the protocol around one auth mechanism unless Assister already has a standard one.

---

# 22. Security Boundaries

Certain capabilities are higher risk:

```text
location.read
clipboard.read
clipboard.write
screen_context.read
screenshot.capture
camera.capture
intent.launch
```

The server must preserve enough metadata to identify:

- Which client supplied the data
- Which client executed an action
- Which interaction requested it
- Whether it succeeded
- When it occurred

Sensitive capability responses should not automatically be dumped into normal logs.

Trace/log behavior should support redaction.

---

# 23. Cancellation

A user must be able to cancel a running interaction.

Example:

```text
interaction.cancel_requested
```

The server should attempt to cancel:

- Current model generation
- Running agents
- Tool calls where cancellation is possible
- TTS generation/playback
- Pending device capability requests

The final interaction state becomes:

```text
cancelled
```

Cancellation must not leave the interaction permanently "running".

---

# 24. Errors

Errors should be structured.

Example:

```json
{
  "type": "interaction.failed",
  "data": {
    "code": "tool_execution_failed",
    "message": "Unable to retrieve Home Assistant history.",
    "recoverable": true
  }
}
```

Avoid requiring clients to parse free-form error strings.

Suggested categories:

```text
authentication_failed
permission_denied
invalid_request
attachment_failed
stt_failed
model_failed
tool_failed
agent_failed
device_unavailable
device_permission_required
tts_failed
cancelled
internal_error
```

---

# 25. Versioning

The interaction protocol must be versionable.

Suggested minimum:

```json
{
  "protocolVersion": 1
}
```

Avoid making clients depend on undocumented server internals.

New event types should generally be additive.

Clients should ignore unknown event types unless explicitly marked required.

---

# 26. Suggested HTTP API Shape

Exact routes are implementation-dependent.

A reasonable shape might be:

```text
GET    /api/conversations
POST   /api/conversations
GET    /api/conversations/{id}

POST   /api/conversations/{id}/interactions
GET    /api/interactions/{id}
POST   /api/interactions/{id}/cancel
POST   /api/interactions/{id}/retry

GET    /api/interactions/{id}/events
GET    /api/interactions/{id}/context
GET    /api/interactions/{id}/trace

POST   /api/attachments
GET    /api/attachments/{id}

GET    /api/clients
POST   /api/clients/register
```

WebSocket example:

```text
/api/ws
```

or:

```text
/api/interactions/{id}/stream
```

The exact choice should follow existing Assister conventions.

---

# 27. Suggested WebSocket Subscription Model

A single client connection may need to observe multiple things.

Suggested messages:

```json
{
  "action": "subscribe",
  "interactionId": "int_...",
  "afterSequence": 123
}
```

Server:

```json
{
  "type": "subscription.ready",
  "interactionId": "int_..."
}
```

The server then sends:

1. Missed persisted events
2. New live events

---

# 28. Audio Streaming

The initial implementation should prefer simplicity.

Possible strategies:

### Option A: WebSocket binary frames

Good for:

- STT microphone streaming
- Simple client implementation

### Option B: Separate media endpoint

Potentially better later for:

- High-rate audio
- Streaming TTS
- More efficient transport

The interaction event protocol should remain independent of the media transport.

For example, even if TTS audio is delivered separately:

```text
tts.started
tts.completed
```

still appear in the interaction event stream.

---

# 29. Web Client as Reference Implementation

Before Android work begins, the web client should prove the protocol.

Recommended order:

```text
1. Persistent interaction model
2. Persisted event model
3. WebSocket event streaming
4. Reconnect/replay
5. Web response streaming
6. Web STT display
7. Attachments
8. Progress/activity UI
9. Context inspection
10. Trace inspection
11. Capability advertisement
12. Generic device request/response protocol
13. Future Android implementation in separate project
```

---

# 30. Implementation Guidance

Keep the first implementation as small as practical.

Avoid building speculative abstractions that are not yet needed.

However, the following should be designed correctly from the beginning because changing them later will be expensive:

- Event ordering
- Event persistence
- Reconnection/replay
- Interaction IDs
- Conversation IDs
- Context provenance
- Trace/context separation
- Capability naming
- Protocol versioning
- Client identity

---

# 31. Data Retention

Interaction data can become large.

Potentially large items include:

- Raw tool results
- Audio
- Images
- Screenshots
- Logs
- Model traces
- Token-level output
- Repeated STT partials

Do not assume every event or artifact must be retained forever.

Design retention so Assister can later support policies such as:

```text
Conversation messages: long-term
Final interaction state: long-term
Context summaries: long-term
Raw trace: configurable
Raw tool output: configurable
Audio: short-term/configurable
STT partials: short-term/configurable
```

This does not need a complex retention UI in the first implementation.

The storage model should simply avoid making retention impossible to change later.

---

# 32. Observability Benefits

This feature should also improve Assister development.

For every interaction, developers should be able to determine:

```text
What did the user send?
What did STT hear?
What context was selected?
Which model ran?
Which agents ran?
Which tools were called?
What did the tools return?
What was filtered?
What reached the final model context?
What response was produced?
How long did each stage take?
Where did a failure occur?
```

This is particularly important when using smaller local models and tool-filtering agents.

---

# 33. Acceptance Criteria

The feature can be considered functionally complete when the following work.

## Core protocol

- [ ] Conversations are persistent
- [ ] Interactions are persistent
- [ ] Interactions have stable IDs
- [ ] Events have monotonically increasing sequence numbers
- [ ] Important events are persisted
- [ ] Clients can subscribe over WebSocket
- [ ] Clients can reconnect and replay missed events
- [ ] Unknown additive event types do not break clients
- [ ] Protocol version is exposed

## Input

- [ ] Typed input works
- [ ] Voice/STT input works
- [ ] Partial STT can be streamed
- [ ] Final STT is persisted
- [ ] Images can be attached
- [ ] Files can be attached
- [ ] Clipboard/pasted images work in web UI

## Execution

- [ ] User-readable execution steps stream live
- [ ] Tool activity streams live
- [ ] Agent activity streams live
- [ ] Concurrent execution can be represented
- [ ] Running interactions can be cancelled

## Response

- [ ] Response text streams incrementally
- [ ] Final response is persisted
- [ ] TTS lifecycle is represented
- [ ] Text remains available even if TTS fails

## Context

- [ ] Context is stored separately from trace
- [ ] Context records include provenance
- [ ] Web UI can show context used
- [ ] Detailed context can be inspected

## Trace

- [ ] Full interaction trace can be inspected
- [ ] Tool calls can be inspected
- [ ] Tool results can be inspected
- [ ] Agent activity can be inspected
- [ ] Timing information is available
- [ ] Raw JSON trace view is available for development

## Web client

- [ ] Full conversation UI
- [ ] Typed input
- [ ] Voice input
- [ ] STT display
- [ ] Attachment support
- [ ] Streaming progress
- [ ] Streaming answer
- [ ] Context view
- [ ] Trace view
- [ ] Cancel
- [ ] Retry/regenerate
- [ ] Refresh/reconnect does not lose interaction state

## Future Android readiness

- [ ] Generic client capability advertisement exists
- [ ] Capability names are extensible
- [ ] Generic device request/response protocol exists
- [ ] Device requests support structured failures
- [ ] Attachments are client-source agnostic
- [ ] Android-specific context can be represented generically
- [ ] No backend API depends on Android UI implementation details

---

# 34. Preferred Architectural Principle

The final design should preserve this separation:

```text
Client
    |
    |  HTTPS / WebSocket
    v
Assister Client Protocol
    |
    +-- Conversation management
    +-- Interaction lifecycle
    +-- Event persistence
    +-- Streaming
    +-- Context/provenance
    +-- Trace/debug
    +-- Attachments
    +-- Device capabilities
    |
    v
Assister Execution Layer
    |
    +-- STT
    +-- LLM
    +-- Agents
    +-- Tools
    +-- Home Assistant
    +-- VLM
    +-- TTS
    +-- Future modules
```

Clients should depend on the **Assister Client Protocol**, not directly on the execution implementation.

This should allow the web UI, Android client, and future clients to evolve independently from model/tool internals.
