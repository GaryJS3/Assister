# S1: rich-client streaming speech

Implemented locally October 23, 2026, on top of S0 in the working tree based on
`b1eb4189dd431331224f6ad5b96c03ba35a319a6`. Deployment verification is tracked separately from these initial local measurements.

## Behavior

The browser opts into `streamSpeech` when Speak is checked and its Web Audio
context can run. Otherwise it uses the existing buffered WAV path. Old API/SDK
submissions omit the new optional field and retain their existing behavior.
Set `Voice__RichStreamingEnabled=false` to force buffered fallback on the server.
Windows/Android application playback implementations are not in this repository;
the C# SDK exposes segment downloads for clients implementing the new contract.

`InteractionRunner` feeds only coordinator-approved text into `SentenceBuffer`
and an eight-item synthesis channel. Tool-capable rounds retain their existing
validation policy. Each sentence uses the existing TTS provider. PCM segments
are at most 500 ms / 64 KiB before WAV headers, with an early flush at sentence
boundaries; even a long sentence can publish audio before its synthesis finishes.
Up to 1024 segments and 8 MiB PCM are allowed, with unchanged format required.
The stream has a three-minute deadline. A synthesis error cancels queued speech
without blocking text generation; changed authoritative output stops the stream.
No previously played audio can be retracted after an upstream error.

Each ordered `tts.segment` references an authenticated WAV URL with its index,
PCM format and size. Audio bytes are stored outside event JSON. Completion saves
one combined WAV with no duplicated PCM, then emits `tts.completed` with the
segment count. Retained WAVs support manual replay and history. Segments expire
with audio retention and are deleted with interaction retention; together with
the complete WAV they can consume approximately twice the previous audio bytes.

The browser serially fetches/decodes segments and schedules buffers on one Web
Audio timeline, with at most about 2.5 seconds of decoded audio scheduled ahead.
The bounded pending list stores only segment metadata. Initial tones precede
speech; Goodbye waits for stream playback completion. The slow-generation cue
stops once streaming response text begins. The complete WAV does not autoplay
again after streaming. Stop, cancellation, sign-out, new submission, changing
conversation or disabling Speak stops queued/scheduled audio. Playback failure
offers the retained recording for manual replay; it does not restart speech from
the beginning automatically. Replayed history stays silent. Event cursors prevent
duplicates on reconnect; a missing/expired segment fails playback explicitly.

## Controlled measurements

The actual legacy `RichSpeech` and new `RichSpeechOutput` were compared using the
same simulated model/TTS work and actual SQLite audio storage. Twenty samples
per scenario/mode, after one warmup, alternating mode order. Times below start
at generation start; they exclude STT, dispatch polling, WebSocket delivery,
HTTP segment download, browser decoding, initial tones and hardware output.
First audio availability uses 1 ms polling, including OS scheduler granularity.

| Scenario | Buffered median first audio | Streaming median first audio | Reduction |
| --- | ---: | ---: | ---: |
| Simulated direct control | 74.92 ms | 75.12 ms | no material change |
| Short knowledge reply | 330.76 ms | 170.98 ms | 48.3% |
| Tool-capable round held for validation | 460.52 ms | 412.02 ms | 10.5% |
| Long reply | 657.00 ms | 173.89 ms | 73.5% |

Long-reply p95 first audio: 660.11 → 176.28 ms. Median total completion:
657.01 → 510.16 ms (22.4% lower). These are controlled orchestration gains,
not measured production or physical-playback gains. Raw samples and complete
configuration: [voice-s1-rich-benchmark.json](voice-s1-rich-benchmark.json).

```powershell
dotnet build tools/Assister.VoiceBench -c Release
dotnet run --project tools/Assister.VoiceBench -c Release --no-build -- --rich 20 > docs/voice-s1-rich-benchmark.json
dotnet test Assister.sln -c Release
node --test tests/browser/*.test.cjs
```

## Validation and remaining acceptance

Release tests: 391 unit/component plus 58 integration tests passed; all 17 browser
script tests passed. Coverage includes early segment delivery during a gated
generation, pre-completion playback reports, owner isolation, retained WAV size,
cancel, changed authoritative answer, TTS failure, a full queue released on failure,
invalid format/frame/size, retention, tool-round speech policy, browser ordering,
tone gating, bounded scheduling, forced buffered fallback, preservation of long
text answers when speech limits are exceeded, missing segments, history silence and cancellation
during download. These checks do not establish speech intelligibility or device
stop latency.

The real in-app browser passed the synthetic PCM harness at a 48 kHz AudioContext
sample rate. Its first buffer was scheduled before simulated synthesis finished;
completion waited for all buffers to end. The reported `started` is the client's
scheduling event, not an independently observed audible start. The harness uses
a low-amplitude synthetic tone, not a useful spoken answer. Screenshot:
[voice-s1-browser.png](voice-s1-browser.png).

Reproduce the browser-only harness from the repository root:
`dotnet run --project tools/Assister.VoiceBench -- --browser-harness`, then open
`http://localhost:8559/` and select **Run browser verification**. It binds only to
localhost and does not contact production services.

Deployment, real provider timing, audible long-reply acceptance on the intended
client/device, end-of-speech to first useful playback, and device cancellation
remain unverified. Native clients continue buffered playback until they implement
the segment player; S2 satellite transports are unchanged.
