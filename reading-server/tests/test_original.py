import unittest
from threading import Event
from types import SimpleNamespace
import numpy as np
from shuo_reading.engine import Reader, SAMPLE_RATE


class OriginalTests(unittest.TestCase):
    def test_original_bypasses_translation_and_uses_auto_language(self):
        calls = []

        def generate(**options):
            calls.append(options)
            yield SimpleNamespace(sample_rate=SAMPLE_RATE, audio=np.zeros(7680, dtype=np.float32))

        reader = Reader()
        # No translator is loaded: original reading must only use the speech model.
        reader.speech = SimpleNamespace(generate=generate)
        source = "Hello. 会議は明日です。你好。"
        events = list(reader.original_events(source, 1, Event()))
        self.assertEqual(events[0], ("text", source))
        self.assertEqual(calls[0]["text"], source)
        self.assertEqual(calls[0]["lang_code"], "auto")
        self.assertEqual(calls[0]["voice"], "Serena")
        self.assertEqual(sum(len(data) for kind, data in events if kind == "audio"), 15360)
