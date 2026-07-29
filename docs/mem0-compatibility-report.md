# Ali local long-term memory compatibility report

## Verified stack

- Mem0 `2.0.12`
- Qdrant server `1.18.2`
- Qdrant Python client `1.18.0`
- Lemonade server `11.5.0` at a loopback-only OpenAI-compatible endpoint
- GPT-OSS `gpt-oss-20b-mxfp4-GGUF`, explicitly loaded with an 8192-token context
- `nomic-embed-text-v1-GGUF`, verified at 768 embedding dimensions
- Portable CPython `3.12.10`

Qdrant 1.18.2 was selected because its upstream release fixes loading
unfinished segment optimizations after cancellation or restart, which directly
matches a Windows restart failure found during Ali's persistence test.

The compatibility spike report is written to
`artifacts/mem0-spike/compatibility-report.json`. The successful persistence
run used a local NTFS temporary directory. Qdrant's mutable data directory must
remain under Ali's local AppData tree and must not be placed in a OneDrive or
other cloud-synchronized directory.

## Verified behaviors

- Add durable memory through Lemonade inference
- Retrieve a paraphrased fact with a strict active-user filter
- Keep two users isolated
- Correct and delete a memory
- List only the active user's memories
- Persist through a worker and Qdrant restart
- Bound recall results
- Reject non-loopback LLM, embedding, and Qdrant configuration
- Disable Mem0 and PostHog telemetry before importing Mem0

Ali registers a process-local subclass of Mem0's Qdrant adapter that skips its
four optional multi-tenant payload indexes. Strict `user_id` filters still run
on every read, but the small personal-memory collection uses a scan rather than
Qdrant's Windows gridstore payload-index path. This was required for reliable
restart recovery after Windows terminates the managed Qdrant process.

## Architecture and safety boundary

Ali resolves one stable local profile ID before calling memory. Tool schemas and
MCP schemas never accept caller-supplied user IDs. The C# adapter injects the
active user, and the Python worker repeats ownership checks before mutation.
Memory recall is time- and result-bounded before the model call. Automatic
learning runs only after the visible answer and is fail-safe.

Mem0 runs as a private stdio child process; it opens no network listener. Its
LLM and embedding calls may target only loopback. Qdrant is a local child
service. Camera and voice observations are not authentication and do not select
or merge profiles automatically.

## Hybrid retrieval helpers

The portable installation now includes spaCy, `en_core_web_sm`, FastEmbed, and
the offline Qdrant/BM25 model data. These CPU-side helpers provide
lemmatization and sparse lexical retrieval alongside semantic embeddings; they
do not consume Ali's limited GPU memory. The publish-folder smoke test imports
the packages, loads the English model, and produces an offline sparse vector.

## Reproduction

Run `tools/RestoreRuntimeAssets.ps1`, restore and build `Ali.sln`, run the
framework test project, and run `tools/mem0-spike/mem0_compatibility_spike.py`
against isolated local Qdrant storage. The manifest and requirements lock file
are the source of truth for portable dependencies.
