"""Ali's local Qdrant adapter for Mem0.

Mem0's server adapter automatically builds indexes for four multi-tenant fields.
Ali has a small local collection and always supplies a strict user filter, so a
scan is both safe and fast. Skipping those optional indexes also avoids Qdrant's
Windows gridstore payload-index corruption path during process termination.
"""

import os
from datetime import datetime, timezone
from pathlib import Path

from qdrant_client.models import PointVectors
from mem0.utils.lemmatization import lemmatize_for_bm25
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

    def ensure_hybrid_indexed(self) -> int:
        """Backfill BM25 vectors for pre-FastEmbed memories without replacing dense vectors."""
        offset = None
        pending = []
        while True:
            points, offset = self.client.scroll(
                collection_name=self.collection_name,
                limit=128,
                offset=offset,
                with_payload=True,
                with_vectors=True,
            )
            for point in points:
                vectors = point.vector or {}
                has_bm25 = isinstance(vectors, dict) and vectors.get("bm25") is not None
                if not has_bm25:
                    pending.append(point)
            if offset is None:
                break

        if not pending:
            return 0
        if not self._has_bm25_slot:
            raise RuntimeError(
                f"Collection '{self.collection_name}' has memories but no BM25 sparse-vector slot"
            )
        if self._get_bm25_encoder() is None:
            raise RuntimeError("FastEmbed BM25 encoder is unavailable; hybrid memory indexing cannot start")

        updated = 0
        for point in pending:
            payload = dict(point.payload or {})
            text = str(payload.get("data") or payload.get("text_lemmatized") or "").strip()
            if not text:
                continue
            lemmatized = lemmatize_for_bm25(text)
            sparse = self._encode_bm25(lemmatized)
            if sparse is None:
                raise RuntimeError(f"FastEmbed could not encode memory {point.id}")
            self.client.update_vectors(
                collection_name=self.collection_name,
                points=[PointVectors(id=point.id, vector={"bm25": sparse})],
            )
            self.client.set_payload(
                collection_name=self.collection_name,
                payload={
                    "text_lemmatized": lemmatized,
                    "hybrid_indexed_utc": datetime.now(timezone.utc).isoformat(),
                },
                points=[point.id],
            )
            updated += 1
        return updated
