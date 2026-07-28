"""Compatibility-spike equivalent of Ali's local Qdrant adapter."""

from mem0.vector_stores.qdrant import Qdrant


class LocalQdrant(Qdrant):
    def _create_filter_indexes(self):
        return
