# CP10 Nomic roles, Vision, and UI verification report

Date: 2026-08-03

Branch: `codex/checkpoint-10-nomic-vision-ui`

Starting commit: `355ad14249833b07df0b1564cb9f53b9f3417fce`

Direct parent branch: `codex/checkpoint-9-runtime-experience`

## Result

CP10 adds settings-bound Nomic v1.5 document/query roles across local knowledge, semantic tool
retrieval, and Mem0; a selected 8,192-context functional probe; exact embedding binding identity;
advisory Vision capability handling; and a visible pasted-image composer experience. It preserves
one authoritative model-driven Agent Framework loop.

## Implemented surfaces

- Embedding provider labels no longer supply a hardcoded endpoint, port, loaded model ID,
  dimensions, or provider behavior. The exact endpoint/model/dimensions/protocol/context/roles are
  visible saved settings.
- Stored RAG chunks, semantic tool drawer descriptions, and Mem0 add/update content use the typed
  document role. Retrieval queries use the typed query role.
- Nomic-compatible modes format exact `search_document:` and `search_query:` prefixes; `Plain`
  remains an explicit settings choice for other models.
- Binding identities and Qdrant payloads cover provider, endpoint, model, dimensions, protocol,
  effective context, and prompt role so incompatible vector spaces cannot be silently mixed.
- The settings test sends a full selected-context query probe and displays its effective role and
  binding identity.
- Mem0 installs a role-aware OpenAI-compatible embedder that consumes Mem0's typed `add`, `search`,
  and `update` action rather than scraping prose.
- Vision probing is advisory. Inconclusive failures preserve the manual setting; typed HTTP 415 or
  422 stability refusals disable Vision for that activation without rewriting saved settings.
- The composer now has an image control beside the text input, and the existing attachment area
  previews pasted screenshots with filename, retention, and removal controls.
- The receipt/activity summary remains directly above the composer, preserves useful receipt
  details, prefers filenames for display, and wraps without horizontal scrolling. The top status
  surface clips bounded overflow.
- Memory, RAG/semantic, MCP, and bridge pills are settings-driven and hidden when disabled. Semantic
  retrieval produces no unavailable warning while disabled. Medieval Chess Arena remains an
  ordinary optional MCP drawer when its enabled tools appear in the live registry.
- Obsolete Ali/Aider/OpenHands assistant-selection radios remain absent. Ali remains the sole
  user-facing programming assistant.

## Release verification

Successful gates at the time of this report:

- Release app build: succeeded with 0 warnings and 0 errors.
- CP10-focused Release tests: 62 passed, 0 failed, 0 skipped.
- `git diff --check`: passed.

The focused suite covers exact document/query prefixes, 8,192-context request construction,
dimension enforcement, binding identity, legacy settings preservation, Mem0 process arguments and
space identity, semantic disabled-state behavior, RAG/semantic fingerprint coverage, advisory and
typed-refusal Vision behavior, composer preview/layout, optional status visibility, and the absence
of retired assistant radios.

An earlier broad filtered run also selected machine-integration tests in the entire local-knowledge
test class. Five remain unavailable in this checkout because its Release test output does not
contain the pinned Qdrant/ripgrep runtime assets. One CP10 test assertion was corrected before the
green run. Those machine-integration failures are not reported as CP10 passes.

## Architecture and safety checks

- No deterministic English interpretation, relevance routing, tool choice, continuation decision,
  or answer construction was added.
- Vision adds image evidence to the existing text/tool/evidence path; it does not create another
  assistant or orchestration loop.
- Semantic retrieval proposes bounded live tool drawers only. The model remains responsible for
  relevance and tool selection.
- Disabled optional capabilities do not emit unavailable warnings or create Medieval Chess/MCP
  tools that are absent from the enabled live registry.
- Identity and Viewport source foundations were not modified.
- No merge, rebase, cherry-pick, shared shortcut change, or desktop launch was performed.

## Deterministic mechanics

See [CP10-DETERMINISTIC-RULE-DISCLOSURE.md](CP10-DETERMINISTIC-RULE-DISCLOSURE.md).

## Known limitations and deferred gates

- No live Nomic embedding model, Mem0 Python runtime, Qdrant binary, LM Studio, Lemonade, Ollama,
  llama.cpp, GPU, camera, or multimodal model was available in this isolated checkout. The pass
  proves bounded contracts with fake HTTP responses and source/UI tests, not live model quality,
  hardware performance, or real 8,192-token tokenizer acceptance.
- Python syntax execution was not available because no Python executable is present in this
  checkout or on this task's command path. The worker contract is compile-inspected by C# tests but
  has not been launched.
- No camera-derived feature, identity recognition, liveness, authentication, consent, or medical
  claim is made by the image attachment or Vision capability probe.
- Live integrated desktop verification remains deferred to the root CP13 merge. The shared desktop
  shortcut was neither modified nor launched.
