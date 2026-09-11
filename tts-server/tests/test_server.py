import json
import math
import tempfile
import unittest
from pathlib import Path

import numpy as np
import soundfile as sf
from fastapi.testclient import TestClient

from shuo_tts.profiles import VoiceProfile, load_profiles, register_voice
from shuo_tts.server import MODEL_PROMPT_PREFIX, MlxCosyVoice, MlxQwen3Voice, create_app


class FakeEngine:
    def __init__(self):
        self.loaded = False
        self.requests = []

    def load(self, profiles):
        self.loaded = True

    def synthesize(self, text, profile):
        self.requests.append((text, profile.id))
        return b"\x01\x00\x02\x00"


class TtsServerTests(unittest.TestCase):
    def test_mlx_generation_uses_model_prompt_and_cross_lingual_mode(self):
        class FakeMlxModel:
            sample_rate = 24000

            def generate(self, **kwargs):
                self.kwargs = kwargs
                return [type("Result", (), {"audio": np.array([0.1], dtype=np.float32)})()]

        model = FakeMlxModel()
        engine = MlxCosyVoice("unused")
        engine.model = model
        engine.reference_audio["voice"] = np.zeros(24000, dtype=np.float32)
        profile = VoiceProfile(
            "voice", "日文参考音色", "これは参照音声です。", Path("unused.wav"), 4, True)

        self.assertEqual(len(engine.synthesize("你好", profile)), 2)
        self.assertEqual(model.kwargs["text"], MODEL_PROMPT_PREFIX + "你好")
        self.assertIsNone(model.kwargs["ref_text"])
        self.assertIsNone(model.kwargs["stt_model"])

        same_language = VoiceProfile(
            "voice", "中文参考音色", "这是参考音频。", Path("unused.wav"), 4, False)
        engine.synthesize("你好", same_language)
        self.assertEqual(
            model.kwargs["ref_text"], MODEL_PROMPT_PREFIX + "这是参考音频。")

    def test_qwen_generation_uses_transcript_for_cross_lingual_clone(self):
        class FakeMlxModel:
            def generate(self, **kwargs):
                self.kwargs = kwargs
                return [type("Result", (), {"audio": np.array([0.1], dtype=np.float32)})()]

        model = FakeMlxModel()
        engine = MlxQwen3Voice("unused")
        engine.model = model
        engine.reference_audio["voice"] = np.zeros(24000, dtype=np.float32)
        profile = VoiceProfile(
            "voice", "日文参考音色", "これは参照音声です。", Path("unused.wav"), 4, True)

        self.assertEqual(len(engine.synthesize("你好", profile)), 2)
        self.assertEqual(model.kwargs["text"], "你好")
        self.assertEqual(model.kwargs["ref_text"], "これは参照音声です。")
        self.assertEqual(model.kwargs["lang_code"], "auto")
        self.assertEqual(model.kwargs["split_pattern"], "")
        self.assertFalse(model.kwargs["stream"])

    def test_registers_canonical_private_voice(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.wav"
            sample_rate = 16000
            samples = np.sin(np.arange(sample_rate * 4) * 2 * math.pi * 220 / sample_rate).astype(np.float32) * 0.2
            sf.write(source, samples, sample_rate)
            profile = register_voice(root / "voices", "test-voice", "测试声音", source,
                                     "这是参考音频的逐字稿。", consent=True)
            info = sf.info(profile.prompt_wav)
            self.assertEqual(info.samplerate, 24000)
            self.assertEqual(info.channels, 1)
            self.assertAlmostEqual(info.duration, 4, places=2)
            manifest = json.loads((profile.prompt_wav.parent / "voice.json").read_text(encoding="utf-8"))
            self.assertEqual(manifest["prompt_text"], "这是参考音频的逐字稿。")
            self.assertFalse(manifest["cross_lingual"])
            self.assertEqual(load_profiles(root / "voices")["test-voice"].name, "测试声音")

    def test_registers_cross_lingual_voice(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.wav"
            sf.write(source, np.ones(64000, dtype=np.float32) * 0.1, 16000)
            profile = register_voice(
                root / "voices", "japanese-voice", "日文参考音色", source,
                "これは参照音声です。", consent=True, cross_lingual=True,
            )
            self.assertTrue(profile.cross_lingual)
            self.assertTrue(load_profiles(root / "voices")["japanese-voice"].cross_lingual)

    def test_requires_consent_and_valid_audio(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.wav"
            sf.write(source, np.ones(48000, dtype=np.float32) * 0.1, 16000)
            with self.assertRaisesRegex(ValueError, "许可"):
                register_voice(root / "voices", "voice", "声音", source, "逐字稿", consent=False)
            with self.assertRaisesRegex(ValueError, "采样率"):
                low_rate = root / "low.wav"
                sf.write(low_rate, np.ones(24000, dtype=np.float32) * 0.1, 8000)
                register_voice(root / "voices", "voice", "声音", low_rate, "逐字稿", consent=True)

    def test_health_and_pcm_protocol(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            voice_dir = root / "voice"
            voice_dir.mkdir()
            (voice_dir / "prompt.wav").write_bytes(b"unused")
            (voice_dir / "voice.json").write_text(json.dumps({
                "name": "测试声音", "prompt_text": "逐字稿", "duration_seconds": 4,
            }), encoding="utf-8")
            profiles = load_profiles(root)
            engine = FakeEngine()
            with TestClient(create_app(engine, profiles)) as client:
                health = client.get("/health").json()
                self.assertTrue(health["ready"])
                self.assertEqual(health["engine"], "test")
                self.assertEqual(health["voices"], [{"id": "voice", "name": "测试声音"}])
                response = client.post("/v1/tts", json={"protocol": 1, "text": " 你好 ", "voice": "voice"})
                self.assertEqual(response.status_code, 200)
                self.assertEqual(response.content, b"\x01\x00\x02\x00")
                self.assertEqual(response.headers["x-shuo-audio-format"], "pcm_s16le")
                self.assertEqual(response.headers["x-shuo-sample-rate"], "24000")
                self.assertEqual(engine.requests, [("你好", "voice")])
                self.assertEqual(client.post("/v1/tts", json={"protocol": 1, "text": "你好", "voice": "missing"}).status_code, 404)
                self.assertEqual(client.post("/v1/tts", json={"protocol": 2, "text": "你好", "voice": "voice"}).status_code, 400)


if __name__ == "__main__":
    unittest.main()
