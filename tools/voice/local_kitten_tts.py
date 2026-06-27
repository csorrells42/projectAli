#!/usr/bin/env python3
"""Local KittenTTS bridge for Ali.

Reads speakable text from stdin and writes a WAV file. The Python environment
must already have KittenTTS and numpy available.
"""

from __future__ import annotations

import argparse
import sys
import wave
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run local KittenTTS for Ali.")
    parser.add_argument("--model", required=True, help="Local KittenTTS model path.")
    parser.add_argument("--voice", required=True, help="KittenTTS voice id.")
    parser.add_argument("--output", required=True, help="Output WAV path.")
    parser.add_argument("--rate", default="1.0", help="Reserved for future rate control.")
    parser.add_argument("--sample-rate", type=int, default=24000, help="Output sample rate.")
    return parser.parse_args()


def write_wav(path: Path, samples, sample_rate: int) -> None:
    import numpy as np

    audio = np.asarray(samples)
    if audio.ndim > 1:
        audio = audio.reshape(-1)

    if audio.dtype != np.int16:
        audio = np.clip(audio, -1.0, 1.0)
        audio = (audio * 32767).astype(np.int16)

    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(audio.tobytes())


def main() -> int:
    args = parse_args()
    text = sys.stdin.read().strip()
    if not text:
        print("No speakable text was provided.", file=sys.stderr)
        return 2

    try:
        from kittentts import KittenTTS

        model_path = Path(args.model)
        model = KittenTTS(cache_dir=str(model_path)) if model_path.is_dir() else KittenTTS(args.model)
        audio = model.generate(text, voice=args.voice)
        write_wav(Path(args.output), audio, args.sample_rate)
        return 0
    except Exception as exc:
        print(f"KittenTTS failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
