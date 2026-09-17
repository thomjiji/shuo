# /// script
# requires-python = ">=3.12,<3.13"
# dependencies = ["mlx-lm==0.31.3", "mlx-audio==0.5.3", "soundfile==0.14.0"]
# ///
"""Opt-in Apple Silicon translation/TTS benchmark; writes only public test samples.

Run with a uv-managed environment containing mlx-lm, mlx-audio and soundfile.
Model downloads use the normal persistent Hugging Face cache.

`--chunk-bytes` additionally synthesizes one multi-paragraph sample with different
speech request budgets, so the resulting WAV files can be compared by ear against the
first-audio latency each budget costs. The sample is grouped by paragraph, while the
app also splits a single oversized paragraph at sentence or word boundaries. These
requests go straight to the model, so a budget above the service limit of 3000 UTF-8
bytes shows what the model accepts, not what the service currently allows.
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
parser.add_argument("--tts", default="mlx-community/Qwen3-TTS-12Hz-1.7B-CustomVoice-8bit")
parser.add_argument("--voice", default="Serena")
parser.add_argument("--chunk-bytes", type=int, nargs="+",
                    help="Compare speech request budgets in UTF-8 bytes; 0 sends the whole sample at once")
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
llm_id = "mlx-community/Qwen3-8B-4bit"
tts_id = args.tts
print("Loading translation model", flush=True)
llm, tokenizer = load(llm_id)
print("Loading speech model", flush=True)
tts = load_model(tts_id)
print("Models loaded", flush=True)
samples = {
    "english": "Please save your work before restarting the computer. The update should take about five minutes, and your files will not be deleted.",
    "japanese": "会議は明日の午後三時に始まります。資料を忘れないでください。参加費は無料です。",
}
# Public multi-paragraph sample for the request-budget comparison.
chunk_sample = "\n\n".join([
    "文字朗读服务把选中的文字拆成若干请求，每段在本机 GPU 上单独生成语音，生成好的音频按顺序进入同一个播放器。",
    "每次请求都是一次独立的生成：模型从这一段的开头重新规划停顿、重音和语气，结束时会自然收束，因此下一段开始时会像重新开口。",
    "段落切得越小，接缝越多，听感上的差别就越明显；一段话被切成四块，就相当于换了四次语气。",
    "把段落合并成更大的请求可以减少接缝，代价是首段音频出现得更晚，一次失败需要重发的文字也更多。",
    "模型一次能生成约五分钟音频，上下文长度远大于这个量级，所以切段并不是在绕开模型的输入上限。",
    "播放器缓冲满时会暂停生成并等待客户端确认，停止播放会立即取消后续生成，所以暂停和停止都不依赖小段切分。",
    "音色由固定的说话人向量决定，段与段之间不会换人，但语速和语调的细微差别仍然存在。",
    "段落边界通常也是文章的语义边界，把切换放在这里比放在句子中间更自然。",
    "翻译模式先按同样的段落请求译文，再把译文交给语音合成，所以译文的接缝也需要一起观察。",
    "采样温度偏高时，同一段文字每次生成的语气都会有差别，这种差别在长段落内部也可能出现。",
    "如果听感仍然不连贯，可以试着调整请求上限，或者接受段落之间的语气变化。",
    "服务端会拒绝超过上限的请求，并在日志里说明原因，客户端的分段预算需要与它保持一致。",
    "朗读在线时只处理一个请求，其他请求会收到忙碌提示，因此合并段落也不会带来并发的收益。",
    "每段音频进入播放器前只做一次变速处理，变速保持音调不变，不会影响段落之间的一致性。",
    "文字越长，预填时间越长，首段音频出现得越晚；流式解码仍然按固定间隔吐出音频，所以等待时间主要是预填。",
    "把整篇文章放在一个请求里可以彻底消除接缝，但失去按段落重试和快速停止的能力。",
    "段落数量少时，接缝本来就少，请求预算取多大对听感的影响也有限。",
    "段落数量多而且话题连续时，接缝出现在话题中间最容易听出来，这时更值得加大请求预算。",
    "默认预算取在两者之间：既让常见的一到两段文字只请求一次，又不至于让首段音频等得太久。",
    "上面的现象都可以用同一段文字和不同的请求预算复现，听感差异比任何参数表都直接。",
    "长段落一次生成的时间也长，暂停和停止仍然按音频包即时生效，不会等到整段生成完。",
    "段落越多，需要重发的窗口越小，网络断开或模型报错时损失的文字越少。",
    "如果选中的文字本身只有一个段落，切分不会发生，效果与整篇一次生成相同。",
    "段落之间的间隔由文本内容和模型的收束决定，请求边界只改变起点，不会凭空增加停顿。",
    "观察首段音频延迟时要注意，它包含模型的预填时间，文本越长这一段越明显。",
    "同一段文字可以反复试听，确认差别来自请求边界，还是来自这段文字本身的重音安排。",
    "记录每次试听的请求预算和段落字节数，才能在调整之后判断效果是真的变好了。",
])


def split_passages(text, limit):
    """Group paragraphs until the next one would exceed limit; 0 keeps the whole text."""
    if not limit:
        return [text]
    passages, current = [], ""
    for paragraph in (part for part in text.split("\n\n") if part.strip()):
        candidate = f"{current}\n\n{paragraph}" if current else paragraph
        if current and len(candidate.encode("utf-8")) > limit:
            passages.append(current)
            current = paragraph
        else:
            current = candidate
    if current:
        passages.append(current)
    return passages


def synthesize(text, voice):
    """One request: a fresh generation with streaming decode, exactly like the service."""
    started = time.perf_counter()
    chunks = []
    first_audio = None
    sample_rate = 24000
    for result in tts.generate(text=text, voice=voice, lang_code="Chinese", stream=True, streaming_interval=0.32):
        audio = np.asarray(result.audio)
        if audio.size and first_audio is None:
            first_audio = time.perf_counter() - started
        chunks.append(audio)
        sample_rate = result.sample_rate
    if not chunks:
        raise RuntimeError("Speech model returned no audio")
    return np.concatenate(chunks), sample_rate, first_audio, time.perf_counter() - started


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
        pcm, sample_rate, first_audio, tts_seconds = synthesize(text, args.voice)
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
        (args.output / "results.json").write_text(json.dumps(dict(llm=llm_id, tts=tts_id, voice=args.voice, results=rows), ensure_ascii=False, indent=2))

if args.chunk_bytes:
    print("Comparing speech request budgets", flush=True)
    chunk_rows = []
    for limit in args.chunk_bytes:
        passages = split_passages(chunk_sample, limit)
        started = time.perf_counter()
        audio = []
        first_audio = None
        sample_rate = 24000
        for passage in passages:
            pcm, sample_rate, _, _ = synthesize(passage, args.voice)
            if first_audio is None:
                first_audio = time.perf_counter() - started
            audio.append(pcm)
        speech_seconds = time.perf_counter() - started
        combined = np.concatenate(audio)
        duration = len(combined) / sample_rate
        label = "whole" if not limit else str(limit)
        sf.write(args.output / f"chunks-{label}.wav", combined, sample_rate)
        row = dict(budget_bytes=limit, passages=len(passages),
                   passage_bytes=[len(passage.encode("utf-8")) for passage in passages],
                   first_audio_seconds=first_audio, speech_seconds=speech_seconds, audio_seconds=duration,
                   realtime_factor=speech_seconds / duration,
                   peak_mlx_gib=mx.get_peak_memory() / 1024**3)
        chunk_rows.append(row)
        print(json.dumps(row, ensure_ascii=False), flush=True)
        (args.output / "chunk-results.json").write_text(
            json.dumps(dict(tts=tts_id, voice=args.voice, text=chunk_sample, results=chunk_rows), ensure_ascii=False, indent=2))
