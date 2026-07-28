"""Pinned, local-only Mem0/Lemonade/Qdrant compatibility proof for Project Ali."""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.request import Request, urlopen


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPOSITORY_ROOT / "artifacts" / "mem0-spike" / "packages"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

os.environ["MEM0_TELEMETRY"] = "false"
os.environ["POSTHOG_DISABLED"] = "true"
os.environ["OPENAI_API_KEY"] = "ali-local-only"
os.environ["OPENAI_BASE_URL"] = "http://127.0.0.1:13305/api/v1"
os.environ["NO_PROXY"] = "127.0.0.1,localhost"
os.environ["HTTP_PROXY"] = "http://127.0.0.1:1"
os.environ["HTTPS_PROXY"] = "http://127.0.0.1:1"

from mem0 import Memory  # noqa: E402
from mem0.configs.llms.openai import OpenAIConfig  # noqa: E402
from mem0.utils.factory import LlmFactory, VectorStoreFactory  # noqa: E402

from ali_mem0.lemonade_llm import LemonadeLLM, assert_local_configuration  # noqa: E402,F401


MEM0_VERSION = "2.0.12"
QDRANT_VERSION = "1.18.2"
LEMONADE_ENDPOINT = "http://127.0.0.1:13305/api/v1"
LLM_MODEL = "gpt-oss-20b-mxfp4-GGUF"
EMBEDDING_MODEL = "nomic-embed-text-v1-GGUF"
EMBEDDING_DIMENSIONS = 768
COLLECTION = "ali_user_memories_spike"
LEMONADE_CONTEXT_TOKENS = 8192

EXTRACTION_INSTRUCTIONS = """
Extract durable personal memories only. Return strict JSON facts.
Keep people and relationships, preferences, important dates and places,
explicitly taught facts, procedures, stories, events, corrections, and
accessibility or communication preferences. Ignore greetings, filler,
temporary questions, copied web results, and assistant guesses.
Never infer private or consequential facts that the user did not state.
Input: Hello, how are you? Output: {"facts": []}
Input: Remember that my neighbor is Bill. Output: {"facts": ["The user's neighbor is Bill"]}
""".strip()


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def wait_for_http(url: str, process: subprocess.Popen, timeout: float = 20.0) -> None:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"Qdrant exited early with code {process.returncode}")
        try:
            with urlopen(url, timeout=1.0) as response:
                if response.status == 200:
                    return
        except Exception as error:  # noqa: BLE001 - report final connection error
            last_error = error
        time.sleep(0.2)
    raise TimeoutError(f"Qdrant did not become healthy: {last_error}")


def post_json(url: str, payload: dict, timeout: float = 300.0) -> dict:
    request = Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urlopen(request, timeout=timeout) as response:
        body = response.read().decode("utf-8")
        return json.loads(body) if body.strip() else {}


def prepare_lemonade() -> None:
    with urlopen(f"{LEMONADE_ENDPOINT}/health", timeout=10.0) as response:
        health = json.loads(response.read().decode("utf-8"))
    loaded = health.get("all_models_loaded") or []
    if LLM_MODEL in loaded or health.get("model_loaded") == LLM_MODEL:
        post_json(f"{LEMONADE_ENDPOINT}/unload", {"model_name": LLM_MODEL}, timeout=60.0)
    post_json(
        f"{LEMONADE_ENDPOINT}/load",
        {
            "model_name": LLM_MODEL,
            "ctx_size": LEMONADE_CONTEXT_TOKENS,
            "save_options": False,
        },
        timeout=300.0,
    )


def start_qdrant(executable: Path, data_root: Path, http_port: int, grpc_port: int):
    data_root.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    environment.update(
        {
            "QDRANT__SERVICE__HOST": "127.0.0.1",
            "QDRANT__SERVICE__HTTP_PORT": str(http_port),
            "QDRANT__SERVICE__GRPC_PORT": str(grpc_port),
            "QDRANT__STORAGE__STORAGE_PATH": str(data_root / "storage"),
            "QDRANT__STORAGE__SNAPSHOTS_PATH": str(data_root / "snapshots"),
            "QDRANT__STORAGE__ON_DISK_PAYLOAD": "false",
            "QDRANT__STORAGE__OPTIMIZERS__FLUSH_INTERVAL_SEC": "1",
            "QDRANT__TELEMETRY_DISABLED": "true",
        }
    )
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    log_stream = (data_root / "qdrant.log").open("ab")
    process = subprocess.Popen(
        [str(executable)],
        cwd=str(data_root),
        env=environment,
        stdout=log_stream,
        stderr=subprocess.STDOUT,
        creationflags=flags,
    )
    process._ali_log_stream = log_stream  # type: ignore[attr-defined]
    try:
        wait_for_http(f"http://127.0.0.1:{http_port}/healthz", process)
    except Exception as error:
        log_stream.flush()
        diagnostic = (data_root / "qdrant.log").read_text(encoding="utf-8", errors="replace")[-4000:]
        stop_owned_process(process)
        raise RuntimeError(f"{error}\nQdrant log:\n{diagnostic}") from error
    return process


def stop_owned_process(process: subprocess.Popen | None) -> None:
    if process is None:
        return
    if process.poll() is None:
        time.sleep(3.0)
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    log_stream = getattr(process, "_ali_log_stream", None)
    if log_stream is not None and not log_stream.closed:
        log_stream.close()


def config_for(root: Path, qdrant_port: int) -> dict:
    config = {
        "version": "v1.1",
        "history_db_path": str(root / "history.db"),
        "custom_instructions": EXTRACTION_INSTRUCTIONS,
        "llm": {
            "provider": "openai",
            "config": {
                "model": LLM_MODEL,
                "api_key": "ali-local-only",
                "openai_base_url": LEMONADE_ENDPOINT,
                "temperature": 0.1,
                "max_tokens": 512,
                "reasoning_effort": "low",
                "is_reasoning_model": True,
            },
        },
        "embedder": {
            "provider": "openai",
            "config": {
                "model": EMBEDDING_MODEL,
                "api_key": "ali-local-only",
                "openai_base_url": LEMONADE_ENDPOINT,
            },
        },
        "vector_store": {
            "provider": "qdrant",
            "config": {
                "collection_name": COLLECTION,
                "embedding_model_dims": EMBEDDING_DIMENSIONS,
                "host": "127.0.0.1",
                "port": qdrant_port,
                "path": None,
                "on_disk": True,
            },
        },
    }
    assert_local_configuration(config)
    return config


def results(value: dict) -> list[dict]:
    return list(value.get("results", []))


def contains(items: list[dict], text: str) -> bool:
    return any(text.lower() in str(item.get("memory", "")).lower() for item in items)


def run(args: argparse.Namespace) -> dict:
    executable = Path(args.qdrant).resolve()
    root = Path(args.data_root).resolve()
    if not executable.is_file():
        raise FileNotFoundError(executable)

    # Mem0's validated config keeps the standard OpenAI provider name. Only the
    # process-local factory mapping is replaced, leaving the pinned package untouched.
    LlmFactory.provider_to_class["openai"] = (
        "ali_mem0.lemonade_llm.LemonadeLLM",
        OpenAIConfig,
    )
    VectorStoreFactory.provider_to_class["qdrant"] = "ali_mem0.local_qdrant.LocalQdrant"
    http_port = free_port()
    grpc_port = free_port()
    while grpc_port == http_port:
        grpc_port = free_port()
    process = None
    checks: dict[str, bool] = {}
    details: dict[str, object] = {}
    try:
        prepare_lemonade()
        process = start_qdrant(executable, root / "qdrant", http_port, grpc_port)
        memory = Memory.from_config(config_for(root, http_port))
        user_a = "profile_spike_alice"
        user_b = "profile_spike_bob"

        added = memory.add(
            [
                {"role": "user", "content": "Remember that my neighbor is Bill."},
                {"role": "assistant", "content": "I'll remember that Bill is your neighbor."},
            ],
            user_id=user_a,
            metadata={"category": "people_relationships", "explicitly_taught": True},
        )
        added_items = results(added)
        checks["lemonade_inference_add"] = bool(added_items)
        search_a = results(memory.search("Who is my neighbor?", top_k=5, filters={"user_id": user_a}))
        search_b = results(memory.search("Who is my neighbor?", top_k=5, filters={"user_id": user_b}))
        checks["filtered_search_finds_user_a"] = contains(search_a, "Bill")
        checks["strict_user_isolation"] = not contains(search_b, "Bill")

        before_filler = len(results(memory.get_all(filters={"user_id": user_a}, top_k=100)))
        filler = memory.add(
            [
                {"role": "user", "content": "Hello, how are you today?"},
                {"role": "assistant", "content": "I'm doing well, thank you."},
            ],
            user_id=user_a,
            metadata={"category": "conversation", "explicitly_taught": False},
        )
        after_filler = len(results(memory.get_all(filters={"user_id": user_a}, top_k=100)))
        checks["filler_not_stored"] = not results(filler) and after_filler == before_filler

        memory_id = search_a[0]["id"]
        memory.update(memory_id, text="The user's neighbor is William (Bill).")
        updated = results(memory.search("What is my neighbor's name?", top_k=5, filters={"user_id": user_a}))
        checks["update"] = contains(updated, "William")

        raw = memory.add(
            "The user's temporary test color is ultraviolet.",
            user_id=user_a,
            metadata={"category": "test"},
            infer=False,
        )
        raw_id = results(raw)[0]["id"]
        memory.delete(raw_id)
        deleted = results(memory.search("temporary test color", top_k=5, filters={"user_id": user_a}))
        checks["delete"] = not contains(deleted, "ultraviolet")
        before_restart = results(memory.get_all(filters={"user_id": user_a}, top_k=20))
        checks["strict_get_all_filter"] = bool(before_restart) and all(
            item.get("user_id") == user_a for item in before_restart
        )
        details["memory_id"] = memory_id
        details["before_restart_count"] = len(before_restart)

        del memory
        stop_owned_process(process)
        process = None
        time.sleep(0.5)

        process = start_qdrant(executable, root / "qdrant", http_port, grpc_port)
        restarted = Memory.from_config(config_for(root, http_port))
        after_restart = results(
            restarted.search("Who is my neighbor?", top_k=5, filters={"user_id": user_a})
        )
        checks["restart_persistence"] = contains(after_restart, "William")
        checks["bounded_result_count"] = len(after_restart) <= 5
        details["after_restart_count"] = len(after_restart)
        details["after_restart_memories"] = [item.get("memory") for item in after_restart]
    finally:
        stop_owned_process(process)

    report = {
        "passed": all(checks.values()),
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "versions": {
            "mem0ai": MEM0_VERSION,
            "qdrant": QDRANT_VERSION,
            "python": sys.version.split()[0],
        },
        "endpoints": {
            "lemonade": LEMONADE_ENDPOINT,
            "qdrant": f"http://127.0.0.1:{http_port}",
        },
        "models": {
            "llm": LLM_MODEL,
            "embedding": EMBEDDING_MODEL,
            "embedding_dimensions": EMBEDDING_DIMENSIONS,
            "reasoning_effort": "low",
            "reasoning_mapping": "chat_template_kwargs.reasoning_effort",
            "context_tokens": LEMONADE_CONTEXT_TOKENS,
        },
        "local_only": {
            "mem0_telemetry": False,
            "loopback_enforced": True,
            "remote_http_proxy_blocked": True,
        },
        "checks": checks,
        "details": details,
    }
    output = Path(args.report).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--qdrant", required=True)
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    return 0 if run(args)["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
