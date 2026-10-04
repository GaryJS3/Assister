"""ESPHome transport only. No STT, LLM, device intents or conversation storage."""
import asyncio
import json
import os
import re
import sys
import uuid

import grpc
from aioesphomeapi import APIClient, VoiceAssistantEventType as Event, MediaPlayerEntityState, MediaPlayerState, MediaPlayerCommand, LogLevel
import satellite_pb2 as wire
import satellite_pb2_grpc as services


def capabilities(flags, has_player):
    """Native flag values never leave this adapter."""
    return wire.Capabilities(
        microphone=bool(flags & 1), voice_assistant=bool(flags & 1),
        speaker=bool(flags & 2), api_audio=bool(flags & 4), timers=bool(flags & 8),
        announcement_playback=has_player, start_conversation=bool(flags & 32),
        multi_channel_microphone=bool(flags & 64), media_player=has_player,
        media_playback=has_player, volume_control=has_player)


async def connect_once(inspect_only=False):
    key_file = os.environ.get("ESPHOME_ENCRYPTION_KEY_FILE")
    key = open(key_file, encoding="utf-8").read().strip() if key_file else os.environ.get("ESPHOME_ENCRYPTION_KEY", "")
    if not key:
        raise RuntimeError("ESPHome encryption key is required")
    client = APIClient(os.environ["ESPHOME_HOST"], int(os.environ.get("ESPHOME_PORT", "6053")),
                       None, noise_psk=key, client_info="Assister ESPHome bridge")
    disconnected = asyncio.Event()
    async def on_disconnect(expected):
        disconnected.set()
    await client.connect(on_stop=on_disconnect)
    unsubscribe = None
    try:
        info = await client.device_info()
        if inspect_only:
            print(json.dumps({"name": info.name, "mac": info.mac_address,
                              "voice_assistant_feature_flags": info.voice_assistant_feature_flags}), flush=True)
            return
        satellite_id = os.environ["ESPHOME_SATELLITE_ID"]
        outgoing = asyncio.Queue(maxsize=128)
        current_session = ""
        current_trace = ""
        conversation_id = ""
        capturing = False
        busy = False
        task_set = set()
        entities, _ = await client.list_entities_services()
        players = [entity for entity in entities if type(entity).__name__ == "MediaPlayerInfo"
                   and any(fmt.purpose == 1 for fmt in entity.supported_formats)]
        player_key = players[0].key if len(players) == 1 else None
        playback = None
        playback_frame = None
        playing = False
        log_unsubscribe = None
        log_generation = 0

        def media_state(state):
            nonlocal playing
            if not isinstance(state, MediaPlayerEntityState) or state.key != player_key:
                return
            # State telemetry is bounded; PCM never enters it.
            try:
                outgoing.put_nowait(wire.BridgeFrame(type="media-state", satellite_id=satellite_id,
                    text=state.state.name if state.state else "Unknown", volume=state.volume, muted=state.muted))
            except asyncio.QueueFull:
                pass
            if playback is None:
                return
            if state.state == MediaPlayerState.ANNOUNCING:
                if not playing and playback_frame is not None:
                    try:
                        outgoing.put_nowait(wire.BridgeFrame(type="playback-started", satellite_id=satellite_id,
                            session_id=playback_frame.session_id, playback_id=playback_frame.playback_id, trace_id=playback_frame.trace_id))
                    except asyncio.QueueFull:
                        pass
                playing = True
            elif playing and state.state in (MediaPlayerState.IDLE, MediaPlayerState.PLAYING, MediaPlayerState.PAUSED) and not playback.done():
                playback.set_result(True)

        client.subscribe_states(media_state)

        async def send(kind, **fields):
            await outgoing.put(wire.BridgeFrame(type=kind, satellite_id=satellite_id,
                                               trace_id=fields.pop("trace_id", current_trace),
                                               session_id=fields.pop("session_id", current_session), **fields))

        async def voice_configuration():
            try:
                config = await client.get_voice_assistant_configuration(5)
                await send("configuration", configuration=wire.VoiceConfiguration(
                    available_wake_words=[wire.WakeWord(id=model.id, name=model.wake_word) for model in config.available_wake_words],
                    active_wake_words=config.active_wake_words, max_active_wake_words=config.max_active_wake_words))
            except TimeoutError:
                await send("device-error", text="Voice configuration unavailable")

        def stop_logs():
            nonlocal log_unsubscribe, log_generation
            log_generation += 1
            if log_unsubscribe:
                log_unsubscribe()
                log_unsubscribe = None
            client.subscribe_logs(lambda _: None, log_level=LogLevel.LOG_LEVEL_NONE)()

        async def diagnostics_logs(enabled):
            nonlocal log_unsubscribe, log_generation
            stop_logs()
            if not enabled:
                return
            generation = log_generation
            secrets = [value for name, value in os.environ.items()
                       if value and re.search("KEY|TOKEN|PASSWORD|SECRET|AUTHORIZATION", name, re.I)]
            secrets.append(key)
            def on_log(message):
                text = message.message.decode("utf-8", errors="replace")
                for secret in secrets:
                    text = text.replace(secret, "[redacted]")
                text = re.sub(r"(?i)bearer\s+\S+", "Bearer [redacted]", text)
                text = re.sub(r"(?i)(?:api[_-]?key|password|access[_-]?token|authorization)\s*[:=]\s*\S+", "[redacted credential]", text)
                try:
                    outgoing.put_nowait(wire.BridgeFrame(type="device-log", satellite_id=satellite_id, text=text[:512]))
                except asyncio.QueueFull:
                    pass
            log_unsubscribe = client.subscribe_logs(on_log, log_level=LogLevel.LOG_LEVEL_WARN, dump_config=False)
            await asyncio.sleep(60)
            if generation == log_generation:
                stop_logs()

        async def verify_subscription():
            nonlocal unsubscribe, log_unsubscribe
            if unsubscribe is not None:
                return
            def on_log(message):
                # Do not forward raw firmware logs; they may contain credentials or URLs.
                if b"Multiple API Clients attempting to connect to Voice Assistant" in message.message:
                    try:
                        outgoing.put_nowait(wire.BridgeFrame(type="ownership", satellite_id=satellite_id, ownership="Conflict"))
                    except asyncio.QueueFull:
                        pass
            log_unsubscribe = client.subscribe_logs(on_log, log_level=LogLevel.LOG_LEVEL_ERROR, dump_config=False)
            await send("ownership", ownership="Unknown")
            unsubscribe = client.subscribe_voice_assistant(handle_start=start, handle_stop=stop, handle_audio=audio)
            generation = log_generation
            await asyncio.sleep(5)
            if generation == log_generation:
                stop_logs()

        def event(kind, data=None):
            client.send_voice_assistant_event(kind, data)

        async def start(device_conversation, flags, settings, wake_word):
            nonlocal current_session, current_trace, capturing, busy
            if busy:
                return 0
            busy = True
            current_session = str(uuid.uuid4())
            current_trace = ""
            capturing = True
            # Assister's satellite-aware store owns continuity. Only reuse IDs issued by Assister.
            await send("ownership", ownership="OwnedByAssister")
            await send("start", conversation_id=conversation_id, wake_word=wake_word or "")
            event(Event.VOICE_ASSISTANT_RUN_START)
            event(Event.VOICE_ASSISTANT_STT_START)
            event(Event.VOICE_ASSISTANT_STT_VAD_START)
            return 0  # Native API audio, not a UDP listener.

        async def stop(abort):
            nonlocal capturing
            if not capturing and not (abort and busy):
                return
            capturing = False
            await send("cancel" if abort else "stop")
            if abort:
                # Hardware feedback should end even if coordinator cancellation is still unwinding.
                event(Event.VOICE_ASSISTANT_RUN_END)

        async def audio(data, data2):
            if capturing:
                # Select channel one for the existing STT path, retaining source channel metadata.
                await send("audio", pcm=data, sample_rate=16000, sample_width=2, channels=1,
                           source_channels=2 if data2 is not None else 1)

        async def requests():
            yield wire.BridgeFrame(type="register", satellite_id=satellite_id,
                                   name=os.environ.get("ESPHOME_NAME", info.name), area=os.environ.get("ESPHOME_AREA", ""))
            while True:
                yield await outgoing.get()

        async def play_response(frame):
            nonlocal playback, playing, playback_frame
            playback = asyncio.get_running_loop().create_future()
            playback_frame = frame
            playing = False
            print(json.dumps({"status": "playback-starting", "session": current_session}), flush=True)
            try:
                if player_key is None:
                    raise RuntimeError("Announcement playback unsupported")
                client.media_player_command(player_key, media_url=frame.url, announcement=True)
                if not await asyncio.wait_for(playback, timeout=40):
                    raise RuntimeError("Playback was stopped")
                await send("playback-finished", text="succeeded", playback_id=frame.playback_id, session_id=frame.session_id, trace_id=frame.trace_id)
                print(json.dumps({"status": "playback-completed", "session": current_session}), flush=True)
            except Exception as error:
                await send("playback-finished", text="failed", playback_id=frame.playback_id, session_id=frame.session_id, trace_id=frame.trace_id)
                print(json.dumps({"status": "playback-failed", "failure_type": type(error).__name__}), flush=True)
            finally:
                playback = None
                playback_frame = None

        async with grpc.aio.insecure_channel(os.environ.get("ASSISTER_GRPC", "assister:8082"),
                options=[("grpc.max_receive_message_length", 131072)]) as channel:
            stub = services.SatelliteTransportStub(channel)
            call = stub.Connect(requests(), metadata=[("authorization", "Bearer " + os.environ["SATELLITE_BRIDGE_TOKEN"])])
            async def watch_disconnect():
                await disconnected.wait()
                call.cancel()
            watcher = asyncio.create_task(watch_disconnect())
            async def heartbeat():
                while True:
                    await asyncio.sleep(30)
                    await send("heartbeat")
            heartbeats = asyncio.create_task(heartbeat())
            try:
                async for frame in call:
                    if frame.session_id and frame.session_id != current_session:
                        continue
                    if frame.trace_id:
                        current_trace = frame.trace_id
                    if frame.type == "registered":
                        version = client.api_version
                        await send("metadata", device=wire.DeviceMetadata(device_name=info.name, model=info.model,
                            firmware_version=getattr(info, "project_version", ""), esphome_version=info.esphome_version,
                            api_version=f"{version.major}.{version.minor}" if version else ""),
                            capabilities=capabilities(info.voice_assistant_feature_flags, player_key is not None))
                        await voice_configuration()
                        if info.voice_assistant_feature_flags & 1 and info.voice_assistant_feature_flags & 4:
                            task = asyncio.create_task(verify_subscription())
                            task_set.add(task)
                            task.add_done_callback(task_set.discard)
                        else:
                            await send("ownership", ownership="Unsupported")
                        print(json.dumps({"status": "api-connected", "satellite": satellite_id}), flush=True)
                    elif frame.type == "set-wake-words":
                        desired = json.loads(frame.text)
                        config = await client.get_voice_assistant_configuration(5)
                        available = {model.id for model in config.available_wake_words}
                        if len(desired) <= config.max_active_wake_words and len(set(desired)) == len(desired) and all(word in available for word in desired):
                            await client.set_voice_assistant_configuration(active_wake_words=desired)
                            await voice_configuration()
                        else:
                            await send("device-error", text="Invalid wake-word configuration")
                    elif frame.type == "set-volume" and player_key is not None:
                        volume = float(frame.text)
                        if 0 <= volume <= 1:
                            client.media_player_command(player_key, volume=volume)
                    elif frame.type == "stop-playback" and player_key is not None:
                        # Stop only the announcement path, preserving normal media.
                        client.media_player_command(player_key, command=MediaPlayerCommand.STOP, announcement=True)
                        if playback is not None and not playback.done():
                            playback.set_result(False)
                    elif frame.type == "retry-ownership":
                        if unsubscribe:
                            unsubscribe()
                            unsubscribe = None
                        task = asyncio.create_task(verify_subscription())
                        task_set.add(task)
                        task.add_done_callback(task_set.discard)
                    elif frame.type == "device-logs":
                        task = asyncio.create_task(diagnostics_logs(frame.text == "start"))
                        task_set.add(task)
                        task.add_done_callback(task_set.discard)
                    elif frame.type == "end-of-speech":
                        capturing = False
                        event(Event.VOICE_ASSISTANT_STT_VAD_END)
                    elif frame.type == "transcribed":
                        event(Event.VOICE_ASSISTANT_STT_END, {"text": frame.text})
                    elif frame.type == "processing":
                        event(Event.VOICE_ASSISTANT_INTENT_START)
                    elif frame.type == "response":
                        event(Event.VOICE_ASSISTANT_INTENT_END, {"conversation_id": frame.conversation_id})
                        event(Event.VOICE_ASSISTANT_TTS_START, {"text": frame.text})
                    elif frame.type == "audio-ready":
                        task = asyncio.create_task(play_response(frame))
                        task_set.add(task)
                        task.add_done_callback(task_set.discard)
                    elif frame.type == "finished":
                        event(Event.VOICE_ASSISTANT_RUN_END)
                    elif frame.type == "session-result":
                        busy = False
                        conversation_id = frame.conversation_id
                        if frame.text not in ("succeeded", "ambiguous", "not-found"):
                            event(Event.VOICE_ASSISTANT_ERROR, {"code": frame.text, "message": "Assister could not complete this turn."})
                            event(Event.VOICE_ASSISTANT_RUN_END)
            finally:
                watcher.cancel()
                heartbeats.cancel()
                call.cancel()
                await asyncio.gather(watcher, heartbeats, return_exceptions=True)
                for task in task_set:
                    task.cancel()
                await asyncio.gather(*task_set, return_exceptions=True)
                if log_unsubscribe:
                    stop_logs()
    finally:
        if not disconnected.is_set():
            try:
                client.send_voice_assistant_event(Event.VOICE_ASSISTANT_RUN_END)
            except Exception:
                pass
        if unsubscribe:
            unsubscribe()
        await client.disconnect()


async def main():
    if "--inspect" in sys.argv:
        try:
            await connect_once(inspect_only=True)
        except Exception as error:
            print(json.dumps({"status": "failed", "failure_type": type(error).__name__}), flush=True)
            sys.exit(1)
        return
    delay = 5
    while True:
        started = asyncio.get_running_loop().time()
        try:
            await connect_once()
        except asyncio.CancelledError:
            raise
        except Exception as error:
            # Encryption keys and raw device errors must never enter logs.
            print(json.dumps({"status": "reconnecting", "failure_type": type(error).__name__}), flush=True)
        if asyncio.get_running_loop().time() - started > 120:
            delay = 5
        await asyncio.sleep(delay)
        delay = min(delay * 2, 60)


if __name__ == "__main__":
    asyncio.run(main())
