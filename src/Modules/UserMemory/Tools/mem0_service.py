"""Local stdio Mem0 worker owned by Ali.

Every operation requires a stable active-user ID supplied by Ali's identity session.
The worker never listens on a network interface and rejects non-loopback providers.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


SCRIPT_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPT_ROOT))
os.environ["MEM0_TELEMETRY"] = "false"
os.environ["POSTHOG_DISABLED"] = "true"
os.environ["OPENAI_API_KEY"] = "ali-local-only"
os.environ["NO_PROXY"] = "127.0.0.1,localhost"
os.environ["HTTP_PROXY"] = "http://127.0.0.1:1"
os.environ["HTTPS_PROXY"] = "http://127.0.0.1:1"

from mem0 import Memory  # noqa: E402
from mem0.configs.llms.openai import OpenAIConfig  # noqa: E402
from mem0.utils.factory import LlmFactory, VectorStoreFactory  # noqa: E402


EXTRACTION_INSTRUCTIONS = """
Extract only durable personal information explicitly stated or taught by the user.
Allowed categories: people_relationships, preferences, dates_places, taught_facts,
procedures, stories_experiences, events, corrections, accessibility_communication.
Ignore greetings, filler, temporary questions, copied web results, raw documents,
large media, and unsupported assistant guesses. Do not infer sensitive, private, or
consequential facts. Prefer concise standalone facts. Return the exact JSON schema
requested by Mem0. "Remember that my neighbor is Bill" is durable; "hello" is not.
""".strip()


def require_loopback(value: str, name: str) -> str:
    normalized = value.rstrip("/")
    if not normalized.lower().startswith(("http://127.0.0.1:", "http://localhost:")):
        raise ValueError(f"{name} must be a loopback HTTP endpoint")
    return normalized


def parse_time(value):
    if not value:
        return None
    return value


def item(value: dict) -> dict:
    metadata = value.get("metadata") or {}
    score_details = value.get("score_details") or {}
    category = metadata.get("category") or (value.get("categories") or ["general"])[0]
    return {
        "memoryId": str(value.get("id", "")),
        "text": str(value.get("memory", "")),
        "category": str(category),
        "createdUtc": parse_time(value.get("created_at")),
        "updatedUtc": parse_time(value.get("updated_at")),
        "score": value.get("score"),
        "semanticScore": score_details.get("semantic_score"),
        "keywordScore": score_details.get("bm25_score"),
        "entityBoost": score_details.get("entity_boost"),
        "explicitlyTaught": bool(metadata.get("explicitly_taught", False)),
        "source": str(metadata.get("source", "mem0")),
    }


class Worker:
    def __init__(self, args):
        llm_endpoint = require_loopback(args.llm_endpoint, "LLM endpoint")
        embedding_endpoint = require_loopback(args.embedding_endpoint, "embedding endpoint")
        if args.qdrant_host not in {"127.0.0.1", "localhost", "::1"}:
            raise ValueError("Qdrant must use a loopback host")
        Path(args.data_root).mkdir(parents=True, exist_ok=True)
        LlmFactory.provider_to_class["openai"] = (
            "lemonade_llm.LemonadeLLM",
            OpenAIConfig,
        )
        VectorStoreFactory.provider_to_class["qdrant"] = "local_qdrant.LocalQdrant"
        self.memory = Memory.from_config(
            {
                "version": "v1.1",
                "history_db_path": str(Path(args.data_root) / "history.db"),
                "custom_instructions": EXTRACTION_INSTRUCTIONS,
                "llm": {
                    "provider": "openai",
                    "config": {
                        "model": args.llm_model,
                        "api_key": "ali-local-only",
                        "openai_base_url": llm_endpoint,
                        "temperature": 0.1,
                        "max_tokens": args.llm_output_tokens,
                    },
                },
                "embedder": {
                    "provider": "openai",
                    "config": {
                        "model": args.embedding_model,
                        "api_key": "ali-local-only",
                        "openai_base_url": embedding_endpoint,
                    },
                },
                "vector_store": {
                    "provider": "qdrant",
                    "config": {
                        "collection_name": args.collection,
                        "embedding_model_dims": args.embedding_dimensions,
                        "host": args.qdrant_host,
                        "port": args.qdrant_port,
                        "path": None,
                        "on_disk": True,
                    },
                },
            }
        )
        self.hybrid_backfill_count = self.memory.vector_store.ensure_hybrid_indexed()

    @staticmethod
    def user(request):
        user = request.get("user") or {}
        stable_id = str(user.get("stableId", "")).strip()
        if not stable_id:
            raise ValueError("A stable active-user ID is required")
        if str(user.get("resolutionMethod", "")).strip() == "identity-profile-selection-required":
            raise PermissionError("Select the active user profile before accessing personal memory")
        return stable_id, user

    def list_for(self, stable_id: str, maximum: int = 100):
        values = self.memory.get_all(filters={"user_id": stable_id}, top_k=max(1, min(maximum, 500)))
        return list(values.get("results", []))

    def owns(self, stable_id: str, memory_id: str):
        return next((value for value in self.list_for(stable_id, 500) if str(value.get("id")) == memory_id), None)

    def handle(self, request: dict) -> dict:
        operation = str(request.get("operation", "")).strip().lower()
        stable_id, user = self.user(request)
        if operation == "health":
            memories = self.list_for(stable_id, 500)
            return self.ok(
                f"Mem0 and Qdrant are ready with hybrid retrieval; backfilled {self.hybrid_backfill_count} memory item(s).",
                memories=[],
                count=len(memories),
            )
        if operation == "recall":
            query = str(request.get("query", "")).strip()
            maximum = max(1, min(int(request.get("maximumResults", 5)), 8))
            values = self.memory.search(
                query,
                top_k=maximum,
                filters={"user_id": stable_id},
                explain=True,
            )
            return self.ok("Memory recall complete.", values.get("results", []))
        if operation == "list":
            # C# deliberately sends a JSON null when the caller requests the
            # complete inventory. str(None) is "None", which previously became
            # an accidental category filter and made existing memories appear
            # to be missing.
            category = str(request.get("category") or "").strip().lower()
            values = self.list_for(stable_id, 500)
            if category:
                values = [value for value in values if item(value)["category"].lower() == category]
            return self.ok("Current-user memories loaded.", values, count=len(values))
        if operation == "remember":
            conversation = str(request.get("conversation", "")).strip()
            if not conversation:
                raise ValueError("Conversation is empty")
            source = str(request.get("source", "conversation")).strip() or "conversation"
            explicit = source == "explicit_user_request"
            model_selected = source == "model_selected_user_fact"
            user_stated = explicit or model_selected
            metadata = {
                "display_name_snapshot": str(user.get("displayName", "")),
                "category": str(request.get("category") or "general"),
                "source": source,
                "explicitly_taught": user_stated,
                "confidence": 1.0 if user_stated else 0.8,
                "identity_resolution_method": str(user.get("resolutionMethod", "explicit-selection")),
                "updated_utc": datetime.now(timezone.utc).isoformat(),
            }
            # An explicit low-level import can still request verbatim storage. Facts
            # selected by Ali's conversation model go through Mem0's own semantic
            # ADD/UPDATE/DELETE/NONE reconciliation against this user's memories.
            result = self.memory.add(
                conversation,
                user_id=stable_id,
                metadata=metadata,
                infer=not explicit,
            )
            values = result.get("results", [])
            message = "I'll remember that for this user." if values else "No durable personal memory was found in that turn."
            return self.ok(message, values)
        if operation == "correct":
            memory_id = str(request.get("memoryId", "")).strip()
            correction = str(request.get("correction", "")).strip()
            if not memory_id:
                raise ValueError("An exact memory ID is required for correction")
            if not correction:
                raise ValueError("Correction is empty")
            if self.owns(stable_id, memory_id) is None:
                raise PermissionError("That memory does not belong to the active user")
            self.memory.update(memory_id, text=correction, metadata={"source": "correction", "updated_utc": datetime.now(timezone.utc).isoformat()})
            return self.ok("The current user's memory was corrected.", [self.memory.get(memory_id)])
        if operation == "forget":
            # Backward-compatible operation name, but deliberately exact. Semantic
            # similarity is suitable for recall only and must never choose mutation
            # targets. Older clients may still send `request`; treat it as an ID.
            memory_id = str(request.get("memoryId") or request.get("request") or "").strip()
            owned = self.owns(stable_id, memory_id)
            if owned is None:
                raise PermissionError("That memory does not belong to the active user")
            self.memory.delete(memory_id)
            return self.ok("The selected current-user memory was deleted.", [owned])
        if operation == "delete":
            memory_id = str(request.get("memoryId", "")).strip()
            owned = self.owns(stable_id, memory_id)
            if owned is None:
                raise PermissionError("That memory does not belong to the active user")
            self.memory.delete(memory_id)
            return self.ok("The selected current-user memory was deleted.", [owned])
        raise ValueError(f"Unknown operation: {operation}")

    @staticmethod
    def ok(message: str, memories=None, count=None):
        values = [item(value) for value in (memories or []) if value]
        return {"success": True, "message": message, "memories": values, "count": len(values) if count is None else count}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--collection", required=True)
    parser.add_argument("--llm-endpoint", required=True)
    parser.add_argument("--llm-model", required=True)
    parser.add_argument("--llm-output-tokens", type=int, required=True)
    parser.add_argument("--embedding-endpoint", required=True)
    parser.add_argument("--embedding-model", required=True)
    parser.add_argument("--embedding-dimensions", type=int, required=True)
    parser.add_argument("--qdrant-host", required=True)
    parser.add_argument("--qdrant-port", type=int, required=True)
    worker = Worker(parser.parse_args())
    for line in sys.stdin:
        if not line.strip():
            continue
        request = json.loads(line)
        request_id = request.get("id")
        try:
            response = worker.handle(request)
        except PermissionError as error:
            response = {"success": False, "message": str(error), "memories": [], "errorCode": "permission_denied"}
        except Exception as error:  # process boundary must return a safe structured failure
            response = {"success": False, "message": str(error), "memories": [], "errorCode": type(error).__name__}
        response["id"] = request_id
        print(json.dumps(response, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
