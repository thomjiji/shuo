# /// script
# requires-python = ">=3.12,<3.13"
# dependencies = ["mlx-lm==0.31.3", "mlx-audio==0.5.3", "soundfile==0.14.0"]
# ///
"""Opt-in Apple Silicon translation/TTS benchmark; writes only public test samples.

Run with a uv-managed environment containing mlx-lm, mlx-audio and soundfile.
Model downloads use the normal persistent Hugging Face cache.
"""
import argparse
import json
import re
import resource
import time
from pathlib import Path

import mlx.core as mx
import numpy as np
import soundfile as sf
from mlx_lm import load, stream_generate
from mlx_lm.sample_utils import make_sampler
from mlx_audio.tts.utils import load_model

parser = argparse.ArgumentParser()
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
llm_id = "mlx-community/Qwen3-8B-4bit"
tts_id = "mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-8bit"
print("Loading translation model", flush=True)
llm, tokenizer = load(llm_id)
print("Loading speech model", flush=True)
tts = load_model(tts_id)
print("Models loaded", flush=True)
samples = {
    "english": "Please save your work before restarting the computer. The update should take about five minutes, and your files will not be deleted.",
    "japanese": "会議は明日の午後三時に始まります。資料を忘れないでください。参加費は無料です。",
}
rows = []
for round_index in range(2):
    for name, source in samples.items():
        prompt = tokenizer.apply_chat_template(
            [{"role": "user", "content": "将以下原文完整忠实地翻译成简体中文，保留数字和否定。只输出译文，不解释、不总结。\n\n" + source}],
            add_generation_prompt=True, tokenize=False, enable_thinking=False,
        )
        started = time.perf_counter()
        text = ""
        first_text = None
        first_sentence = None
        for part in stream_generate(llm, tokenizer, prompt, max_tokens=256, sampler=make_sampler(temp=0)):
            text += part.text
            elapsed = time.perf_counter() - started
            if part.text and first_text is None:
                first_text = elapsed
            if first_sentence is None and re.search(r"[。！？]", text):
                first_sentence = elapsed
        translation_seconds = time.perf_counter() - started
        audio = []
        tts_started = time.perf_counter()
        first_audio = None
        for result in tts.generate(text=text, voice="Vivian", lang_code="Chinese", stream=True, streaming_interval=0.32):
            chunk = np.asarray(result.audio)
            if chunk.size and first_audio is None:
                first_audio = time.perf_counter() - tts_started
            audio.append(chunk)
            sample_rate = result.sample_rate
        tts_seconds = time.perf_counter() - tts_started
        pcm = np.concatenate(audio)
        duration = len(pcm) / sample_rate
        sf.write(args.output / f"{name}-{round_index + 1}.wav", pcm, sample_rate)
        row = dict(sample=name, round=round_index + 1, source=source, translation=text,
                   first_text_seconds=first_text, first_sentence_seconds=first_sentence,
                   translation_seconds=translation_seconds, tts_first_audio_seconds=first_audio,
                   end_to_end_first_audio_seconds=translation_seconds + first_audio,
                   tts_seconds=tts_seconds, audio_seconds=duration,
                   realtime_factor=tts_seconds / duration,
                   peak_mlx_gib=mx.get_peak_memory() / 1024**3,
                   peak_process_rss_gib=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024**3)
        rows.append(row)
        print(json.dumps(row, ensure_ascii=False), flush=True)
        (args.output / "results.json").write_text(json.dumps(dict(llm=llm_id, tts=tts_id, results=rows), ensure_ascii=False, indent=2))
