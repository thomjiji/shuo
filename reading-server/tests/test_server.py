import threading
import time
import unittest
from fastapi.testclient import TestClient
from shuo_reading.server import create_app

START = dict(type="start", protocol=1, text="会議は明日です。", speed=1.0)


class Reader:
    def __init__(self, block=False, fail=False):
        self.block = block
        self.fail = fail
        self.started = threading.Event()
        self.closed = threading.Event()
        self.chunks = 0

    def load(self):
        pass

    def events(self, text, speed, stopped):
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
        for changes in (dict(protocol=2), dict(text=""), dict(text="字" * 301), dict(speed=2), dict(speed=True)):
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
