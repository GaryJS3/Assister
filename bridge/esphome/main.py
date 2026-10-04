"""ESPHome transport only. No STT, LLM, device intents or conversation storage."""
import asyncio
import json
import os
import sys
import uuid

import grpc
from aioesphomeapi import APIClient, VoiceAssistantEventType as Event, MediaPlayerEntityState, MediaPlayerState
import satellite_pb2 as wire
import satellite_pb2_grpc as services


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
        if not info.voice_assistant_feature_flags:
            raise RuntimeError("Device does not advertise voice assistant support")
        if inspect_only:
            print(json.dumps({"name": info.name, "mac": info.mac_address,
                              "voice_assistant_feature_flags": info.voice_assistant_feature_flags}), flush=True)
            return
        satellite_id = os.environ["ESPHOME_SATELLITE_ID"]
        outgoing = asyncio.Queue(maxsize=128)
        current_session = ""
        conversation_id = ""
        capturing = False
        task_set = set()
        playback = None
        playing = False

        def media_state(state):
            nonlocal playing
            if not isinstance(state, MediaPlayerEntityState) or playback is None:
                return
            if state.state == MediaPlayerState.ANNOUNCING:
                playing = True
            elif playing and state.state == MediaPlayerState.IDLE and not playback.done():
                playback.set_result(True)

        client.subscribe_states(media_state)

        async def send(kind, **fields):
            await outgoing.put(wire.BridgeFrame(type=kind, satellite_id=satellite_id,
                                               session_id=current_session, **fields))

        def event(kind, data=None):
            client.send_voice_assistant_event(kind, data)

        async def start(device_conversation, flags, settings, wake_word):
            nonlocal current_session, capturing
            current_session = str(uuid.uuid4())
            capturing = True
            # Assister's satellite-aware store owns continuity. Only reuse IDs issued by Assister.
            await send("start", conversation_id=conversation_id)
            event(Event.VOICE_ASSISTANT_RUN_START)
            event(Event.VOICE_ASSISTANT_STT_START)
            event(Event.VOICE_ASSISTANT_STT_VAD_START)
            return 0  # Native API audio, not a UDP listener.

        async def stop(abort):
            nonlocal capturing
            if not capturing:
                return
            capturing = False
            await send("cancel" if abort else "stop")

        async def audio(data, data2):
            if capturing:
                await send("audio", pcm=data, sample_rate=16000, sample_width=2, channels=1)

        async def requests():
            yield wire.BridgeFrame(type="register", satellite_id=satellite_id,
                                   name=os.environ.get("ESPHOME_NAME", info.name), area=os.environ.get("ESPHOME_AREA", ""))
            while True:
                yield await outgoing.get()

        async def announce(url):
            try:
                result = await client.send_voice_assistant_announcement_await_response(url, timeout=30)
                await send("playback-finished", text="succeeded" if result.success else "failed")
            except Exception:
                await send("playback-finished", text="failed")

        async def play_response(url):
            nonlocal playback, playing
            playback = asyncio.get_running_loop().create_future()
            playing = False
            print(json.dumps({"status": "playback-starting", "session": current_session}), flush=True)
            try:
                event(Event.VOICE_ASSISTANT_TTS_END, {"url": url})
                await asyncio.wait_for(playback, timeout=40)
                await send("playback-finished", text="succeeded")
                print(json.dumps({"status": "playback-completed", "session": current_session}), flush=True)
            except Exception as error:
                await send("playback-finished", text="failed")
                print(json.dumps({"status": "playback-failed", "failure_type": type(error).__name__}), flush=True)
            finally:
                playback = None

        async with grpc.aio.insecure_channel(os.environ.get("ASSISTER_GRPC", "assister:8082"),
                options=[("grpc.max_receive_message_length", 131072)]) as channel:
            stub = services.SatelliteTransportStub(channel)
            call = stub.Connect(requests(), metadata=[("authorization", "Bearer " + os.environ["SATELLITE_BRIDGE_TOKEN"])])
            async def watch_disconnect():
                await disconnected.wait()
                call.cancel()
            watcher = asyncio.create_task(watch_disconnect())
            try:
                async for frame in call:
                    if frame.session_id and frame.session_id != current_session:
                        continue
                    if frame.type == "registered":
                        unsubscribe = client.subscribe_voice_assistant(handle_start=start, handle_stop=stop, handle_audio=audio)
                        print(json.dumps({"status": "connected", "satellite": satellite_id}), flush=True)
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
                        if frame.text == "announcement":
                            task = asyncio.create_task(announce(frame.url))
                            task_set.add(task)
                            task.add_done_callback(task_set.discard)
                        else:
                            task = asyncio.create_task(play_response(frame.url))
                            task_set.add(task)
                            task.add_done_callback(task_set.discard)
                    elif frame.type == "finished":
                        event(Event.VOICE_ASSISTANT_RUN_END)
                    elif frame.type == "session-result":
                        conversation_id = frame.conversation_id
                        if frame.text not in ("succeeded", "ambiguous", "not-found"):
                            event(Event.VOICE_ASSISTANT_ERROR, {"code": frame.text, "message": "Assister could not complete this turn."})
                            event(Event.VOICE_ASSISTANT_RUN_END)
            finally:
                watcher.cancel()
                call.cancel()
                await asyncio.gather(watcher, return_exceptions=True)
                for task in task_set:
                    task.cancel()
                await asyncio.gather(*task_set, return_exceptions=True)
    finally:
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
    while True:
        try:
            await connect_once()
        except asyncio.CancelledError:
            raise
        except Exception as error:
            # Encryption keys and raw device errors must never enter logs.
            print(json.dumps({"status": "reconnecting", "failure_type": type(error).__name__}), flush=True)
        await asyncio.sleep(5)


if __name__ == "__main__":
    asyncio.run(main())
