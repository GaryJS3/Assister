# S2 satellite playback streaming

Implemented in C# for EchoMuse voice turns. Enable with
`ECHOMUSE_STREAMING_PLAYBACK_ENABLED=true` in the Compose stack environment
(`EchoMuse:StreamingPlaybackEnabled` in application configuration). The default
is false; enable only for an audited controller with incremental WAV decoding
and device-stream playback. `/api/satellites` exposes `streamingVoicePlayback`.
ESPHome remains buffered and reports false.

## Installed transport audit

The installed EchoMuse controller mounts runtime revision
`7be7df32b1481c93dfeb98c5dd43b54ee7d8973c`, independently of its image label.
Its `em_external_voice.py` voice response path passes the URL decoder directly
to the streaming device playback callback. `em_audio_stream.py` reads HTTP
incrementally; 48 kHz mono S16 WAV bypasses FFmpeg. `em_wav.py` explicitly accepts
unknown RIFF/data sizes. `em_controller.py` streams device periods and awaits
real device playback completion statistics. Three EchoMuse devices were online:
Kitchen/MBedroom firmware 2.17.0 and Office 2.18.0-ea.1. The ESPHome device was
offline, so its hardware acceptance cannot be tested.

The controller's standalone announcement path still buffers its URL before
playback. Announcements and short feedback cues retain their existing buffered
path. No controller or firmware changes are required for voice streaming.

## Behavior and limits

- Final response text and follow-up metadata are known before sending the URL.
  This stage overlaps synthesis and playback, not model generation and speech.
- Native streaming TTS receives bounded sentences from the final spoken text;
  providers without native streaming retain sentence-call fallback. Non-streaming
  TTS providers can still supply incremental PCM through their regular API.
- A progressive 48 kHz mono WAV is resampled continuously across chunk boundaries.
  Unknown WAV lengths are intentional. Each HTTP write contains at most 8192 PCM
  bytes, with HTTP backpressure instead of an accumulating output queue.
- Bearer URLs are unguessable, single-consumer, non-cacheable, non-rangeable and
  expire after two minutes. Input/output and concurrent-object limits match the
  buffered store. URLs are removed on completion, failure or cancellation.
- Consumer disconnect, synthesis failure and stop cancel the transfer. A failure
  after audio has started cancels the controller turn rather than replaying it.
  Buffered fallback is selected before delivery by the configuration switch.
- Awake/confirmed/done cues finish before the response URL is sent. Goodbye stays
  at the end of the same PCM stream; questions suppress it and retain continuation.
  Session matching and completed-turn cleanup protect immediate follow-up turns.
- Both successful HTTP transfer and the matching controller playback completion
  acknowledgement are required. An early playback failure also terminates an
  unclaimed or incomplete transfer promptly.
- Producer execution context is preserved across the independent HTTP request,
  so TTS/audio diagnostics remain attached to the voice run. Playback diagnostics
  record first PCM HTTP write and transfer completion separately from the device
  acknowledgement. First device playback time remains explicitly unknown.

## Real TTS / HTTP comparison

[Raw measurements](voice-s2-live-tts.json) use the installed Wyoming TTS endpoint
and voice `en_US-hfc_female-medium`, a real loopback HTTP server and incremental
HTTP client. Fixed short and four-sentence texts were tested ten times per mode,
with mode order alternating. Buffered mode synthesizes the whole response;
streaming mode supplies sentences through the provider's streaming API and
serves progressive PCM through the same store/encoder used by satellite turns.

Times start before synthesis and end at the first PCM received by the HTTP
client. They exclude microphone capture, endpointing, STT, model routing, tones,
controller decoding/pacing, device priming and acoustic output. Samples include
cold-start/provider variation and varying speech lengths; ten samples are a
preliminary comparison, not a production tail-latency estimate.

| Scenario | Buffered median first HTTP PCM | Streaming median | Difference |
| --- | ---: | ---: | ---: |
| One sentence | 144.65 ms | 143.65 ms | 1.00 ms / 0.7% sooner |
| Four sentences | 724.34 ms | 123.96 ms | 600.38 ms / 82.9% sooner |

Nearest-rank p95: short 434.88 -> 330.15 ms; long 1200.30 -> 167.03 ms.
Median complete HTTP delivery: short 144.72 -> 151.23 ms; long
732.10 -> 659.46 ms. Output length varies with synthesis, so throughput and
small short-response differences should not be treated as controlled gains.
Every streaming sample (20/20) received PCM before synthesis completed;
no buffered sample did.

## Validation and acceptance

Unit and integration tests cover chunk-boundary resampling against buffered
output at 8/16/22.05/44.1/48/96 kHz, mono/stereo, invalid/empty PCM, progressive
writes with a gated producer, single-consumer semantics, disconnect cancellation,
producer diagnostic context, matching playback acknowledgements, early playback
failure, continuation metadata and bearer cleanup. The fake controller suite
runs streaming and buffered modes with tones both on and off, multiplexed devices,
immediate continuation, public stop/cancel and subsequent announcement playback.

Physical acceptance is still pending: source audit establishes the installed
voice path can stream, and HTTP testing proves early delivery, but neither proves
the speaker actually started before full synthesis completed. In particular,
EchoMuse `play_started` is sent before the controller even fetches the URL and
must not be used as device-onset evidence. Controller first-period-send logs also
measure socket delivery, not acoustic onset. A real voice activation with
correlated synthesis/first-device-period/completion evidence is required to close
that acceptance item; acoustic onset needs additional device/audio observation.
No physical EchoMuse latency reduction or ESPHome hardware gain is claimed.

Repeat the real-provider comparison with:
`dotnet /probe/Assister.ServiceProbe.dll --satellite-stream` in the configured
application container, or run the ServiceProbe locally with the TTS host, port
and voice environment settings. It does not take over the controller voice socket
or play audio on household devices.

## Deployed verification

Application commit `37ab58b4c13dce1cfda3254ee243467014021e16` was built from
an exact Git archive, pushed, and deployed through Dockhand Git stack 10 on
Automation environment 8. Running container:
`6c76fbdbb2ad0cd48a130ad444da359c2ab6aa807d202cb2ae493c867bc74203`.
Running image manifest:
`sha256:7e9a46609b7bab68573f265fc790573c397253760346aa5ff30deb81eb70fa8c`.
The actual image revision label matches the committed source. Dockhand reports
`lastCommit=37ab58b`, `syncStatus=synced`, no sync error. Its deploy-stream API
again returned `success:false, error:"No result"`; running identity and health,
not that response, establish the deployment result.

The single added stack variable is `ECHOMUSE_STREAMING_PLAYBACK_ENABLED=true`.
All 31 pre-existing stack variables were preserved, including masked secrets.
The running container's complete original environment fingerprint matches the
pre-deploy fingerprint after excluding only the new application streaming flag.
The `assister_assister-data` volume is unchanged. The ESPHome bridge retains its
original container ID and image. The application is Healthy, restart count zero;
all three EchoMuse satellites reconnected as owned by Assister and report
`streamingVoicePlayback=true`. ESPHome remains offline with streaming false.
Before/after evidence: [before](voice-s2-deploy-before.json),
[after](voice-s2-deploy-after.json).

The full .NET suite passed **502 tests** (442 unit, 60 integration); all 18
browser regression tests also passed. All eight deployed service probes passed,
including actual TTS synthesis, TTS-to-STT transcription, model completion,
authenticated Home Assistant REST/WebSocket and controller reachability. See
[service checks](voice-s2-postdeploy-services.json). The legacy service probe's
controller detail says its audio transport is not tested; those eight checks
must not be interpreted as a physical satellite playback test. Existing decoder
pytest suites could not run inside the controller because pytest is not installed;
no packages or controller files were changed to work around that limitation.
Application logs contained startup framework warnings, with no application errors.

The same fixed-text comparison was repeated **inside the deployed application
container**, ten samples per mode and scenario, alternating mode order. See
[deployed raw measurements](voice-s2-postdeploy-tts.json). This verifies the
packaged C# implementation against production TTS and loopback HTTP delivery:

| Scenario | Buffered median first HTTP PCM | Streaming median | Difference |
| --- | ---: | ---: | ---: |
| One sentence | 182.42 ms | 147.42 ms | 35.00 ms / 19.2% sooner |
| Four sentences | 675.42 ms | 136.40 ms | 539.02 ms / 79.8% sooner |

Nearest-rank p95: short 645.66 -> 298.13 ms; long 1025.19 -> 271.15 ms.
Median complete delivery: short 182.69 -> 181.20 ms; long 676.21 -> 712.62 ms
(the long-response stream finished 36.42 ms / 5.4% later despite earlier first
PCM). All 20 streaming samples received PCM before synthesis completed.
Single-sentence benefit varied between the workstation and deployed runs;
small samples and synthesis variance limit conclusions for short replies.
The repeatable large gain is earlier delivery of multi-sentence replies.
These percentages apply to synthesis/HTTP delivery, not whole voice-turn latency
or physical speaker onset. Physical EchoMuse acceptance remains pending, and no
ESPHome hardware performance result is available.
