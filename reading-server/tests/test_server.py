import threading
import time
import unittest
from fastapi.testclient import TestClient
from shuo_reading.engine import DEFAULT_SPEECH_INSTRUCTION, SPEECH, SPEECH_LARGE, VOICE, VOICE_ALTERNATIVE
from shuo_reading.server import create_app

START = dict(type="start", protocol=1, text="会議は明日です。", speed=1.0)


class Reader:
    def __init__(self, block=False, fail=False):
        self.block = block
        self.fail = fail
        self.started = threading.Event()
        self.closed = threading.Event()
        self.chunks = 0
        self.speech_requests = []

    def load(self):
        self.capabilities = {name: dict(ready=True, state="ready") for name in ("translation", "speech")}

    def require(self, *capabilities):
        pass

    def translation_events(self, text, target, stopped):
        self.started.set()
        try:
            if self.block:
                stopped.wait(3)
                return
            yield "text", "会议是明天。" if target == "zh" else "The meeting is tomorrow."
        finally:
            self.closed.set()

    def original_events(self, text, speed, stopped, speech_model, speech_instruct=None, speech_voice=VOICE):
        self.speech_requests.append((speech_model, speech_instruct, speech_voice))
        yield "text", text
        yield "audio", b"\x01\x00" * 120

    def events(self, text, speed, stopped, speech_model, speech_instruct=None, speech_voice=VOICE):
        self.speech_requests.append((speech_model, speech_instruct, speech_voice))
        try:
            self.started.set()
            if self.block:
                stopped.wait(3)
                return
            if self.fail:
                raise RuntimeError("private source text")
            yield "text", "会议是明天。"
            for _ in range(3):
                self.chunks += 1
                yield "audio", b"\x01\x00" * 120
        finally:
            self.closed.set()


class ProtocolTests(unittest.TestCase):
    def test_original_speech_preserves_source_and_requires_ack(self):
        with TestClient(create_app(Reader())) as client:
            with client.websocket_connect("/v1/speech") as ws:
                ws.send_json(START)
                self.assertEqual(ws.receive_json()["voice"], "Serena")
                self.assertEqual(ws.receive_json(), dict(type="text", text=START["text"]))
                self.assertEqual(ws.receive_bytes(), b"\x01\x00" * 120)
                self.assertTrue(client.get("/health").json()["busy"])
                ws.send_json(dict(type="ack"))
                self.assertEqual(ws.receive_json(), dict(type="done"))
            self.wait_idle(client)

    def test_selected_speech_model_voice_and_custom_instruction_reach_reader(self):
        for selected in (SPEECH, SPEECH_LARGE):
            for selected_voice in (VOICE, VOICE_ALTERNATIVE):
                with self.subTest(selected=selected, selected_voice=selected_voice):
                    reader = Reader()
                    with TestClient(create_app(reader)) as client:
                        health = client.get("/health").json()
                        self.assertEqual(health["protocol"], 1)
                        self.assertTrue(health["speech_instruct"])
                        self.assertIn(selected, health["speech_models"])
                        self.assertIn(selected_voice, health["voices"])
                        with client.websocket_connect("/v1/speech") as ws:
                            ws.send_json({**START, "protocol": 2, "speech_model": selected,
                                          "speech_instruct": "请清晰地朗读。", "speech_voice": selected_voice})
                            ready = ws.receive_json()
                            self.assertEqual(ready["speech_model"], selected)
                            self.assertEqual(ready["speech_voice"], selected_voice)
                            self.assertEqual(ready["voice"], selected_voice)
                            self.assertTrue(ready["speech_instruct"])
                            self.assertEqual(ready["protocol"], 2)
                            ws.receive_json()
                            ws.receive_bytes()
                            ws.send_json(dict(type="ack"))
                            self.assertEqual(ws.receive_json(), dict(type="done"))
                    self.assertEqual(reader.speech_requests,
                                     [(selected, "请清晰地朗读。", selected_voice)])

    def test_protocol_two_defaults_missing_instruction_for_older_clients(self):
        reader = Reader()
        with TestClient(create_app(reader)) as client:
            with client.websocket_connect("/v1/speech") as ws:
                ws.send_json({**START, "protocol": 2, "speech_model": SPEECH})
                self.assertTrue(ws.receive_json()["speech_instruct"])
                ws.receive_json()
                ws.receive_bytes()
                ws.send_json(dict(type="ack"))
                self.assertEqual(ws.receive_json(), dict(type="done"))
        self.assertEqual(reader.speech_requests, [(SPEECH, DEFAULT_SPEECH_INSTRUCTION, VOICE)])

    def test_translation_only_in_both_languages(self):
        for target, expected in (("zh", "会议是明天。"), ("en", "The meeting is tomorrow.")):
            reader = Reader()
            with TestClient(create_app(reader)) as client:
                with client.websocket_connect("/v1/translation") as ws:
                    ws.send_json({**START, "target": target})
                    self.assertEqual(ws.receive_json(), dict(type="ready", protocol=1))
                    self.assertEqual(ws.receive_json(), dict(type="text", text=expected))
                    self.assertEqual(ws.receive_json(), dict(type="done"))
                self.wait_idle(client)
                self.assertEqual(reader.chunks, 0)

    def test_translation_cancellation_and_shared_busy_slot(self):
        reader = Reader(block=True)
        with TestClient(create_app(reader)) as client:
            with client.websocket_connect("/v1/translation") as ws:
                ws.send_json(START)
                ws.receive_json()
                self.assertTrue(reader.started.wait(1))
                with client.websocket_connect("/v1/reading") as other:
                    self.assertEqual(other.receive_json()["type"], "error")
                ws.send_json(dict(type="cancel"))
                self.assertTrue(reader.closed.wait(1))
                self.wait_idle(client)

    def test_invalid_translation_language(self):
        reader = Reader()
        with TestClient(create_app(reader)) as client:
            with client.websocket_connect("/v1/translation") as ws:
                ws.send_json({**START, "target": "unsupported"})
                self.assertEqual(ws.receive_json()["type"], "error")
            self.assertFalse(reader.started.is_set())

    def wait_idle(self, client):
        deadline = time.monotonic() + 2
        while client.get("/health").json()["busy"] and time.monotonic() < deadline:
            time.sleep(.01)
        self.assertFalse(client.get("/health").json()["busy"])

    def test_complete_requires_ack_and_preserves_pcm(self):
        reader = Reader()
        with TestClient(create_app(reader)) as client:
            self.assertEqual(client.get("/health").json()["voice"], "Serena")
            with client.websocket_connect("/v1/reading") as ws:
                ws.send_json(START)
                self.assertEqual(ws.receive_json()["sample_rate"], 24000)
                self.assertEqual(ws.receive_json(), dict(type="text", text="会议是明天。"))
                for index in range(3):
                    self.assertEqual(ws.receive_bytes(), b"\x01\x00" * 120)
                    time.sleep(.02)
                    self.assertEqual(reader.chunks, index + 1)
                    ws.send_json(dict(type="ack"))
                self.assertEqual(ws.receive_json(), dict(type="done"))
            self.wait_idle(client)
        self.assertEqual(reader.speech_requests, [(SPEECH, None, VOICE)])
        self.assertTrue(reader.closed.is_set())

    def test_busy_and_disconnect_release_slot(self):
        reader = Reader()
        with TestClient(create_app(reader)) as client:
            with client.websocket_connect("/v1/reading") as first:
                first.send_json(START)
                first.receive_json()
                first.receive_json()
                first.receive_bytes()
                with client.websocket_connect("/v1/reading") as second:
                    self.assertEqual(second.receive_json()["type"], "error")
                first.close()
                self.wait_idle(client)
            self.assertTrue(reader.closed.is_set())

    def test_cancel_during_generation_interrupts_worker(self):
        reader = Reader(block=True)
        with TestClient(create_app(reader)) as client:
            with client.websocket_connect("/v1/reading") as ws:
                ws.send_json(START)
                ws.receive_json()
                self.assertTrue(reader.started.wait(1))
                ws.send_json(dict(type="cancel"))
                self.assertTrue(reader.closed.wait(1))
                self.wait_idle(client)

    def test_invalid_request_does_not_run_model(self):
        for changes in (dict(protocol=3), dict(text=""), dict(text="字" * 301), dict(speed=2), dict(speed=True),
                        dict(protocol=2, speech_model="unsupported"), dict(protocol=2, speech_instruct=" "),
                        dict(protocol=2, speech_instruct="字" * 401), dict(speech_instruct="不支持"),
                        dict(protocol=2, speech_voice="unsupported"), dict(speech_voice=VOICE_ALTERNATIVE)):
            reader = Reader()
            with TestClient(create_app(reader)) as client:
                with client.websocket_connect("/v1/reading") as ws:
                    ws.send_json({**START, **changes})
                    self.assertEqual(ws.receive_json()["type"], "error")
                self.assertFalse(reader.started.is_set())

    def test_inference_error_is_sanitized(self):
        with TestClient(create_app(Reader(fail=True))) as client:
            with client.websocket_connect("/v1/reading") as ws:
                ws.send_json(START)
                ws.receive_json()
                error = ws.receive_json()
                self.assertEqual(error["type"], "error")
                self.assertNotIn("private", error["message"])

    def test_browser_origin_is_rejected(self):
        from starlette.websockets import WebSocketDisconnect
        with TestClient(create_app(Reader())) as client:
            with self.assertRaises(WebSocketDisconnect):
                with client.websocket_connect("/v1/reading", headers={"origin": "https://example.com"}):
                    pass
