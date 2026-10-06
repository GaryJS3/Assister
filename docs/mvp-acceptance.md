# MVP acceptance, timers/memory and streaming

Updated: October 5, 2026. This increment is included in deployed revision `c48745f`. The latest complete suite passes 207 tests (189 unit/component, 18 integration). Native timer remaining-time queries pass through the microphone; two timers survived redeployment with unchanged deadlines. Spoken brightness, clarification/follow-ups and cancellation/recovery are user-confirmed. Timer expiry acceptance remains pending. The original 157-test module checks below remain historical evidence; they do not independently establish physical satellite acceptance. See [progress.md](../progress.md) and [service-validation.md](service-validation.md) for the current deployment and remaining checks.

## Changes

- Temperature search uses sensor metadata, finding abbreviated names such as `OfficeTemp-tempc`. Results identify temperature sensors and prefer available entities within equal matches.
- Empty weather configuration is treated as unset, allowing automatic selection of the single available forecast provider.
- Model context includes explicit UTC/local timestamps with the correct offset. Invalid history ranges return bounded corrective guidance. Read tools require exact IDs returned by search in the current request.
- Historical and forecast questions require their data tool. Successful retrieval ends tool selection and permits streamed final prose. Forecast failure gets an explanation; history validation permits at most three failed history attempts within the existing overall loop limit.
- Tool outputs containing `error` are recorded as failed in diagnostics/audit. Conversation topic notes count toward the existing 12,000-character budget; raw tool results remain excluded from persisted turns and follow-up context.
- Timer expiry queries connected satellites separately, attempts one pending timer per satellite per tick, and permits up to four concurrent deliveries. SQLite writes remain sequential. Offline backlogs, delivery failures and slow playback cannot block another satellite in the same batch.
- Memory mutations require explicit current remember/forget commands. Recall questions and negative phrases cannot authorize mutation. Personal preference questions can select search; common query words are removed before FTS retrieval.
- Voice generation overlaps bounded sentence synthesis. `Voice:StreamingEnabled` defaults to true; `Voice__StreamingEnabled=false` restores buffered generation.
- Wyoming negotiates `supports_synthesize_streaming` on the first advertised TTS program. Native requests use `synthesize-start/chunk/stop` and require `synthesize-stopped`. Blank-line boundaries flush complete sentences. Legacy servers receive ordinary sentence requests. Invalid/changing formats, premature completion, EOF and cancellation fail explicitly.
- Failed model streams discard partial buffered replies; synthesis failures preserve completed text. Cancellation releases the session for a subsequent turn. ESPHome bridge audio objects are removed after playback/cancellation.

## Automated acceptance

`dotnet test Assister.sln`: **157 passing tests** (139 unit/component, 18 integration), up from 125. Release probe publication and `git diff --check` pass.

| Scenario | Evidence | Limit |
| --- | --- | --- |
| Direct power/brightness/temperature with model offline | Actual coordinators and voice pipeline; model is never called | Simulated microphone, speech and output |
| Comparative history | Actual tools/broker; 3,000 upstream samples reduced to bounded summaries | Simulated HA HTTP data |
| Weather follow-up | Actual forecast tool, same conversation, daily/twice-daily retrieval | Simulated HA HTTP data |
| Conversation continuity/isolation | SQLite reopen, cross-satellite rejection, bounded prompts and tool-result exclusion | No physical satellite reboot |
| Timer reliability | SQLite reopen, overdue delivery, busy deferral, offline backlog, failed/slow satellites, explicit dismissal and next-timer recovery | Simulated playback acknowledgments |
| Memory lifecycle | SQLite reopen, actual FTS/broker, relevant retrieval, deletion and mutation rejection | Shared local memory remains the MVP design |
| Streaming lifecycle | Decimal/split sentences, bounded queues, early audio, tool-prose buffering, incomplete-model rejection, failure preservation and cancellation recovery | Early playback transport is simulated |
| Wyoming protocol | Real TCP framing, concurrent text/audio, terminal acknowledgment and legacy fallback | Actual synthesis checked below |

Timers retain **at-least-once delivery**: a crash after playback but before completion persistence can replay an announcement. Named timer cancellation does not promise to interrupt an announcement already playing; satellite stop explicitly dismisses it.

## Live updated-module validation

`--mvp-modules` was published for `linux-x64`, copied to a temporary directory in the running container, and executed with its existing environment. It uses a separate temporary SQLite database and a read-only HA connection/registry without control tools. It does not connect to EchoMuse, claim voice ownership, play satellite announcements, or change HA devices. Credentials stay in the container. Temporary probe artifacts were removed afterward.

The final run exited **0**:

| Check | Observed result |
| --- | --- |
| Office temperature | Actual direct cached measurement; 110 ms |
| Comparative afternoon history | Searched sensors, successful history retrieval and model answer; 18.44 s |
| Weekend weather | Actual forecast data; 4.67 s |
| Sunday-night follow-up | Actual forecast tool, same conversation; 7.64 s |
| Native Wyoming streaming | Audio before text input finished; first audio 335 ms, total 606 ms; WAV encoding validated |
| LLM-to-TTS overlap | Twelve-sentence diagnostic: first text 1.54 s, first synthesized audio 1.88 s, generation complete 4.65 s, synthesis complete 4.87 s |

These are individual observations, not benchmarks or physical time-to-audible-reply measurements. `--mvp-readonly` now checks diagnostic tool evidence and fails if a forecast/history request only returns an unavailable-data explanation. `--mvp-modules --stream-only` narrows validation to synthetic model/TTS checks without satellite playback.

## Remaining physical acceptance

EchoMuse and the installed ESPHome bridge accept complete audio objects; physical playback starts after synthesis finishes. This increment overlaps generation and synthesis while preserving that contract. `IStreamingAudioPlayback` is an explicit opt-in for a future transport that starts playback before its source completes.

Consolidated real-microphone acceptance of all seven original MVP scenarios remains open. Physical timer restart announcements, audible cancellation, LEDs, media coexistence and time to first audible output need device checks. Wake-word interruption during speech remains unsupported on the installed path.

## Protocol references

- [OpenAI function calling and streaming assembly](https://developers.openai.com/api/docs/guides/function-calling)
- [Wyoming streaming TTS events](https://github.com/OHF-Voice/wyoming/blob/master/wyoming/tts.py)
- [Wyoming TTS capabilities](https://github.com/OHF-Voice/wyoming/blob/master/wyoming/info.py)
- [Wyoming Piper streaming handler](https://github.com/OHF-Voice/wyoming-piper/blob/main/wyoming_piper/handler.py)
- [Sentence Stream boundaries](https://github.com/OHF-Voice/sentence-stream)
