# Confirmed actions and room-scoped groups

October 24, 2026. Follow-up to the audited Kitchen runs.

## Failure and fixes

The semantic tool path resolved `kitchen lights` through broad discovery. That
matched `light.kitchen_living_room_lights` even though it is assigned to Living
Room. The model's area-filtered search and control correctly selected only
`light.kitchen_main_lights` and `light.kitchen_sink_light`, but the completion
tracker expected the extra Living Room target. Its next model round therefore
required another tool call instead of allowing a final confirmation. The audited
run then failed with `InvalidOperationException` and returned model unavailability
despite confirmed device changes.

Named room scopes now use the same area-aware resolver as direct commands. A
room token in an old entity ID cannot expand that room's authorized controls.
Exact named devices and existing ambiguity checks remain in effect.

Separately, post-control model failures now use the current request's confirmed
action receipt to generate the response. Confirmed full completion returns a
plain confirmation naming the controlled devices. Partial completion names only
the confirmed devices and warns that remaining targets are unconfirmed. No
control is retried. Caller cancellation still cancels the request. This fallback
does not infer successful actions from model prose or from a previous request.

The direct resolver previously recognized `kitchen lights` but treated
`all kitchen lights` as a literal device name. Shared quantifier normalization
now handles `all kitchen lights`, `all the kitchen lights`, and
`all of the kitchen lights`, including percentage commands. The current room
remains required for an otherwise unscoped group; unknown rooms and singular
ambiguous targets do not widen into other rooms.

## Verification

- Regression tests reproduced both failures before the fixes.
- Release solution: 416 unit/component tests and 58 integration tests passed.
- All 18 browser-script tests passed.
- Fault injection covers invalid model responses, HTTP failures and provider
  timeout after confirmed full or partial control, with buffered and streaming
  responses. Caller cancellation, repeat-control prevention and room isolation
  remain covered.
- The read-only intent inspection API returned not-found for the audited group
  phrasing before deployment; post-deployment inspection can verify actual
  running-service target resolution without executing controls.
- `--control-receipts` uses real Home Assistant registry metadata but intercepts
  every control in memory and injects the final model failure. Before deployment,
  its seven checks passed: three group resolutions and four full/partial,
  buffered/streaming receipt cases. It performs no real device controls or model
  inference and can be repeated from the installed image:

  ```text
  dotnet /probe/Assister.ServiceProbe.dll --control-receipts
  ```

These checks establish resolution and action-reporting behavior, not physical
satellite microphone/audio acceptance. The previously deployed continuation fix
is retained.
