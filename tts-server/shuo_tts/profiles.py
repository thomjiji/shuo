import argparse
import json
import math
import re
from dataclasses import dataclass
from pathlib import Path

VOICE_ID = re.compile(r"^[a-z0-9][a-z0-9_-]{0,31}$")


@dataclass(frozen=True)
class VoiceProfile:
    id: str
    name: str
    prompt_text: str
    prompt_wav: Path
    duration_seconds: float
    cross_lingual: bool


def validate_voice_id(value: str) -> str:
    if not VOICE_ID.fullmatch(value):
        raise ValueError("音色 ID 只能包含小写字母、数字、连字符和下划线，且不超过 32 个字符。")
    return value


def load_profiles(voices_dir: Path) -> dict[str, VoiceProfile]:
    profiles = {}
    if not voices_dir.exists():
        return profiles
    for directory in sorted(path for path in voices_dir.iterdir() if path.is_dir()):
        voice_id = validate_voice_id(directory.name)
        manifest_path = directory / "voice.json"
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            name = manifest["name"].strip()
            prompt_text = manifest["prompt_text"].strip()
            duration = float(manifest["duration_seconds"])
            cross_lingual = manifest.get("cross_lingual", False)
        except (FileNotFoundError, KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
            raise ValueError(f"无法读取音色 {voice_id} 的 voice.json。") from error
        prompt_wav = directory / "prompt.wav"
        if (not name or not prompt_text or not prompt_wav.is_file()
                or not math.isfinite(duration) or not isinstance(cross_lingual, bool)):
            raise ValueError(f"音色 {voice_id} 的资料不完整。")
        profiles[voice_id] = VoiceProfile(
            voice_id, name, prompt_text, prompt_wav, duration, cross_lingual)
    return profiles


def register_voice(voices_dir: Path, voice_id: str, name: str, audio_path: Path,
                   prompt_text: str, *, consent: bool, cross_lingual: bool = False) -> VoiceProfile:
    import numpy as np
    import soundfile as sf
    from scipy.signal import resample_poly

    voice_id = validate_voice_id(voice_id)
    name = name.strip()
    prompt_text = prompt_text.strip()
    if not consent:
        raise ValueError("只有得到声音所有者明确许可后才能创建克隆音色。")
    if not name:
        raise ValueError("请填写音色名称。")
    if not prompt_text or len(prompt_text) > 500:
        raise ValueError("参考音频逐字稿必须为 1 到 500 个字符。")
    if not audio_path.is_file():
        raise ValueError("找不到参考音频。")
    target = voices_dir / voice_id
    if target.exists():
        raise ValueError(f"音色 {voice_id} 已存在；请先选择新的 ID。")

    samples, sample_rate = sf.read(audio_path, dtype="float32", always_2d=True)
    duration = samples.shape[0] / sample_rate
    if sample_rate < 16000:
        raise ValueError("参考音频采样率必须至少为 16 kHz。")
    if duration < 3 or duration > 30:
        raise ValueError("参考音频时长必须在 3 到 30 秒之间，建议使用 3 到 10 秒。")
    mono = samples.mean(axis=1)
    if not np.isfinite(mono).all() or float(np.max(np.abs(mono))) < 0.01:
        raise ValueError("参考音频无声或包含无效采样。")
    if sample_rate != 24000:
        divisor = math.gcd(sample_rate, 24000)
        mono = resample_poly(mono, 24000 // divisor, sample_rate // divisor)

    voices_dir.mkdir(parents=True, exist_ok=True)
    target.mkdir()
    sf.write(target / "prompt.wav", mono, 24000, subtype="PCM_16")
    manifest = {
        "name": name,
        "prompt_text": prompt_text,
        "duration_seconds": round(duration, 3),
        "cross_lingual": cross_lingual,
    }
    (target / "voice.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return load_profiles(voices_dir)[voice_id]


def main():
    parser = argparse.ArgumentParser(description="Register a consented CosyVoice reference voice")
    parser.add_argument("--voices-dir", type=Path, default=Path("voices"))
    parser.add_argument("--id", required=True, dest="voice_id")
    parser.add_argument("--name", required=True)
    parser.add_argument("--audio", required=True, type=Path)
    parser.add_argument("--text", required=True, dest="prompt_text")
    parser.add_argument(
        "--cross-lingual",
        action="store_true",
        help="Use when the reference and generated speech use different languages",
    )
    parser.add_argument("--confirm-consent", action="store_true")
    args = parser.parse_args()
    try:
        profile = register_voice(args.voices_dir, args.voice_id, args.name, args.audio,
                                 args.prompt_text, consent=args.confirm_consent,
                                 cross_lingual=args.cross_lingual)
    except ValueError as error:
        parser.error(str(error))
    print(f"Registered {profile.id}: {profile.name} ({profile.duration_seconds:.1f}s)")


if __name__ == "__main__":
    main()
