import unittest
from fastapi.testclient import TestClient
from shuo_reading.engine import Reader
from shuo_reading.server import create_app


class Model:
    def __init__(self, fail=False):
        self.fail = fail
        self.unloaded = False

    def load(self):
        if self.fail:
            raise RuntimeError("private model path")

    def unload(self):
        self.unloaded = True

    def translation_events(self, source, target, stopped):
        yield "text", "translation"

    def speech_events(self, text, speed, language, stopped):
        yield "audio", b"\x01\x00"


class CapabilityTests(unittest.TestCase):
    def test_independent_model_failures(self):
        for translation_failed, speech_failed in ((False, True), (True, False), (True, True)):
            with self.subTest(translation_failed=translation_failed, speech_failed=speech_failed):
                translation, speech = Model(translation_failed), Model(speech_failed)
                with TestClient(create_app(Reader(translation, speech))) as client:
                    health = client.get("/health").json()
                    self.assertEqual(health["ready"], not (translation_failed and speech_failed))
                    self.assertEqual(health["capabilities"]["translation"]["ready"], not translation_failed)
                    self.assertEqual(health["capabilities"]["speech"]["ready"], not speech_failed)
                    self.assertEqual(translation.unloaded, translation_failed)
                    self.assertEqual(speech.unloaded, speech_failed)
                    for route, fails in (("translation", translation_failed), ("speech", speech_failed),
                                         ("reading", translation_failed or speech_failed)):
                        with client.websocket_connect("/v1/" + route) as ws:
                            ws.send_json(dict(type="start", protocol=1, text="hello", speed=1.0))
                            first = ws.receive_json()
                            if fails:
                                self.assertEqual(first["type"], "error")
                                self.assertNotIn("private", first["message"])
                            else:
                                self.assertEqual(first["type"], "ready")
                                self.assertEqual(ws.receive_json()["type"], "text")
                                if route == "speech":
                                    self.assertEqual(ws.receive_bytes(), b"\x01\x00")
                                    ws.send_json(dict(type="ack"))
                                self.assertEqual(ws.receive_json()["type"], "done")
