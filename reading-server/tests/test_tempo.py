import unittest
import numpy as np
from shuo_reading.engine import Tempo, SAMPLE_RATE


class TempoTests(unittest.TestCase):
    def test_streaming_tempo_changes_duration_without_pitch_shift(self):
        signal = (.4 * np.sin(2 * np.pi * 440 * np.arange(SAMPLE_RATE * 3) / SAMPLE_RATE)).astype(np.float32)
        for speed in (.85, 1., 1.15, 1.3):
            tempo = Tempo(speed)
            chunks = []
            for offset in range(0, len(signal), 7680):
                chunks.extend(tempo.push(signal[offset:offset + 7680]))
            chunks.extend(tempo.push(None))
            output = np.concatenate(chunks)
            self.assertLess(abs(len(output) / (len(signal) / speed) - 1), .03)
            frequency = np.fft.rfftfreq(len(output), 1 / SAMPLE_RATE)[np.abs(np.fft.rfft(output)).argmax()]
            self.assertLess(abs(frequency - 440), 2)
            if speed == 1:
                np.testing.assert_array_equal(output, signal)
