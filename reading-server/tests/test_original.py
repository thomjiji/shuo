import unittest
from threading import Event
from types import SimpleNamespace
import numpy as np
from shuo_reading.engine import Reader, SAMPLE_RATE, SPEECH, SPEECH_INSTRUCTIONS, SPEECH_LARGE, SpeechSynthesizer


class OriginalTests(unittest.TestCase):
    def test_original_bypasses_translation_and_uses_auto_language(self):
        calls = []

        def generate(**options):
            calls.append(options)
            yield SimpleNamespace(sample_rate=SAMPLE_RATE, audio=np.zeros(7680, dtype=np.float32))

        reader = Reader()
        # No translator is loaded: original reading must only use the speech model.
        reader.speech.model = SimpleNamespace(generate=generate)
        reader.speech.model_id = SPEECH
        source = "Hello. 会議は明日です。你好。"
        events = list(reader.original_events(source, 1, Event()))
        self.assertEqual(events[0], ("text", source))
        self.assertEqual(calls[0]["text"], source)
        self.assertEqual(calls[0]["lang_code"], "auto")
        self.assertEqual(calls[0]["voice"], "Serena")
        self.assertNotIn("instruct", calls[0])
        self.assertEqual(sum(len(data) for kind, data in events if kind == "audio"), 15360)

    def test_large_model_uses_fixed_chinese_reading_instruction(self):
        calls = []

        def generate(**options):
            calls.append(options)
            yield SimpleNamespace(sample_rate=SAMPLE_RATE, audio=np.zeros(7680, dtype=np.float32))

        reader = Reader()
        reader.speech.model = SimpleNamespace(generate=generate)
        reader.speech.model_id = SPEECH_LARGE
        list(reader.original_events("这是结论。", 1, Event(), SPEECH_LARGE))
        self.assertEqual(calls[0]["instruct"], SPEECH_INSTRUCTIONS[SPEECH_LARGE])
        self.assertEqual(calls[0]["text"], "这是结论。")
        self.assertEqual(SPEECH, "mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-8bit")

    def test_switch_keeps_only_the_selected_model_reference(self):
        loaded = []

        def load(model_id):
            model = SimpleNamespace(id=model_id)
            loaded.append(model)
            return model

        speech = SpeechSynthesizer(load)
        speech._select_model(SPEECH)
        first = speech.model
        speech._select_model(SPEECH_LARGE)
        self.assertIsNot(speech.model, first)
        self.assertEqual(speech.model.id, SPEECH_LARGE)
        self.assertEqual(speech.model_id, SPEECH_LARGE)
        speech._select_model(SPEECH_LARGE)
        self.assertEqual(len(loaded), 2)
