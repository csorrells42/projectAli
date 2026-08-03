"""Local stdio Mem0 worker owned by Ali.

Every admitted operation carries an exact participant tenant, roster revision, and
embedding-space identity. The worker never listens on a network interface, rejects
non-loopback providers, and rejects the legacy active-user protocol in this collection.
"""

from __future__ import annotations

import argparse
import hashlib
import ipaddress
import io
import json
import math
import os
import re
import secrets
import stat
import sys
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from functools import wraps
from pathlib import Path
from urllib.parse import urlsplit

if os.name == "nt":
    import msvcrt
else:
    import fcntl


# Reserve the original stdout handle exclusively for the framed protocol before
# importing third-party packages. Incidental library output is bounded and redacted
# before it reaches Ali's diagnostic capture.
PROTOCOL_STDOUT = sys.stdout


class BoundedRedactingStderr(io.TextIOBase):
    _maximum_write = 1024
    _maximum_total = 32768

    def __init__(self, destination):
        self._destination = destination
        self._written = 0
        self._reported_truncation = False

    @property
    def encoding(self):
        return getattr(self._destination, "encoding", "utf-8")

    def writable(self):
        return True

    @staticmethod
    def _redact(value: str) -> str:
        # Library diagnostics are not a trusted structured channel and can echo
        # prompts, paths, endpoints, or memory text without labels. Preserve only
        # a bounded occurrence marker; typed protocol failures carry safe details.
        return "[worker diagnostic redacted]\n" if value.strip() else value

    def write(self, value):
        if not value:
            return 0
        original = str(value)
        if self._written >= self._maximum_total:
            if not self._reported_truncation:
                self._destination.write("[diagnostics truncated]\n")
                self._reported_truncation = True
            return len(original)
        safe = self._redact(original)[: self._maximum_write]
        remaining = self._maximum_total - self._written
        safe = safe[:remaining]
        self._destination.write(safe)
        self._written += len(safe)
        return len(original)

    def flush(self):
        self._destination.flush()


DIAGNOSTIC_STDERR = BoundedRedactingStderr(sys.stderr)
sys.stdout = DIAGNOSTIC_STDERR
sys.stderr = DIAGNOSTIC_STDERR

SCRIPT_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPT_ROOT))
os.environ["MEM0_TELEMETRY"] = "false"
os.environ["POSTHOG_DISABLED"] = "true"
os.environ["OPENAI_API_KEY"] = "ali-local-only"
os.environ["NO_PROXY"] = "127.0.0.1,localhost,::1"
os.environ["HTTP_PROXY"] = "http://127.0.0.1:1"
os.environ["HTTPS_PROXY"] = "http://127.0.0.1:1"

from mem0 import Memory  # noqa: E402
from mem0.configs.llms.openai import OpenAIConfig  # noqa: E402
from mem0.embeddings.base import EmbeddingBase  # noqa: E402
from mem0.utils.factory import LlmFactory, VectorStoreFactory  # noqa: E402
from openai import OpenAI  # noqa: E402


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
    parsed = urlsplit(normalized)
    host = parsed.hostname or ""
    try:
        loopback = host.lower() == "localhost" or ipaddress.ip_address(host).is_loopback
    except ValueError:
        loopback = False
    if parsed.scheme.lower() != "http" or not loopback or parsed.username or parsed.password:
        raise ValueError(f"{name} must be a loopback HTTP endpoint")
    return normalized


def participant_item(value: dict, embedding_space_id: str) -> dict:
    metadata = value.get("metadata") or {}
    score_details = value.get("score_details") or {}
    return {
        "memoryId": str(value.get("id", "")),
        "tenantId": str(metadata.get("tenant_id", "")),
        "text": str(metadata.get("display_text") or value.get("memory") or value.get("data") or ""),
        "category": str(metadata.get("category", "general")),
        "speakerParticipantReference": metadata.get("speaker_participant_reference"),
        "subjectParticipantReferences": list(metadata.get("subject_participant_references") or []),
        "witnessParticipantReferences": list(metadata.get("witness_participant_references") or []),
        "sharedEventReference": metadata.get("shared_event_reference"),
        "claimKind": str(metadata.get("claim_kind", "other")),
        "evidenceKind": str(metadata.get("evidence_kind", "unknown")),
        "visibility": str(metadata.get("visibility", "private")),
        "audienceParticipantReferences": list(metadata.get("audience_participant_references") or []),
        "sensitivity": str(metadata.get("sensitivity", "low")),
        "attributionConfidence": float(metadata.get("attribution_confidence", 0)),
        "state": str(metadata.get("state", "confirmed")),
        "provenance": dict(metadata.get("provenance") or {}),
        "consentReceipts": list(metadata.get("consent_receipts") or []),
        "correctsMemoryId": metadata.get("corrects_memory_id"),
        "supersedesMemoryId": metadata.get("supersedes_memory_id"),
        "disputesMemoryId": metadata.get("disputes_memory_id"),
        "createdUtc": metadata.get("created_utc") or value.get("created_at"),
        "confirmedUtc": metadata.get("confirmed_utc"),
        "correctedUtc": metadata.get("corrected_utc"),
        "revokedUtc": metadata.get("revoked_utc"),
        "archivedUtc": metadata.get("archived_utc"),
        "embeddingSpaceId": str(metadata.get("embedding_space_id") or embedding_space_id),
        "score": value.get("score"),
        "semanticScore": score_details.get("semantic_score"),
        "keywordScore": score_details.get("bm25_score"),
    }


class RoleAwareOpenAIEmbedding(EmbeddingBase):
    """OpenAI-compatible embedder that preserves Mem0's typed add/search action."""

    def __init__(
        self,
        api_base: str,
        model: str,
        dimensions: int,
        document_prompt_mode: str,
        query_prompt_mode: str,
    ):
        super().__init__(None)
        self.client = OpenAI(api_key="ali-local-only", base_url=api_base)
        self.model = model
        self.dimensions = dimensions
        self.document_prompt_mode = document_prompt_mode
        self.query_prompt_mode = query_prompt_mode

    @staticmethod
    def apply_prompt(text: str, mode: str) -> str:
        normalized = str(text).replace("\n", " ")
        if mode == "Plain":
            return normalized
        if mode == "SearchDocument":
            return f"search_document: {normalized}"
        if mode == "SearchQuery":
            return f"search_query: {normalized}"
        raise ValueError(f"Unsupported embedding prompt mode: {mode}")

    def mode_for(self, memory_action: str | None) -> str:
        return self.query_prompt_mode if memory_action == "search" else self.document_prompt_mode

    def embed(self, text, memory_action=None):
        prompted = self.apply_prompt(text, self.mode_for(memory_action))
        response = self.client.embeddings.create(
            input=[prompted],
            model=self.model,
            encoding_format="float",
        )
        vector = response.data[0].embedding
        if len(vector) != self.dimensions:
            raise ValueError(
                f"Embedding model returned {len(vector)} dimensions; exactly {self.dimensions} are configured"
            )
        return vector

    def embed_batch(self, texts, memory_action="add"):
        mode = self.mode_for(memory_action)
        prompted = [self.apply_prompt(text, mode) for text in texts]
        response = self.client.embeddings.create(
            input=prompted,
            model=self.model,
            encoding_format="float",
        )
        vectors = [item.embedding for item in sorted(response.data, key=lambda value: value.index)]
        if len(vectors) != len(prompted) or any(len(vector) != self.dimensions for vector in vectors):
            raise ValueError("Embedding batch did not match the configured count and dimensions")
        return vectors
class EmbeddingSpaceMismatchError(PermissionError):
    pass


class WorkerProtocolError(Exception):
    def __init__(self, error_code: str, safe_message: str, *, retryable: bool = False, details=None):
        super().__init__(safe_message)
        self.error_code = error_code
        self.safe_message = safe_message
        self.retryable = retryable
        self.details = details or {}


class ParticipantMutationJournal:
    """Bounded crash journal keyed by the caller's stable mutation request ID."""

    _maximum_receipts = 4096
    _maximum_receipt_bytes = 2 * 1024 * 1024
    _rollback_window = timedelta(hours=24)
    _stale_temporary_age = timedelta(hours=1)
    _terminal_statuses = {
        "committed",
        "rolled_back",
        "erased_by_later_delete",
        "recovery_expired",
    }
    _content_fields = {
        "authorization_snapshot",
        "target_snapshot",
        "created_ids",
        "expected_record_digest",
        "fingerprint",
        "authorized_access_keys",
        "requesting_participant_reference",
    }

    def __init__(self, data_root: str, embedding_space_id: str):
        data_path = Path(data_root)
        if os.path.lexists(data_path):
            self._require_safe_directory(data_path)
        else:
            data_path.mkdir(parents=True, exist_ok=False, mode=0o700)
            self._require_safe_directory(data_path)
        self._root = data_path / "participant-mutation-journal"
        if os.path.lexists(self._root):
            self._require_safe_directory(self._root)
        else:
            self._root.mkdir(parents=False, exist_ok=False, mode=0o700)
            self._require_safe_directory(self._root)
        self._tighten_permissions(self._root, 0o700)
        self._embedding_space_id = embedding_space_id
        self._cleanup_stale_temporaries()

    @staticmethod
    def _tighten_permissions(path: Path, mode: int):
        try:
            os.chmod(path, mode)
        except OSError:
            # LocalAppData is already scoped to the Windows account. chmod is a
            # best-effort extra boundary because Windows ACL semantics differ.
            pass

    @staticmethod
    def _digest(value) -> str:
        return hashlib.sha256(str(value or "").encode("utf-8")).hexdigest()

    @staticmethod
    def _parse_utc(value):
        try:
            parsed = datetime.fromisoformat(str(value or "").replace("Z", "+00:00"))
            return parsed if parsed.tzinfo is not None else parsed.replace(tzinfo=timezone.utc)
        except (TypeError, ValueError):
            return None

    @staticmethod
    def _is_reparse(stat_result) -> bool:
        return stat.S_ISLNK(stat_result.st_mode) or bool(
            getattr(stat_result, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        )

    @classmethod
    def _require_safe_directory(cls, path: Path):
        try:
            value = os.lstat(path)
        except OSError as error:
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The participant-memory journal directory is unavailable.",
                details={"mutationStatus": "in_doubt"},
            ) from error
        if not stat.S_ISDIR(value.st_mode) or cls._is_reparse(value):
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The participant-memory journal directory is a reparse point or is not a directory.",
                details={"mutationStatus": "in_doubt"},
            )

    @classmethod
    def _require_safe_regular_file(cls, path: Path, *, allow_missing: bool = False):
        try:
            value = os.lstat(path)
        except FileNotFoundError:
            if allow_missing:
                return None
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable mutation receipt is unavailable.",
                details={"mutationStatus": "in_doubt"},
            )
        except OSError as error:
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable mutation receipt cannot be inspected safely.",
                details={"mutationStatus": "in_doubt"},
            ) from error
        if (
            not stat.S_ISREG(value.st_mode)
            or cls._is_reparse(value)
            or getattr(value, "st_nlink", 1) != 1
        ):
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable mutation receipt is linked or is not a regular file.",
                details={"mutationStatus": "in_doubt"},
            )
        return value

    def _cleanup_stale_temporaries(self):
        cutoff = datetime.now(timezone.utc) - self._stale_temporary_age
        for path in self._root.glob(".tmp-*"):
            if path.parent != self._root:
                continue
            try:
                value = self._require_safe_regular_file(path)
                modified = datetime.fromtimestamp(value.st_mtime, timezone.utc)
                if modified < cutoff:
                    path.unlink()
            except (OSError, WorkerProtocolError):
                # Unsafe entries are never followed or removed. A later exact
                # receipt operation reports the safe failure without widening scope.
                continue

    def _flush_directory_best_effort(self):
        if os.name == "nt":
            return
        descriptor = None
        try:
            descriptor = os.open(
                self._root,
                os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
            )
            os.fsync(descriptor)
        except OSError:
            pass
        finally:
            if descriptor is not None:
                os.close(descriptor)

    def _files(self) -> list[Path]:
        values = []
        for path in self._root.glob("*.json"):
            if path.parent != self._root:
                continue
            try:
                self._require_safe_regular_file(path)
            except WorkerProtocolError:
                continue
            values.append(path)
        return sorted(values)

    def _read_path(self, path: Path) -> dict:
        descriptor = None
        try:
            if path.parent != self._root:
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The durable mutation receipt path is outside the journal.",
                    details={"mutationStatus": "in_doubt"},
                )
            self._require_safe_regular_file(path)
            descriptor = os.open(
                path,
                os.O_RDONLY
                | getattr(os, "O_BINARY", 0)
                | getattr(os, "O_NOFOLLOW", 0),
            )
            opened = os.fstat(descriptor)
            current_path = os.lstat(path)
            if (
                not stat.S_ISREG(opened.st_mode)
                or self._is_reparse(opened)
                or getattr(opened, "st_nlink", 1) != 1
                or not os.path.samestat(opened, current_path)
            ):
                raise ValueError("receipt is not one unlinked regular file")
            if opened.st_size > self._maximum_receipt_bytes:
                raise ValueError("receipt exceeds the bounded size")
            with os.fdopen(descriptor, "rb") as stream:
                descriptor = None
                encoded = stream.read(self._maximum_receipt_bytes + 1)
            if len(encoded) > self._maximum_receipt_bytes:
                raise ValueError("receipt exceeds the bounded size while reading")
            value = json.loads(encoded.decode("utf-8"))
            self._require_safe_regular_file(path)
        except WorkerProtocolError:
            raise
        except Exception as error:
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable mutation receipt is unreadable; deliberate repair is required.",
                details={"mutationStatus": "in_doubt"},
            ) from error
        finally:
            if descriptor is not None:
                os.close(descriptor)
        if not isinstance(value, dict):
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable mutation receipt is malformed; deliberate repair is required.",
                details={"mutationStatus": "in_doubt"},
            )
        return value

    def _atomic_write(self, path: Path, value: dict):
        if path.parent != self._root:
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable mutation receipt path is unsafe.",
                details={"mutationStatus": "in_doubt"},
            )
        if os.path.lexists(path):
            self._require_safe_regular_file(path)
        encoded = json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        if len(encoded.encode("utf-8")) > self._maximum_receipt_bytes:
            raise WorkerProtocolError(
                "invalid_request",
                "The bounded durable mutation receipt is too large.",
            )
        temporary = self._root / f".tmp-{secrets.token_hex(16)}"
        descriptor = None
        try:
            descriptor = os.open(
                temporary,
                os.O_WRONLY
                | os.O_CREAT
                | os.O_EXCL
                | getattr(os, "O_BINARY", 0)
                | getattr(os, "O_NOFOLLOW", 0),
                0o600,
            )
            with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
                descriptor = None
                self._tighten_permissions(temporary, 0o600)
                stream.write(encoded)
                stream.flush()
                os.fsync(stream.fileno())
            self._require_safe_regular_file(temporary)
            os.replace(temporary, path)
            self._require_safe_regular_file(path)
            self._tighten_permissions(path, 0o600)
            self._flush_directory_best_effort()
        finally:
            if descriptor is not None:
                os.close(descriptor)
            try:
                if os.path.lexists(temporary):
                    self._require_safe_regular_file(temporary)
                    temporary.unlink()
            except (OSError, WorkerProtocolError):
                pass

    def _redacted_tombstone(
        self,
        receipt: dict,
        *,
        status: str,
        outcome: str,
        erased_target_id: str | None = None,
    ) -> dict:
        principal = receipt.get("requesting_participant_reference")
        tenant_id = receipt.get("tenant_id")
        target_id = erased_target_id or receipt.get("target_id")
        timestamps = {
            key: receipt.get(key)
            for key in (
                "started_utc",
                "committed_utc",
                "rolled_back_utc",
                "reconciled_utc",
                "delete_staged_utc",
            )
            if receipt.get(key)
        }
        return {
            "redacted": True,
            "mutation_request_id": self.require_request_id(
                receipt.get("mutation_request_id")
            ),
            "embedding_space_id": self._embedding_space_id,
            "operation": str(receipt.get("operation") or "unknown"),
            "status": status,
            "outcome": outcome,
            "tenant_id_hash": self._digest(tenant_id),
            "requesting_participant_hash": self._digest(principal),
            "target_id_hash": self._digest(target_id),
            "updated_utc": datetime.now(timezone.utc).isoformat(),
            **timestamps,
        }

    @staticmethod
    def _references_target(
        receipt: dict,
        target_id: str,
        originating_mutation_request_id: str = "",
    ) -> bool:
        if (
            originating_mutation_request_id
            and str(receipt.get("mutation_request_id") or "")
            == originating_mutation_request_id
        ):
            return True
        if str(receipt.get("target_id") or "") == target_id:
            return True
        if target_id in [str(value or "") for value in receipt.get("created_ids") or []]:
            return True
        for field in ("target_snapshot", "authorization_snapshot"):
            snapshot = dict(receipt.get(field) or {})
            if str(snapshot.get("id") or "") == target_id:
                return True
            metadata = dict((snapshot.get("payload") or {}).get("metadata") or {})
            if target_id in {
                str(metadata.get("corrects_memory_id") or ""),
                str(metadata.get("supersedes_memory_id") or ""),
                str(metadata.get("disputes_memory_id") or ""),
            }:
                return True
        return False

    def _expire_terminal_receipt(self, path: Path, receipt: dict, now: datetime) -> dict:
        status = str(receipt.get("status") or "")
        started = self._parse_utc(receipt.get("started_utc"))
        if (
            not receipt.get("redacted")
            and status in {"committed", "rolled_back"}
            and started is not None
            and now - started > self._rollback_window
        ):
            receipt = self._redacted_tombstone(
                receipt,
                status="recovery_expired",
                outcome="bounded_rollback_window_expired",
            )
            self._atomic_write(path, receipt)
        return receipt

    def _quarantine_corrupt_receipt(self, path: Path):
        self._require_safe_regular_file(path)
        quarantine = path.with_suffix(".corrupt")
        if os.path.lexists(quarantine):
            self._require_safe_regular_file(quarantine)
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The journal already contains a quarantine marker for this receipt.",
                details={"mutationStatus": "in_doubt"},
            )
        if len(list(self._root.glob("*.corrupt"))) >= self._maximum_receipts:
            raise WorkerProtocolError(
                "unavailable",
                "The bounded corrupt-receipt quarantine is full; deliberate local repair is required.",
            )
        os.replace(path, quarantine)
        self._require_safe_regular_file(quarantine)
        self._flush_directory_best_effort()

    def _maintain(self, *, required_free: int = 0):
        self._cleanup_stale_temporaries()
        now = datetime.now(timezone.utc)
        receipts = []
        for path in self._files():
            try:
                receipt = self._read_path(path)
            except WorkerProtocolError:
                self._quarantine_corrupt_receipt(path)
                continue
            receipt = self._expire_terminal_receipt(path, receipt, now)
            receipts.append((path, receipt))

        maximum_after_maintenance = self._maximum_receipts - max(0, required_free)
        current_count = len(self._files())
        if current_count > maximum_after_maintenance:
            compactable = []
            for path, receipt in receipts:
                started = self._parse_utc(receipt.get("started_utc"))
                if (
                    receipt.get("redacted") is True
                    and str(receipt.get("status") or "") in self._terminal_statuses
                    and started is not None
                    and now - started > self._rollback_window
                ):
                    compactable.append((started, path))
            for _, path in sorted(compactable):
                if current_count <= maximum_after_maintenance:
                    break
                self._require_safe_regular_file(path)
                path.unlink()
                current_count -= 1
            self._flush_directory_best_effort()
        if current_count > maximum_after_maintenance:
            raise WorkerProtocolError(
                "unavailable",
                "The bounded participant-memory recovery journal is temporarily full; retry after terminal receipts cross the 24-hour recovery window.",
            )

    @staticmethod
    def require_request_id(value) -> str:
        request_id = str(value or "").strip()
        if not request_id or len(request_id) > 128 or any(ord(char) < 32 for char in request_id):
            raise WorkerProtocolError(
                "invalid_request",
                "A stable bounded mutation request ID is required.",
            )
        return request_id

    @classmethod
    def require_fresh_mutation_request_id(cls, value) -> str:
        request_id = cls.require_request_id(value)
        parts = request_id.split(":", 2)
        if (
            len(parts) != 3
            or parts[0]
            not in {"participant-mutation", "participant-request", "participant-desktop"}
            or not parts[1].isdigit()
            or not parts[2]
        ):
            raise WorkerProtocolError(
                "invalid_request",
                "A new mutation requires Ali's timestamped stable request-ID format.",
            )
        try:
            issued = datetime.fromtimestamp(int(parts[1]), timezone.utc)
        except (OverflowError, OSError, ValueError) as error:
            raise WorkerProtocolError(
                "invalid_request",
                "The stable mutation request ID has an invalid issuance time.",
            ) from error
        now = datetime.now(timezone.utc)
        if issued > now + timedelta(minutes=5) or now - issued > cls._rollback_window:
            raise WorkerProtocolError(
                "conflict",
                "The stable mutation request ID is outside its 24-hour admission window.",
            )
        return request_id

    def _path(self, request_id: str) -> Path:
        digest = hashlib.sha256(request_id.encode("utf-8")).hexdigest()
        return self._root / f"{digest}.json"

    def load(self, request_id: str):
        path = self._path(request_id)
        quarantine = path.with_suffix(".corrupt")
        if os.path.lexists(quarantine):
            self._require_safe_regular_file(quarantine)
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The exact durable mutation receipt is quarantined and requires deliberate local repair.",
                details={"mutationStatus": "in_doubt"},
            )
        if not os.path.lexists(path):
            return None
        value = self._read_path(path)
        if (
            not isinstance(value, dict)
            or value.get("embedding_space_id") != self._embedding_space_id
            or value.get("mutation_request_id") != request_id
        ):
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable mutation receipt does not match this embedding space.",
                details={"mutationStatus": "in_doubt"},
            )
        return self._expire_terminal_receipt(path, value, datetime.now(timezone.utc))

    def save(self, receipt: dict):
        request_id = self.require_request_id(receipt.get("mutation_request_id"))
        value = dict(receipt)
        value["mutation_request_id"] = request_id
        value["embedding_space_id"] = self._embedding_space_id
        value["updated_utc"] = datetime.now(timezone.utc).isoformat()
        path = self._path(request_id)
        quarantine = path.with_suffix(".corrupt")
        if os.path.lexists(quarantine):
            self._require_safe_regular_file(quarantine)
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The exact durable mutation receipt is quarantined and cannot be reused.",
                details={"mutationStatus": "in_doubt"},
            )
        if os.path.lexists(path):
            current = self._read_path(path)
            current = self._expire_terminal_receipt(
                path,
                current,
                datetime.now(timezone.utc),
            )
            if current.get("redacted") and not value.get("redacted"):
                raise WorkerProtocolError(
                    "conflict",
                    "The bounded recovery window for this mutation receipt has expired.",
                    details={
                        "mutationRequestId": request_id,
                        "mutationStatus": current.get("status"),
                        "mutationOperation": current.get("operation"),
                    },
                )
        elif len(self._files()) >= self._maximum_receipts:
            self._maintain(required_free=1)
        self._atomic_write(path, value)

    def has_other_active_reference(self, request_id: str, target_ids: list[str]) -> bool:
        exact_targets = {str(value or "") for value in target_ids if str(value or "")}
        if not exact_targets:
            return False
        for path in self._files():
            receipt = self._read_path(path)
            if str(receipt.get("mutation_request_id") or "") == request_id:
                continue
            if str(receipt.get("status") or "") == "rolled_back":
                continue
            if any(self._references_target(receipt, target_id) for target_id in exact_targets):
                return True
        return False

    def finalize_delete(self, delete_receipt: dict) -> dict:
        """Redact every receipt that can retain the exact deleted point.

        Referencing receipts are rewritten first. The delete receipt is rewritten
        last, so a committed delete tombstone is also the crash-safe completion
        marker for the bounded multi-file scrub.
        """

        request_id = self.require_request_id(delete_receipt.get("mutation_request_id"))
        target_id = str(delete_receipt.get("target_id") or "")
        if not target_id:
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The staged delete receipt lost its exact target.",
                details={"mutationStatus": "in_doubt"},
            )
        deleted_snapshot_metadata = dict(
            ((delete_receipt.get("target_snapshot") or {}).get("payload") or {}).get("metadata")
            or {}
        )
        originating_mutation_request_id = str(
            deleted_snapshot_metadata.get("mutation_request_id") or ""
        )
        matching = []
        for path in self._files():
            receipt = self._read_path(path)
            if self._references_target(
                receipt,
                target_id,
                originating_mutation_request_id,
            ):
                matching.append((path, receipt))
        if not any(
            str(receipt.get("mutation_request_id") or "") == request_id
            for _, receipt in matching
        ):
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The staged delete receipt is unavailable for finalization.",
                details={"mutationStatus": "in_doubt"},
            )

        delete_path = self._path(request_id)
        for path, receipt in matching:
            if path == delete_path:
                continue
            self._atomic_write(
                path,
                self._redacted_tombstone(
                    receipt,
                    status="erased_by_later_delete",
                    outcome="content_erased_by_authenticated_delete",
                    erased_target_id=target_id,
                ),
            )

        committed = dict(delete_receipt)
        committed["committed_utc"] = datetime.now(timezone.utc).isoformat()
        tombstone = self._redacted_tombstone(
            committed,
            status="committed",
            outcome="active_point_deleted_and_journal_redacted",
            erased_target_id=target_id,
        )
        self._atomic_write(delete_path, tombstone)

        for path, _ in matching:
            persisted = self._read_path(path)
            if not persisted.get("redacted") or self._content_fields.intersection(persisted):
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "Deletion finalization did not redact every associated durable receipt.",
                    details={"mutationStatus": "in_doubt"},
                )
        return tombstone


class ParticipantMutationLease:
    """One nonblocking OS lease for every writer in an embedding space."""

    def __init__(self, data_root: str, embedding_space_id: str):
        root = Path(data_root) / "participant-mutation-journal"
        ParticipantMutationJournal._require_safe_directory(root)
        identity = hashlib.sha256(embedding_space_id.encode("utf-8")).hexdigest()
        self._path = root / f"writer-{identity}.lock"

    @contextmanager
    def acquire(self):
        if os.path.lexists(self._path):
            ParticipantMutationJournal._require_safe_regular_file(self._path)
        descriptor = os.open(
            self._path,
            os.O_RDWR
            | os.O_CREAT
            | os.O_APPEND
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        opened = os.fstat(descriptor)
        current_path = os.lstat(self._path)
        if (
            not stat.S_ISREG(opened.st_mode)
            or ParticipantMutationJournal._is_reparse(opened)
            or getattr(opened, "st_nlink", 1) != 1
            or not os.path.samestat(opened, current_path)
        ):
            os.close(descriptor)
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The participant-memory writer lease is linked or unsafe.",
                details={"mutationStatus": "in_doubt"},
            )
        ParticipantMutationJournal._require_safe_regular_file(self._path)
        stream = os.fdopen(descriptor, "a+b")
        acquired = False
        try:
            stream.seek(0, os.SEEK_END)
            if stream.tell() == 0:
                stream.write(b"\0")
                stream.flush()
                os.fsync(stream.fileno())
            stream.seek(0)
            try:
                if os.name == "nt":
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                acquired = True
            except OSError as error:
                raise WorkerProtocolError(
                    "conflict",
                    "Another Ali process currently owns the participant-memory writer lease.",
                    retryable=True,
                ) from error
            yield
        finally:
            if acquired:
                try:
                    stream.seek(0)
                    if os.name == "nt":
                        msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
                    else:
                        fcntl.flock(stream.fileno(), fcntl.LOCK_UN)
                finally:
                    acquired = False
            stream.close()


def participant_single_writer(handler):
    """Hold the cross-process lease for a complete write or recovery action."""

    @wraps(handler)
    def serialized(worker, *args, **kwargs):
        try:
            with worker.mutation_lease.acquire():
                return handler(worker, *args, **kwargs)
        except WorkerProtocolError as error:
            # A nonblocking lease conflict occurs before the handler can load its
            # receipt. The journal is atomically replaced, so a read-only snapshot
            # can still attach the exact durable operation for deliberate recovery.
            request = args[0] if args and isinstance(args[0], dict) else {}
            request_id = str(request.get("mutationRequestId") or "").strip()
            if error.error_code == "conflict" and request_id:
                try:
                    receipt = worker.mutation_journal.load(request_id)
                    if receipt is not None:
                        worker.enrich_mutation_error(error, receipt)
                except (WorkerProtocolError, OSError, ValueError):
                    pass
            raise

    return serialized


def require_prompt_configuration(mode_value, prefix_value, role: str) -> tuple[str, str]:
    mode = str(mode_value or "").strip().lower()
    prefix = str(prefix_value or "")
    if len(prefix) > 128 or any(ord(char) < 32 for char in prefix):
        raise ValueError(f"The {role} embedding prefix is invalid")
    if mode == "none-v1" and prefix == "":
        return mode, prefix
    if mode == "prefix-v1" and prefix.strip():
        return mode, prefix
    raise ValueError(f"The {role} embedding prompt mode and prefix do not form a supported exact pair")


class Worker:
    def __init__(self, args):
        llm_endpoint = require_loopback(args.llm_endpoint, "LLM endpoint")
        embedding_api_base = require_loopback(args.embedding_api_base, "embedding API base")
        if args.embedding_protocol != "openai-compatible-embeddings-v1":
            raise ValueError("Unsupported embedding protocol identity")
        if args.embedding_context_tokens < 1:
            raise ValueError("Embedding context tokens must be positive")
        self.embedding_provider = str(args.embedding_provider).strip()
        self.embedding_space_id = str(args.embedding_space_id).strip()
        self.embedding_protocol = str(args.embedding_protocol).strip()
        self.embedding_resolved_model = str(args.embedding_resolved_model).strip()
        self.embedding_quantization = str(args.embedding_quantization).strip()
        self.embedding_context_tokens = int(args.embedding_context_tokens)
        (
            self.embedding_query_prompt_mode,
            self.embedding_query_prefix,
        ) = require_prompt_configuration(
            args.embedding_query_prompt_mode,
            args.embedding_query_prefix,
            "query",
        )
        (
            self.embedding_document_prompt_mode,
            self.embedding_document_prefix,
        ) = require_prompt_configuration(
            args.embedding_document_prompt_mode,
            args.embedding_document_prefix,
            "document",
        )
        if not self.embedding_provider:
            raise ValueError("An embedding provider is required")
        if not self.embedding_space_id or not self.embedding_protocol:
            raise ValueError("An embedding-space and protocol identity are required")
        if args.qdrant_host not in {"127.0.0.1", "localhost", "::1"}:
            raise ValueError("Qdrant must use a loopback host")
        qdrant_api_key_environment_variable = str(args.qdrant_api_key_environment_variable).strip()
        qdrant_api_key = (
            os.environ.get(qdrant_api_key_environment_variable)
            if qdrant_api_key_environment_variable
            else None
        )
        Path(args.data_root).mkdir(parents=True, exist_ok=True)
        LlmFactory.provider_to_class["openai"] = (
            "openai_compatible_llm.LocalOpenAICompatibleLLM",
            OpenAIConfig,
        )
        VectorStoreFactory.provider_to_class["qdrant"] = "local_qdrant.LocalQdrant"
        qdrant_config = {
            "collection_name": args.collection,
            "embedding_model_dims": args.embedding_dimensions,
            "host": args.qdrant_host,
            "port": args.qdrant_port,
            "path": None,
            "https": args.qdrant_use_tls == "true",
            "on_disk": True,
        }
        if qdrant_api_key:
            qdrant_config["api_key"] = qdrant_api_key
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
                        "openai_base_url": embedding_api_base,
                        "embedding_dims": args.embedding_dimensions,
                    },
                },
                "vector_store": {
                    "provider": "qdrant",
                    "config": qdrant_config,
                },
            }
        )
        self.mutation_journal = ParticipantMutationJournal(
            args.data_root,
            self.embedding_space_id,
        )
        self.mutation_lease = ParticipantMutationLease(
            args.data_root,
            self.embedding_space_id,
        )

    def require_embedding_space(self, request: dict):
        requested = str(request.get("embeddingSpaceId", "")).strip()
        if requested != self.embedding_space_id:
            raise EmbeddingSpaceMismatchError(
                "The request targets a different embedding space"
            )

    @staticmethod
    def value_from_point(point) -> dict:
        payload = dict(point.payload or {})
        return {
            "id": str(point.id),
            "memory": str(payload.get("data") or payload.get("memory") or ""),
            "metadata": dict(payload.get("metadata") or {}),
            "created_at": payload.get("created_at"),
            "updated_at": payload.get("updated_at"),
        }

    def participant_owned(self, tenant_id: str, memory_id: str):
        point = self.memory.vector_store.exact_point(memory_id)
        if point is None:
            return None
        payload = dict(point.payload or {})
        metadata = dict(payload.get("metadata") or {})
        if str(payload.get("user_id", "")) != tenant_id:
            return None
        if str(metadata.get("embedding_space_id", "")) != self.embedding_space_id:
            raise EmbeddingSpaceMismatchError(
                "The selected memory belongs to another embedding space"
            )
        return self.value_from_point(point)

    @staticmethod
    def bounded_references(values, field_name: str, maximum: int = 16) -> list[str]:
        if not isinstance(values, list):
            raise WorkerProtocolError("conflict", f"Participant memory {field_name} is malformed.")
        references = []
        for value in values:
            reference = str(value or "").strip()
            if not reference or len(reference) > 128 or any(ord(char) < 32 for char in reference):
                raise WorkerProtocolError("conflict", f"Participant memory {field_name} is malformed.")
            references.append(reference)
        if len(references) > maximum or len(set(references)) != len(references):
            raise WorkerProtocolError("conflict", f"Participant memory {field_name} is malformed.")
        return references

    @staticmethod
    def exact_access_keys(visibility_value, sensitivity_value, audience_values) -> list[str]:
        visibility = str(visibility_value or "").strip().replace("_", "").lower()
        sensitivity = str(sensitivity_value or "").strip().lower()
        audience = Worker.bounded_references(audience_values or [], "audience")
        if sensitivity not in {"low", "sensitive"}:
            raise WorkerProtocolError("conflict", "Participant memory sensitivity is malformed.")
        if visibility == "general":
            if audience or sensitivity == "sensitive":
                raise WorkerProtocolError("conflict", "Participant memory general audience is malformed.")
            return [f"scope:general:{sensitivity}"]
        if visibility == "private":
            if len(audience) != 1:
                raise WorkerProtocolError("conflict", "Participant memory private audience is malformed.")
            prefix = "participant"
        elif visibility == "shared":
            if len(audience) < 2:
                raise WorkerProtocolError("conflict", "Participant memory shared audience is malformed.")
            prefix = "participant"
        elif visibility == "teamproject":
            if not audience:
                raise WorkerProtocolError("conflict", "Participant memory team audience is malformed.")
            prefix = "team"
        else:
            raise WorkerProtocolError("conflict", "Participant memory visibility is malformed.")
        return sorted({f"{prefix}:{reference}:{sensitivity}" for reference in audience})

    def validate_participant_record(self, value: dict, tenant_id: str, *, current=False) -> dict:
        metadata = dict(value.get("metadata") or {})
        if str(metadata.get("tenant_id") or "") != tenant_id:
            raise WorkerProtocolError("permission_denied", "The exact participant memory is outside this tenant.")
        if str(metadata.get("embedding_space_id") or "") != self.embedding_space_id:
            raise EmbeddingSpaceMismatchError("The selected memory belongs to another embedding space")
        state = str(metadata.get("state") or "").strip().lower()
        if state not in {"candidate", "confirmed", "disputed", "superseded", "revoked", "archived"}:
            raise WorkerProtocolError("conflict", "The exact participant memory state is malformed.")
        if current and state != "confirmed":
            raise WorkerProtocolError("conflict", "The exact participant memory is no longer current.")
        claim_kind = str(metadata.get("claim_kind") or "")
        evidence_kind = str(metadata.get("evidence_kind") or "")
        confidence = float(metadata.get("attribution_confidence", -1))
        if claim_kind not in {
            "directStatement", "hearsay", "directObservation", "preference",
            "sharedExperience", "directive", "other",
        } or evidence_kind not in {
            "statedDirectly", "reportedByParticipant", "observedDirectly", "unknown",
        }:
            raise WorkerProtocolError("conflict", "Participant memory role evidence is malformed.")
        if not math.isfinite(confidence) or confidence < 0 or confidence > 1:
            raise WorkerProtocolError("conflict", "Participant memory attribution confidence is malformed.")
        speaker = metadata.get("speaker_participant_reference")
        if speaker is not None:
            speaker = str(speaker).strip()
            if not speaker or len(speaker) > 128 or any(ord(char) < 32 for char in speaker):
                raise WorkerProtocolError("conflict", "Participant memory speaker metadata is malformed.")
        self.bounded_references(
            metadata.get("subject_participant_references") or [],
            "subject roles",
        )
        self.bounded_references(
            metadata.get("witness_participant_references") or [],
            "witness roles",
        )
        audience = self.bounded_references(
            metadata.get("audience_participant_references") or [],
            "audience",
        )
        expected_keys = self.exact_access_keys(
            metadata.get("visibility"),
            metadata.get("sensitivity"),
            audience,
        )
        stored_keys = self.bounded_references(
            metadata.get("access_keys") or [],
            "access keys",
        )
        if sorted(stored_keys) != expected_keys:
            raise WorkerProtocolError("conflict", "Participant memory audience authorization is inconsistent.")
        return metadata

    @staticmethod
    def normalize_authorized_access_keys(values) -> list[str]:
        if not isinstance(values, list) or len(values) > 16:
            raise WorkerProtocolError("permission_denied", "Participant-memory authority is malformed.")
        authorized = set()
        for item in values:
            key = str(item or "").strip()
            if (
                not key
                or len(key) > 256
                or any(ord(char) < 32 for char in key)
                or not re.fullmatch(
                    r"(?:scope:general|participant:[^:]+|team:[^:]+):(low|sensitive)",
                    key,
                )
            ):
                raise WorkerProtocolError("permission_denied", "Participant-memory authority is malformed.")
            authorized.add(key)
        return sorted(authorized)

    @staticmethod
    def require_participant_access(value: dict, authorized_access_keys: list[str]):
        metadata = dict(value.get("metadata") or {})
        record_access_keys = {str(item) for item in (metadata.get("access_keys") or [])}
        authorized = set(Worker.normalize_authorized_access_keys(authorized_access_keys))
        if not record_access_keys.intersection(authorized):
            raise WorkerProtocolError(
                "permission_denied",
                "The requesting principal is not authorized for the exact participant memory.",
            )

    @staticmethod
    def require_authenticated_target_actor(request: dict, value: dict) -> str:
        principal = str(request.get("requestingParticipantReference") or "").strip()
        authenticated = request.get("requestingParticipantAuthenticated")
        if (
            not principal
            or len(principal) > 128
            or any(ord(char) < 32 for char in principal)
            or authenticated is not True
        ):
            raise WorkerProtocolError(
                "permission_denied",
                "An independently authenticated requesting participant is required for this exact mutation.",
            )

        metadata = dict(value.get("metadata") or {})
        provenance = dict(metadata.get("provenance") or {})
        target_roles = set(
            Worker.bounded_references(
                metadata.get("subject_participant_references") or [],
                "subject roles",
            )
        )
        speaker = metadata.get("speaker_participant_reference")
        if speaker is not None:
            target_roles.add(str(speaker).strip())
        reporter = provenance.get("reportedByParticipantReference")
        if reporter is not None:
            reporter = str(reporter).strip()
            if not reporter or len(reporter) > 128 or any(ord(char) < 32 for char in reporter):
                raise WorkerProtocolError("conflict", "Participant memory reporter metadata is malformed.")
            target_roles.add(reporter)
        if principal not in target_roles:
            raise WorkerProtocolError(
                "permission_denied",
                "The requesting principal is not the target memory speaker, subject, or reporter.",
            )
        return principal

    def participant_metadata(
        self,
        tenant_id: str,
        proposal: dict,
        access_keys: list[str],
        mutation_request_id: str,
        provenance_value: dict,
        consent_receipt_values: list,
    ) -> dict:
        now = datetime.now(timezone.utc).isoformat()
        provenance = dict(provenance_value or {})
        text = str(proposal.get("text") or "").strip()
        category = str(proposal.get("category") or "general").strip()
        claim_kind = str(proposal.get("claimKind") or "").strip()
        evidence_kind = str(proposal.get("evidenceKind") or "").strip()
        shared_event = proposal.get("sharedEventReference")
        if shared_event is not None:
            shared_event = str(shared_event).strip()
            if not shared_event or len(shared_event) > 128 or any(ord(char) < 32 for char in shared_event):
                raise WorkerProtocolError("invalid_request", "Participant-memory shared-event identity is malformed.")
        confidence = float(proposal.get("attributionConfidence", 0))
        if not text or len(text) > 4096 or not category or len(category) > 128:
            raise WorkerProtocolError("invalid_request", "Participant-memory text or category exceeds its bound.")
        if claim_kind not in {
            "directStatement", "hearsay", "directObservation", "preference",
            "sharedExperience", "directive", "other",
        } or evidence_kind not in {
            "statedDirectly", "reportedByParticipant", "observedDirectly", "unknown",
        }:
            raise WorkerProtocolError("invalid_request", "Participant-memory claim or evidence kind is malformed.")
        if not math.isfinite(confidence) or confidence < 0 or confidence > 1:
            raise WorkerProtocolError("invalid_request", "Participant-memory attribution confidence is malformed.")
        if not provenance or len(json.dumps(provenance, ensure_ascii=False)) > 4096:
            raise WorkerProtocolError("invalid_request", "Participant-memory provenance is missing or oversized.")
        if (
            not isinstance(consent_receipt_values, list)
            or len(consent_receipt_values) > 16
            or len(json.dumps(consent_receipt_values, ensure_ascii=False)) > 16384
        ):
            raise WorkerProtocolError("invalid_request", "Participant-memory consent receipts are malformed.")
        subjects = self.bounded_references(
            proposal.get("subjectParticipantReferences") or [],
            "subject roles",
        )
        witnesses = self.bounded_references(
            proposal.get("witnessParticipantReferences") or [],
            "witness roles",
        )
        audience = self.bounded_references(
            proposal.get("audienceParticipantReferences") or [],
            "audience",
        )
        exact_keys = self.exact_access_keys(
            proposal.get("visibility"),
            proposal.get("sensitivity"),
            audience,
        )
        supplied_keys = self.bounded_references(access_keys or [], "access keys")
        if sorted(supplied_keys) != exact_keys:
            raise WorkerProtocolError(
                "permission_denied",
                "The proposed audience does not match its exact storage authorization.",
            )
        metadata = {
            "tenant_id": tenant_id,
            "category": category,
            "speaker_participant_reference": proposal.get("speakerParticipantReference"),
            "subject_participant_references": subjects,
            "witness_participant_references": witnesses,
            "shared_event_reference": shared_event,
            "claim_kind": claim_kind,
            "evidence_kind": evidence_kind,
            "visibility": str(proposal.get("visibility") or "private"),
            "audience_participant_references": audience,
            "sensitivity": str(proposal.get("sensitivity") or "low"),
            "attribution_confidence": confidence,
            "state": "confirmed",
            "provenance": provenance,
            "consent_receipts": list(consent_receipt_values or []),
            "access_keys": exact_keys,
            "embedding_space_id": self.embedding_space_id,
            "embedding_protocol": self.embedding_protocol,
            "embedding_resolved_model": self.embedding_resolved_model,
            "embedding_quantization": self.embedding_quantization,
            "embedding_context_tokens": self.embedding_context_tokens,
            "embedding_query_prompt_mode": self.embedding_query_prompt_mode,
            "embedding_document_prompt_mode": self.embedding_document_prompt_mode,
            "display_text": text,
            "mutation_request_id": mutation_request_id,
            "created_utc": now,
            "confirmed_utc": now,
            "corrects_memory_id": None,
            "supersedes_memory_id": None,
            "disputes_memory_id": None,
        }
        self.validate_participant_record(
            {"metadata": metadata},
            tenant_id,
            current=True,
        )
        return metadata

    @staticmethod
    def mutation_fingerprint(request: dict, mutation_request_id: str) -> str:
        canonical = {
            "mutationRequestId": mutation_request_id,
            "tenantId": request.get("tenantId"),
            "rosterRevision": request.get("rosterRevision"),
            "embeddingSpaceId": request.get("embeddingSpaceId"),
            "proposal": request.get("proposal"),
            "provenance": request.get("provenance"),
            "consentReceipts": request.get("consentReceipts"),
            "accessKeys": sorted(set(request.get("accessKeys") or [])),
            "authorizedAccessKeys": sorted(set(request.get("authorizedAccessKeys") or [])),
            "requestingParticipantReference": request.get("requestingParticipantReference"),
            "requestingParticipantAuthenticated": request.get("requestingParticipantAuthenticated"),
        }
        encoded = json.dumps(
            canonical,
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
        return hashlib.sha256(encoded).hexdigest()

    @staticmethod
    def participant_record_contract_digest(metadata: dict) -> str:
        contract = {
            field: metadata.get(field)
            for field in (
                "tenant_id",
                "display_text",
                "category",
                "speaker_participant_reference",
                "subject_participant_references",
                "witness_participant_references",
                "shared_event_reference",
                "claim_kind",
                "evidence_kind",
                "visibility",
                "audience_participant_references",
                "sensitivity",
                "attribution_confidence",
                "provenance",
                "consent_receipts",
                "access_keys",
                "embedding_space_id",
                "mutation_request_id",
                "corrects_memory_id",
                "supersedes_memory_id",
                "disputes_memory_id",
            )
        }
        encoded = json.dumps(
            contract,
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
        return hashlib.sha256(encoded).hexdigest()

    def bind_expected_record_contract(self, receipt: dict, metadata: dict):
        expected = self.participant_record_contract_digest(metadata)
        existing = str(receipt.get("expected_record_digest") or "")
        if existing and existing != expected:
            raise WorkerProtocolError(
                "conflict",
                "The durable mutation record contract changed for the same request ID.",
            )
        receipt["expected_record_digest"] = expected

    def mutation_points(
        self,
        tenant_id: str,
        mutation_request_id: str,
        maximum: int = 2,
    ) -> list[dict]:
        return [
            self.value_from_point(point)
            for point in self.memory.vector_store.scroll_exact_filters(
                {
                    "user_id": tenant_id,
                    "metadata.embedding_space_id": self.embedding_space_id,
                    "metadata.mutation_request_id": mutation_request_id,
                },
                maximum=maximum,
            )
        ]

    def hybrid_status(self, tenant_id: str, authorized_access_keys: list[str]) -> dict:
        return self.memory.vector_store.inspect_hybrid_indexed_for_access_keys(
            {
                "user_id": tenant_id,
                "metadata.embedding_space_id": self.embedding_space_id,
                "metadata.state": "confirmed",
            },
            self.normalize_authorized_access_keys(authorized_access_keys),
        )

    def update_participant_state(self, memory_id: str, state: str, **timestamps):
        changes = {"state": state}
        changes.update(timestamps)
        self.memory.vector_store.set_exact_metadata(memory_id, changes)

    @staticmethod
    def snapshot_value(snapshot: dict) -> dict:
        payload = dict((snapshot or {}).get("payload") or {})
        return {
            "id": str((snapshot or {}).get("id") or ""),
            "memory": str(payload.get("data") or payload.get("memory") or ""),
            "metadata": dict(payload.get("metadata") or {}),
            "created_at": payload.get("created_at"),
            "updated_at": payload.get("updated_at"),
        }

    def mark_mutation_in_doubt(self, receipt: dict, stage: str):
        receipt["status"] = "in_doubt"
        receipt["failure_stage"] = stage
        self.mutation_journal.save(receipt)

    def mutation_receipt_values(self, receipt: dict) -> list[dict]:
        if receipt.get("redacted"):
            if (
                str(receipt.get("operation") or "") == "delete"
                and str(receipt.get("status") or "") == "committed"
                and str(receipt.get("outcome") or "")
                == "active_point_deleted_and_journal_redacted"
            ):
                return []
            raise WorkerProtocolError(
                "conflict",
                "The durable receipt is a content-free terminal tombstone and cannot be replayed.",
            )
        tenant_id = str(receipt.get("tenant_id") or "")
        mutation = str(receipt.get("operation") or "")
        target_id = str(receipt.get("target_id") or "")
        request_id = str(receipt.get("mutation_request_id") or "")
        if mutation in {"add", "correct", "dispute"}:
            values = self.mutation_points(tenant_id, request_id)
            if len(values) != 1:
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The mutation receipt does not resolve to exactly one stored record.",
                    details={"mutationStatus": "in_doubt"},
                )
            metadata = self.validate_participant_record(values[0], tenant_id)
            expected_record_digest = str(receipt.get("expected_record_digest") or "")
            if (
                str(metadata.get("state") or "").lower() != "confirmed"
                or str(metadata.get("mutation_request_id") or "") != request_id
                or str(metadata.get("pending_mutation_request_id") or "")
                or not expected_record_digest
                or self.participant_record_contract_digest(metadata)
                != expected_record_digest
            ):
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The mutation successor is not in its exact committed request state.",
                    details={"mutationStatus": "in_doubt"},
                )
            if mutation == "correct" and (
                str(metadata.get("corrects_memory_id") or "") != target_id
                or str(metadata.get("supersedes_memory_id") or "") != target_id
            ):
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The correction successor does not have the exact target lineage.",
                    details={"mutationStatus": "in_doubt"},
                )
            if mutation == "dispute" and str(
                metadata.get("disputes_memory_id") or ""
            ) != target_id:
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The dispute successor does not have the exact target lineage.",
                    details={"mutationStatus": "in_doubt"},
                )
            if mutation in {"correct", "dispute"}:
                target = self.participant_owned(tenant_id, target_id)
                if target is None:
                    raise WorkerProtocolError(
                        "mutation_in_doubt",
                        "The mutation target is missing after successor commit.",
                        details={"mutationStatus": "in_doubt"},
                    )
                target_metadata = self.validate_participant_record(target, tenant_id)
                expected_state = "superseded" if mutation == "correct" else "disputed"
                if (
                    str(target_metadata.get("state") or "").lower() != expected_state
                    or str(target_metadata.get("last_mutation_request_id") or "")
                    != request_id
                    or str(target_metadata.get("pending_mutation_request_id") or "")
                ):
                    raise WorkerProtocolError(
                        "mutation_in_doubt",
                        "The mutation target does not match the exact committed request state.",
                        details={"mutationStatus": "in_doubt"},
                    )
            return values
        if mutation in {"revoke", "archive"}:
            value = self.participant_owned(tenant_id, target_id)
            if value is None:
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The mutation target is missing after commit.",
                    details={"mutationStatus": "in_doubt"},
                )
            metadata = self.validate_participant_record(value, tenant_id)
            if (
                str(metadata.get("state") or "").lower() != mutation + "d"
                or str(metadata.get("last_mutation_request_id") or "") != request_id
                or str(metadata.get("pending_mutation_request_id") or "")
            ):
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The mutation target no longer matches its committed receipt.",
                    details={"mutationStatus": "in_doubt"},
                )
            return [value]
        if mutation == "delete":
            if self.participant_owned(tenant_id, target_id) is not None:
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The deleted mutation target is present.",
                    details={"mutationStatus": "in_doubt"},
                )
            return [self.snapshot_value(dict(receipt.get("target_snapshot") or {}))]
        raise WorkerProtocolError("conflict", "The durable mutation receipt has an unknown operation.")

    def authorize_mutation_receipt(
        self,
        receipt: dict,
        tenant_id: str,
        authorized_keys: list[str],
        request: dict,
    ):
        if receipt.get("redacted"):
            current_principal = str(
                request.get("requestingParticipantReference") or ""
            )
            if (
                receipt.get("tenant_id_hash")
                != self.mutation_journal._digest(tenant_id)
                or receipt.get("requesting_participant_hash")
                != self.mutation_journal._digest(current_principal)
                or request.get("requestingParticipantAuthenticated") is not True
            ):
                raise WorkerProtocolError(
                    "permission_denied",
                    "The redacted mutation receipt requires the exact authenticated participant.",
                )
            return
        if str(receipt.get("tenant_id") or "") != tenant_id:
            raise WorkerProtocolError("permission_denied", "The mutation receipt is outside this tenant.")
        target_snapshot = dict(receipt.get("target_snapshot") or {})
        if target_snapshot:
            value = self.snapshot_value(target_snapshot)
        else:
            values = self.mutation_points(tenant_id, str(receipt.get("mutation_request_id") or ""))
            if len(values) == 1:
                value = values[0]
            else:
                authorization_snapshot = dict(receipt.get("authorization_snapshot") or {})
                if not authorization_snapshot:
                    stored_principal = str(
                        receipt.get("requesting_participant_reference") or ""
                    )
                    current_principal = str(
                        request.get("requestingParticipantReference") or ""
                    )
                    stored_keys = set(
                        self.normalize_authorized_access_keys(
                            receipt.get("authorized_access_keys") or []
                        )
                    )
                    current_keys = set(
                        self.normalize_authorized_access_keys(authorized_keys)
                    )
                    requires_authentication = str(
                        receipt.get("operation") or ""
                    ) != "add"
                    if (
                        not stored_principal
                        or stored_principal != current_principal
                        or not stored_keys.intersection(current_keys)
                        or (
                            requires_authentication
                            and (
                                receipt.get("requesting_participant_authenticated")
                                is not True
                                or request.get("requestingParticipantAuthenticated")
                                is not True
                            )
                        )
                    ):
                        raise WorkerProtocolError(
                            "permission_denied",
                            "The mutation receipt cannot be authorized exactly.",
                        )
                    return
                value = self.snapshot_value(authorization_snapshot)
        self.validate_participant_record(value, tenant_id)
        self.require_participant_access(value, authorized_keys)
        if str(receipt.get("operation") or "") != "add":
            self.require_authenticated_target_actor(request, value)

    def committed_mutation_response(self, receipt: dict, roster_revision: str, *, reconciled=False):
        try:
            values = self.mutation_receipt_values(receipt)
        except WorkerProtocolError as error:
            self.enrich_mutation_error(error, receipt)
            raise
        return self.participant_ok(
            "The existing committed participant-memory receipt was reconciled."
            if reconciled
            else "Participant-memory mutation committed.",
            roster_revision,
            values,
            mutationRequestId=receipt["mutation_request_id"],
            mutationStatus="committed",
            mutationOperation=receipt["operation"],
            reconciled=reconciled,
            deletionFinalized=(
                receipt.get("redacted") is True
                and str(receipt.get("operation") or "") == "delete"
                and str(receipt.get("outcome") or "")
                == "active_point_deleted_and_journal_redacted"
            ),
        )

    @staticmethod
    def enrich_mutation_error(error: WorkerProtocolError, receipt: dict):
        error.details.setdefault("mutationRequestId", receipt.get("mutation_request_id"))
        error.details.setdefault("mutationStatus", receipt.get("status"))
        error.details["mutationOperation"] = receipt.get("operation")

    def mutation_is_fully_committed(self, receipt: dict) -> bool:
        if (
            str(receipt.get("operation") or "") == "delete"
            and receipt.get("redacted") is not True
        ):
            # A nonredacted delete is only staged. Committed deletion requires
            # the content-free final tombstone written after authority recheck.
            return False
        try:
            values = self.mutation_receipt_values(receipt)
            if str(receipt.get("operation") or "") in {"add", "correct", "dispute"}:
                value = values[0]
                receipt["created_ids"] = [str(value.get("id") or "")]
                receipt["authorization_snapshot"] = {
                    "id": str(value.get("id") or ""),
                    "payload": {"metadata": dict(value.get("metadata") or {})},
                }
            return True
        except (WorkerProtocolError, EmbeddingSpaceMismatchError):
            return False

    def mutation_is_confirmed_no_effect(self, receipt: dict) -> bool:
        if str(receipt.get("status") or "") != "prepared":
            return False
        tenant_id = str(receipt.get("tenant_id") or "")
        request_id = str(receipt.get("mutation_request_id") or "")
        mutation = str(receipt.get("operation") or "")
        target_id = str(receipt.get("target_id") or "")
        if self.mutation_points(tenant_id, request_id):
            return False
        if mutation == "add":
            return True
        snapshot = dict(receipt.get("target_snapshot") or {})
        current = self.memory.vector_store.exact_point_snapshot(target_id)
        if not snapshot:
            return current is None or self.participant_owned(tenant_id, target_id) is not None
        return current == snapshot

    def rollback_exact_partial_mutation(self, receipt: dict) -> bool:
        tenant_id = str(receipt.get("tenant_id") or "")
        request_id = str(receipt.get("mutation_request_id") or "")
        mutation = str(receipt.get("operation") or "")
        target_id = str(receipt.get("target_id") or "")
        status = str(receipt.get("status") or "")
        try:
            successors = self.mutation_points(tenant_id, request_id, maximum=33)
            if len(successors) > 32:
                return False
            recorded_successor_ids = [
                str(value or "") for value in receipt.get("created_ids") or []
            ]
            actual_successor_ids = [str(value.get("id") or "") for value in successors]
            recorded_set = set(recorded_successor_ids)
            actual_set = set(actual_successor_ids)
            missing_after_started_rollback = (
                status == "rollback_started"
                and mutation in {"add", "correct", "dispute"}
                and len(recorded_successor_ids) == 1
                and not actual_successor_ids
            )
            if missing_after_started_rollback:
                target_is_already_restored = mutation == "add" or (
                    bool(receipt.get("target_snapshot"))
                    and self.memory.vector_store.exact_point_snapshot(target_id)
                    == receipt.get("target_snapshot")
                )
                missing_after_started_rollback = (
                    target_is_already_restored
                    and not self.mutation_journal.has_other_active_reference(
                        request_id,
                        recorded_successor_ids,
                    )
                )
            if (
                any(not value for value in recorded_successor_ids)
                or len(recorded_successor_ids) != len(set(recorded_successor_ids))
                or (
                    recorded_successor_ids
                    and recorded_set != actual_set
                    and not missing_after_started_rollback
                )
                or (
                    status == "rollback_started"
                    and mutation in {"add", "correct", "dispute"}
                    and len(recorded_successor_ids) != 1
                )
            ):
                # Once a creation receipt records its exact successor, absence or
                # replacement can mean a later mutation owns that lineage. Never
                # resurrect the prior target or delete a replacement by guessing.
                return False
            expected_digest = str(receipt.get("expected_record_digest") or "")
            successor_metadata = []
            for successor in successors:
                metadata = self.validate_participant_record(successor, tenant_id)
                if (
                    str(metadata.get("mutation_request_id") or "") != request_id
                    or str(metadata.get("pending_mutation_request_id") or "")
                    or str(metadata.get("last_mutation_request_id") or "")
                    not in {"", request_id}
                    or not expected_digest
                    or self.participant_record_contract_digest(metadata)
                    != expected_digest
                ):
                    return False
                successor_metadata.append(metadata)
            if mutation == "add":
                for successor in successors:
                    self.memory.delete(str(successor.get("id") or ""))
                return not self.mutation_points(tenant_id, request_id)

            snapshot = dict(receipt.get("target_snapshot") or {})
            if not snapshot:
                return status == "prepared" and not successors
            current_snapshot = self.memory.vector_store.exact_point_snapshot(target_id)
            if current_snapshot is None:
                if mutation != "delete" and status not in {
                    "target_changed",
                    "in_doubt",
                }:
                    return False
                self.memory.vector_store.restore_exact_point(snapshot)
            else:
                current = self.participant_owned(tenant_id, target_id)
                if current is None:
                    return False
                metadata = self.validate_participant_record(current, tenant_id)
                exact_snapshot_unchanged = current_snapshot == snapshot
                owned_by_request = (
                    str(metadata.get("pending_mutation_request_id") or "")
                    == request_id
                    or str(metadata.get("last_mutation_request_id") or "")
                    == request_id
                )
                if not exact_snapshot_unchanged and not owned_by_request:
                    return False
                if not exact_snapshot_unchanged:
                    self.memory.vector_store.replace_exact_payload(
                        target_id,
                        snapshot["payload"],
                    )
            for successor in successors:
                self.memory.delete(str(successor.get("id") or ""))
            restored = self.memory.vector_store.exact_point_snapshot(target_id)
            return restored == snapshot and not self.mutation_points(
                tenant_id,
                request_id,
            )
        except (WorkerProtocolError, EmbeddingSpaceMismatchError, KeyError, ValueError):
            return False

    def resume_started_rollback(self, receipt: dict, roster_revision: str, *, reconciled: bool):
        if str(receipt.get("status") or "") != "rollback_started":
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The durable receipt does not contain an active rollback intent.",
            )
        if not self.rollback_exact_partial_mutation(receipt):
            raise WorkerProtocolError(
                "conflict",
                "Rollback stopped because the exact point or lineage is now owned by a later mutation.",
                details={
                    "mutationRequestId": receipt.get("mutation_request_id"),
                    "mutationStatus": "rollback_started",
                    "mutationOperation": receipt.get("operation"),
                },
            )
        receipt["status"] = "rolled_back"
        receipt["rolled_back_utc"] = datetime.now(timezone.utc).isoformat()
        receipt["rollback_roster_revision"] = roster_revision
        self.mutation_journal.save(receipt)
        return self.participant_ok(
            "Participant-memory active state rolled back; the durable audit receipt was retained.",
            roster_revision,
            mutationRequestId=receipt["mutation_request_id"],
            mutationStatus="rolled_back",
            mutationOperation=receipt["operation"],
            reconciled=reconciled,
        )

    @participant_single_writer
    def handle_participant_reconcile(self, request: dict, tenant_id: str, roster_revision: str):
        request_id = self.mutation_journal.require_request_id(request.get("mutationRequestId"))
        receipt = self.mutation_journal.load(request_id)
        if receipt is None:
            raise WorkerProtocolError("not_found", "No durable mutation receipt exists for that request ID.")
        try:
            self.authorize_mutation_receipt(
                receipt,
                tenant_id,
                list(request.get("authorizedAccessKeys") or [])[:16],
                request,
            )
        except WorkerProtocolError as error:
            self.enrich_mutation_error(error, receipt)
            raise
        status = str(receipt.get("status") or "in_doubt")
        if status == "rollback_started":
            # A durable rollback intent is monotonic. Reconciliation may resume
            # the exact undo, but it must never promote this receipt back to a
            # committed or staged-delete state.
            return self.resume_started_rollback(receipt, roster_revision, reconciled=True)
        if (
            receipt.get("redacted") is not True
            and str(receipt.get("operation") or "") == "delete"
            and status != "rolled_back"
            and receipt.get("target_snapshot")
            and self.participant_owned(
                tenant_id,
                str(receipt.get("target_id") or ""),
            ) is None
        ):
            # A crash may occur after the active point is deleted but before the
            # delete_staged receipt is fsynced. Recover that exact state without
            # ever promoting a content-bearing receipt to committed.
            if status != "delete_staged":
                receipt["status"] = "delete_staged"
                receipt["delete_staged_utc"] = datetime.now(timezone.utc).isoformat()
                self.mutation_journal.save(receipt)
            status = "delete_staged"
        if status == "erased_by_later_delete":
            raise WorkerProtocolError(
                "conflict",
                "The receipt's stored content was erased by a later authenticated delete.",
                details={
                    "mutationRequestId": request_id,
                    "mutationStatus": status,
                    "mutationOperation": receipt.get("operation"),
                },
            )
        if status == "recovery_expired":
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "The bounded exact rollback window expired; content recovery is unavailable.",
                details={
                    "mutationRequestId": request_id,
                    "mutationStatus": status,
                    "mutationOperation": receipt.get("operation"),
                },
            )
        if status == "committed":
            return self.committed_mutation_response(receipt, roster_revision, reconciled=True)
        if status == "delete_staged":
            if str(receipt.get("operation") or "") != "delete":
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "A non-delete receipt has an invalid staged-delete state.",
                )
            target_id = str(receipt.get("target_id") or "")
            if self.participant_owned(tenant_id, target_id) is not None:
                raise WorkerProtocolError(
                    "mutation_in_doubt",
                    "The staged deletion target is still present.",
                    details={"mutationStatus": "in_doubt"},
                )
            if request.get("finalizeDelete") is True:
                receipt = self.mutation_journal.finalize_delete(receipt)
                return self.committed_mutation_response(
                    receipt,
                    roster_revision,
                    reconciled=True,
                )
            return self.participant_ok(
                "The deletion is staged and awaits final authority revalidation.",
                roster_revision,
                self.mutation_receipt_values(receipt),
                mutationRequestId=request_id,
                mutationStatus="delete_staged",
                mutationOperation="delete",
                reconciled=True,
                deletionFinalized=False,
            )
        if status == "rolled_back":
            return self.participant_ok(
                "The participant-memory mutation was already rolled back.",
                roster_revision,
                mutationRequestId=request_id,
                mutationStatus="rolled_back",
                mutationOperation=receipt["operation"],
                reconciled=True,
            )
        if self.mutation_is_confirmed_no_effect(receipt):
            receipt["status"] = "rolled_back"
            receipt["no_effect_confirmed"] = True
            receipt["reconciled_utc"] = datetime.now(timezone.utc).isoformat()
            self.mutation_journal.save(receipt)
            return self.participant_ok(
                "The participant-memory mutation was confirmed to have no durable effect.",
                roster_revision,
                mutationRequestId=request_id,
                mutationStatus="rolled_back",
                mutationOperation=receipt["operation"],
                reconciled=True,
            )
        if self.mutation_is_fully_committed(receipt):
            receipt["status"] = "committed"
            receipt["reconciled_utc"] = datetime.now(timezone.utc).isoformat()
            self.mutation_journal.save(receipt)
            return self.committed_mutation_response(receipt, roster_revision, reconciled=True)
        if self.rollback_exact_partial_mutation(receipt):
            receipt["status"] = "rolled_back"
            receipt["no_effect_confirmed"] = True
            receipt["reconciled_utc"] = datetime.now(timezone.utc).isoformat()
            self.mutation_journal.save(receipt)
            return self.participant_ok(
                "The exact partial participant-memory mutation was rolled back without reapplying it.",
                roster_revision,
                mutationRequestId=request_id,
                mutationStatus="rolled_back",
                mutationOperation=receipt["operation"],
                reconciled=True,
            )
        raise WorkerProtocolError(
            "mutation_in_doubt",
            "The mutation has no complete commit receipt; reconcile did not reapply it.",
            retryable=True,
            details={
                "mutationRequestId": request_id,
                "mutationStatus": status,
                "mutationOperation": receipt["operation"],
            },
        )

    @participant_single_writer
    def handle_participant_rollback(self, request: dict, tenant_id: str, roster_revision: str):
        request_id = self.mutation_journal.require_request_id(request.get("mutationRequestId"))
        receipt = self.mutation_journal.load(request_id)
        if receipt is None:
            raise WorkerProtocolError("not_found", "No durable mutation receipt exists for that request ID.")
        try:
            self.authorize_mutation_receipt(
                receipt,
                tenant_id,
                list(request.get("authorizedAccessKeys") or [])[:16],
                request,
            )
        except WorkerProtocolError as error:
            self.enrich_mutation_error(error, receipt)
            raise
        status = str(receipt.get("status") or "in_doubt")
        if receipt.get("redacted"):
            raise WorkerProtocolError(
                "conflict",
                "A content-free terminal mutation receipt cannot be rolled back.",
                details={
                    "mutationRequestId": request_id,
                    "mutationStatus": status,
                    "mutationOperation": receipt.get("operation"),
                },
            )
        if status == "rolled_back":
            return self.participant_ok(
                "The participant-memory mutation was already rolled back.",
                roster_revision,
                mutationRequestId=request_id,
                mutationStatus="rolled_back",
                mutationOperation=receipt["operation"],
            )
        mutation = str(receipt.get("operation") or "")
        if status not in {"committed", "rollback_started"} and not (
            mutation == "delete" and status == "delete_staged"
        ):
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "Only a complete committed mutation can be rolled back safely.",
                details={
                    "mutationRequestId": request_id,
                    "mutationStatus": status,
                    "mutationOperation": receipt["operation"],
                },
            )

        try:
            if status != "rollback_started":
                # Prove the committed state before recording the crash-resumable undo
                # stage. No Qdrant write occurs before this durable marker.
                committed_values = self.mutation_receipt_values(receipt)
                if mutation in {"add", "correct", "dispute"}:
                    committed_value = committed_values[0]
                    receipt["created_ids"] = [str(committed_value.get("id") or "")]
                    receipt["authorization_snapshot"] = {
                        "id": str(committed_value.get("id") or ""),
                        "payload": {
                            "metadata": dict(committed_value.get("metadata") or {})
                        },
                    }
                receipt["status"] = "rollback_started"
                receipt["rollback_started_utc"] = datetime.now(timezone.utc).isoformat()
                self.mutation_journal.save(receipt)
            return self.resume_started_rollback(
                receipt,
                roster_revision,
                reconciled=False,
            )
        except WorkerProtocolError:
            raise
        except Exception as error:
            self.mark_mutation_in_doubt(receipt, "rollback")
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "Participant-memory rollback is incomplete; deliberate repair is required.",
                details={
                    "mutationRequestId": request_id,
                    "mutationStatus": "in_doubt",
                    "mutationOperation": receipt["operation"],
                },
            ) from error

    def acquire_mutation_lock(self, target_id: str, tenant_id: str, request_id: str, mutation: str):
        value = self.participant_owned(tenant_id, target_id)
        if value is None:
            raise WorkerProtocolError("not_found", "The exact participant memory is unavailable in this tenant.")
        metadata = self.validate_participant_record(value, tenant_id)
        existing = str(metadata.get("pending_mutation_request_id") or "")
        if existing and existing != request_id:
            raise WorkerProtocolError("conflict", "Another exact mutation already owns this target.")
        if not existing:
            self.memory.vector_store.set_exact_metadata(
                target_id,
                {
                    "pending_mutation_request_id": request_id,
                    "pending_mutation_kind": mutation,
                    "pending_mutation_utc": datetime.now(timezone.utc).isoformat(),
                },
            )

    @participant_single_writer
    def handle_participant_mutation(self, request: dict, tenant_id: str, roster_revision: str):
        request_id = self.mutation_journal.require_request_id(request.get("mutationRequestId"))
        proposal = dict(request.get("proposal") or {})
        mutation = str(proposal.get("operation") or "").strip().lower()
        if mutation not in {"add", "correct", "dispute", "revoke", "archive", "delete"}:
            raise WorkerProtocolError("invalid_request", "The participant-memory mutation is unsupported.")
        target_id = str(proposal.get("targetMemoryId") or "").strip()
        if mutation != "add" and not target_id:
            raise WorkerProtocolError("invalid_request", "An exact target memory ID is required.")
        access_keys = [str(value) for value in (request.get("accessKeys") or [])][:16]
        authorized_keys = self.normalize_authorized_access_keys(
            request.get("authorizedAccessKeys") or []
        )
        if mutation in {"add", "correct", "dispute"} and not set(access_keys).intersection(
            authorized_keys
        ):
            raise WorkerProtocolError(
                "permission_denied",
                "The requesting participant is outside the proposed memory audience.",
            )
        fingerprint = self.mutation_fingerprint(request, request_id)
        receipt = self.mutation_journal.load(request_id)
        receipt_is_new = receipt is None
        if receipt_is_new:
            self.mutation_journal.require_fresh_mutation_request_id(request_id)
        if receipt is not None:
            if receipt.get("redacted"):
                self.authorize_mutation_receipt(
                    receipt,
                    tenant_id,
                    authorized_keys,
                    request,
                )
                if (
                    str(receipt.get("status") or "") == "committed"
                    and str(receipt.get("operation") or "") == "delete"
                    and mutation == "delete"
                    and receipt.get("target_id_hash")
                    == self.mutation_journal._digest(target_id)
                ):
                    return self.committed_mutation_response(
                        receipt,
                        roster_revision,
                        reconciled=True,
                    )
                raise WorkerProtocolError(
                    "conflict",
                    "The stable mutation request ID belongs to a content-free terminal receipt and cannot be reapplied.",
                    details={
                        "mutationRequestId": request_id,
                        "mutationStatus": receipt.get("status"),
                        "mutationOperation": receipt.get("operation"),
                    },
                )
            if str(receipt.get("fingerprint") or "") != fingerprint:
                raise WorkerProtocolError("conflict", "The stable mutation request ID was reused with different content.")
            durable_status = str(receipt.get("status") or "")
            if durable_status == "rollback_started":
                self.authorize_mutation_receipt(receipt, tenant_id, authorized_keys, request)
                self.resume_started_rollback(receipt, roster_revision, reconciled=True)
                raise WorkerProtocolError(
                    "conflict",
                    "The stable mutation request was already directed to roll back and cannot be reapplied.",
                    details={
                        "mutationRequestId": request_id,
                        "mutationStatus": "rolled_back",
                        "mutationOperation": receipt.get("operation"),
                    },
                )
            if durable_status == "committed":
                self.authorize_mutation_receipt(receipt, tenant_id, authorized_keys, request)
                return self.committed_mutation_response(receipt, roster_revision, reconciled=True)
            if durable_status == "delete_staged":
                self.authorize_mutation_receipt(receipt, tenant_id, authorized_keys, request)
                if mutation != "delete" or self.participant_owned(tenant_id, target_id) is not None:
                    raise WorkerProtocolError(
                        "mutation_in_doubt",
                        "The staged delete receipt no longer matches its exact absent target.",
                        details={
                            "mutationRequestId": request_id,
                            "mutationStatus": "in_doubt",
                            "mutationOperation": receipt.get("operation"),
                        },
                    )
                return self.participant_ok(
                    "The deletion is staged and awaits final authority revalidation.",
                    roster_revision,
                    self.mutation_receipt_values(receipt),
                    mutationRequestId=request_id,
                    mutationStatus="delete_staged",
                    mutationOperation="delete",
                    reconciled=True,
                    deletionFinalized=False,
                )
            if durable_status != "rolled_back" and self.mutation_is_fully_committed(receipt):
                self.authorize_mutation_receipt(receipt, tenant_id, authorized_keys, request)
                receipt["status"] = "committed"
                receipt["reconciled_utc"] = datetime.now(timezone.utc).isoformat()
                self.mutation_journal.save(receipt)
                return self.committed_mutation_response(receipt, roster_revision, reconciled=True)
            if durable_status in {"rolled_back", "in_doubt"}:
                raise WorkerProtocolError(
                    "conflict",
                    "The stable mutation request cannot be reapplied in its durable state.",
                    details={
                        "mutationRequestId": request_id,
                        "mutationStatus": receipt.get("status"),
                        "mutationOperation": receipt.get("operation"),
                    },
                )
        else:
            receipt = {
                "mutation_request_id": request_id,
                "fingerprint": fingerprint,
                "tenant_id": tenant_id,
                "roster_revision": roster_revision,
                "operation": mutation,
                "target_id": target_id,
                "status": "prepared",
                "created_ids": [],
                "authorized_access_keys": authorized_keys,
                "requesting_participant_reference": request.get(
                    "requestingParticipantReference"
                ),
                "requesting_participant_authenticated": request.get(
                    "requestingParticipantAuthenticated"
                ) is True,
                "started_utc": datetime.now(timezone.utc).isoformat(),
            }

        metadata = None
        display_text = None
        if mutation in {"add", "correct", "dispute"}:
            display_text = str(proposal.get("text") or "").strip()
            if not display_text:
                raise WorkerProtocolError("invalid_request", "Participant-memory text is empty.")
        if mutation == "add":
            metadata = self.participant_metadata(
                tenant_id,
                proposal,
                access_keys,
                request_id,
                dict(request.get("provenance") or {}),
                list(request.get("consentReceipts") or []),
            )
            self.bind_expected_record_contract(receipt, metadata)
            receipt.setdefault(
                "authorization_snapshot",
                {"id": "pending", "payload": {"metadata": metadata}},
            )
            # Persist only after every no-side-effect Add validation succeeds.
            self.mutation_journal.save(receipt)
        else:
            current = self.participant_owned(tenant_id, target_id)
            if current is None:
                raise WorkerProtocolError("not_found", "The exact participant memory is unavailable in this tenant.")
            current_metadata = self.validate_participant_record(current, tenant_id)
            self.require_participant_access(current, authorized_keys)
            self.require_authenticated_target_actor(request, current)
            if not receipt.get("target_snapshot"):
                if str(current_metadata.get("state") or "").lower() != "confirmed":
                    raise WorkerProtocolError("conflict", "The exact participant memory is no longer current.")
                receipt["target_snapshot"] = self.memory.vector_store.exact_point_snapshot(target_id)
            if mutation in {"correct", "dispute"}:
                metadata = self.participant_metadata(
                    tenant_id,
                    proposal,
                    access_keys,
                    request_id,
                    dict(request.get("provenance") or {}),
                    list(request.get("consentReceipts") or []),
                )
                original_metadata = dict(receipt["target_snapshot"]["payload"].get("metadata") or {})
                for field in (
                    "visibility",
                    "sensitivity",
                    "audience_participant_references",
                    "access_keys",
                ):
                    if metadata.get(field) != original_metadata.get(field):
                        raise WorkerProtocolError(
                            "permission_denied",
                            "A correction or dispute cannot silently change the exact target audience.",
                        )
                metadata["state"] = "candidate"
                metadata["confirmed_utc"] = None
                if mutation == "correct":
                    metadata["corrects_memory_id"] = target_id
                    metadata["supersedes_memory_id"] = target_id
                    metadata["corrected_utc"] = datetime.now(timezone.utc).isoformat()
                else:
                    metadata["disputes_memory_id"] = target_id
                self.bind_expected_record_contract(receipt, metadata)
            current_state = str(current_metadata.get("state") or "").lower()
            current_last_request = str(current_metadata.get("last_mutation_request_id") or "")
            resumable_state = (
                "superseded" if mutation == "correct" else "disputed"
            ) if mutation in {"correct", "dispute"} else mutation + "d"
            if receipt_is_new or receipt.get("status") == "prepared":
                # Persist only after target, access, actor, state, audience, and
                # successor-contract validation, and before the Qdrant lock write.
                self.mutation_journal.save(receipt)
            if current_state == "confirmed":
                self.acquire_mutation_lock(target_id, tenant_id, request_id, mutation)
            elif not (current_state == resumable_state and current_last_request == request_id):
                raise WorkerProtocolError(
                    "conflict",
                    "The exact target state no longer belongs to this mutation.",
                )
            if receipt.get("status") == "prepared":
                receipt["status"] = "locked"
                self.mutation_journal.save(receipt)

        try:
            if mutation in {"add", "correct", "dispute"}:
                successors = self.mutation_points(tenant_id, request_id)
                if not successors:
                    self.memory.add(
                        self.embedding_document_prefix + display_text,
                        user_id=tenant_id,
                        metadata=metadata,
                        infer=False,
                    )
                    successors = self.mutation_points(tenant_id, request_id)
                if len(successors) != 1:
                    cleanup_complete = True
                    for duplicate in successors:
                        try:
                            self.memory.delete(str(duplicate.get("id") or ""))
                        except Exception:
                            cleanup_complete = False
                    if mutation in {"correct", "dispute"} and receipt.get("target_snapshot"):
                        try:
                            self.memory.vector_store.replace_exact_payload(
                                target_id,
                                receipt["target_snapshot"]["payload"],
                            )
                        except Exception:
                            cleanup_complete = False
                    if self.mutation_points(tenant_id, request_id):
                        cleanup_complete = False
                    self.mark_mutation_in_doubt(
                        receipt,
                        "successor_count_cleaned" if cleanup_complete else "successor_count",
                    )
                    raise WorkerProtocolError(
                        "mutation_in_doubt",
                        "The mutation did not produce exactly one attributable successor; it was not committed.",
                        details={"mutationRequestId": request_id, "mutationStatus": "in_doubt"},
                    )
                successor = successors[0]
                successor_metadata = self.validate_participant_record(successor, tenant_id)
                receipt["created_ids"] = [str(successor.get("id") or "")]
                receipt["authorization_snapshot"] = {
                    "id": str(successor.get("id") or ""),
                    "payload": {"metadata": successor_metadata},
                }
                # Persist the real successor ID before any later state transition. A
                # crash after Mem0 add must still let reconciliation and authenticated
                # deletion find and redact the content-bearing creation receipt.
                self.mutation_journal.save(receipt)
                if mutation == "add":
                    if str(successor_metadata.get("state") or "").lower() != "confirmed":
                        self.mark_mutation_in_doubt(receipt, "add_state")
                        raise WorkerProtocolError("mutation_in_doubt", "The added participant memory is not confirmed.")
                else:
                    lineage_field = "corrects_memory_id" if mutation == "correct" else "disputes_memory_id"
                    if (
                        str(successor_metadata.get(lineage_field) or "") != target_id
                        or str(successor_metadata.get("state") or "").lower() not in {"candidate", "confirmed"}
                    ):
                        self.mark_mutation_in_doubt(receipt, "successor_lineage")
                        raise WorkerProtocolError("mutation_in_doubt", "The mutation successor lineage is inconsistent.")
                    receipt["status"] = "successor_staged"
                    self.mutation_journal.save(receipt)
                    current = self.participant_owned(tenant_id, target_id)
                    if current is None:
                        self.mark_mutation_in_doubt(receipt, "target_missing")
                        raise WorkerProtocolError("mutation_in_doubt", "The exact prior target is missing.")
                    current_metadata = self.validate_participant_record(current, tenant_id)
                    desired_state = "superseded" if mutation == "correct" else "disputed"
                    if str(current_metadata.get("state") or "").lower() == "confirmed":
                        if str(current_metadata.get("pending_mutation_request_id") or "") != request_id:
                            self.mark_mutation_in_doubt(receipt, "target_lock")
                            raise WorkerProtocolError("mutation_in_doubt", "The exact target mutation lock was lost.")
                        if len(self.mutation_points(tenant_id, request_id)) != 1:
                            self.mark_mutation_in_doubt(receipt, "pre_state_successor_count")
                            raise WorkerProtocolError("mutation_in_doubt", "The prior state was preserved because successor count changed.")
                        self.update_participant_state(
                            target_id,
                            desired_state,
                            corrected_utc=datetime.now(timezone.utc).isoformat() if mutation == "correct" else None,
                            last_mutation_request_id=request_id,
                            pending_mutation_request_id=None,
                            pending_mutation_kind=None,
                        )
                    elif not (
                        str(current_metadata.get("state") or "").lower() == desired_state
                        and str(current_metadata.get("last_mutation_request_id") or "") == request_id
                    ):
                        self.mark_mutation_in_doubt(receipt, "target_state")
                        raise WorkerProtocolError("mutation_in_doubt", "The exact target state changed outside this mutation.")
                    receipt["status"] = "target_changed"
                    self.mutation_journal.save(receipt)
                    if str(successor_metadata.get("state") or "").lower() == "candidate":
                        self.update_participant_state(
                            str(successor.get("id") or ""),
                            "confirmed",
                            confirmed_utc=datetime.now(timezone.utc).isoformat(),
                            last_mutation_request_id=request_id,
                        )
            elif mutation in {"revoke", "archive"}:
                current = self.participant_owned(tenant_id, target_id)
                if current is None:
                    raise WorkerProtocolError("mutation_in_doubt", "The exact mutation target is missing.")
                current_metadata = self.validate_participant_record(current, tenant_id)
                desired_state = mutation + "d"
                if str(current_metadata.get("state") or "").lower() == "confirmed":
                    if str(current_metadata.get("pending_mutation_request_id") or "") != request_id:
                        raise WorkerProtocolError("mutation_in_doubt", "The exact target mutation lock was lost.")
                    self.update_participant_state(
                        target_id,
                        desired_state,
                        **{
                            f"{mutation}d_utc": datetime.now(timezone.utc).isoformat(),
                            "last_mutation_request_id": request_id,
                            "pending_mutation_request_id": None,
                            "pending_mutation_kind": None,
                        },
                    )
                elif not (
                    str(current_metadata.get("state") or "").lower() == desired_state
                    and str(current_metadata.get("last_mutation_request_id") or "") == request_id
                ):
                    raise WorkerProtocolError("mutation_in_doubt", "The exact target state changed outside this mutation.")
            elif mutation == "delete":
                current = self.participant_owned(tenant_id, target_id)
                if current is not None:
                    current_metadata = self.validate_participant_record(current, tenant_id)
                    if str(current_metadata.get("pending_mutation_request_id") or "") != request_id:
                        raise WorkerProtocolError("mutation_in_doubt", "The exact target mutation lock was lost.")
                    self.memory.delete(target_id)
                if self.participant_owned(tenant_id, target_id) is not None:
                    raise WorkerProtocolError("mutation_in_doubt", "The exact target remains after delete.")
        except WorkerProtocolError:
            raise
        except Exception as error:
            # Keep the last durable stage so a retry with the same stable request ID
            # can inspect/reconcile and resume without guessing or duplicating work.
            receipt["last_failure_stage"] = "apply"
            self.mutation_journal.save(receipt)
            durable_status = str(receipt.get("status") or "prepared")
            raise WorkerProtocolError(
                "mutation_in_doubt",
                "Participant-memory mutation completion is uncertain; reconcile or roll back deliberately.",
                retryable=True,
                details={
                    "mutationRequestId": request_id,
                    "mutationStatus": durable_status,
                    "mutationOperation": mutation,
                },
            ) from error

        if mutation == "delete":
            receipt["status"] = "delete_staged"
            receipt["delete_staged_utc"] = datetime.now(timezone.utc).isoformat()
            self.mutation_journal.save(receipt)
            return self.participant_ok(
                "The deletion is staged and awaits final authority revalidation.",
                roster_revision,
                self.mutation_receipt_values(receipt),
                mutationRequestId=request_id,
                mutationStatus="delete_staged",
                mutationOperation="delete",
                reconciled=False,
                deletionFinalized=False,
            )

        # Validate the exact final state before making the terminal commit marker.
        # If this process dies after validation, reconciliation can observe and commit;
        # it must never strand an invalid record behind a committed receipt.
        self.mutation_receipt_values(receipt)
        receipt["status"] = "committed"
        receipt["committed_utc"] = datetime.now(timezone.utc).isoformat()
        self.mutation_journal.save(receipt)
        return self.committed_mutation_response(receipt, roster_revision)

    @participant_single_writer
    def handle_participant_repair(self, request: dict, tenant_id: str, roster_revision: str):
        repair_request_id = self.mutation_journal.require_request_id(
            request.get("repairRequestId")
        )
        requested_ids = request.get("repairPointIds")
        if not isinstance(requested_ids, list) or not requested_ids or len(requested_ids) > 32:
            raise WorkerProtocolError(
                "invalid_request",
                "Deliberate hybrid repair requires one to 32 exact point IDs.",
            )
        point_ids = []
        for value in requested_ids:
            point_id = str(value or "").strip()
            if not point_id or len(point_id) > 128 or any(ord(char) < 32 for char in point_id):
                raise WorkerProtocolError("invalid_request", "A hybrid repair point ID is malformed.")
            if point_id not in point_ids:
                point_ids.append(point_id)
        authorized_keys = self.normalize_authorized_access_keys(
            request.get("authorizedAccessKeys") or []
        )
        if not authorized_keys:
            raise WorkerProtocolError("permission_denied", "Deliberate hybrid repair requires exact participant authority.")

        repairable_ids = []
        authorization_failed_ids = []
        for point_id in point_ids:
            try:
                value = self.participant_owned(tenant_id, point_id)
                if value is None:
                    raise WorkerProtocolError(
                        "not_found",
                        "An exact hybrid repair point is unavailable in this tenant.",
                    )
                self.validate_participant_record(value, tenant_id)
                self.require_participant_access(value, authorized_keys)
                repairable_ids.append(point_id)
            except (WorkerProtocolError, EmbeddingSpaceMismatchError, ValueError, TypeError):
                # One bad or unauthorized ID must not block other explicitly
                # authorized IDs in the same bounded repair request.
                authorization_failed_ids.append(point_id)

        repair = (
            self.memory.vector_store.repair_hybrid_indexed(repairable_ids)
            if repairable_ids
            else {"status": "degraded", "updated": 0, "unchanged": 0, "failed": 0, "failed_ids": []}
        )
        failed_ids = list(dict.fromkeys(
            authorization_failed_ids + list(repair.get("failed_ids") or [])
        ))[:32]
        repair["failed_ids"] = failed_ids
        repair["failed"] = len(failed_ids)
        repair["status"] = "ready" if not failed_ids else "degraded"
        repair["requested"] = len(point_ids)
        repair["authorized"] = len(repairable_ids)
        status = self.hybrid_status(tenant_id, authorized_keys)
        return self.participant_ok(
            "Deliberate participant-memory hybrid repair completed.",
            roster_revision,
            degraded=status.get("status") != "ready" or bool(failed_ids),
            hybridIndex=status,
            repair=repair,
            repairRequestId=repair_request_id,
        )

    def handle_participant(self, operation: str, request: dict) -> dict:
        tenant_id = str(request.get("tenantId", "")).strip()
        roster_revision = str(request.get("rosterRevision", "")).strip()
        if not tenant_id or not roster_revision or len(tenant_id) > 128 or len(roster_revision) > 256:
            raise WorkerProtocolError("invalid_request", "Participant memory requires exact bounded tenant and roster identities.")

        if operation == "participant_health":
            authorized_keys = self.normalize_authorized_access_keys(
                request.get("authorizedAccessKeys") or []
            )
            if not authorized_keys:
                raise WorkerProtocolError(
                    "permission_denied",
                    "Participant-memory health requires exact participant authority.",
                )
            hybrid = self.hybrid_status(tenant_id, authorized_keys)
            return self.participant_ok(
                "Fresh participant-aware Mem0 and Qdrant storage is ready."
                if hybrid.get("status") == "ready"
                else "Participant memory is available with degraded hybrid coverage.",
                roster_revision,
                count=hybrid.get("total", 0),
                degraded=hybrid.get("status") != "ready",
                hybridIndex=hybrid,
                embeddingAvailable=True,
                mem0Available=True,
                qdrantAvailable=True,
            )

        if operation == "participant_reconcile_mutation":
            return self.handle_participant_reconcile(request, tenant_id, roster_revision)
        if operation == "participant_rollback_mutation":
            return self.handle_participant_rollback(request, tenant_id, roster_revision)
        if operation == "participant_repair_hybrid":
            return self.handle_participant_repair(request, tenant_id, roster_revision)

        if operation == "participant_list":
            maximum = max(1, min(int(request.get("maximumResults", 8)), 8))
            authorized_keys = self.normalize_authorized_access_keys(
                request.get("authorizedAccessKeys") or []
            )
            if not authorized_keys:
                return self.participant_ok("No participant-memory audience is authorized.", roster_revision)
            eligible: dict[str, dict] = {}
            for access_key in authorized_keys:
                # Qdrant applies tenant, embedding, lifecycle, and one exact authorized
                # audience key before any record is admitted to the bounded inventory.
                for point in self.memory.vector_store.scroll_exact_filters(
                    {
                        "user_id": tenant_id,
                        "metadata.embedding_space_id": self.embedding_space_id,
                        "metadata.state": "confirmed",
                        "metadata.access_keys": str(access_key),
                    }
                ):
                    value = self.value_from_point(point)
                    self.validate_participant_record(value, tenant_id, current=True)
                    self.require_participant_access(value, authorized_keys)
                    eligible[str(value.get("id") or "")] = value
                    if len(eligible) > maximum:
                        oldest_id = min(
                            eligible,
                            key=lambda point_id: (
                                str((eligible[point_id].get("metadata") or {}).get("created_utc") or ""),
                                point_id,
                            ),
                        )
                        del eligible[oldest_id]
            ordered = sorted(
                eligible.values(),
                key=lambda value: (
                    str((value.get("metadata") or {}).get("created_utc") or ""),
                    str(value.get("id") or ""),
                ),
                reverse=True,
            )[:maximum]
            hybrid = self.hybrid_status(tenant_id, authorized_keys)
            return self.participant_ok(
                "Participant-memory inventory loaded without semantic scoring.",
                roster_revision,
                ordered,
                degraded=hybrid.get("status") != "ready",
                hybridIndex=hybrid,
            )

        if operation == "participant_recall":
            query = str(request.get("query", "")).strip()
            if not query:
                return self.participant_ok("No participant-memory query was supplied.", roster_revision)
            if len(query) > 4096:
                raise WorkerProtocolError(
                    "invalid_request",
                    "Participant-memory recall queries may contain at most 4096 characters.",
                )
            maximum = max(1, min(int(request.get("maximumResults", 5)), 8))
            access_keys = self.normalize_authorized_access_keys(request.get("accessKeys") or [])
            if not access_keys:
                return self.participant_ok("No participant-memory audience is authorized.", roster_revision)

            # Every dense/BM25 query is constrained to one already-authorized key.
            # Results are never scored first and filtered for privacy afterward.
            eligible: dict[str, dict] = {}
            embedded_query = self.embedding_query_prefix + query
            dense_vector = self.memory.embedding_model.embed(embedded_query, "search")
            for access_key in access_keys:
                response = self.memory.vector_store.search_exact_hybrid(
                    query,
                    dense_vector,
                    {
                        "user_id": tenant_id,
                        "metadata.access_keys": str(access_key),
                        "metadata.state": "confirmed",
                        "metadata.embedding_space_id": self.embedding_space_id,
                    },
                    maximum,
                )
                for scored in response:
                    payload = dict(scored.get("payload") or {})
                    value = {
                        "id": str(scored.get("id") or ""),
                        "memory": str(payload.get("data") or payload.get("memory") or ""),
                        "metadata": dict(payload.get("metadata") or {}),
                        "created_at": payload.get("created_at"),
                        "updated_at": payload.get("updated_at"),
                        "score": scored.get("score"),
                        "score_details": dict(scored.get("score_details") or {}),
                    }
                    self.validate_participant_record(value, tenant_id, current=True)
                    memory_id = str(value.get("id", ""))
                    previous = eligible.get(memory_id)
                    if previous is None or float(value.get("score") or 0) > float(previous.get("score") or 0):
                        eligible[memory_id] = value

            ordered = sorted(
                eligible.values(),
                key=lambda value: float(value.get("score") or 0),
                reverse=True,
            )[:maximum]
            hybrid = self.hybrid_status(tenant_id, access_keys)
            return self.participant_ok(
                "Participant-aware memory recall complete."
                if hybrid.get("status") == "ready"
                else "Participant-aware recall completed with degraded hybrid coverage.",
                roster_revision,
                ordered,
                degraded=hybrid.get("status") != "ready",
                hybridIndex=hybrid,
            )

        if operation == "participant_mutate":
            return self.handle_participant_mutation(request, tenant_id, roster_revision)
        raise WorkerProtocolError("invalid_request", "The participant-memory operation is unsupported.")

    def handle(self, request: dict) -> dict:
        operation = str(request.get("operation", "")).strip().lower()
        self.require_embedding_space(request)
        participant_operations = {
            "participant_health",
            "participant_list",
            "participant_recall",
            "participant_mutate",
            "participant_reconcile_mutation",
            "participant_rollback_mutation",
            "participant_repair_hybrid",
        }
        if operation in participant_operations:
            return self.handle_participant(operation, request)
        # This worker owns the fresh CP11 participant collection. Legacy active-user
        # operations must never read, infer into, update, or delete records here.
        raise WorkerProtocolError(
            "legacy_operation_rejected",
            "Legacy user-memory operations cannot enter the participant-memory collection.",
        )

    def participant_ok(self, message: str, roster_revision: str, memories=None, count=None, **details):
        values = [
            participant_item(value, self.embedding_space_id)
            for value in (memories or [])
            if value
        ]
        response = {
            "success": True,
            "message": message,
            "memories": [],
            "participantMemories": values,
            "count": len(values) if count is None else count,
            "embeddingSpaceId": self.embedding_space_id,
            "rosterRevision": roster_revision,
        }
        response.update(details)
        hybrid = details.get("hybridIndex")
        repair = details.get("repair")
        if isinstance(hybrid, dict):
            response["degradedPointCount"] = int(hybrid.get("pending") or 0)
            response["failedPointIds"] = list(hybrid.get("pending_ids") or [])[:32]
            response["deliberateRepairAvailable"] = bool(hybrid.get("repair_available"))
        if isinstance(repair, dict):
            response["updatedPointCount"] = int(repair.get("updated") or 0)
            response["unchangedPointCount"] = int(repair.get("unchanged") or 0)
            response["requestedPointCount"] = int(repair.get("requested") or 0)
            response["degradedPointCount"] = int(repair.get("failed") or 0)
            response["failedPointIds"] = list(repair.get("failed_ids") or [])[:32]
        return response


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--collection", required=True)
    parser.add_argument("--llm-endpoint", required=True)
    parser.add_argument("--llm-model", required=True)
    parser.add_argument("--llm-output-tokens", type=int, required=True)
    parser.add_argument("--embedding-provider", required=True)
    parser.add_argument("--embedding-api-base", required=True)
    parser.add_argument("--embedding-model", required=True)
    parser.add_argument("--embedding-dimensions", type=int, required=True)
    parser.add_argument("--embedding-space-id", required=True)
    parser.add_argument("--embedding-protocol", required=True)
    parser.add_argument("--embedding-resolved-model", required=True)
    parser.add_argument("--embedding-quantization", required=True)
    parser.add_argument("--embedding-context-tokens", type=int, required=True)
    parser.add_argument("--embedding-query-prompt-mode", required=True)
    parser.add_argument("--embedding-document-prompt-mode", required=True)
    parser.add_argument("--embedding-query-prefix", required=True)
    parser.add_argument("--embedding-document-prefix", required=True)
    parser.add_argument("--qdrant-host", required=True)
    parser.add_argument("--qdrant-port", type=int, required=True)
    parser.add_argument("--qdrant-grpc-port", type=int, required=True)
    parser.add_argument("--qdrant-use-tls", choices=("true", "false"), required=True)
    parser.add_argument("--qdrant-api-key-environment-variable", required=True)
    worker = Worker(parser.parse_args())
    for line in sys.stdin:
        if not line.strip():
            continue
        request_id = None
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise WorkerProtocolError("invalid_request", "The worker request must be a JSON object.")
            request_id = request.get("id")
            if not isinstance(request_id, str) or len(request_id) > 128:
                request_id = None
            response = worker.handle(request)
        except EmbeddingSpaceMismatchError:
            response = {
                "success": False,
                "message": "The request targets a different embedding space.",
                "memories": [],
                "participantMemories": [],
                "errorCode": "embedding_space_mismatch",
            }
        except WorkerProtocolError as error:
            response = {
                "success": False,
                "message": error.safe_message[:256],
                "memories": [],
                "participantMemories": [],
                "errorCode": error.error_code[:64],
                "retryable": error.retryable,
            }
            response.update(error.details)
        except PermissionError:
            response = {
                "success": False,
                "message": "Participant-memory access was denied.",
                "memories": [],
                "participantMemories": [],
                "errorCode": "permission_denied",
            }
        except Exception:  # process boundary must never expose paths, prompts, text, or stack details
            response = {
                "success": False,
                "message": "Participant memory failed safely at the worker boundary.",
                "memories": [],
                "participantMemories": [],
                "errorCode": "internal_error",
            }
        response["id"] = request_id
        response["embeddingSpaceId"] = worker.embedding_space_id
        PROTOCOL_STDOUT.write(json.dumps(response, separators=(",", ":")) + "\n")
        PROTOCOL_STDOUT.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
