"""Protocol adapter tests only: fake ESPHome and gRPC peers, no hardware or secrets."""
import asyncio
from types import SimpleNamespace as Object
import unittest
from unittest.mock import patch, AsyncMock
import main as adapter


class FakeClient:
    instance = None
    conflict = False

    def __init__(self, *args, **kwargs):
        FakeClient.instance = self
        self.api_version = Object(major=1, minor=12)
        self.active = ["nabu"]
        self.subscriptions = 0
        self.log_levels = []
        self.commands = []
        self.events = []

    async def connect(self, on_stop):
        self.on_stop = on_stop

    async def device_info(self):
        return Object(name="voice", model="Fake", voice_assistant_feature_flags=125,
                      esphome_version="test", project_version="test")

    async def list_entities_services(self):
        player_type = type("MediaPlayerInfo", (), {})
        player = player_type()
        player.key = 1
        player.supported_formats = [Object(purpose=1)]
        return [player], []

    def subscribe_states(self, callback):
        self.state = callback

    async def get_voice_assistant_configuration(self, timeout):
        return Object(available_wake_words=[Object(id="nabu", wake_word="Okay Nabu"),
                                           Object(id="jarvis", wake_word="Hey Jarvis")],
                      active_wake_words=self.active, max_active_wake_words=1)

    async def set_voice_assistant_configuration(self, active_wake_words):
        self.active = active_wake_words

    def subscribe_logs(self, callback, log_level=None, dump_config=None):
        self.log = callback
        self.log_levels.append(log_level)
        return lambda: None

    def subscribe_voice_assistant(self, **handlers):
        self.subscriptions += 1
        self.handlers = handlers
        if self.conflict:
            self.log(Object(message=b"Multiple API Clients attempting to connect to Voice Assistant"))
        return lambda: None

    def send_voice_assistant_event(self, *args):
        self.events.append(args[0])

    def media_player_command(self, key, **kwargs):
        self.commands.append(kwargs)
        if FakeCall.mode == "playback-failure":
            raise IOError("fake playback failure")
        self.state(adapter.MediaPlayerEntityState(key=key, state=adapter.MediaPlayerState.ANNOUNCING, volume=0.6))
        # Announcement completion while ordinary music resumes, rather than IDLE.
        self.state(adapter.MediaPlayerEntityState(key=key, state=adapter.MediaPlayerState.PLAYING, volume=0.6))

    async def disconnect(self):
        pass

    async def send_voice_assistant_announcement_await_response(self, url, timeout, text):
        self.commands.append({"announcement": True, "url": url, "text": text})
        return Object(success=FakeCall.mode != "native-failure")


class FakeChannel:
    async def __aenter__(self):
        return self

    async def __aexit__(self, *args):
        pass


class FakeCall:
    frames = []
    mode = "conflict"

    def __init__(self, requests):
        self.responses = asyncio.Queue()
        self.responses.put_nowait(adapter.wire.BridgeFrame(type="registered"))
        self.task = asyncio.create_task(self.consume(requests))

    async def consume(self, requests):
        async for frame in requests:
            FakeCall.frames.append(frame)
            client = FakeClient.instance
            if self.mode == "conflict" and frame.ownership == "Conflict":
                self.responses.put_nowait(None)
            elif self.mode == "recovery" and frame.ownership == "Conflict":
                client.conflict = False
                self.responses.put_nowait(adapter.wire.BridgeFrame(type="retry-ownership"))
            elif self.mode == "recovery" and frame.ownership == "Unknown" and client.subscriptions == 2:
                await client.handlers["handle_start"]("", 0, None, "Hey Jarvis")
            elif self.mode == "recovery" and frame.type == "start":
                self.responses.put_nowait(None)
            elif self.mode == "wake" and frame.ownership == "Unknown":
                await client.handlers["handle_start"]("", 0, None, "Hey Jarvis")
                # An overlapping start must not replace the current transport session.
                result = await client.handlers["handle_start"]("", 0, None, "Hey Jarvis")
                assert result == 0
                await client.handlers["handle_audio"](b"\0\0", b"\0\0")
                await client.handlers["handle_stop"](False)
            elif self.mode == "wake" and frame.type == "stop":
                self.responses.put_nowait(None)
            elif self.mode == "cancel" and frame.ownership == "Unknown":
                await client.handlers["handle_start"]("", 0, None, "Hey Jarvis")
                await client.handlers["handle_stop"](True)
            elif self.mode == "cancel" and frame.type == "cancel":
                self.responses.put_nowait(None)
            elif self.mode in ("native", "native-failure") and frame.ownership == "Unknown":
                await client.handlers["handle_start"]("", 0, None, "Hey Jarvis")
            elif self.mode in ("native", "native-failure") and frame.type == "start":
                self.responses.put_nowait(adapter.wire.BridgeFrame(type="audio-ready", url="http://fake/audio",
                    playback_id="native-test", session_id=frame.session_id, trace_id="trace-test"))
            elif self.mode in ("native", "native-failure") and frame.type == "playback-finished":
                self.responses.put_nowait(None)
            elif self.mode == "configuration" and frame.type == "configuration":
                if list(frame.configuration.active_wake_words) == ["jarvis"]:
                    self.responses.put_nowait(None)
                else:
                    self.responses.put_nowait(adapter.wire.BridgeFrame(type="set-wake-words", text='["jarvis"]'))
            elif self.mode in ("playback", "playback-failure") and frame.type == "metadata":
                self.responses.put_nowait(adapter.wire.BridgeFrame(type="audio-ready", url="http://fake/audio",
                    text="announcement", playback_id="playback-test", trace_id="trace-test"))
            elif self.mode in ("playback", "playback-failure") and frame.type == "playback-finished":
                self.responses.put_nowait(None)
            elif self.mode == "invalid-wake" and frame.type == "configuration":
                self.responses.put_nowait(adapter.wire.BridgeFrame(type="set-wake-words", text='["missing"]'))
            elif self.mode == "invalid-wake" and frame.type == "device-error":
                self.responses.put_nowait(None)

    def __aiter__(self):
        return self

    async def __anext__(self):
        frame = await self.responses.get()
        if frame is None:
            raise StopAsyncIteration
        return frame

    def cancel(self):
        self.task.cancel()


class FakeStub:
    def __init__(self, channel):
        pass

    def Connect(self, requests, metadata):
        return FakeCall(requests)


class AdapterTests(unittest.IsolatedAsyncioTestCase):
    async def test_reconnect_backoff_is_bounded_and_cancellation_stops_retries(self):
        delays = []
        async def sleep(delay):
            delays.append(delay)
            if len(delays) == 6:
                raise asyncio.CancelledError()
        with patch.object(adapter, "connect_once", AsyncMock(side_effect=IOError("fake disconnect"))) as connect, \
                patch.object(adapter.asyncio, "sleep", sleep), patch.object(adapter.sys, "argv", ["main.py"]):
            with self.assertRaises(asyncio.CancelledError):
                await adapter.main()
        self.assertEqual([5, 10, 20, 40, 60, 60], delays)
        self.assertEqual(6, connect.await_count)

    async def run_bridge(self, mode):
        FakeClient.conflict = mode in ("conflict", "recovery")
        FakeCall.mode = mode
        FakeCall.frames = []
        with patch.dict(adapter.os.environ, {"ESPHOME_ENCRYPTION_KEY": "fake-key", "ESPHOME_HOST": "fake",
                "ESPHOME_SATELLITE_ID": "test", "SATELLITE_BRIDGE_TOKEN": "fake-token"}), \
                patch.object(adapter, "APIClient", FakeClient), \
                patch.object(adapter.grpc.aio, "insecure_channel", lambda *a, **k: FakeChannel()), \
                patch.object(adapter.services, "SatelliteTransportStub", FakeStub):
            await asyncio.wait_for(adapter.connect_once(), 3)
        return FakeCall.frames

    def test_feature_mask_125_is_normalized(self):
        cap = adapter.capabilities(125, True)
        self.assertTrue(cap.voice_assistant)
        self.assertFalse(cap.speaker)
        self.assertTrue(cap.api_audio)
        self.assertTrue(cap.timers)
        self.assertTrue(cap.announcement_playback)
        self.assertTrue(cap.start_conversation)
        self.assertTrue(cap.multi_channel_microphone)
        self.assertFalse(adapter.capabilities(0, False).voice_assistant)

    async def test_api_connection_does_not_imply_voice_ownership_and_conflict_is_passive(self):
        frames = await self.run_bridge("conflict")
        self.assertIn("metadata", [f.type for f in frames])
        self.assertEqual(["Unknown", "Conflict"], [f.ownership for f in frames if f.type == "ownership"])
        self.assertEqual(1, FakeClient.instance.subscriptions)
        self.assertEqual(adapter.LogLevel.LOG_LEVEL_NONE, FakeClient.instance.log_levels[-1])

    async def test_real_voice_request_confirms_ownership_and_audio_identity(self):
        frames = await self.run_bridge("wake")
        self.assertIn("OwnedByAssister", [f.ownership for f in frames])
        starts = [f for f in frames if f.type == "start"]
        self.assertEqual(1, len(starts))
        self.assertEqual("Hey Jarvis", starts[0].wake_word)
        audio = next(f for f in frames if f.type == "audio")
        self.assertEqual(starts[0].session_id, audio.session_id)
        self.assertEqual(2, audio.source_channels)
        self.assertEqual(1, audio.channels)

    async def test_active_wake_words_are_read_back_after_update(self):
        frames = await self.run_bridge("configuration")
        configs = [f.configuration for f in frames if f.type == "configuration"]
        self.assertEqual(["nabu"], list(configs[0].active_wake_words))
        self.assertEqual(["jarvis"], list(configs[-1].active_wake_words))
        self.assertEqual(1, configs[-1].max_active_wake_words)

    async def test_unavailable_wake_model_does_not_change_device(self):
        frames = await self.run_bridge("invalid-wake")
        self.assertIn("device-error", [f.type for f in frames])
        self.assertEqual(["nabu"], FakeClient.instance.active)

    async def test_explicit_retry_after_other_client_releases_channel(self):
        frames = await self.run_bridge("recovery")
        self.assertEqual(2, FakeClient.instance.subscriptions)
        self.assertEqual(["Unknown", "Conflict", "Unknown", "OwnedByAssister"], [f.ownership for f in frames if f.type == "ownership"])

    async def test_announcement_completion_when_normal_media_resumes(self):
        frames = await self.run_bridge("playback")
        finished = next(f for f in frames if f.type == "playback-finished")
        self.assertEqual("succeeded", finished.text)
        self.assertEqual("playback-test", finished.playback_id)
        self.assertEqual("trace-test", finished.trace_id)
        self.assertEqual("", finished.session_id)
        self.assertTrue(FakeClient.instance.commands[0]["announcement"])
        self.assertIn("playback-started", [f.type for f in frames])

    async def test_playback_failure_preserves_playback_identity(self):
        frames = await self.run_bridge("playback-failure")
        finished = next(f for f in frames if f.type == "playback-finished")
        self.assertEqual("failed", finished.text)
        self.assertEqual("playback-test", finished.playback_id)

    async def test_aborted_capture_immediately_ends_device_feedback(self):
        frames = await self.run_bridge("cancel")
        self.assertIn("cancel", [f.type for f in frames])
        self.assertIn(adapter.Event.VOICE_ASSISTANT_RUN_END, FakeClient.instance.events)

    async def test_owned_voice_uses_native_announcement_acknowledgment(self):
        frames = await self.run_bridge("native")
        finished = next(f for f in frames if f.type == "playback-finished")
        self.assertEqual("succeeded", finished.text)
        self.assertEqual("native-test", finished.playback_id)
        self.assertTrue(finished.session_id)
        self.assertEqual("http://fake/audio", FakeClient.instance.commands[0]["url"])

    async def test_native_announcement_failure_is_reported(self):
        frames = await self.run_bridge("native-failure")
        self.assertEqual("failed", next(f for f in frames if f.type == "playback-finished").text)


if __name__ == "__main__":
    unittest.main()
