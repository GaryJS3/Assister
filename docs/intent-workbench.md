# Intent workbench

Open `/intents.html` from the diagnostics navigation. The workbench and normal voice/text pipeline share the same persisted C# intent engine. Saved changes apply to the next request without restarting.

Each intent now selects an **Integration** and a registered **Action**. Stable qualified IDs such as `home-assistant.set-brightness` identify execution ownership. Existing handler definitions are upgraded automatically. See [integrations.md](integrations.md) for registration, dispatch and the initial scope.

## Author and inspect

Built-ins cover power, light brightness, device state, temperature, local time and date. Their native device recognizers remain in C#; the editor shows examples and supports additional aliases, response changes and disabling. Create custom intents for fixed replies or additional phrases bound to an existing handler. Custom intents can be deleted. Concurrent edits require reloading instead of silently overwriting another save.

Each phrase is literal text with optional typed slots: `{target}`, `{target:light}`, `{target:switch}`, `{target:entity}`, `{brightness}` / `{brightness:percent}`, and `{area}` / `{area:area}`. Put each alternative on its own line. A slot cannot be repeated or directly adjacent to another slot. Fixed target, brightness and area values can replace slots. Brightness is 0–100. Arbitrary regular expressions and executable handler code are not accepted.

Inspect shows normalization, matching definitions, extracted slots, resolved Home Assistant entities, ambiguity and expected response. Inspection and saved tests never send device commands. Draft previews cannot execute. Execute requires an explicit confirmation and the fingerprint of a current saved plan; the server recomputes the plan and rejects changes. Failed commands retain their failure response instead of rendering a custom success message.

Response placeholders are `{response}`, `{target}`, `{brightness}`, `{area}`, `{time}` and `{date}`. Time/date use `Assister:TimeZone` (default `America/New_York`). A Reply handler needs its own text; device handlers normally retain `{response}`.

## Routing and regression tests

Different matching definitions or conflicting slot extractions are ambiguous and execute nothing. Unmatched requests continue to the LLM in the normal pipeline. Invalid brightness is rejected. Timers retain their dedicated module; the inspector identifies that route but does not execute it.

Saved tests assert match status, optional rule, target and brightness. Device resolution appears separately: a recognition test can pass while Home Assistant is unavailable. Run all tests after changing phrases. The seeded suite includes `Set living room light to 100.`. Recent LLM requests provide candidates for manual inspection and authoring; they never create rules automatically.

Definitions and examples are stored in SQLite and covered by the existing startup database migration/backup flow.

## Home Assistant reference

The design draws on Home Assistant's separation of [sentence recognition](https://developers.home-assistant.io/docs/voice/intent-recognition/), [sentence templates and slots](https://developers.home-assistant.io/docs/voice/intent-recognition/template-sentence-syntax/) and [intent handlers](https://developers.home-assistant.io/docs/intent_builtin/). This is an original C# implementation with a deliberately smaller grammar, not a HassIL-compatible parser. No upstream code or sentence corpus was copied, and no Python or YAML is needed to author Assister intents.

If importing upstream sentences later, retain attribution and follow the [OHF-Voice intents CC BY 4.0 license](https://github.com/OHF-Voice/intents/blob/main/LICENSE.md). The [HassIL parser's Apache 2.0 license](https://github.com/OHF-Voice/hassil/blob/main/LICENSE.md) is separate; open source does not mean every related repository has the same license.
