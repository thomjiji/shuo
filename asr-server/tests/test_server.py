import time
import unittest
from fastapi.testclient import TestClient
from shuo_asr.server import create_app

START = {"type": "start", "protocol": 1, "format": "pcm_s16le", "sample_rate": 16000, "language": "zh"}
VOICE = b"\x01\x00" * 320


class Vad:
    def is_speech(self, data, rate):
        return any(data)


class Recognizer:
    def __init__(self):
        self.calls = []
        self.fail = False
    def load(self):
        pass
    def transcribe(self, audio, language):
        if self.fail:
            raise RuntimeError("inference failed")
        self.calls.append((audio, language))
        return "测试文字。"


class ProtocolTests(unittest.TestCase):
    def client(self, recognizer=None, **kwargs):
        return TestClient(create_app(recognizer or Recognizer(), vad_factory=Vad, **kwargs))

    def test_short_recording_flushes_tail_and_silence_returns_empty_without_inference(self):
        r = Recognizer()
        with self.client(r) as client:
            self.assertTrue(client.get('/health').json()['ready'])
            self.assertEqual(client.get('/health').json()['segmentation']['silence_seconds'], 2)
            for audio in [bytes(6400), VOICE * 10 + VOICE[:120]]:
                before = len(r.calls)
                with client.websocket_connect('/v1/asr') as ws:
                    ws.send_json(START)
                    self.assertEqual(ws.receive_json()['type'], 'ready')
                    ws.send_bytes(audio)
                    ws.send_json({'type': 'finish'})
                    events = []
                    while True:
                        event = ws.receive_json(); events.append(event)
                        if event['type'] == 'final': break
                    if any(audio):
                        self.assertEqual(events[-1]['text'], '测试文字。')
                        self.assertEqual(len(r.calls), before + 1)
                        self.assertEqual(r.calls[-1][0][:len(audio)], audio)
                    else:
                        self.assertEqual(events, [{'type':'final','text':'','language':'zh'}])
                        self.assertEqual(len(r.calls), before)

    def test_preview_is_available_before_finish_and_final_does_not_duplicate_it(self):
        with self.client(preview_seconds=.2) as client:
            with client.websocket_connect('/v1/asr') as ws:
                ws.send_json(START); ws.receive_json()
                ws.send_bytes(VOICE * 12)
                self.assertEqual(ws.receive_json(), {'type': 'partial', 'text': '测试文字。'})
                ws.send_json({'type': 'finish'})
                self.assertEqual(ws.receive_json()['type'], 'partial')
                self.assertEqual(ws.receive_json()['text'], '测试文字。')

    def test_busy_session_is_rejected_and_disconnect_releases_slot(self):
        with self.client() as client:
            with client.websocket_connect('/v1/asr') as first:
                first.send_json(START); first.receive_json()
                with client.websocket_connect('/v1/asr') as second:
                    self.assertIn('另一段', second.receive_json()['message'])
                first.close()
                deadline = time.monotonic() + 1
                while client.get('/health').json()['busy'] and time.monotonic() < deadline:
                    time.sleep(.01)
                self.assertFalse(client.get('/health').json()['busy'])
            with client.websocket_connect('/v1/asr') as third:
                third.send_json(START)
                self.assertEqual(third.receive_json()['type'], 'ready')
                third.send_json({'type':'finish'})
                self.assertEqual(third.receive_json()['type'], 'final')

    def test_bad_protocol_pcm_and_language_fail_without_final_text(self):
        with self.client() as client:
            for config, data in [(dict(START, protocol=2), None), (dict(START,language='invalid'), None), (START,b'odd')]:
                with client.websocket_connect('/v1/asr') as ws:
                    ws.send_json(config)
                    event = ws.receive_json()
                    if data:
                        self.assertEqual(event['type'],'ready'); ws.send_bytes(data); event=ws.receive_json()
                    self.assertEqual(event['type'],'error')

    def test_inference_failure_is_not_reported_as_a_successful_final(self):
        r=Recognizer(); r.fail=True
        with self.client(r) as client:
            with client.websocket_connect('/v1/asr') as ws:
                ws.send_json(START); ws.receive_json()
                ws.send_bytes(VOICE * 10); ws.send_json({'type':'finish'})
                self.assertEqual(ws.receive_json()['type'],'error')

    def test_model_selection_routes_each_session_and_rejects_unknown_models(self):
        large, small = Recognizer(), Recognizer()
        with self.client(large, models={"Qwen3-ASR-0.6B-8bit": small}) as client:
            self.assertEqual(len(client.get('/health').json()['models']), 2)
            for model, target in [("Qwen3-ASR-0.6B-8bit", small), (None, large), ("Qwen3-ASR-1.7B-8bit", large)]:
                config = dict(START, model=model) if model else START
                before = len(target.calls)
                with client.websocket_connect('/v1/asr') as ws:
                    ws.send_json(config)
                    self.assertEqual(ws.receive_json()['model'], model or 'Qwen3-ASR-1.7B-8bit')
                    ws.send_bytes(VOICE * 10); ws.send_json({'type': 'finish'})
                    while ws.receive_json()['type'] != 'final': pass
                self.assertEqual(len(target.calls), before + 1)
            for invalid in ['missing', [], None]:
                with client.websocket_connect('/v1/asr') as ws:
                    ws.send_json(dict(START, model=invalid))
                    self.assertEqual(ws.receive_json()['type'], 'error')

    def test_segmentation_settings_are_reported_and_invalid_durations_rejected(self):
        with self.client(max_seconds=30, silence_seconds=1, preview_seconds=1) as client:
            self.assertEqual(client.get('/health').json()['segmentation'],
                             {'max_seconds': 30, 'hard_seconds': 35, 'silence_seconds': 1, 'preview_seconds': 1,
                              'short_voice_seconds': 2, 'short_silence_seconds': 2, 'soft_pause_seconds': .3})
        for field in ['max_seconds', 'hard_seconds', 'silence_seconds', 'preview_seconds']:
            for value in [0, -1, float('nan'), float('inf')]:
                with self.assertRaises(ValueError):
                    create_app(Recognizer(), vad_factory=Vad, **{field: value})

    def test_hard_limit_cannot_precede_soft_limit(self):
        with self.assertRaises(ValueError):
            create_app(Recognizer(), max_seconds=30, hard_seconds=29)

    def test_stalled_sender_times_out(self):
        with self.client(idle_timeout=.05) as client:
            with client.websocket_connect('/v1/asr') as ws:
                ws.send_json(START); ws.receive_json()
                self.assertEqual(ws.receive_json()['type'],'error')


if __name__ == '__main__':
    unittest.main()
