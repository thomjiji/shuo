import unittest
from threading import Event
from types import SimpleNamespace
import numpy as np
from shuo_reading.engine import Reader, SAMPLE_RATE, SPEECH_LARGE, SPEECH_SMALL, VOICE, VOICE_ALTERNATIVE, SpeechSynthesizer


class OriginalTests(unittest.TestCase):
    def test_original_bypasses_translation_and_uses_auto_language(self):
        calls = []

        def generate(**options):
            calls.append(options)
            yield SimpleNamespace(sample_rate=SAMPLE_RATE, audio=np.zeros(7680, dtype=np.float32))

        reader = Reader()
        # No translator is loaded: original reading must only use the speech model.
        reader.speech.model = SimpleNamespace(generate=generate)
        reader.speech.model_id = SPEECH_LARGE
        source = "Hello. 会議は明日です。你好。"
        events = list(reader.original_events(source, 1, Event()))
        self.assertEqual(events[0], ("text", source))
        self.assertEqual(calls[0]["text"], source)
        self.assertEqual(calls[0]["lang_code"], "auto")
        self.assertEqual(calls[0]["voice"], "Serena")
        self.assertNotIn("instruct", calls[0])
        self.assertEqual(sum(len(data) for kind, data in events if kind == "audio"), 15360)

    def test_translated_reading_uses_auto_language(self):
        calls = []

        def generate(**options):
            calls.append(options)
            yield SimpleNamespace(sample_rate=SAMPLE_RATE, audio=np.zeros(7680, dtype=np.float32))

        reader = Reader()
        reader.translation_events = lambda source, target, stopped: iter([("text", "中文 Qwen3.8-Omni-Flash")])
        reader.speech.model = SimpleNamespace(generate=generate)
        reader.speech.model_id = SPEECH_LARGE
        list(reader.events("source", 1, Event()))
        self.assertEqual(calls[0]["text"], "中文 Qwen3.8-Omni-Flash")
        self.assertEqual(calls[0]["lang_code"], "auto")

    def test_custom_instruction_and_voice_reach_both_speech_models(self):
        for model_id in (SPEECH_SMALL, SPEECH_LARGE):
            for voice in (VOICE, VOICE_ALTERNATIVE):
                with self.subTest(model_id=model_id, voice=voice):
                    calls = []

                    def generate(**options):
                        calls.append(options)
                        yield SimpleNamespace(sample_rate=SAMPLE_RATE, audio=np.zeros(7680, dtype=np.float32))

                    reader = Reader()
                    reader.speech.model = SimpleNamespace(generate=generate)
                    reader.speech.model_id = model_id
                    list(reader.original_events("这是结论。", 1, Event(), model_id, "自然而稳定地朗读。", voice))
                    self.assertEqual(calls[0]["instruct"], "自然而稳定地朗读。")
                    self.assertEqual(calls[0]["voice"], voice)
                    self.assertEqual(calls[0]["text"], "这是结论。")
        self.assertEqual(SPEECH_SMALL, "mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-8bit")

    def test_switch_keeps_only_the_selected_model_reference(self):
        loaded = []

        def load(model_id):
            model = SimpleNamespace(id=model_id)
            loaded.append(model)
            return model

        speech = SpeechSynthesizer(load)
        speech._select_model(SPEECH_SMALL)
        first = speech.model
        speech._select_model(SPEECH_LARGE)
        self.assertIsNot(speech.model, first)
        self.assertEqual(speech.model.id, SPEECH_LARGE)
        self.assertEqual(speech.model_id, SPEECH_LARGE)
        speech._select_model(SPEECH_LARGE)
        self.assertEqual(len(loaded), 2)
