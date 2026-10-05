# Device discovery and conversational controls

## October 4 audit

| Observed request | Evidence | Cause |
| --- | --- | --- |
| Turn off the kitchen lights | Direct resolver reported no target | Kitchen Main Lights has no HA area; the resolver treated the room as a strict area filter. |
| What lights are in the kitchen? | Search found `light.kitchen_main_lights`, then `ha_control(turn_off)` completed with HA state confirmation | The model replayed an earlier failed command during an information question. The broker lacked a current-request authorization check. |
| What lights are in the office? | Plural search returned no lights; fallback returned trackers, a fan and helpers | Device nouns were substring queries rather than domain filters; partial word scores admitted unrelated results. |
| Broad light searches | Tool calls failed with `JsonException` | Compact projections read optional attributes without type checks. HA off lights can have `brightness: null`; a numeric read inside deferred serialization fails the whole result. |
| Both of them? | Two controls proposed; first rejected before execution | IDs from an earlier turn were not valid current observations. The model also inferred off from an on-or-off question. |
| Later question about whether lights changed | Model denied an HA-confirmed action | History contained prose but no authoritative receipt. |

## Shared search and execution

HA tool definitions remain available on every LLM request; data is retrieved on demand.
General questions can use no tools, and an earlier weather topic does not force a forecast
for an unrelated question.

`HomeAssistantEntitySearch` normalizes device nouns and singular/plural forms, infers
domains, requires meaningful query terms to match and considers IDs, names, aliases,
device names and areas. Empty area searches can find unassigned devices whose names
contain that room; they never widen to another assigned room. Exact names win over
incidental matches. Deterministic resolution shares these semantics and retains ambiguity
checks. Compact results check optional attribute types before serialization. Failed
searches stay failures rather than becoming claims that devices do not exist.

`ControlRequest` derives action, brightness and target from the current utterance.
Questions, negations and conditional instructions do not authorize immediate changes.
The deterministic parser is a fast path, not a required grammar for LLM controls. If
the model proposes a control for unmatched wording, the broker makes a separate,
bounded semantic interpretation of the current user message. That interpretation
receives no conversation history, search results or proposed control arguments. It
extracts action, target, brightness and explicit area; an information question,
negation or future/conditional request produces no immediate control. Invalid or
unavailable interpretation cannot execute a command. This fallback adds one model
call only when a control is proposed and deterministic interpretation failed.
The broker checks authorization and the HA
tool verifies action, brightness, target scope, current search observations and fresh
entity availability. Searching an arbitrary entity does not authorize changing it;
generic room-less commands cannot select a device arbitrarily.

`ha_control` accepts a batch `entity_ids` or a singular `entity_id`, never both. All targets
are checked before sending the service call, and readback confirms the entire batch.
Separate calls can control distinct devices, but attempted devices cannot be retried.
Partial completion reports confirmed targets and preserves uncertainty about remaining
operations rather than claiming complete success or complete failure.

## Structured follow-ups

`Conversations.DeviceContextJson` stores bounded references, a pending explicit command,
the target/action clarification state, the last action attempt and the last confirmed
receipt. The additive migration defaults old conversations to empty context. Credentials
and raw tool dumps are excluded. Reference/pending state expires after five minutes.

A pending `turn off` command can accept `White Light` or `both` as its target reply.
An on-or-off question does not create an off command: `both` selects two references and
asks for a direction; `off` supplies it. Bare `off` or singular `it` cannot select several
devices silently. Unrelated questions consume pending commands. Pronoun commands use
server-recorded IDs and fresh cache validation. Their authorization snapshot cannot be
redirected by a later model search in the same loop.

Receipts include action, IDs, brightness, time and outcome. Questions such as `Did you
change them?` use these records and describe the last operation, without asserting that
state stayed unchanged afterward. An unconfirmed recent attempt is not replaced by an
older success.

Regression tests cover the audited transcripts, null attributes, helpers, missing areas,
action replay, wrong actions/targets, batches, partial completion, duplicate attempts,
search errors, clarification, expiry, satellite isolation, persistence and topic changes.
Physical voice and HA readback acceptance are separate from fake-client tests; discovery
validation does not issue device controls.

## Natural wording fix — October 5, 2026 (local, not deployed)

The 18:39 voice run `56a48abe-051c-47a1-bb1a-af4c2cfd893e` correctly transcribed
"Hey Jarvis, can you turn the kitchen lights on 100%?", found Kitchen Main Lights,
and proposed brightness 100. The deterministic authorization parser returned no
command, so the broker rejected it before HA execution. Wake-word greetings,
stacked polite prefixes and on-at-percentage phrases now take the direct path.
Unmatched paraphrases can use the semantic fallback while retaining search,
target/action/brightness validation, ambiguity checks and completion receipts.

`dotnet test Assister.sln` passes 219 tests. The read-only C# service probe mode
`--control-language` checks semantic interpretation against the configured model;
it never sends Home Assistant controls. All eight interpretation cases passed against
the live Qwen3.6-35B-A3B-UD-Q4_K_XL endpoint, including natural commands,
information questions, negation and conditional requests. These changes have not been deployed or
verified through physical voice.

## Deployment and live verification — October 4, 2026

Device fixes were deployed as `fa78a75`, then included in the cache-refresh deployment `d51ca90`.
The latest full suite passes 196 tests. Live text runs found Office Yellow Light and Office White
Light (`1c029abb-28a9-4015-b627-41d3862bb110`) and Kitchen Main Lights
(`02cf5995-584f-48c9-9c17-ca0551e922f1`). Kitchen naming returned the correct name
(`1c586a29-daa1-463d-ac50-e7dfb2decfd9`). All three traces contain zero control calls and zero
failed tools. "Both of them?" explicitly asked on or off
(`c8125187-1a19-4626-85df-0060ef8a98fd`). Both office lights stayed on; the kitchen light stayed off.

Cache freshness is a separate concern from search semantics. The deployed client subscribes to
entity/device/area registry changes, reloads complete snapshots every 15 minutes and on reconnect,
and supports `POST /api/homeassistant/cache/rebuild`. Snapshot loading preserves incoming state
events. The live manual rebuild returned a current 1,967-entity cache. Actual registry edits and
elapsed scheduled reload still need production acceptance; both have automated coverage.
