"""Ali's local Qdrant adapter for Mem0.

Mem0's server adapter automatically builds indexes for four multi-tenant fields.
Ali has a small local collection and always supplies a strict user filter, so a
scan is both safe and fast. Skipping those optional indexes also avoids Qdrant's
Windows gridstore payload-index corruption path during process termination.
"""

import math
import os
import unicodedata
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

from qdrant_client.models import (
    FieldCondition,
    Filter,
    MatchValue,
    PointStruct,
    PointVectors,
)
from mem0.vector_stores.qdrant import Qdrant


class LocalQdrant(Qdrant):
    def _create_filter_indexes(self):
        return

    def _get_bm25_encoder(self):
        """Load Ali's pinned English-only BM25 data without any network fallback."""
        if self._bm25_encoder is None:
            from fastembed import SparseTextEmbedding

            cache_root = Path(os.environ["FASTEMBED_CACHE_PATH"])
            revision = (cache_root / "models--Qdrant--bm25" / "refs" / "main").read_text().strip()
            model_path = cache_root / "models--Qdrant--bm25" / "snapshots" / revision
            if not (model_path / "english.txt").is_file():
                raise RuntimeError(f"Pinned FastEmbed English BM25 data is missing: {model_path}")
            self._bm25_encoder = SparseTextEmbedding(
                model_name="Qdrant/bm25",
                specific_model_path=str(model_path),
                local_files_only=True,
            )
        return self._bm25_encoder

    def exact_point(self, point_id: str, with_vectors: bool = False):
        values = self.client.retrieve(
            collection_name=self.collection_name,
            ids=[point_id],
            with_payload=True,
            with_vectors=with_vectors,
        )
        return values[0] if values else None

    @staticmethod
    def _json_value(value):
        if hasattr(value, "model_dump"):
            return value.model_dump(mode="json")
        if isinstance(value, dict):
            return {str(key): LocalQdrant._json_value(item) for key, item in value.items()}
        if isinstance(value, (list, tuple)):
            return [LocalQdrant._json_value(item) for item in value]
        return value

    def exact_point_snapshot(self, point_id: str):
        """Return a JSON-safe exact payload/vector snapshot for explicit rollback."""
        point = self.exact_point(point_id, with_vectors=True)
        if point is None:
            return None
        return {
            "id": str(point.id),
            "payload": self._json_value(dict(point.payload or {})),
            "vector": self._json_value(point.vector or {}),
        }

    def restore_exact_point(self, snapshot: dict):
        """Restore one absent exact point; never overwrite an unrelated current point."""
        point_id = str((snapshot or {}).get("id") or "").strip()
        vector = (snapshot or {}).get("vector")
        payload = (snapshot or {}).get("payload")
        if not point_id or not isinstance(vector, (dict, list)) or not isinstance(payload, dict):
            raise ValueError("The rollback snapshot is incomplete")
        if self.exact_point(point_id) is not None:
            raise FileExistsError("The rollback target already exists")
        self.client.upsert(
            collection_name=self.collection_name,
            points=[PointStruct(id=point_id, vector=vector, payload=payload)],
            wait=True,
        )
        return self.exact_point(point_id)

    def replace_exact_payload(self, point_id: str, payload: dict):
        """Replace, rather than merge, a payload so rollback removes later fields too."""
        if self.exact_point(point_id) is None:
            raise KeyError("The exact rollback target was not found")
        self.client.overwrite_payload(
            collection_name=self.collection_name,
            payload=dict(payload or {}),
            points=[point_id],
            wait=True,
        )

    def scroll_exact(self, field: str, value: str):
        yield from self.scroll_exact_filters({field: value})

    def scroll_exact_filters(self, exact_filters: dict, maximum: int | None = None):
        if maximum is not None and maximum <= 0:
            return
        offset = None
        yielded = 0
        while True:
            page_limit = 128 if maximum is None else min(128, maximum - yielded)
            points, offset = self.client.scroll(
                collection_name=self.collection_name,
                scroll_filter=Filter(
                    must=[
                        FieldCondition(key=str(field), match=MatchValue(value=value))
                        for field, value in exact_filters.items()
                    ]
                ),
                limit=page_limit,
                offset=offset,
                with_payload=True,
                with_vectors=False,
            )
            for point in points:
                yield point
                yielded += 1
                if maximum is not None and yielded >= maximum:
                    return
            if offset is None:
                break

    def count_exact_filters(self, exact_filters: dict) -> int:
        result = self.client.count(
            collection_name=self.collection_name,
            count_filter=Filter(
                must=[
                    FieldCondition(key=str(field), match=MatchValue(value=value))
                    for field, value in exact_filters.items()
                ]
            ),
            exact=True,
        )
        return int(result.count)

    @staticmethod
    def _lexical_tokens(text: str) -> list[str]:
        """Return language-neutral Unicode letter/digit runs without interpretation."""
        normalized = unicodedata.normalize("NFKC", str(text or "")).casefold()
        tokens = []
        current = []
        for character in normalized:
            if character.isalnum():
                current.append(character)
            elif current:
                tokens.append("".join(current))
                current = []
        if current:
            tokens.append("".join(current))
        return tokens

    @staticmethod
    def _bm25_parameters(query_tokens: list[str]) -> tuple[float, float]:
        terms = len(query_tokens) if query_tokens else 1
        if terms <= 3:
            return 5.0, 0.7
        if terms <= 6:
            return 7.0, 0.6
        if terms <= 9:
            return 9.0, 0.5
        if terms <= 15:
            return 10.0, 0.5
        return 12.0, 0.5

    @classmethod
    def _candidate_bm25_scores(cls, points: list, query_tokens: list[str]) -> dict[str, float]:
        """Score only dense-authorized candidates with language-neutral BM25 mechanics."""
        if not points or not query_tokens:
            return {}
        documents = []
        for point in points:
            payload = dict(point.payload or {})
            tokens = cls._lexical_tokens(payload.get("data") or payload.get("memory") or "")
            documents.append((str(point.id), Counter(tokens), len(tokens)))
        average_length = sum(length for _, _, length in documents) / len(documents)
        average_length = max(average_length, 1.0)
        unique_query_terms = list(dict.fromkeys(query_tokens))
        document_frequencies = {
            term: sum(1 for _, frequencies, _ in documents if frequencies.get(term, 0) > 0)
            for term in unique_query_terms
        }
        midpoint, steepness = cls._bm25_parameters(query_tokens)
        k1 = 1.5
        b = 0.75
        normalized_scores = {}
        for point_id, frequencies, length in documents:
            raw_score = 0.0
            for term in unique_query_terms:
                frequency = frequencies.get(term, 0)
                if frequency <= 0:
                    continue
                document_frequency = document_frequencies[term]
                inverse_document_frequency = math.log(
                    1.0
                    + (len(documents) - document_frequency + 0.5)
                    / (document_frequency + 0.5)
                )
                denominator = frequency + k1 * (
                    1.0 - b + b * length / average_length
                )
                raw_score += inverse_document_frequency * (
                    frequency * (k1 + 1.0) / denominator
                )
            if raw_score > 0:
                normalized_scores[point_id] = 1.0 / (
                    1.0 + math.exp(-steepness * (raw_score - midpoint))
                )
        return normalized_scores

    def search_exact_hybrid(
        self,
        query_text: str,
        dense_vector: list,
        exact_filters: dict,
        top_k: int,
    ) -> list[dict]:
        """Ali-owned dense+BM25 ranking with exact Qdrant prefilters.

        This deliberately bypasses Memory.search so participant recall never runs
        Mem0 entity extraction, entity embeddings, or entity-link boosts.
        """

        maximum = max(1, min(int(top_k), 8))
        internal_limit = max(maximum * 4, 60)
        semantic_results = self.search(
            query=query_text,
            vectors=dense_vector,
            top_k=internal_limit,
            filters=exact_filters,
        )
        query_tokens = self._lexical_tokens(query_text)
        bm25_scores = self._candidate_bm25_scores(semantic_results, query_tokens)

        divisor = 2.0 if bm25_scores else 1.0
        scored = []
        for point in semantic_results:
            semantic_score = float(getattr(point, "score", 0.0) or 0.0)
            if not math.isfinite(semantic_score):
                continue
            semantic_score = max(0.0, min(semantic_score, 1.0))
            if semantic_score < 0.1:
                continue
            point_id = str(point.id)
            bm25_score = float(bm25_scores.get(point_id, 0.0))
            if not math.isfinite(bm25_score):
                bm25_score = 0.0
            bm25_score = max(0.0, min(bm25_score, 1.0))
            combined = max(
                0.0,
                min((semantic_score + bm25_score) / divisor, 1.0),
            )
            scored.append({
                "id": point_id,
                "payload": dict(point.payload or {}),
                "score": combined,
                "score_details": {
                    "semantic_score": semantic_score,
                    "bm25_score": bm25_score,
                    "raw_score": semantic_score + bm25_score,
                    "max_possible_score": divisor,
                    "final_score": combined,
                    "threshold": 0.1,
                },
            })
        scored.sort(key=lambda value: (-float(value["score"]), value["id"]))
        return scored[:maximum]

    def set_exact_metadata(self, point_id: str, changes: dict):
        point = self.exact_point(point_id)
        if point is None:
            raise KeyError(f"Memory {point_id} was not found")
        metadata = dict((point.payload or {}).get("metadata") or {})
        metadata.update(changes)
        self.client.set_payload(
            collection_name=self.collection_name,
            payload={"metadata": metadata},
            points=[point_id],
        )

    @staticmethod
    def _has_bm25(point) -> bool:
        vectors = point.vector or {}
        return isinstance(vectors, dict) and vectors.get("bm25") is not None

    def inspect_hybrid_indexed(self, exact_filters: dict | None = None) -> dict:
        """Inspect hybrid coverage without mutating vectors at worker startup."""
        conditions = [
            FieldCondition(key=str(field), match=MatchValue(value=value))
            for field, value in (exact_filters or {}).items()
        ]
        scroll_filter = Filter(must=conditions) if conditions else None
        offset = None
        pending_ids = []
        total = 0
        while True:
            points, offset = self.client.scroll(
                collection_name=self.collection_name,
                scroll_filter=scroll_filter,
                limit=128,
                offset=offset,
                with_payload=True,
                with_vectors=True,
            )
            for point in points:
                total += 1
                if not self._has_bm25(point):
                    pending_ids.append(str(point.id))
            if offset is None:
                break
        return {
            "status": "ready" if not pending_ids else "degraded",
            "total": total,
            "pending": len(pending_ids),
            "pending_ids": pending_ids[:32],
            "repair_available": bool(pending_ids),
        }

    def inspect_hybrid_indexed_for_access_keys(
        self,
        exact_filters: dict,
        authorized_access_keys: list[str],
    ) -> dict:
        """Inspect only current points intersecting the caller's exact audience keys."""
        keys = list(dict.fromkeys(str(value) for value in authorized_access_keys if value))
        if not keys:
            return {
                "status": "ready",
                "total": 0,
                "pending": 0,
                "pending_ids": [],
                "repair_available": False,
            }
        seen_ids = set()
        pending_ids = []
        for access_key in keys:
            conditions = [
                FieldCondition(key=str(field), match=MatchValue(value=value))
                for field, value in exact_filters.items()
            ]
            conditions.append(
                FieldCondition(
                    key="metadata.access_keys",
                    match=MatchValue(value=access_key),
                )
            )
            offset = None
            while True:
                points, offset = self.client.scroll(
                    collection_name=self.collection_name,
                    scroll_filter=Filter(must=conditions),
                    limit=128,
                    offset=offset,
                    with_payload=False,
                    with_vectors=True,
                )
                for point in points:
                    point_id = str(point.id)
                    if point_id in seen_ids:
                        continue
                    seen_ids.add(point_id)
                    if not self._has_bm25(point):
                        pending_ids.append(point_id)
                if offset is None:
                    break
        return {
            "status": "ready" if not pending_ids else "degraded",
            "total": len(seen_ids),
            "pending": len(pending_ids),
            "pending_ids": pending_ids[:32],
            "repair_available": bool(pending_ids),
        }

    def repair_hybrid_indexed(self, point_ids: list[str]) -> dict:
        """Deliberately repair only caller-authorized exact point IDs."""
        if not isinstance(point_ids, list) or not point_ids or len(point_ids) > 32:
            raise ValueError("One to 32 exact repair points are required")
        exact_ids = []
        for value in point_ids:
            point_id = str(value or "").strip()
            if not point_id or len(point_id) > 128 or any(ord(char) < 32 for char in point_id):
                raise ValueError("A repair point ID is malformed")
            if point_id not in exact_ids:
                exact_ids.append(point_id)
        if not self._has_bm25_slot:
            raise RuntimeError("The participant collection has no BM25 sparse-vector slot")
        if self._get_bm25_encoder() is None:
            raise RuntimeError("FastEmbed BM25 encoder is unavailable; hybrid memory indexing cannot start")

        updated = 0
        unchanged = 0
        failed_ids = []
        for point_id in exact_ids:
            try:
                point = self.exact_point(point_id, with_vectors=True)
                if point is None:
                    raise KeyError("The repair point was not found")
                if self._has_bm25(point):
                    unchanged += 1
                    continue
                payload = dict(point.payload or {})
                text = str(payload.get("data") or payload.get("text_lemmatized") or "").strip()
                if not text:
                    raise ValueError("Memory text is empty")
                lexical_text = " ".join(self._lexical_tokens(text))
                if not lexical_text:
                    raise ValueError("Memory text has no Unicode letter/digit tokens")
                sparse = self._encode_bm25(lexical_text)
                if sparse is None:
                    raise RuntimeError("FastEmbed did not return a sparse vector")
                self.client.update_vectors(
                    collection_name=self.collection_name,
                    points=[PointVectors(id=point.id, vector={"bm25": sparse})],
                )
                self.client.set_payload(
                    collection_name=self.collection_name,
                    payload={
                        # Mem0 names this compatibility field text_lemmatized. Ali stores
                        # only its language-neutral token stream here; no lemma/stem/router.
                        "text_lemmatized": lexical_text,
                        "hybrid_indexed_utc": datetime.now(timezone.utc).isoformat(),
                    },
                    points=[point.id],
                )
                updated += 1
            except Exception:
                # Never install an empty sparse vector or silently omit the point.
                # The opaque point ID is safe for a deliberate repair receipt.
                failed_ids.append(str(point_id))
        return {
            "status": "ready" if not failed_ids else "degraded",
            "updated": updated,
            "unchanged": unchanged,
            "failed": len(failed_ids),
            "failed_ids": failed_ids[:32],
        }
