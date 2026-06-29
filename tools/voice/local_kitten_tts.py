#!/usr/bin/env python3
"""Local KittenTTS bridge for Ali.

Reads speakable text from stdin and writes a WAV file. The Python environment
must already have KittenTTS and numpy available.
"""

from __future__ import annotations

import argparse
import inspect
import os
import sys
import wave
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run local KittenTTS for Ali.")
    parser.add_argument("--model", required=True, help="Local KittenTTS model path.")
    parser.add_argument("--voice", required=True, help="KittenTTS voice id.")
    parser.add_argument("--output", required=True, help="Output WAV path.")
    parser.add_argument("--rate", type=float, default=1.0, help="Speech speed multiplier.")
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


def first_file(root: Path, pattern: str) -> Path | None:
    if root.is_file() and root.match(pattern):
        return root
    if not root.is_dir():
        return None
    return next(root.rglob(pattern), None)


def load_kitten_model(kitten_tts, model_path: Path):
    signature = inspect.signature(kitten_tts)
    parameters = signature.parameters
    onnx_path = first_file(model_path, "*.onnx")
    voices_path = first_file(model_path, "voices.npz")
    attempts = []

    if "cache_dir" in parameters and model_path.is_dir():
        attempts.append(lambda: kitten_tts(cache_dir=str(model_path)))

    if "model_path" in parameters and onnx_path is not None:
        if "voices_path" in parameters and voices_path is not None:
            attempts.append(lambda: kitten_tts(model_path=str(onnx_path), voices_path=str(voices_path)))
        attempts.append(lambda: kitten_tts(model_path=str(onnx_path)))

    if "model" in parameters and onnx_path is not None:
        attempts.append(lambda: kitten_tts(model=str(onnx_path)))

    if onnx_path is not None:
        attempts.append(lambda: kitten_tts(str(onnx_path)))

    attempts.append(lambda: kitten_tts())

    errors = []
    for attempt in attempts:
        try:
            return attempt()
        except TypeError as exc:
            errors.append(str(exc))

    joined = "; ".join(errors[-3:])
    raise TypeError(f"No compatible KittenTTS constructor worked for {model_path}. {joined}")


def main() -> int:
    args = parse_args()
    text = sys.stdin.read().strip()
    if not text:
        print("No speakable text was provided.", file=sys.stderr)
        return 2

    try:
        try:
            import espeakng_loader

            espeakng_loader.make_library_available()
            os.environ.setdefault("PHONEMIZER_ESPEAK_LIBRARY", espeakng_loader.get_library_path())
            os.environ.setdefault("ESPEAK_DATA_PATH", espeakng_loader.get_data_path())
        except Exception:
            # If the bundled loader is unavailable, phonemizer will fall back to
            # a system eSpeak installation and report a clear failure if needed.
            pass

        from kittentts import KittenTTS

        model_path = Path(args.model)
        model = load_kitten_model(KittenTTS, model_path)

        speed = max(0.75, min(args.rate, 1.6))
        audio = model.generate(text, voice=args.voice, speed=speed)
        write_wav(Path(args.output), audio, args.sample_rate)
        return 0
    except Exception as exc:
        print(f"KittenTTS failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
