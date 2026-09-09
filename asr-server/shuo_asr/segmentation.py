"""Bounded PCM segmentation, independent of the inference backend."""
from collections import deque
from dataclasses import dataclass
from typing import Callable
import re

SAMPLE_RATE = 16000
FRAME_BYTES = 640  # 20 ms, signed 16-bit mono PCM


@dataclass(frozen=True)
class AudioJob:
    kind: str
    segment: int
    audio: bytes = b""


class Segmenter:
    def __init__(self, is_speech: Callable[[bytes], bool], *, preview_seconds=1.0,
                 silence_seconds=0.6, max_seconds=20.0):
        self.is_speech = is_speech
        self.preview_frames = max(1, round(preview_seconds / .02))
        self.silence_frames = max(1, round(silence_seconds / .02))
        self.max_frames = max(1, round(max_seconds / .02))
        self.pending = b""
        self.preroll = deque(maxlen=10)
        self.frames = []
        self.voiced = 0
        self.silence = 0
        self.last_preview = 0
        self.segment = 0

    def feed(self, audio: bytes) -> list[AudioJob]:
        if len(audio) % 2:
            raise ValueError("PCM byte count must be even")
        data = self.pending + audio
        stop = len(data) // FRAME_BYTES * FRAME_BYTES
        self.pending = data[stop:]
        jobs = []
        for start in range(0, stop, FRAME_BYTES):
            jobs.extend(self._frame(data[start:start + FRAME_BYTES]))
        return jobs

    def _frame(self, frame: bytes) -> list[AudioJob]:
        # Zero PCM must never invoke the recognizer, even if a VAD has hangover.
        speech = any(frame) and self.is_speech(frame)
        if not self.frames:
            self.preroll.append(frame)
            if not speech:
                return []
            self.frames = list(self.preroll)
            self.preroll.clear()
        else:
            self.frames.append(frame)
        if speech:
            self.voiced += 1
            self.silence = 0
        else:
            self.silence += 1
        if self.silence >= self.silence_frames or len(self.frames) >= self.max_frames:
            return self._commit()
        if self.voiced >= 6 and len(self.frames) - self.last_preview >= self.preview_frames:
            self.last_preview = len(self.frames)
            return [AudioJob("preview", self.segment, b"".join(self.frames))]
        return []

    def _commit(self) -> list[AudioJob]:
        # Keep 200 ms of trailing silence; never remove an internal pause.
        trim = max(0, self.silence - 10)
        audio = b"".join(self.frames[:-trim] if trim else self.frames)
        jobs = [AudioJob("segment", self.segment, audio)] if self.voiced >= 6 else []
        self.preroll.clear()
        # Only silent frames can be carried into the next segment: voiced audio
        # must not be decoded twice when the hard duration cap is reached.
        if self.silence:
            self.preroll.extend(self.frames[-min(self.silence, 10):])
        self.frames = []
        self.voiced = self.silence = self.last_preview = 0
        if jobs:
            self.segment += 1
        return jobs

    def finish(self) -> list[AudioJob]:
        jobs = []
        if self.pending:
            tail = self.pending
            self.pending = b""
            # Padding is only for the VAD's required frame width (under 20 ms).
            jobs.extend(self._frame(tail.ljust(FRAME_BYTES, b"\0")))
        if self.frames:
            jobs.extend(self._commit())
        jobs.append(AudioJob("finish", self.segment))
        return jobs


class PendingJobs:
    """Keep committed segments in order, replacing only obsolete previews."""
    def __init__(self, max_segments=3):
        self.items = deque()
        self.max_segments = max_segments

    def put(self, job: AudioJob):
        if job.kind in ("preview", "segment"):
            self.items = deque(item for item in self.items
                               if not (item.kind == "preview" and item.segment == job.segment))
        if job.kind == "segment" and sum(item.kind == "segment" for item in self.items) >= self.max_segments:
            raise ValueError("识别积压过多，请缩短听写或检查 Mac 的负载。")
        self.items.append(job)

    def pop(self):
        return self.items.popleft()

    def __bool__(self):
        return bool(self.items)


def join_text(left: str, right: str) -> str:
    if not left or not right:
        return left or right
    separator = " " if re.search(r"[A-Za-z0-9.!?]$", left) and re.match(r"[A-Za-z0-9]", right) else ""
    return left + separator + right
