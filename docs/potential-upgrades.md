# Potential Upgrades

These are optional improvements to evaluate only after the current stable behavior demonstrates a concrete need. They are not committed runtime requirements.

## CPU-only result reranking

- **Status:** Deferred; current Nomic, Qdrant, Tree-sitter, and ripgrep retrieval is working.
- **Purpose:** Reorder a small, bounded candidate set when several local-document or code results are similarly relevant.
- **Scope:** Local Knowledge and code retrieval only. Keep personal-memory recall on its current low-latency path.
- **Resource policy:** CPU and system RAM only; do not consume additional GPU VRAM.
- **Candidate implementations:** FastEmbed `TextCrossEncoder`, FlashRank, or a Lemonade reranker explicitly configured with the CPU llama.cpp backend.
- **Safety requirements:** Optional setting, short timeout, bounded candidate count, fail-safe fallback to the original Qdrant/ripgrep ordering, and an Ali-specific benchmark before adoption.

