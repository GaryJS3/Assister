# Voice audit — October 11, 2026

Read-only audit of the latest ten live diagnostic runs, spanning October 10 at 23:47 through October 11 at 11:35, America/New_York. No commands were submitted and no Home Assistant configuration was changed. Prior findings below come from the October 9 audit in this chat, not a fresh replay of that batch.

## Latest sample

| Run prefix | Transcript / event | Result | Interpretation |
| --- | --- | --- | --- |
| 68be1fd0 | Set kitchen lights to 50% please. | Succeeded, 9.58 s | Main Lights and Sink Light resolved; HA confirmed both. |
| 75767f72 | Empty transcript | No speech, 6.25 s | Kitchen provided 4.24 s of audio; STT returned empty. Does not establish whether speech was present. |
| 8b3d5836 | MBedroom activation, hey_mycroft_v0.1 | Cancelled, 5.69 s | Controller audio_end reason no_speech_timeout; no recorded microphone-start event. |
| 71b1d333 | MBedroom activation, hey_mycroft_v0.1 | Cancelled, 5.73 s | Same controller reason and absent microphone-start event. |
| 40cad193 | MBedroom activation, hey_mycroft_v0.1 | Cancelled, 5.70 s | Same controller reason and absent microphone-start event. |
| 399e798a | MBedroom activation, hey_mycroft_v0.1 | Cancelled, 5.60 s | Same controller reason and absent microphone-start event. |
| ee7beeb0 | Turn off the kitchen lights. | Succeeded, 7.17 s | Both kitchen light entities confirmed off. |
| 4440159e | What? Canceled. | Conversational reply, 15.81 s | Exact stop recognizer missed the utterance; model replied instead. |
| 45714d44 | Just a voice activated, dude. | Conversational reply, 11.72 s | Model asked How can I help you?; next activation had no wake word, consistent with follow-up capture. Intended speech cannot be verified from text alone. |
| 89364433 | Turn kitchen lights to 20%. | Succeeded, 10.87 s | Turn ... to percentage correctly selected brightness, not power. |

Three of three device commands completed. Two other transcripts produced conversational replies. Five activations produced no usable transcript. These are different denominators: neither five successes out of ten nor three out of three alone is a useful overall voice reliability score. This sample provides no new test of device-name recovery, pronouns, multi-target phrasing or fan commands.

HA execution took 409–503 ms on the three controls. Speech finalization after audio ended took 1.05–2.01 s. Overall run duration includes capture, feedback, synthesis and playback. The four bedroom cancellations are confirmed controller no-speech endings; false wake activation, microphone/VAD behavior and actual user silence remain possible explanations. Do not change wake sensitivity or speech deadlines based on this sample alone.

## Patterns across audits

- Clear named/room commands execute quickly and reliably in the observed samples. Kitchen area membership correctly includes main and sink lights.
- Natural references and compound requests remain the larger coverage gaps: that light, the light we were just talking about, and den lights, the living room lights as well failed in the previous sample.
- All four recovery attempts in the October 9 sample timed out at 15 s. This latest sample does not show that issue resolved. The earlier post-deployment synthetic requests succeeded, so benchmark the same inputs under current model load before choosing a timeout change.
- A prior search found Den Light but authorization could not resolve the conversational target. The final reply incorrectly claimed the device was missing, and the run was labelled succeeded. Outcome reporting must reflect fulfilled user intent and authoritative tool results.
- Natural cancellation needs better coverage. Current StopCommands accepts a short exact list and does not recognize canceled/cancelled or the observed multi-clause utterance.

## Processing guidelines

1. Use one structured request plan for action, target references, area and amount across deterministic parsing, model interpretation, clarification and execution. Avoid independently interpreting the same request in different layers and obtaining incompatible target meanings.
2. Treat a deterministic parse as a fast path, not proof the extracted noun phrase is complete. When resolution fails, let semantic interpretation repair the phrase or identify multiple targets before declaring not-found. Preserve working deterministic routes.
3. Resolve recent structured references before name correction. Handle that light, that fan, the one we discussed, conjunctions, commas and as well as language forms. Ask only when the actual action or target remains uncertain. Earlier conversation supplies a referent, not authorization for an old action.
4. Keep execution checks at the side-effect boundary: current requested action, resolved targets, valid supported parameters, current availability and authoritative execution receipts. Broad language support should not require every command to fit a growing whitelist. Do not automatically retry a control whose completion is uncertain.
5. For uncertain name corrections, retain the original action/amount and request confirmation. Use a small relevance-ranked catalog including names, aliases and areas; infrastructure settings should not crowd out ordinary devices. An explicitly named infrastructure target should still be discoverable under the applicable control policy.
6. Route clear stop/cancel speech promptly, including spelling variants and conversational framing. Distinguish cancel speech/output from cancel a named timer or another domain task; a global substring check would break valid requests.
7. Preserve provider termination reasons and partial capture metadata in the run itself. Classify no_speech_timeout as no speech rather than generic cancelled. Separate no audio, empty STT, user stop, controller cancellation, dependency timeout, ambiguity and execution failure.
8. Keep a corpus of observed transcripts, intended interpretation, expected targets and result category. Test positive coverage and nearby unrelated requests together. Report activation-to-transcript, command fulfillment, clarification resolution and latency separately; compare matching categories between audit batches.

## Home Assistant and Assister room guidelines

Live inspection confirms Kitchen, MBedroom and Office satellites are online but their Assister AreaId values are null. EchoMuseConnection.Area comes from the Assister satellite registration, so configuring only the HA device area will not populate voice context. Set these registrations to kitchen, master_bedroom and office respectively, matching the HA room catalog.

| Current entity name | Suggested name | Natural aliases |
| --- | --- | --- |
| Main Lights (Kitchen) | Kitchen Main Lights | Main Lights; Kitchen Ceiling Lights |
| Sink Light (Kitchen) | Kitchen Sink Light | Sink Light; Sink Lamp |
| Den Light | Keep | Den Lights; Den Lamp |
| Hallway Lights | Keep | Hallway Light; Hall Lights |
| Living Room Lights | Keep | Living Room Light; Lounge Lights if actually used |
| Office Yellow Light / Office White Light | Clarify physical function if known | Warm/Cool or Ceiling/Fan variants only if they accurately describe hardware |

- Use one canonical room name and consistent area IDs. Add room synonyms where supported instead of creating duplicate rooms. MBedroom is a satellite display name; Master Bedroom is the HA room name.
- Prefer human names that identify room and function. Preserve entity IDs unless a separate migration is necessary; existing IDs may be referenced by automations. light.kitchen_living_room_lights is confusing to humans but has the correct Living Room area and resolved successfully; renaming its ID is not a priority.
- Give every ordinary controlled light/fan/switch its correct area, including through inherited device areas. DriveWay Light currently has no area; normalize its display name and assign its actual location. Do not assign utility LEDs to a room's general lighting set merely to eliminate missing areas.
- Define whether kitchen lights means all kitchen lighting or just the main fixture. Current behavior includes both main and sink lights and succeeded. If groups are introduced, avoid selecting both a group and its member entities for one request.
- Use authentic synonyms, not ambiguous aliases for every transcription error. Dim light could describe brightness or a different fixture; treat dim/den as an uncertain correction requiring confirmation rather than a permanent global alias.
- Audit voice exposure/eligibility for utility LEDs, network toggles, reboot switches and HVAC relays. Assister currently searches its own HA cache; HA Assist exposure settings alone must not be assumed to filter Assister. Implement a consistent discovery/selection policy if desired, without silently blocking explicitly requested supported devices.

Priority: repair outcome/termination reporting and bedroom capture observability; expand reference, compound-target and cancellation interpretation; improve recovery relevance and measure current model latency; apply satellite areas and friendly-name/alias cleanup. Naming improvements complement these changes but do not fix the language-processing gaps by themselves.
