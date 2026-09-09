"""Single-session ASR service. Audio and transcripts are never written to disk."""
import argparse
import asyncio
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager, suppress
import logging
import math
import re

from .segmentation import PendingJobs, Segmenter

logger = logging.getLogger("shuo_asr")
LANGUAGES = dict(zip(
    "zh en yue ar de fr es pt id it ko ru th vi ja tr hi ms nl sv da fi pl cs fil fa el hu mk ro".split(),
    "Chinese English Cantonese Arabic German French Spanish Portuguese Indonesian Italian Korean Russian Thai Vietnamese Japanese Turkish Hindi Malay Dutch Swedish Danish Finnish Polish Czech Filipino Persian Greek Hungarian Macedonian Romanian".split(),
))


class Recognizer:
    def __init__(self, model_path):
        self.model_path = model_path
        self.model = None

    def load(self):
        import mlx.core as mx
        import numpy as np
        from mlx_audio.stt import load
        self.model = load(self.model_path)
        mx.eval(self.model.parameters())
        # Compile kernels before accepting the first dictation session.
        self.model.generate(np.zeros(16000, dtype=np.float32), max_tokens=16, language="Chinese")
        logger.info("Model ready; MLX active memory %.2f GB", mx.get_active_memory() / 1e9)

    def transcribe(self, pcm, language):
        import numpy as np
        samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
        return self.model.generate(samples, language=language, max_tokens=512).text.strip()


def create_app(recognizer, *, model_name="Qwen3-ASR-1.7B-8bit", vad_factory=None,
               models=None, preview_seconds=1.0, silence_seconds=1.0, max_seconds=30.0, hard_seconds=None,
               idle_timeout=30.0, finish_timeout=60.0):
    if not all(math.isfinite(value) and value > 0 for value in (preview_seconds, silence_seconds, max_seconds)):
        raise ValueError("Segmentation durations must be finite and greater than zero")
    from fastapi import FastAPI, WebSocket, WebSocketDisconnect
    hard_seconds = max_seconds + 5 if hard_seconds is None else hard_seconds
    if not math.isfinite(hard_seconds) or hard_seconds < max_seconds:
        raise ValueError("Hard duration must be finite and at least the soft duration")
    from .segmentation import join_text

    if vad_factory is None:
        import webrtcvad
        vad_factory = lambda: webrtcvad.Vad(2)
    pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="mlx-asr")
    busy = asyncio.Lock()
    available = {model_name: recognizer, **(models or {})}

    @asynccontextmanager
    async def lifespan(app):
        try:
            for instance in available.values():
                await asyncio.get_running_loop().run_in_executor(pool, instance.load)
            yield
        finally:
            pool.shutdown(wait=True, cancel_futures=True)

    app = FastAPI(lifespan=lifespan)

    @app.get("/health")
    async def health():
        return {"ready": True, "protocol": 1, "model": model_name, "models": list(available), "busy": busy.locked(),
                "segmentation": {"preview_seconds": preview_seconds, "silence_seconds": silence_seconds, "max_seconds": max_seconds,
                                 "hard_seconds": hard_seconds, "short_voice_seconds": 2,
                                 "short_silence_seconds": max(2, silence_seconds), "soft_pause_seconds": .3}}

    @app.websocket("/v1/asr")
    async def asr(ws: WebSocket):
        await ws.accept()
        acquired = False
        tasks = []
        try:
            if busy.locked():
                await ws.send_json({"type": "error", "message": "Mac 正在处理另一段听写，请稍后重试。"})
                return
            await busy.acquire()
            acquired = True
            config = await asyncio.wait_for(ws.receive_json(), 10)
            if not isinstance(config, dict) or config.get("type") != "start" or config.get("protocol") != 1:
                raise ValueError("客户端协议不兼容，请更新 shuo。")
            if config.get("sample_rate") != 16000 or config.get("format") != "pcm_s16le":
                raise ValueError("服务只接收 16 kHz、16-bit 单声道 PCM。")
            code = config.get("language", "auto")
            if not isinstance(code, str):
                raise ValueError("无效的识别语言。")
            code = code.lower().split("-", 1)[0]
            language = None if code == "auto" else LANGUAGES.get(code)
            if code != "auto" and language is None:
                raise ValueError("不支持该识别语言，请使用 auto 或支持的语言代码。")
            selected = config.get("model", model_name)
            if not isinstance(selected, str) or selected not in available:
                raise ValueError("Mac 未安装所选模型，请选择其他模型或更新 Mac 服务。")
            session_recognizer = available[selected]
            vad = vad_factory()
            segmenter = Segmenter(lambda data: vad.is_speech(data, 16000),
                                  preview_seconds=preview_seconds, silence_seconds=silence_seconds,
                                  max_seconds=max_seconds, hard_seconds=hard_seconds)
            jobs = PendingJobs()
            wake = asyncio.Event()

            def enqueue(new_jobs):
                for job in new_jobs:
                    jobs.put(job)
                if new_jobs:
                    wake.set()

            async def receive_audio():
                total_bytes = 0
                while True:
                    message = await asyncio.wait_for(ws.receive(), idle_timeout)
                    if message["type"] == "websocket.disconnect":
                        raise WebSocketDisconnect()
                    data = message.get("bytes")
                    if data is not None:
                        if not data or len(data) > 32000 or len(data) % 2:
                            raise ValueError("音频包格式无效或超过 1 秒。")
                        total_bytes += len(data)
                        if total_bytes > 32000 * 1800:
                            raise ValueError("单次听写超过 30 分钟，请停止后重新开始。")
                        enqueue(segmenter.feed(data))
                    else:
                        import json
                        try:
                            event = json.loads(message.get("text", ""))
                        except (ValueError, TypeError):
                            raise ValueError("无效的客户端事件。") from None
                        if not isinstance(event, dict) or event.get("type") != "finish":
                            raise ValueError("无效的客户端事件。")
                        enqueue(segmenter.finish())
                        return

            async def produce_text():
                confirmed = ""
                while True:
                    await wake.wait()
                    if not jobs:
                        wake.clear()
                        continue
                    job = jobs.pop()
                    if not jobs:
                        wake.clear()
                    if job.kind == "finish":
                        detected = "zh" if code == "auto" and re.search(r"[\u3400-\u9fff]", confirmed) else code
                        await ws.send_json({"type": "final", "text": confirmed, "language": detected})
                        return
                    future = asyncio.get_running_loop().run_in_executor(pool, session_recognizer.transcribe, job.audio, language)
                    try:
                        text = await asyncio.shield(future)
                    except asyncio.CancelledError:
                        # Keep the session busy until any native GPU work actually stops.
                        with suppress(Exception):
                            await future
                        raise
                    if job.kind == "segment":
                        confirmed = join_text(confirmed, text)
                        snapshot = confirmed
                    else:
                        snapshot = join_text(confirmed, text)
                    await ws.send_json({"type": "partial", "text": snapshot})

            await ws.send_json({"type": "ready", "protocol": 1, "model": selected})
            consumer = asyncio.create_task(receive_audio())
            producer = asyncio.create_task(produce_text())
            tasks = [consumer, producer]
            completed, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
            if consumer in completed:
                await consumer
                await asyncio.wait_for(producer, finish_timeout)
            else:
                await producer
        except WebSocketDisconnect:
            pass
        except (ValueError, asyncio.TimeoutError) as error:
            with suppress(Exception):
                await ws.send_json({"type": "error", "message": str(error) if isinstance(error, ValueError) else "识别连接超时，请重新开始听写。"})
        except Exception:
            logger.exception("ASR session failed")
            with suppress(Exception):
                await ws.send_json({"type": "error", "message": "模型识别失败，请检查 Mac 服务日志。"})
        finally:
            for task in tasks:
                task.cancel()
            try:
                if tasks:
                    await asyncio.gather(*tasks, return_exceptions=True)
            finally:
                if acquired:
                    busy.release()
            with suppress(Exception):
                await ws.close()

    return app


def main():
    import uvicorn
    parser = argparse.ArgumentParser(description="Shuo ASR for Apple Silicon")
    parser.add_argument("--model", default="mlx-community/Qwen3-ASR-1.7B-8bit")
    parser.add_argument("--model-name", default="Qwen3-ASR-1.7B-8bit")
    parser.add_argument("--small-model", help="Also preload a Qwen3-ASR-0.6B-8bit model directory or repository")
    parser.add_argument("--max-segment-seconds", type=float, default=30.0, help="Soft audio duration; prefer a pause after this point (default: 30)")
    parser.add_argument("--hard-segment-seconds", type=float, default=None, help="Hard duration limit (default: soft limit + 5 seconds)")
    parser.add_argument("--silence-seconds", type=float, default=1.0, help="Normal endpoint silence (default: 1; short speech waits at least 2)")
    parser.add_argument("--preview-seconds", type=float, default=1.0, help="Interval between partial transcriptions (default: 1)")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=18765)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO)
    models = {"Qwen3-ASR-0.6B-8bit": Recognizer(args.small_model)} if args.small_model else {}
    app = create_app(Recognizer(args.model), model_name=args.model_name, models=models,
                     max_seconds=args.max_segment_seconds, hard_seconds=args.hard_segment_seconds, silence_seconds=args.silence_seconds,
                     preview_seconds=args.preview_seconds)
    uvicorn.run(app, host=args.host, port=args.port, ws_max_size=32000,
                ws_max_queue=32, ws_ping_interval=15, ws_ping_timeout=15,
                timeout_graceful_shutdown=30)


if __name__ == "__main__":
    main()
