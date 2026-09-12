# /// script
# requires-python = ">=3.12,<3.13"
# dependencies = ["mlx-audio==0.5.3", "soundfile==0.14.0"]
# ///
"""Generate comparable Chinese voice samples on Apple Silicon."""
import argparse
import json
from pathlib import Path

import mlx.core as mx
import numpy as np
import soundfile as sf
from mlx_audio.tts.utils import load_model

parser = argparse.ArgumentParser()
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
model = load_model("mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-8bit")
text = "请在重新启动计算机前保存你的工作。更新大约需要五分钟，你的文件不会被删除。"
for voice in ("Serena", "Uncle_Fu"):
    mx.random.seed(42)
    chunks = []
    for result in model.generate(text=text, voice=voice, lang_code="Chinese",
                                 stream=True, streaming_interval=0.32, max_tokens=512):
        chunks.append(np.asarray(result.audio))
        sample_rate = result.sample_rate
    audio = np.concatenate(chunks)
    if not audio.size or not np.all(np.isfinite(audio)) or not np.any(audio):
        raise RuntimeError(f"Invalid audio: {voice}")
    sf.write(args.output / f"{voice.lower()}.wav", audio, sample_rate)
    print(json.dumps(dict(voice=voice, text=text, seconds=len(audio) / sample_rate), ensure_ascii=False), flush=True)
