import argparse
import json
import sys

from faster_whisper import WhisperModel


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Persistent local Faster-Whisper worker."
    )
    parser.add_argument("--model-root", required=True)
    parser.add_argument("--model-id", default="small.en")
    args = parser.parse_args()

    model = WhisperModel(
        args.model_id,
        device="cpu",
        compute_type="int8",
        cpu_threads=8,
        download_root=args.model_root,
        local_files_only=True,
    )

    for line in sys.stdin:
        request = {}
        try:
            request = json.loads(line)
            request_id = int(request["id"])
            segments, _info = model.transcribe(
                request["audio"],
                language="en",
                beam_size=3,
                vad_filter=True,
                vad_parameters={"min_silence_duration_ms": 500},
                condition_on_previous_text=False,
            )
            accepted = []
            for segment in segments:
                text = segment.text.strip()
                if (
                    text
                    and segment.no_speech_prob <= 0.60
                    and segment.avg_logprob >= -1.20
                ):
                    accepted.append(text)
            response = {
                "id": request_id,
                "ok": True,
                "text": " ".join(accepted).strip(),
                "error": "",
            }
        except Exception as error:
            response = {
                "id": request.get("id", -1)
                if isinstance(request, dict)
                else -1,
                "ok": False,
                "text": "",
                "error": str(error),
            }
        print(json.dumps(response, ensure_ascii=False), flush=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
