import argparse
import json
from pathlib import Path

from faster_whisper import WhisperModel


def main() -> int:
    parser = argparse.ArgumentParser(description="Ali local Faster-Whisper STT wrapper.")
    parser.add_argument("--audio", required=True)
    parser.add_argument("--model-root", required=True)
    parser.add_argument("--model-id", default="small.en")
    parser.add_argument("--output-base", required=True)
    parser.add_argument("--vad-filter", action="store_true")
    parser.add_argument("--min-silence-duration-ms", type=int, default=500)
    parser.add_argument("--max-no-speech-prob", type=float, default=0.60)
    parser.add_argument("--min-avg-logprob", type=float, default=-1.20)
    args = parser.parse_args()

    audio_path = Path(args.audio)
    model_root = Path(args.model_root)
    output_path = Path(args.output_base + ".txt")
    metadata_path = Path(args.output_base + ".json")

    if not audio_path.exists():
        raise FileNotFoundError(f"Audio file not found: {audio_path}")
    if not model_root.exists():
        raise FileNotFoundError(f"Whisper model root not found: {model_root}")

    model = WhisperModel(
        args.model_id,
        device="cpu",
        compute_type="int8",
        cpu_threads=8,
        download_root=str(model_root),
        local_files_only=True,
    )
    segments, _info = model.transcribe(
        str(audio_path),
        language="en",
        beam_size=3,
        vad_filter=args.vad_filter,
        vad_parameters={"min_silence_duration_ms": args.min_silence_duration_ms},
        condition_on_previous_text=False,
    )
    accepted_segments = []
    rejected_segments = []

    for segment in segments:
        segment_text = segment.text.strip()
        segment_meta = {
            "text": segment_text,
            "start": segment.start,
            "end": segment.end,
            "avg_logprob": segment.avg_logprob,
            "no_speech_prob": segment.no_speech_prob,
            "compression_ratio": segment.compression_ratio,
        }

        if (
            segment_text
            and segment.no_speech_prob <= args.max_no_speech_prob
            and segment.avg_logprob >= args.min_avg_logprob
        ):
            accepted_segments.append(segment_meta)
        else:
            rejected_segments.append(segment_meta)

    text = " ".join(segment["text"] for segment in accepted_segments).strip()
    output_path.write_text(text, encoding="utf-8")
    metadata_path.write_text(
        json.dumps(
            {
                "model_id": args.model_id,
                "vad_filter": args.vad_filter,
                "max_no_speech_prob": args.max_no_speech_prob,
                "min_avg_logprob": args.min_avg_logprob,
                "accepted_segments": accepted_segments,
                "rejected_segments": rejected_segments,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
