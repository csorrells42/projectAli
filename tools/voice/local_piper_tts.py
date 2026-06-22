import argparse
import sys
import wave
from pathlib import Path

from piper import PiperVoice
from piper.config import SynthesisConfig


def main() -> int:
    parser = argparse.ArgumentParser(description="Ali local Piper TTS wrapper.")
    parser.add_argument("--model", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--rate", type=float, default=1.0)
    args = parser.parse_args()

    model_path = Path(args.model)
    output_path = Path(args.output)
    if not model_path.exists():
        raise FileNotFoundError(f"Piper voice model not found: {model_path}")

    text = sys.stdin.read().strip()
    if not text:
        raise ValueError("No text was provided on stdin.")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    voice = PiperVoice.load(model_path)
    length_scale = 1.0 / max(0.75, min(args.rate, 1.5))
    with wave.open(str(output_path), "wb") as wav_file:
        voice.synthesize_wav(
            text,
            wav_file,
            syn_config=SynthesisConfig(length_scale=length_scale),
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
