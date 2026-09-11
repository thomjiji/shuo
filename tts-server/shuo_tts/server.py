import argparse
import asyncio
import logging
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Protocol

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, ConfigDict, Field

from .profiles import VoiceProfile, load_profiles

logger = logging.getLogger("shuo_tts")
SAMPLE_RATE = 24000
MODEL_PROMPT_PREFIX = "You are a helpful assistant.<|endofprompt|>"
DEFAULT_MODELS = {
    "cosyvoice": "mlx-community/Fun-CosyVoice3-0.5B-2512-4bit",
    "qwen3": "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-4bit",
}


class SynthesisRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    protocol: int
    text: str = Field(min_length=1, max_length=300)
    voice: str = Field(min_length=1, max_length=32)


class SpeechEngine(Protocol):
    def load(self, profiles: dict[str, VoiceProfile]) -> None: ...
    def synthesize(self, text: str, profile: VoiceProfile) -> bytes: ...


class MlxCosyVoice:
    engine_id = "cosyvoice"

    def __init__(self, model_path: str):
        self.model_path = model_path
        self.model_name = model_path.rsplit("/", 1)[-1]
        self.model = None
        self.reference_audio = {}

    def load(self, profiles: dict[str, VoiceProfile]) -> None:
        from unittest.mock import patch

        from mlx_audio.tts.generate import load_audio
        from mlx_audio.tts.utils import load_model
        from transformers import AutoTokenizer

        self.model = load_model(self.model_path)
        if self.model.sample_rate != SAMPLE_RATE:
            raise RuntimeError(f"模型采样率 {self.model.sample_rate} 与服务协议不兼容。")
        original_tokenizer_loader = AutoTokenizer.from_pretrained

        def load_fixed_tokenizer(*args, **kwargs):
            kwargs.setdefault("fix_mistral_regex", True)
            return original_tokenizer_loader(*args, **kwargs)

        # mlx-audio-plus 0.1.8 loads this lazily without the Transformers regex fix.
        # Preload all inference components so /health only reports ready afterwards.
        self.model._ensure_model_loaded()
        with patch.object(AutoTokenizer, "from_pretrained", side_effect=load_fixed_tokenizer):
            self.model._ensure_tokenizers_loaded()
        self.reference_audio = {
            voice_id: load_audio(str(profile.prompt_wav), sample_rate=SAMPLE_RATE)
            for voice_id, profile in profiles.items()
        }

    def synthesize(self, text: str, profile: VoiceProfile) -> bytes:
        import numpy as np

        if self.model is None:
            raise RuntimeError("模型尚未加载。")
        reference_text = (
            None
            if profile.cross_lingual
            else MODEL_PROMPT_PREFIX + profile.prompt_text
        )
        results = self.model.generate(
            text=MODEL_PROMPT_PREFIX + text,
            ref_audio=self.reference_audio[profile.id],
            ref_text=reference_text,
            stt_model=None,
            stream=False,
            verbose=False,
        )
        audio = [np.asarray(result.audio, dtype=np.float32) for result in results]
        if not audio:
            raise RuntimeError("模型没有返回音频。")
        samples = np.concatenate(audio)
        if samples.size == 0 or not np.isfinite(samples).all():
            raise RuntimeError("模型返回了无效音频。")
        return (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()


class MlxQwen3Voice:
    engine_id = "qwen3"

    def __init__(self, model_path: str):
        self.model_path = model_path
        self.model_name = model_path.rsplit("/", 1)[-1]
        self.model = None
        self.reference_audio = {}

    def load(self, profiles: dict[str, VoiceProfile]) -> None:
        from mlx_audio.tts.utils import load_model
        from mlx_audio.utils import load_audio

        self.model = load_model(self.model_path)
        if getattr(self.model, "model_type", None) != "qwen3_tts":
            raise RuntimeError("指定的模型不是 Qwen3-TTS。")
        if self.model.sample_rate != SAMPLE_RATE:
            raise RuntimeError(f"模型采样率 {self.model.sample_rate} 与服务协议不兼容。")
        self.reference_audio = {
            voice_id: load_audio(str(profile.prompt_wav), sample_rate=SAMPLE_RATE)
            for voice_id, profile in profiles.items()
        }

    def synthesize(self, text: str, profile: VoiceProfile) -> bytes:
        import numpy as np

        if self.model is None:
            raise RuntimeError("模型尚未加载。")
        results = self.model.generate(
            text=text,
            ref_audio=self.reference_audio[profile.id],
            ref_text=profile.prompt_text,
            lang_code="auto",
            split_pattern="",
            stream=False,
            verbose=False,
        )
        audio = [np.asarray(result.audio, dtype=np.float32) for result in results]
        if not audio:
            raise RuntimeError("模型没有返回音频。")
        samples = np.concatenate(audio)
        if samples.size == 0 or not np.isfinite(samples).all():
            raise RuntimeError("模型返回了无效音频。")
        return (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()


def create_app(engine: SpeechEngine, profiles: dict[str, VoiceProfile], *, timeout_seconds: float = 120) -> FastAPI:
    pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="mlx-tts")
    busy = asyncio.Lock()
    ready = False

    @asynccontextmanager
    async def lifespan(app):
        nonlocal ready
        try:
            await asyncio.get_running_loop().run_in_executor(pool, engine.load, profiles)
            ready = True
            yield
        finally:
            ready = False
            pool.shutdown(wait=True, cancel_futures=True)

    app = FastAPI(lifespan=lifespan)

    @app.get("/health")
    async def health():
        return {
            "ready": ready,
            "protocol": 1,
            "engine": getattr(engine, "engine_id", "test"),
            "model": getattr(engine, "model_name", type(engine).__name__),
            "sample_rate": SAMPLE_RATE,
            "busy": busy.locked(),
            "voices": [{"id": item.id, "name": item.name} for item in profiles.values()],
        }

    @app.post("/v1/tts")
    async def synthesize(request: SynthesisRequest):
        if request.protocol != 1:
            raise HTTPException(400, "客户端协议不兼容，请更新 Shuo。")
        text = request.text.strip()
        if not text:
            raise HTTPException(400, "没有可朗读的文字。")
        profile = profiles.get(request.voice)
        if profile is None:
            raise HTTPException(404, "Mac 上没有所选音色。")
        if busy.locked():
            raise HTTPException(409, "Mac 正在合成另一段语音，请稍后重试。")
        await busy.acquire()
        future = asyncio.get_running_loop().run_in_executor(pool, engine.synthesize, text, profile)
        try:
            pcm = await asyncio.wait_for(asyncio.shield(future), timeout_seconds)
        except TimeoutError:
            await asyncio.shield(future)
            raise HTTPException(504, "语音合成超时，请缩短文字后重试。") from None
        except asyncio.CancelledError:
            await asyncio.shield(future)
            raise
        except Exception:
            logger.exception("TTS synthesis failed")
            raise HTTPException(500, "Mac 语音合成失败，请检查服务日志。") from None
        finally:
            busy.release()
        if not pcm or len(pcm) % 2:
            raise HTTPException(500, "Mac 语音合成返回了无效音频。")
        return Response(pcm, media_type="application/octet-stream", headers={
            "X-Shuo-Protocol": "1",
            "X-Shuo-Audio-Format": "pcm_s16le",
            "X-Shuo-Sample-Rate": str(SAMPLE_RATE),
        })

    return app


def main():
    import uvicorn

    parser = argparse.ArgumentParser(description="Shuo MLX TTS service for Apple Silicon")
    parser.add_argument("--engine", choices=sorted(DEFAULT_MODELS), default="cosyvoice")
    parser.add_argument("--model")
    parser.add_argument("--voices-dir", type=Path, default=Path("voices"))
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=18766)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO)
    profiles = load_profiles(args.voices_dir.resolve())
    if not profiles:
        parser.error("没有可用音色；请先运行 shuo-tts-voice 注册参考声音。")
    model_path = args.model or DEFAULT_MODELS[args.engine]
    if args.engine == "cosyvoice":
        engine = MlxCosyVoice(model_path)
    else:
        engine = MlxQwen3Voice(model_path)
    app = create_app(engine, profiles, timeout_seconds=args.timeout_seconds)
    uvicorn.run(app, host=args.host, port=args.port, timeout_graceful_shutdown=150)


if __name__ == "__main__":
    main()
