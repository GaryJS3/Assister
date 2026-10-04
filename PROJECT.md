# Project: Assister

Build a self-hosted local voice-assistant orchestration service named **Assister**.

The primary problem Assister is intended to solve is that Home Assistant's existing local AI pipeline puts too much responsibility into Home Assistant and tends to expose too much context to the LLM. Assister should own voice processing, deterministic intent handling, conversations, LLM/tool orchestration, memory, timers, and future external integrations.

Home Assistant should be treated primarily as an external automation/data platform that Assister can query and control.

The architecture must specifically avoid sending all Home Assistant entities, states, history, memories, or tool definitions to the LLM on every request.

## Core technology

Use:

- .NET 10
- C#
- ASP.NET Core
- PascalCase naming
- Allman brace style
- async/await throughout
- SQLite for initial persistence
- EF Core SQLite unless there is a compelling reason for a lower-level persistence layer
- Docker
- `compose.yaml`
- Git-friendly configuration suitable for Dockhand Git sync and redeployment

The main Assister application must be C#.

Do not introduce Redis, PostgreSQL, RabbitMQ, Kafka, or other infrastructure unless there is an actual technical requirement.

Do not use Python for Wyoming support. Wyoming is simple enough to implement directly in C#.

A small Python sidecar using `aioesphomeapi` is acceptable specifically for ESPHome Native API satellite support because there is currently no equivalent mature .NET library. Keep that component strictly isolated behind an Assister satellite abstraction.

## High-level architecture

Use this basic flow:

```text
Satellite
   |
   v
Satellite Transport
   |
   v
Voice Session
   |
   v
Wyoming STT
   |
   v
Transcript
   |
   v
Request Router
   |
   +--------------------+
   |                    |
   v                    v
Direct Intent        LLM Path
   |                    |
   v                    v
Tools / HA          Tool Router
   |                    |
   |                    v
   |                  LLM
   |                    |
   |                 tool_calls
   |                    |
   +-----------> Tool Broker
                       |
                       v
                 Module / Tool
                       |
                       v
                     LLM
                       |
                       v
                  Response Text
                       |
                       v
                Voice Formatter
                       |
                       v
                  Wyoming TTS
                       |
                       v
                    Audio
                       |
                       v
                   Satellite
```

The LLM is one component in the system. It must not be the default processing path for every request.

---

# Solution structure

Use a structure approximately like:

```text
Assister/
├── compose.yaml
├── .env.example
├── .gitignore
├── README.md
├── Assister.sln
│
├── src/
│   ├── Assister/
│   │   ├── Assister.csproj
│   │   ├── Program.cs
│   │   │
│   │   ├── Api/
│   │   ├── Configuration/
│   │   ├── Voice/
│   │   ├── Satellites/
│   │   ├── Speech/
│   │   │   └── Wyoming/
│   │   ├── Intents/
│   │   ├── Conversations/
│   │   ├── Llm/
│   │   ├── Tools/
│   │   ├── Modules/
│   │   │   ├── HomeAssistant/
│   │   │   ├── Timers/
│   │   │   └── Memory/
│   │   ├── Persistence/
│   │   └── Diagnostics/
│   │
│   └── Assister.Contracts/
│
├── bridge/
│   └── esphome/
│
├── tests/
│   ├── Assister.Tests/
│   └── Assister.IntegrationTests/
│
└── docker/
    ├── Assister.Dockerfile
    └── EspHomeBridge.Dockerfile
```

Do not split the C# code into many deployable microservices. Logical separation through interfaces and namespaces is sufficient.

---

# 1. Voice sessions

Everything related to one spoken interaction should belong to a `VoiceSession`.

Create a model similar to:

```csharp
public sealed class VoiceSession
{
    public Guid Id { get; init; }

    public string SatelliteId { get; init; } = string.Empty;

    public string? AreaId { get; init; }

    public Guid? ConversationId { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    public string? WakeWord { get; init; }

    public string Language { get; init; } = "en";
}
```

A voice session represents a single activation/request.

A conversation represents multiple related interactions.

Do not treat those as the same object.

Every pipeline run should also get a `TraceId` so that all STT, intent, tool, LLM, TTS, and satellite events can be correlated.

---

# 2. Satellite abstraction

Create a protocol-independent satellite layer.

Something conceptually similar to:

```csharp
public interface ISatelliteConnection
{
    string SatelliteId { get; }

    string Name { get; }

    string? AreaId { get; }

    IAsyncEnumerable<AudioChunk> ReceiveAudioAsync(
        CancellationToken cancellationToken);

    Task SendAudioAsync(
        IAsyncEnumerable<AudioChunk> audio,
        CancellationToken cancellationToken);

    Task SendEventAsync(
        SatelliteEvent satelliteEvent,
        CancellationToken cancellationToken);
}
```

Do not let ESPHome-specific types leak into the rest of Assister.

Create a manager responsible for:

- satellite registration
- connection state
- area assignment
- capabilities
- current voice session
- audio input
- audio output
- timer notifications
- disconnect/reconnect handling

## ESPHome satellites

### Authoritative ownership (Milestone 7 realignment)

Assister owns the voice-assistant subscription, wake events, microphone audio, voice sessions,
conversations, STT, shared deterministic/LLM/tool routing, TTS and voice playback on managed
satellites. Home Assistant remains an external automation/data/media backend. Never route
Assister voice requests through Home Assistant Assist.

ESPHome supports multiple Native API connections, but `voice_assistant` has one active API
client and rejects another subscriber. HA's ESPHome Assist Satellite entity subscribes to
the same component. TCP/Noise success does not prove voice ownership. Represent Unknown,
Available, OwnedByAssister, Conflict and Unsupported explicitly. The current subscription
protocol has no success acknowledgment: an incoming voice request confirms ownership;
the explicit device rejection log confirms conflict. Otherwise ownership remains Unknown.
Do not steal ownership or reconnect aggressively on conflict.

Keep HA's ESPHome integration for compatible normal entities, `media_player`, volume, sensors,
buttons and Music Assistant. Validate media and media-player announcement coexistence on
hardware before claiming support. HA `assist_satellite.announce` is outside this guarantee.
See [satellite-architecture.md](docs/satellite-architecture.md) for sources and acceptance.

The boundaries remain: ESPHome owns hardware/audio primitives; the thin Python bridge owns
encrypted protocol translation; SatelliteManager owns endpoint state/capabilities/output
routing; VoicePipeline owns spoken interaction; the shared coordinator owns request semantics;
the HA module owns validated HA queries/actions. Use one ESPHome provider for compatible
devices regardless of brand; inspect EchoMuse's actual protocol before selecting an adapter.

Persist identity, name, area, provider, manually configured endpoint, enabled policy and desired
runtime configuration in existing SQLite. Keep observed connection, capabilities, metadata,
ownership, voice session and playback state in memory. Refresh on reconnect, since firmware
may change capabilities. Satellite, transport session, voice session, conversation and trace
identities must stay separate. Reject duplicate starts and drop late audio/playback events.

Native ESPHome feature bits stay inside the bridge. Expose normalized microphone, multiple
source channels, API audio, wake word/configuration, speaker, media/announcement playback,
volume/mute, timers and start-conversation capabilities. Hide/disable unsupported UI actions.
Preserve source-channel metadata while choosing the existing mono STT stream.

Separate voice response, announcement and media. Preserve HTTP response delivery and the
device announcement pipeline; let firmware duck/resume music. No custom mixer or music
system. Timer policy remains in Assister; expiry and future notifications use SatelliteManager.

Wake-word selection must use reported model IDs, obey the active-model limit, and read back
actual configuration. Persist desired configuration, reconcile without silently substituting
missing models, and display drift. Firmware-derived settings are read-only. No compiler,
flasher, discovery, custom firmware, or Milestone 9 streaming/barge-in work in this increment.

Use bounded semantic traces and satellite events, with session/trace correlation and sanitized
debug payloads. Preserve completed response text on TTS/playback failure and never route after
STT failure. Device logs use short explicit diagnostic leases and turn off at timeout. Secrets
remain external; separate bridges can use separate encryption-key files or secret values.
Never write credentials to records, audit, API/UI, logs or Git.

Device-reachable HTTP audio URLs use opaque random identifiers, correct MIME, bounded object
count/size and short TTL with periodic cleanup. Assister serves its own audio directly.

HA Voice and compatible ESPHome satellites use ESPHome Native API rather than Wyoming.
The deployed EchoMuse fork also provides an external voice v1 WebSocket protocol. Use a
native C# EchoMuse provider for that distinct protocol: one authenticated controller socket
multiplexes approved inventory devices, microphone turns and playback acknowledgments.
Correlate every turn by both controller device ID and session ID; standalone announcements
use request ID plus device ID. EchoMuse owns its hardware capture/endpointing and playback
transport; Assister uses the same VoicePipeline and request coordinator as other satellites.
Do not double-apply energy endpointing. No HA Assist pipeline participates in external turns.
Serve opaque 48 kHz mono WAV response URLs through Assister; retain them through playback,
then remove them or expire by TTL. Controller credentials stay in deployment secrets.
This adapter is justified by the fork's protocol, not the Echo brand. Native ESPHome devices
continue to use the existing encrypted bridge. Approved controller inventory can initialize
logical records when this explicitly configured provider is enabled; it does not add mDNS
discovery, provisioning, firmware management or a second conversation/request pipeline.

For the initial implementation, create an optional `esphome-bridge` container based on `aioesphomeapi`.

Assister should communicate with this bridge using a small, strongly defined streaming API.

gRPC is preferred for this internal link because we need bidirectional streaming and binary audio.

The bridge should do as little as possible.

Its responsibilities are only:

```text
ESPHome Native API
        |
        v
connection management
capability detection
voice start/stop events
microphone audio
speaker audio
conversation identifiers
timer events where supported
        |
        v
Assister gRPC contract
```

Do not put:

- intent handling
- Home Assistant logic
- LLM logic
- conversation logic
- memory
- timers
- STT
- TTS

inside the bridge.

Initially allow satellites to be configured manually by hostname/IP.

Automatic ESPHome discovery can come later.

---

# 3. Wyoming protocol

Implement a native C# Wyoming client.

Do not add a Python Wyoming dependency.

Wyoming framing consists of:

```text
JSON header + newline
optional data bytes
optional payload bytes
```

The header contains fields including:

```text
type
data
data_length
payload_length
```

Implement the parser carefully so partial TCP reads are supported.

Prefer `System.IO.Pipelines` or another buffered streaming implementation rather than assuming one socket read equals one Wyoming message.

Create types such as:

```text
WyomingConnection
WyomingEvent
WyomingEventReader
WyomingEventWriter
WyomingAudioChunk
WyomingServiceInfo
```

Support `describe` / `info` capability discovery.

Do not blindly assume that a configured endpoint supports the requested service.

---

# 4. Wyoming STT provider

Create:

```csharp
public interface ISpeechToTextProvider
{
    Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        SpeechToTextOptions options,
        CancellationToken cancellationToken);
}
```

Implement:

```text
WyomingSpeechToTextProvider
```

The Wyoming STT request should follow:

```text
transcribe
audio-start
audio-chunk
audio-chunk
...
audio-stop
transcript
```

Support streaming transcript events when the server advertises or provides them:

```text
transcript-start
transcript-chunk
transcript
transcript-stop
```

Do not require streaming transcripts for the first working version.

Preserve audio metadata:

- sample rate
- sample width
- channels

Do not unnecessarily convert PCM formats.

Only resample if the selected STT backend requires it.

---

# 5. Wyoming TTS provider

Create:

```csharp
public interface ITextToSpeechProvider
{
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        TextToSpeechOptions options,
        CancellationToken cancellationToken);
}
```

Implement:

```text
WyomingTextToSpeechProvider
```

Support the normal flow:

```text
synthesize
     |
     v
audio-start
audio-chunk
audio-chunk
...
audio-stop
```

Design the implementation so streaming text synthesis can later use:

```text
synthesize-start
synthesize-chunk
synthesize-stop
```

This is important because we eventually want:

```text
LLM token stream
      |
sentence boundary
      |
      v
Wyoming TTS
      |
      v
satellite playback
```

Do not require streaming LLM-to-TTS for the first milestone, but do not design the interfaces in a way that prevents it.

---

# 6. Home Assistant integration

Create a persistent WebSocket client to:

```text
ws://homeassistant:8123/api/websocket
```

Authenticate using a long-lived access token supplied through configuration/environment variables.

Do not include the HA token in any LLM prompt, tool result, diagnostic page, or normal log.

Create:

```text
HomeAssistantClient
HomeAssistantStateCache
HomeAssistantEntityIndex
```

At startup:

```text
connect
authenticate
get_states
get_services
load registry metadata
subscribe to state_changed
```

Use the Home Assistant WebSocket API as the primary source for current state.

Maintain a local in-memory cache instead of querying HA for every request.

Subscribe to:

```text
state_changed
```

and update the local cache.

Also obtain metadata from the entity, device, and area registries where available.

Relevant Home Assistant WebSocket commands currently include:

```text
config/entity_registry/list
config/device_registry/list
config/area_registry/list
```

Keep these calls encapsulated so that changes in Home Assistant do not affect the rest of Assister.

Join the registries into an internal representation similar to:

```csharp
public sealed class HomeAssistantEntity
{
    public string EntityId { get; init; } = string.Empty;

    public string Domain { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> Aliases { get; init; }
        = Array.Empty<string>();

    public string? AreaId { get; init; }

    public string? AreaName { get; init; }

    public string? DeviceId { get; init; }

    public string State { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, object?> Attributes { get; set; }
        = new Dictionary<string, object?>();
}
```

---

# 7. Entity resolution

Entity resolution must be a separate subsystem.

Do not rely on the LLM to turn every phrase into an entity ID.

Create:

```text
IEntityResolver
HomeAssistantEntityResolver
EntityResolutionResult
```

Resolution should consider:

```text
exact entity name
aliases
entity ID
area
satellite area
domain
device name
current state where useful
fuzzy name matching
```

Example:

```text
Satellite:
Kitchen

Request:
"Turn the light off"

Search priority:
area = kitchen
domain = light

Candidates:
light.kitchen_ceiling

Result:
confidence = 1.0
```

The resolver should return confidence and alternatives.

Example:

```csharp
public sealed record EntityResolutionResult(
    HomeAssistantEntity? Entity,
    double Confidence,
    IReadOnlyList<HomeAssistantEntity> Alternatives);
```

Do not silently operate on a low-confidence target.

If several equally plausible targets exist, either ask the user or allow the LLM/conversation layer to resolve the ambiguity.

---

# 8. Direct intent engine

Build deterministic intent handling before the LLM path.

Create:

```csharp
public interface IDirectIntentHandler
{
    Task<IntentMatch?> TryMatchAsync(
        UserRequest request,
        CancellationToken cancellationToken);

    Task<IntentResult> ExecuteAsync(
        IntentMatch match,
        CancellationToken cancellationToken);
}
```

Initial direct intents should include:

```text
turn entity on
turn entity off
set light brightness
increase brightness
decrease brightness
open
close
lock
unlock
set climate temperature
query simple state
start timer
cancel timer
```

Do not implement this as one giant regular expression file.

Separate:

```text
language parsing
intent classification
slot extraction
entity resolution
execution
```

The parsed intent should be structured.

Example:

```json
{
  "intent": "SetDevice",
  "domain": "light",
  "target": "kitchen light",
  "property": "power",
  "value": false
}
```

Only execute a direct intent if confidence is sufficiently high.

Otherwise pass the request to the LLM path.

---

# 9. Tool system

Create a generic Assister tool abstraction.

Conceptually:

```csharp
public interface IAssisterTool
{
    string Name { get; }

    string Description { get; }

    JsonElement InputSchema { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}
```

The LLM must never directly connect to Home Assistant.

All actions go through Assister's tool broker.

Create:

```text
ToolRegistry
ToolBroker
ToolExecutionContext
ToolResult
```

The broker should provide:

- schema validation
- timeouts
- cancellation
- result-size limits
- exception handling
- audit logging
- security policy
- tracing

Tools should be able to mark themselves as:

```text
ReadOnly
StateChanging
Sensitive
PotentiallyLongRunning
```

This will be useful for permission policies later.

---

# 10. Initial Home Assistant tools

Do not expose every HA service as an LLM function.

Start with a small generic tool set:

```text
ha.search
ha.get_state
ha.control
ha.get_history
```

Optionally:

```text
ha.get_logbook
```

## ha.search

Example input:

```json
{
  "query": "office temperature",
  "domains": [
    "sensor",
    "climate"
  ],
  "area": "office",
  "limit": 10
}
```

Return compact results only.

Example:

```json
[
  {
    "entity_id": "sensor.office_temperature",
    "name": "Office Temperature",
    "area": "Office",
    "state": "74.1",
    "unit": "°F"
  }
]
```

Do not return every HA attribute by default.

## ha.get_state

Return one or a small number of requested entities.

## ha.control

Accept a structured action rather than arbitrary raw HA API requests.

Example:

```json
{
  "entity_id": "light.office",
  "action": "turn_on",
  "parameters": {
    "brightness_pct": 50
  }
}
```

Validate the domain/action combination before calling HA.

## ha.get_history

Use Home Assistant's history API.

Always filter to explicitly requested entities.

Prefer:

```text
minimal_response
no_attributes
significant_changes_only
```

where appropriate.

Implement an optional resolution/downsampling mechanism so that a request for a week of temperature history does not inject thousands of points into the LLM.

The tool result should be optimized for reasoning rather than faithfully dumping the HA database.

---

# 11. LLM provider

Create:

```csharp
public interface ILanguageModel
{
    Task<LlmResponse> CompleteAsync(...);

    IAsyncEnumerable<LlmStreamEvent> StreamAsync(...);
}
```

Initial implementation:

```text
OpenAiCompatibleLanguageModel
```

Target:

```text
POST /v1/chat/completions
```

It must work with llama.cpp.

Use raw `HttpClient` and strongly typed JSON DTOs unless an SDK demonstrably makes compatibility easier.

Do not tightly couple Assister to a specific OpenAI SDK.

Support:

```text
model
temperature
max_tokens
messages
tools
tool_choice
stream
```

Support OpenAI-style function/tool calls.

Assister owns the tool execution loop.

Example:

```text
LLM
 |
 | tool_call: ha.search
 v
ToolBroker
 |
 v
HA Module
 |
 v
tool result
 |
 v
LLM
```

Add configurable maximum tool iterations.

Default to something conservative such as 8.

Prevent infinite loops.

---

# 12. Tool selection / context reduction

Do not send every registered tool to every LLM call.

Implement a `ToolSelector`.

Initially this can use lightweight deterministic routing rather than another LLM.

Example categories:

```text
homeassistant
timers
memory
weather
calendar
system
```

For:

```text
"Was my office hot this afternoon?"
```

select:

```text
ha.search
ha.get_state
ha.get_history
```

For:

```text
"Remember that..."
```

select:

```text
memory.store
memory.search
```

For:

```text
"Set a timer..."
```

the direct intent system should normally handle it without invoking the LLM.

Design the selector so a small model-based router could replace or augment it later.

---

# 13. Conversation management

Create separate models for:

```text
Conversation
ConversationTurn
VoiceSession
ToolExecution
```

Persist conversations in SQLite.

A conversation turn should retain:

```text
user transcript
assistant final response
timestamps
tool calls
tool results
trace ID
```

However, full tool output should not automatically be inserted into subsequent LLM prompts.

Build a `ConversationContextBuilder`.

The normal context for a follow-up should contain:

```text
system prompt
conversation summary if present
last N user/assistant conversational turns
current user request
selected relevant memories
selected tools
current satellite/area/time context
```

Do not automatically include previous raw tool results.

Configure a maximum context budget.

Use approximate token estimation if necessary.

When context becomes too large:

```text
summarize older conversation
retain recent turns
retain durable facts
discard old raw tool data
```

---

# 14. Conversation continuity

Support interactions such as:

```text
User:
What's the weather tomorrow?

Assistant:
...

User:
What about Saturday?
```

The second request should reuse the active conversation.

Conversation association should be based on:

```text
satellite
user if known
recent conversation
explicit satellite conversation ID if available
configurable follow-up timeout
```

The timeout should be configurable.

Do not hard-code conversational state globally.

Different satellites should be capable of independent conversations.

---

# 15. Response formatting for voice

Create a separate:

```text
VoiceResponseFormatter
```

Do not rely exclusively on prompting the LLM.

Before TTS:

- strip Markdown formatting
- remove links unless speaking the URL is useful
- normalize odd punctuation
- convert common symbols into natural speech
- avoid reading JSON/tool syntax
- collapse excessive whitespace
- enforce a configurable spoken response length when appropriate

Examples:

```text
74°F
```

should become something natural like:

```text
74 degrees Fahrenheit
```

The LLM system prompt should also instruct the model that the output is being spoken and should normally be concise.

---

# 16. Timers

Implement timers as native Assister functionality.

Do not make the LLM maintain timers.

Persist them in SQLite.

Create:

```text
TimerService
TimerRepository
TimerIntentHandler
```

A timer should include:

```text
ID
name
created time
due time
satellite ID
area ID
conversation ID
status
```

Example direct request:

```text
"Set a timer for 15 minutes."
```

should become:

```text
DirectIntent
  -> TimerService
```

without using the LLM.

When the timer expires, use the associated satellite to announce or play the timer event.

Design the satellite abstraction to support this even if initial hardware support is incomplete.

---

# 17. Memory

Add a simple memory module but keep it intentionally small.

Initial persistence can use SQLite plus FTS.

Do not add a vector database for the MVP.

Create tools:

```text
memory.search
memory.store
memory.delete
```

Potential stored fields:

```text
ID
content
subject
created
updated
source conversation
tags
```

Only retrieve memories related to the current request.

Never dump the complete memory database into the LLM prompt.

Embedding/vector search can be added later behind the same interface.

---

# 18. Module system

Design Assister so functionality is organized into modules.

Create something similar to:

```csharp
public interface IAssisterModule
{
    string Id { get; }

    void ConfigureServices(
        IServiceCollection services);
}
```

A module should eventually be able to contribute:

```text
tools
direct intent handlers
background services
configuration
database entities/migrations
API endpoints
event handlers
```

Initial modules:

```text
HomeAssistant
Timers
Memory
```

For now these can be compiled into the main application.

Do not implement arbitrary DLL hot-loading yet.

However, avoid architecture that would make external modules impossible later.

---

# 19. Pipeline orchestration

Create a central service such as:

```text
VoicePipelineCoordinator
```

The flow should be explicit and traceable:

```text
Start Session
    |
Receive Audio
    |
STT
    |
Transcript
    |
Direct Intent Router
    |
    +-- matched --> Execute --> Response
    |
    +-- unmatched
           |
           v
       Tool Selector
           |
           v
       Context Builder
           |
           v
           LLM
           |
      tool calls?
       /       \
     yes       no
      |         |
 ToolBroker   Response
      |
      +-------> LLM
                 |
                 v
          Voice Formatter
                 |
                 v
                TTS
                 |
                 v
             Satellite
```

Each stage must record timing.

Example diagnostic stages:

```text
AudioCapture       842 ms
STT                311 ms
IntentRouting        2 ms
EntityResolution     3 ms
LLM                428 ms
ToolCall             8 ms
LLMFinal           201 ms
TTSFirstAudio       92 ms
PlaybackStart       14 ms
```

These measurements will be important for tuning perceived latency.

---

# 20. Diagnostics

Diagnostics are an important part of the MVP.

Expose an API and simple web page showing recent pipeline runs.

For each run display:

```text
trace ID
satellite
area
transcript
direct intent result
entity resolution
LLM calls
tools selected
tool calls
tool execution duration
final response
STT duration
LLM duration
TTS duration
total duration
errors
```

Do not expose:

```text
HA access tokens
LLM API keys
other secrets
```

Add structured logging.

Console logging should work correctly in Docker.

---

# 21. Text testing endpoint

Do not require a physical satellite to develop the rest of Assister.

Add an endpoint such as:

```text
POST /api/test/message
```

Example:

```json
{
  "message": "turn the kitchen light off",
  "satelliteId": "test",
  "area": "kitchen",
  "conversationId": null
}
```

Return:

```json
{
  "response": "Done.",
  "conversationId": "...",
  "handledBy": "direct-intent",
  "traceId": "..."
}
```

This endpoint should exercise the exact same request routing, intents, tools, conversation logic, and LLM logic as voice requests.

Do not create a completely separate text-assistant path.

---

# 22. Configuration

Use normal ASP.NET configuration.

Provide `appsettings.json` with safe defaults and environment-variable overrides.

Example logical configuration:

```json
{
  "Assister": {
    "DataPath": "/data"
  },

  "HomeAssistant": {
    "Url": "http://homeassistant:8123"
  },

  "SpeechToText": {
    "Provider": "Wyoming",
    "Host": "192.168.1.10",
    "Port": 10300,
    "Language": "en"
  },

  "TextToSpeech": {
    "Provider": "Wyoming",
    "Host": "192.168.1.10",
    "Port": 10200,
    "Voice": null
  },

  "LanguageModel": {
    "Provider": "OpenAICompatible",
    "BaseUrl": "http://llama:8080/v1",
    "Model": "qwen",
    "Temperature": 0.2,
    "MaxTokens": 500
  },

  "Conversation": {
    "RecentTurns": 6,
    "FollowUpTimeoutSeconds": 120
  }
}
```

Secrets should be supplied with environment variables:

```text
HomeAssistant__Token
LanguageModel__ApiKey
```

Do not commit secrets.

---

# 23. Docker and Dockhand

Provide a multi-stage Docker build.

The main container must:

- run as a non-root user where practical
- listen on port 8080
- store persistent state only under `/data`
- expose a health endpoint
- tolerate container replacement/restart
- automatically run safe SQLite migrations at startup

Example logical compose layout:

```text
assister
    /data volume
    port 8080

esphome-bridge
    optional
    communicates only with assister
```

Do not include the Wyoming STT/TTS server in the compose file because those services already exist externally.

Do not include llama.cpp because that is also an external service.

Keep `compose.yaml` minimal and compatible with Dockhand Git synchronization/rebuild workflows.

Provide:

```text
.env.example
```

but no real credentials.

---

# 24. Health/status checks

Expose:

```text
GET /health
GET /api/status
```

`/api/status` should report things such as:

```text
database
Home Assistant connection
STT Wyoming connection/capability
TTS Wyoming connection/capability
LLM connection
satellite count
active conversations
```

Do not report the entire application unhealthy merely because one optional satellite is offline.

Differentiate:

```text
Healthy
Degraded
Unavailable
```

where useful.

---

# 25. Failure behavior

The system must fail predictably.

Examples:

If STT is unavailable:

```text
do not invoke the LLM
record clear diagnostic reason
```

If HA disconnects:

```text
mark HA unavailable
attempt reconnect with backoff
retain last cached states but mark them stale
do not perform state-changing actions against stale connectivity
```

If LLM is unavailable:

```text
direct intents must continue working
```

This is important.

Commands like:

```text
turn the kitchen light off
```

should still function even when llama.cpp is offline.

If TTS fails:

```text
retain the text response
log the TTS failure
```

If a tool fails:

```text
return a bounded error result to the LLM
do not dump stack traces into context
```

---

# 26. Security boundaries

Treat LLM output as untrusted.

The LLM may request a tool call, but the Tool Broker determines whether that call is legal.

Do not allow the LLM to construct arbitrary:

```text
URLs
shell commands
SQL
Home Assistant API routes
service names
```

without validation.

For Home Assistant controls, use defined action mappings.

Examples:

```text
light.turn_on
light.turn_off
switch.turn_on
switch.turn_off
cover.open_cover
cover.close_cover
lock.lock
lock.unlock
climate.set_temperature
```

Expand mappings intentionally.

Keep state-changing and read-only tools distinguishable.

---

# 27. Tests

Add unit tests from the beginning.

At minimum test:

```text
Wyoming header parsing
partial TCP frames
Wyoming payload parsing
multiple messages in one buffer
STT request sequence
TTS response sequence
direct intent matching
entity resolution
area-aware entity resolution
ambiguous entities
conversation continuation
tool schema validation
tool iteration limits
context trimming
HA state-cache updates
timer persistence
```

Create fake implementations for:

```text
ISpeechToTextProvider
ITextToSpeechProvider
ILanguageModel
IHomeAssistantClient
ISatelliteConnection
```

so the pipeline can be tested without external systems.

---

# 28. Initial implementation milestones

Implement in this order.

## Milestone 1 — application skeleton

Create:

```text
solution
ASP.NET service
SQLite
configuration
Dockerfile
compose.yaml
health endpoint
logging
test projects
```

The application must build and run in Docker before proceeding.

## Milestone 2 — Wyoming

Implement:

```text
Wyoming framing
describe/info
Wyoming STT
Wyoming TTS
```

Add integration tests using fake TCP Wyoming servers.

Do not depend on a real STT/TTS system for automated tests.

## Milestone 3 — Home Assistant

Implement:

```text
WebSocket authentication
get_states
state_changed subscription
get_services
entity registry
device registry
area registry
local entity index
reconnect logic
```

## Milestone 4 — direct intents

Implement:

```text
on/off
brightness
basic state query
entity resolver
area-aware resolution
```

Validate using the text testing endpoint.

## Milestone 5 — LLM and tools

Implement:

```text
OpenAI-compatible client
chat completions
streaming parser
function calling
ToolBroker
ToolRegistry
ha.search
ha.get_state
ha.control
ha.get_history
```

## Milestone 6 — conversations

Implement:

```text
Conversation
ConversationTurn
conversation persistence
recent-turn context
conversation continuation
tool-result exclusion
context limits
```

## Milestone 7 — satellite integration

Implement:

```text
ISatelliteConnection
SatelliteManager
gRPC contracts
esphome-bridge
audio input
audio playback
voice-session lifecycle
```

Connect the first real EchoMuse/ESPHome satellite.

## Milestone 8 — timers and memory

Implement:

```text
TimerService
TimerIntentHandler
timer persistence
Memory module
SQLite FTS memory search
```

## Milestone 9 — streaming latency improvements

Add:

```text
LLM streaming
sentence-boundary detector
streaming Wyoming TTS
early satellite playback
cancellation/barge-in support where possible
```

---

# MVP acceptance test

The first useful end-to-end version is complete when all of these work:

```text
1.
User says:
"Turn the kitchen light off."

Expected:
Satellite audio
 -> Wyoming STT
 -> direct intent
 -> local entity resolution
 -> HA control
 -> short response
 -> Wyoming TTS
 -> satellite

LLM must not be called.


2.
User says:
"Set the kitchen lights to 50 percent."

Expected:
No LLM call.


3.
User says:
"What is the temperature in the office?"

Expected:
Direct state query if entity resolution is sufficiently confident.


4.
User says:
"Has the office been warmer than the living room this afternoon?"

Expected:
LLM path
 -> ha.search
 -> ha.get_history
 -> reasoning
 -> concise spoken answer


5.
User says:
"What's the weather this weekend?"

Assistant answers.

User follows with:
"What about Sunday night?"

Expected:
Same conversation is reused.


6.
llama.cpp is intentionally stopped.

User says:
"Turn the kitchen light off."

Expected:
Command still succeeds.


7.
A history tool returns a large amount of data.

Expected:
The raw result is bounded/reduced and is not permanently inserted into subsequent conversation context.
```

---

# Things specifically not to implement yet

Avoid scope creep.

Do not initially build:

```text
vector database
multi-agent framework
MCP server/client
arbitrary code execution
browser automation
email integration
calendar integration
bank integrations
complex user authentication
cloud sync
mobile application
wake-word model training
LLM fine-tuning
dynamic plugin marketplace
distributed job queue
Kubernetes
Redis
PostgreSQL
```

The architecture should allow additional modules later, but none of these are required for the initial Assister implementation.

---

# Design priorities

When choosing between implementations, prioritize in this order:

```text
1. Reliability
2. Low voice latency
3. Small LLM context
4. Deterministic behavior when possible
5. Observability
6. Simple deployment
7. Extensibility
```

The important architectural rule is:

> Do not use the LLM for work that Assister can perform deterministically.

And the second most important rule is:

> Do not give the LLM information or tools merely because Assister has access to them.

Retrieve context and expose tools only when they are relevant to the current request.

Build Assister around those two principles.
