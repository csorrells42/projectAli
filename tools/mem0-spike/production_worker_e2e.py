"""End-to-end verification of Ali's shipped Mem0 stdio worker."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from mem0_compatibility_spike import (
    COLLECTION,
    EMBEDDING_DIMENSIONS,
    EMBEDDING_MODEL,
    LEMONADE_ENDPOINT,
    LLM_MODEL,
    free_port,
    prepare_lemonade,
    start_qdrant,
    stop_owned_process,
)


def start_worker(args, data_root: Path, qdrant_port: int):
    environment = os.environ.copy()
    environment.update(
        {
            "MEM0_TELEMETRY": "false",
            "POSTHOG_DISABLED": "true",
            "NO_PROXY": "127.0.0.1,localhost",
            "HTTP_PROXY": "http://127.0.0.1:1",
            "HTTPS_PROXY": "http://127.0.0.1:1",
        }
    )
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    return subprocess.Popen(
        [
            str(Path(args.python).resolve()),
            str(Path(args.worker).resolve()),
            "--data-root",
            str(data_root),
            "--collection",
            f"{COLLECTION}_production",
            "--llm-endpoint",
            LEMONADE_ENDPOINT,
            "--llm-model",
            LLM_MODEL,
            "--embedding-endpoint",
            LEMONADE_ENDPOINT,
            "--embedding-model",
            EMBEDDING_MODEL,
            "--embedding-dimensions",
            str(EMBEDDING_DIMENSIONS),
            "--qdrant-host",
            "127.0.0.1",
            "--qdrant-port",
            str(qdrant_port),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        env=environment,
        creationflags=flags,
    )


def stop_worker(process: subprocess.Popen | None) -> str:
    if process is None:
        return ""
    if process.stdin and not process.stdin.closed:
        process.stdin.close()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
    error = process.stderr.read() if process.stderr else ""
    return error[-4000:]


def send(process: subprocess.Popen, operation: str, user: dict, **payload) -> dict:
    request_id = uuid.uuid4().hex
    request = {"id": request_id, "operation": operation, "user": user, **payload}
    assert process.stdin and process.stdout
    process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
    process.stdin.flush()
    line = process.stdout.readline()
    if not line:
        diagnostic = process.stderr.read() if process.stderr else ""
        raise RuntimeError(f"Worker exited without a response: {diagnostic[-4000:]}")
    response = json.loads(line)
    if response.get("id") != request_id:
        raise RuntimeError("Worker response ID mismatch")
    return response


def texts(response: dict) -> list[str]:
    return [str(item.get("text", "")) for item in response.get("memories", [])]


def run(args) -> dict:
    root = Path(args.data_root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    http_port = free_port()
    grpc_port = free_port()
    while grpc_port == http_port:
        grpc_port = free_port()
    qdrant = None
    worker = None
    checks: dict[str, bool] = {}
    diagnostics: list[str] = []
    alice = {
        "stableId": "profile_e2e_alice",
        "displayName": "Alice",
        "isTestProfile": False,
        "resolutionMethod": "explicit-selection",
    }
    bob = {**alice, "stableId": "profile_e2e_bob", "displayName": "Bob"}
    try:
        prepare_lemonade()
        qdrant = start_qdrant(Path(args.qdrant).resolve(), root / "qdrant", http_port, grpc_port)
        worker = start_worker(args, root / "memory", http_port)

        health = send(worker, "health", alice)
        checks["health"] = bool(health.get("success"))
        remembered = send(
            worker,
            "remember",
            alice,
            conversation="Remember that my neighbor is Bill.",
            source="explicit_user_request",
            category="people_relationships",
        )
        checks["explicit_remember"] = bool(remembered.get("success")) and any("bill" in text.lower() for text in texts(remembered))
        recalled = send(worker, "recall", alice, query="Who lives next door to me?", maximumResults=3)
        isolated = send(worker, "recall", bob, query="Who lives next door to me?", maximumResults=3)
        checks["paraphrase_recall"] = any("bill" in text.lower() for text in texts(recalled))
        checks["strict_user_isolation"] = not any("bill" in text.lower() for text in texts(isolated))

        corrected = send(worker, "correct", alice, correction="The user's neighbor is William (Bill).")
        checks["correction"] = bool(corrected.get("success")) and any("william" in text.lower() for text in texts(corrected))

        before_filler = send(worker, "list", alice).get("count")
        filler = send(
            worker,
            "remember",
            alice,
            conversation="User: Hello, how are you today?\nAssistant: I'm doing well, thank you.",
            source="conversation",
            category="conversation",
        )
        after_filler = send(worker, "list", alice).get("count")
        checks["filler_not_stored"] = not filler.get("memories") and before_filler == after_filler

        send(
            worker,
            "remember",
            alice,
            conversation="Remember that I prefer concise answers.",
            source="explicit_user_request",
            category="preferences",
        )
        rejected = send(
            worker,
            "list",
            {**alice, "resolutionMethod": "identity-profile-selection-required"},
        )
        checks["uncertain_identity_rejected"] = not rejected.get("success") and rejected.get("errorCode") == "permission_denied"

        diagnostics.append(stop_worker(worker))
        worker = None
        stop_owned_process(qdrant)
        qdrant = None
        time.sleep(0.5)

        qdrant = start_qdrant(Path(args.qdrant).resolve(), root / "qdrant", http_port, grpc_port)
        worker = start_worker(args, root / "memory", http_port)
        persisted = send(worker, "recall", alice, query="How should you answer me?", maximumResults=3)
        checks["restart_persistence"] = any("concise" in text.lower() for text in texts(persisted))
        checks["bounded_recall"] = len(texts(persisted)) <= 3

        forgotten = send(worker, "forget", alice, request="my neighbor")
        after_forget = send(worker, "recall", alice, query="Who is my neighbor?", maximumResults=3)
        checks["forget"] = bool(forgotten.get("success")) and not any(
            "bill" in text.lower() or "william" in text.lower() for text in texts(after_forget)
        )
    finally:
        diagnostics.append(stop_worker(worker))
        stop_owned_process(qdrant)

    report = {
        "passed": all(checks.values()),
        "checks": checks,
        "diagnostics": [value for value in diagnostics if value.strip()],
        "qdrant_port": http_port,
    }
    output = Path(args.report).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--python", required=True)
    parser.add_argument("--worker", required=True)
    parser.add_argument("--qdrant", required=True)
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--report", required=True)
    return 0 if run(parser.parse_args())["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
