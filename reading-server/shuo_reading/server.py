"""Private WebSocket reading service. Text and audio are never logged or saved."""
import argparse
import asyncio
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager, suppress
import ipaddress
import logging
from threading import Event

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from .engine import MAX_PASSAGE_BYTES, SAMPLE_RATE, SPEEDS, VOICE, Reader

logger = logging.getLogger("shuo_reading")


def validate_start(config):
    if not isinstance(config, dict) or config.get("type") != "start" or config.get("protocol") != 1:
        raise ValueError("译读协议不兼容，请更新 Shuo。")
    text = config.get("text")
    if not isinstance(text, str) or not text.strip() or len(text.encode("utf-8")) > MAX_PASSAGE_BYTES:
        raise ValueError("本段文字为空或过长，请更新 Shuo 或缩短选文。")
    speed = config.get("speed", 1.0)
    if isinstance(speed, bool) or speed not in SPEEDS:
        raise ValueError("不支持此播放速度。")
    return text, speed


def create_app(reader):
    pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="mlx-reading")
    busy = asyncio.Lock()

    @asynccontextmanager
    async def lifespan(app):
        try:
            await asyncio.get_running_loop().run_in_executor(pool, reader.load)
            yield
        finally:
            pool.shutdown(wait=True, cancel_futures=True)

    app = FastAPI(lifespan=lifespan)

    @app.get("/health")
    async def health():
        return dict(ready=True, protocol=1, voice=VOICE, sample_rate=SAMPLE_RATE,
                    format="pcm_s16le", busy=busy.locked(), speeds=SPEEDS)

    @app.websocket("/v1/reading")
    async def reading(ws: WebSocket):
        # Native clients send no Origin. Reject browser cross-site requests to this private service.
        if ws.headers.get("origin"):
            await ws.close(code=1008)
            return
        await ws.accept()
        acquired = False
        stopped = Event()
        iterator = None
        pending = None
        watcher = None
        acknowledged = asyncio.Queue(maxsize=1)
        loop = asyncio.get_running_loop()

        async def controls():
            try:
                while True:
                    control = await ws.receive_json()
                    if control == {"type": "cancel"}:
                        return
                    if control != {"type": "ack"} or acknowledged.full():
                        raise ValueError("无效的译读播放确认。")
                    acknowledged.put_nowait(True)
            except WebSocketDisconnect:
                pass
            finally:
                stopped.set()
        try:
            if busy.locked():
                await ws.send_json(dict(type="error", message="Mac 正在处理另一段译读，请稍后重试。"))
                return
            await busy.acquire()
            acquired = True
            text, speed = validate_start(await asyncio.wait_for(ws.receive_json(), 10))
            watcher = asyncio.create_task(controls())
            await ws.send_json(dict(type="ready", protocol=1, sample_rate=SAMPLE_RATE, format="pcm_s16le", voice=VOICE))
            iterator = reader.events(text, speed, stopped)
            while True:
                pending = loop.run_in_executor(pool, next, iterator, None)
                event = await asyncio.shield(pending)
                pending = None
                if stopped.is_set():
                    watcher.result()
                    return
                if event is None:
                    await ws.send_json(dict(type="done"))
                    return
                kind, data = event
                if kind == "text":
                    await ws.send_json(dict(type="text", text=data))
                elif kind == "audio":
                    await ws.send_bytes(data)
                    # The app acknowledges after accepting this chunk into its bounded player.
                    # A paused full player holds this await, so inference cannot run ahead.
                    ack = asyncio.create_task(acknowledged.get())
                    try:
                        await asyncio.wait((ack, watcher), return_when=asyncio.FIRST_COMPLETED)
                        if watcher.done():
                            watcher.result()
                            return
                    finally:
                        ack.cancel()
                        with suppress(asyncio.CancelledError):
                            await ack
                else:
                    raise RuntimeError("Unexpected inference event")
        except (WebSocketDisconnect, OSError):
            pass
        except (ValueError, TimeoutError) as error:
            with suppress(Exception):
                message = "译读请求等待超时，请重试。" if isinstance(error, TimeoutError) else str(error)
                await ws.send_json(dict(type="error", message=message))
        except Exception as error:
            # Exceptions from model libraries may contain input; log only the exception class.
            logger.error("Reading failed: %s", type(error).__name__)
            with suppress(Exception):
                await ws.send_json(dict(type="error", message="Mac 译读失败，请重试或检查服务日志。"))
        finally:
            stopped.set()
            if watcher is not None:
                watcher.cancel()
                with suppress(Exception, asyncio.CancelledError):
                    await watcher
            if pending is not None:
                with suppress(Exception):
                    await asyncio.shield(pending)
            if iterator is not None:
                with suppress(Exception):
                    await loop.run_in_executor(pool, iterator.close)
            if acquired:
                busy.release()
            with suppress(Exception):
                await ws.close()

    return app


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=18766)
    args = parser.parse_args()
    address = ipaddress.ip_address(args.host)
    if not (address.is_loopback or address in ipaddress.ip_network("100.64.0.0/10")):
        parser.error("Bind to loopback or the Mac's Tailscale IPv4 address.")
    import uvicorn
    logging.basicConfig(level=logging.INFO)
    uvicorn.run(create_app(Reader()), host=args.host, port=args.port, access_log=False,
                ws_max_size=16384, ws_max_queue=2)


if __name__ == "__main__":
    main()
