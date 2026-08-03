# CP10 deterministic-rule disclosure

Checkpoint 10 does not add deterministic interpretation of user English, task routing,
task decomposition, relevance selection, tool selection, continuation decisions, or answer
construction. Ali's single model-driven Agent Framework loop remains authoritative. Image input,
retrieval, memory, and MCP tools only add evidence or callable capabilities to that loop.

The following mechanical rules were added or made explicit. They do not inspect user prose and
cannot authorize a tool call by themselves.

1. Typed embedding roles and prompt formatting
   - `EmbeddingInputRole.StoredDocument` selects the configured document prompt mode.
   - `EmbeddingInputRole.RetrievalQuery` selects the configured query prompt mode.
   - `SearchDocument` prepends exactly `search_document: `; `SearchQuery` prepends exactly
     `search_query: `; `Plain` preserves the input.
   - RAG chunks and semantic tool drawer descriptions use `StoredDocument`; retrieval queries use
     `RetrievalQuery`.
   - Mem0's typed `search` action uses the configured query mode; its typed `add` and `update`
     actions use the configured document mode.
   - These mappings affect only embedding request representation. They do not decide what content
     is relevant or whether a memory/tool should be used.

2. Exact embedding-space identity
   - SHA-256 identities bind provider, exact endpoint, selected model ID, dimensions, protocol,
     effective context, and effective prompt mode.
   - RAG, semantic tool, and Mem0 identities additionally bind their Qdrant target fields.
   - A changed binding selects a separate Mem0 space or rebuilds an affected vector index before
     use. It does not migrate, reinterpret, or silently mix vectors.

3. Functional context probe
   - The embedding settings test builds the selected number of bounded lexical probe units and
     sends them through the configured query prompt mode.
   - The default selected setting is 8,192; it is editable and part of the binding identity.
   - Success requires one endpoint response with the exact configured vector dimensions. The
     probe does not infer a model's tokenizer or claim that a provider accepted an independently
     verified tokenizer-specific token count.

4. Advisory Vision capability probe
   - A successful one-pixel image request records Vision as supported.
   - Empty, transport, malformed-response, and ordinary HTTP failures record Vision as unknown and
     preserve the operator's manual Vision setting.
   - Only typed HTTP `415 Unsupported Media Type` or `422 Unprocessable Entity` responses are
     treated as stability refusals that disable Vision for that activation. The saved manual
     setting is not rewritten.
   - This rule affects image admission for the current runtime activation only. It does not infer
     capability from a model name or image content.

5. Optional-service visibility
   - Memory, RAG/semantic, MCP, and bridge status pills are present only when their corresponding
     saved enable settings require them.
   - Disabled semantic retrieval uses the bounded live registry without an unavailable warning.
   - MCP tool drawers, including Medieval Chess Arena when configured, are built only from the
     current enabled live registry. Disabled/absent tools are not synthesized.
   - These settings affect availability and status display only; the model still decides whether
     an enabled capability is relevant and whether to call it.

6. Attachment and progress presentation
   - The composer image control copies the current clipboard bitmap into the existing bounded
     attachment flow and displays its preview before sending.
   - Existing activity receipts choose their human display summary mechanically; file paths are
     shortened to filenames when the directory adds no display value.
   - Wrapping, disabled horizontal scrolling, and clipping constrain presentation only. They do
     not change tool output or evidence retained by the turn.

No keyword/prose router, prose scraper, hardcoded decomposition, fixed global attempt limit,
hardcoded embedding model ID, or provider-selected endpoint/port behavior was added by CP10.
