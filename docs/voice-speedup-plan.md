# Voice speedup feature plan

Created: October 23, 2026.

Goal: reduce the time between the user finishing speech and hearing the first useful response, while preserving correct device actions, conversation behavior and cancellation.

Implement one feature at a time in the order below. Each feature gets its own measured before/after result. Implementation, deployment and physical-device verification are separate milestones. No speedup is implemented by this plan.

## Tracking

| ID | Feature | Status | Depends on |
| --- | --- | --- | --- |
| S0 | Establish comparable latency measurements | In progress — offline harness, server milestones and read-only live text baseline; physical timing pending | None |
| S1 | Stream rich-client speech during response generation | Implemented and tested locally; browser scheduling verified; deployment and spoken-device measurement pending | S0 |
| S2 | Stream satellite audio through to physical playback | Deployed 37ab58b for EchoMuse; real TTS/HTTP delivery measured; physical playback acceptance pending; ESPHome offline | S0; reuse S1 concepts where useful |
| S3 | Add adaptive, semantic turn completion | Planned | S0; device capture support |
| S4 | Prepare speculative replies during the end-of-turn pause | Planned | S3; provisional input and cancellable output |
| S5 | Improve model context reuse and inference configuration | Planned | S0 |
| S6 | Evaluate an optional direct-audio model backend | Research backlog | Earlier measurements |

For each feature, record: implementation revision, tests, baseline, measured result, deployed revision, client/device verification and remaining limitations. Mark complete only for the scope actually verified.

## S0 — Establish comparable latency measurements

Implementation and measurement details: [voice-latency-measurements.md](voice-latency-measurements.md).

Record end of actual speech, input endpoint decision, final transcript, routing completion, first model text, first synthesized PCM, first audio delivered and first audio played. Identify client-reported playback times and clock uncertainty explicitly. Existing traces already capture some stages; extend them only where needed.

Use a repeatable set of direct controls, short knowledge questions, tool-backed queries and longer replies. Include interrupted speech and pauses mid-sentence. Separate cold/warm requests, clients, providers and models; report median and p95 with sample counts and configuration. Measure action completion separately from first sound. A tone or filler is not the first useful answer.

Acceptance:

- A baseline identifies which stages dominate each supported path.
- Playback capability and buffering are documented per client/device.
- Measurements distinguish speech duration from processing and endpointing delay.
- Subsequent features can use the same scenarios without performing unauthorized real controls.

## S1 — Stream rich-client speech during response generation

Local implementation, tests and controlled before/after measurements:
[voice-s1-rich-streaming.md](voice-s1-rich-streaming.md). Physical acceptance remains open.

The buffered fallback streams response text but starts rich-client TTS after the complete response. `RichSpeech` collects all PCM into a WAV before publishing audio. Opted-in clients now use `RichSpeechOutput` while generation is in progress.

Feed text that is safe to speak into bounded synthesis queues and deliver playable audio incrementally. Define ordered audio chunks, format metadata, bounded jitter buffering, cancellation and completion in the client protocol. Preserve retained audio/history and a buffered fallback for older clients. Audit tone ordering so cues do not serialize or disable streaming unnecessarily.

Start with `src/Assister/Interactions/InteractionRunner.cs`, `RichSpeech.cs`, the rich-client protocol and the actual client playback implementations. Reuse `Voice/SentenceBuffer.cs` and existing streaming TTS contracts where appropriate.

Acceptance:

- A supported client audibly begins a long reply before generation and synthesis finish.
- Text from tool-capable rounds is held until validated under the existing speech policy.
- Cancellation stops queued and playing audio; failures do not leave playback hanging.
- Audio ordering, backpressure, reconnect/fallback behavior and retained recordings remain correct.
- Record first useful playback latency and total completion time against S0.

## S2 — Stream satellite audio through to physical playback

Implementation, deployment and measured limits: [voice-s2-satellite-streaming.md](voice-s2-satellite-streaming.md).

Before S2, EchoMuse turn playback waited for a complete WAV and then sent an audio URL. The voice pipeline can overlap generation and synthesis, but that does not establish early physical playback. Its tone-enabled satellite branch also bypasses the streaming generation path.

Inspect the installed controller/device capabilities first. Implement incremental delivery for a supported transport, which may require controller changes. Advertise support through the existing streaming-playback capability contract; retain complete-file delivery where necessary. Integrate tones without truncation, overlap races or unnecessary full-response buffering.

Start with `src/Assister/Voice/VoicePipeline.cs`, `Satellites/EchoMuseConnection.cs`, `SatelliteManager.cs` and `VoiceAudioStore.cs`.

Acceptance:

- A real supported satellite begins playback before full synthesis completes.
- Capturing the first PCM byte on the server alone does not count as physical acceptance.
- Stop/cancel, follow-up activation, playback acknowledgements and tone ordering work.
- Unsupported transports keep their buffered fallback and report the limitation.
- Measure separately for EchoMuse and ESPHome rather than assuming shared capabilities.

## S3 — Add adaptive, semantic turn completion

The fallback energy VAD currently waits for 900 ms of silence. EchoMuse owns endpointing, so changing that fallback will not change EchoMuse timing.

Introduce a configurable endpointing policy at the component that actually owns capture. Evaluate a CPU ONNX semantic completion model, such as Smart Turn, through C# ONNX Runtime. Match the required audio feature extraction and normalization. Combine speech detection with completion confidence: end clear statements sooner, allow unfinished phrases more time, and retain a conservative fallback.

Start with `src/Assister/Voice/VoiceActivityDetector.cs`, `VoicePipeline.cs` and provider-owned endpointing contracts.

Acceptance:

- Clear complete turns finish sooner without materially increasing premature cutoffs in the evaluation set.
- Hesitations, short phrases, background noise and model failure have explicit behavior.
- Record CPU cost, endpoint delay and premature-cutoff rate alongside latency.
- Keep provider-owned endpointing and push-to-talk behavior explicit.

## S4 — Prepare speculative replies during the end-of-turn pause

Begin provisional processing before final turn commitment, keeping generated text/audio private until the user is confirmed finished. This requires a useful provisional transcript or direct-audio input; the current final-transcript routing boundary must be addressed first.

Use a per-turn generation identifier, cancellation token and bounded provisional buffers. If speech resumes, discard stale output and continue capturing. Keep conversation writes and all state-changing tools behind final commitment. Revalidate committed input and current-request authorization before executing actions. Initially restrict speculation to speech preparation or clearly read-only work with bounded resource use.

Acceptance:

- Continued speech cancels stale work and no stale audio reaches the client.
- Speculative requests cannot change Home Assistant, timers, reminders, lists or memory.
- Provisional turns do not pollute persisted conversation history.
- Concurrency and wasted inference are bounded and measured.
- Demonstrate improvement beyond S3 alone; otherwise leave speculation disabled.

## S5 — Improve model context reuse and inference configuration

Inspect the actual serving backend for prefix/KV cache support and measure repeated-turn prompt processing. Keep stable prompt prefixes where compatible with on-demand context and tool selection. Evaluate warmup, voice-appropriate response lengths and supported thinking settings against quality and tool correctness.

Do not add all entities, history or tool definitions merely to stabilize a prefix. Shared caches must preserve conversation isolation. GPU kernels and graph capture belong in the serving backend, not the C# orchestration layer.

Acceptance:

- Record backend/model versions, hardware, prompt sizes and cache hit evidence where available.
- Compare first-token and first-playback latency on cold and warm requests.
- Preserve correct tool selection, authorization and useful answer quality.
- Retain only configuration changes with measured benefit.

## S6 — Evaluate an optional direct-audio model backend

Research whether an audio-input Thinker/Talker backend can remove the separate STT finalization boundary and improve natural conversation. Keep Assister orchestration and authorization in C# behind provider abstractions.

This is an experiment, not a prerequisite for S1–S5. Compare hardware/VRAM requirements, deployment complexity, transcription visibility, tool correctness, cancellation, concurrency and model licenses. Do not assume an RX 7900 XTX result transfers to our hardware.

Acceptance:

- A bounded prototype is measured against the improved existing pipeline.
- Adoption requires a demonstrated benefit and acceptable operational costs.
- Existing STT/LLM/TTS providers remain usable.

## Reference implementation

Reviewed ming-voice revision: `d64181e185c5d199fc14fed73d09ded99583bb9a`.

- [Speculative turn handling and output commitment](https://github.com/JMS1717/ming-voice/blob/d64181e185c5d199fc14fed73d09ded99583bb9a/server.py): starts speculative generation after a short pause, buffers output and commits at turn completion.
- [Semantic completion model wrapper](https://github.com/JMS1717/ming-voice/blob/d64181e185c5d199fc14fed73d09ded99583bb9a/turn_detect.py): Smart Turn CPU ONNX input preparation and inference.
- [Streaming engine and persistent model state](https://github.com/JMS1717/ming-voice/blob/d64181e185c5d199fc14fed73d09ded99583bb9a/engine.py): audio-input reasoning, incremental synthesis and cache reuse.
- [Architecture, timing defaults and reported performance](https://github.com/JMS1717/ming-voice/blob/d64181e185c5d199fc14fed73d09ded99583bb9a/README.md): author-reported approximately 230 ms median end-of-speech to first audio on an RX 7900 XTX; not an Assister target guarantee or independent benchmark.
- [MIT code license](https://github.com/JMS1717/ming-voice/blob/d64181e185c5d199fc14fed73d09ded99583bb9a/LICENSE): preserve attribution for copied code; evaluate model and dependency licenses separately.

Borrow architecture and measured techniques first. Implement application features in C#; no Python or new YAML is required by this plan.
