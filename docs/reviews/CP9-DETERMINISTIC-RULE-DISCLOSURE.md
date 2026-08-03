# CP9 deterministic-rule disclosure

Checkpoint 9 does not add deterministic interpretation of user English, task routing,
task decomposition, relevance selection, tool selection, continuation decisions, or answer
construction. Those decisions remain in the single model-driven Agent Framework loop.

The following mechanical rules were added or made explicit for runtime/provider plumbing.
They do not inspect user prose and cannot authorize a tool call by themselves.

1. Endpoint admission
   - Loopback HTTP/HTTPS remains allowed.
   - Private IP endpoints require the existing private-LAN setting.
   - A public/unresolved host requires explicit remote consent, HTTPS, and the
     `OpenAI-compatible/Custom` engine.
   - URL user information is refused. Remote HTTP is refused.
   - Query strings and fragments are refused on runtime base URLs.
   - These rules can prevent inventory, health, or model requests. They cannot choose a task or tool.

2. Remote credential handling
   - A configured environment variable is checked first; otherwise the Windows-current-user
     DPAPI file is used.
   - A Bearer header is attached only for an admitted remote endpoint. A missing key or an
     existing Authorization header fails closed.
   - Redirects are disabled and ordinary platform certificate-chain/hostname validation remains
     enabled.
   - Settings, capability profiles, binding digests, and health summaries contain credential
     references or state only, never the API-key value.

3. Provider inventory projection
   - Inventory rows are accepted from the provider's typed model-list arrays and de-duplicated by
     exact model ID.
   - Context, output, tool, vision, tokenizer, rolling-window, and thinking-convention values are
     taken from provider metadata or explicit settings. Missing values remain unknown/default;
     model names do not select runtime behavior.
   - Embedding-only rows are withheld from the chat-model selector by typed capability/type
     metadata and the existing embedding-ID safeguards.

4. Functional capability probing
   - The native-tool probe requires one typed call to a fixed probe function with the exact nonce
     `ali-runtime-capability-v1`.
   - The compatibility probe requires an exact two-field JSON-schema response containing the
     fixed nonce `ali-structured-decision-v1` and `accepted: true`.
   - Reasoning, streaming, and vision observations are tri-state: supported, unsupported, or
     unknown. A disabled/manual capability is not silently reported as proven.
   - Native tools are selected only after the native probe succeeds. Structured compatibility is
     selected only after its schema probe succeeds. Otherwise the profile is chat-only.

5. Exact capability and dispatch identity
   - A SHA-256 identity is computed from provider, endpoint, model, selected protocol, tokenizer,
     rolling-window mode, context/output budgets, sampling/streaming/thinking settings, and the
     observed capability states.
   - Runtime, model, and generation bindings carry the same protocol/capability identities.
   - The orchestration planner refuses autonomous engineering when those identities disagree,
     are unprobed, or identify a chat-only protocol.

6. Load/unload ownership
   - Lemonade retains its explicit Ali-owned load/readiness/unload lifecycle.
   - LM Studio, Ollama, llama.cpp, and custom OpenAI-compatible providers remain provider-owned;
     Ali disconnects without unloading their models.
   - This engine setting controls lifecycle calls only; it does not infer user intent.

7. Operator-selected generation behavior
   - Context and output values are sent as the selected positive values without a model-name
     ceiling.
   - Thinking convention is an explicit setting/provider metadata value. The enabled toggle and
     reasoning effort are applied only through that selected typed convention.
   - The static selector ladders are UI choices, not provider limits; provider-reported values are
     added and preferred when present.

No fixed global attempt limit, keyword router, prose scraper, or hardcoded task decomposition was
added by CP9.
