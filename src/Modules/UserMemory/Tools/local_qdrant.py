"""Ali's local Qdrant adapter for Mem0.

Mem0's server adapter automatically builds indexes for four multi-tenant fields.
Ali has a small local collection and always supplies a strict user filter, so a
scan is both safe and fast. Skipping those optional indexes also avoids Qdrant's
Windows gridstore payload-index corruption path during process termination.
"""

from mem0.vector_stores.qdrant import Qdrant


class LocalQdrant(Qdrant):
    def _create_filter_indexes(self):
        return
