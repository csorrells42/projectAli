# Checkpoint 6 Deterministic-Rule Disclosure

## Boundary

Ali leaves request meaning, decomposition, tool choice, material-claim selection, and the decision to continue, pause, or finish to the configured model. Checkpoint 6 adds no English keyword intent router, phrase-list classifier, fixed task decomposition, deterministic answer finder, or prose-to-action parser.

The deterministic rules below are mechanical authority, integrity, safety, durability, availability, and resource-bound checks. A failed rule discards the proposed transition, preserves the last accepted state, or creates a typed recoverable pause. It does not reinterpret the request.

## Runtime and model-call binding

- Each planning or completion call must use one captured concrete client, provider/profile identity, model identity, context/output limits, generation settings, and runtime-binding digest.
- The durable state revision, control state, pending/final-publication state, and captured runtime bindings must still match at authorization time.
- A runtime change after capture cannot redirect an authorized call. A retry captures and authorizes a fresh snapshot.
- A pinned dispatch and every physical model unload/switch share one runtime lease. A switch cannot unload a model during its in-flight response/stream, and a captured client whose runtime is no longer active fails before calling that client.
- Runtime service discovery may return the switching facade/wrapper itself or exact `ChatClientMetadata`. All other provider services and keyed requests are rejected, so raw clients, transports, runtime lifecycles, and future provider-specific escape surfaces cannot bypass the shared dispatch/switch lease.
- A legacy or wrapper runtime that cannot prove the concrete bound dispatch fails closed with zero model calls.
- Planner calls suppress persona injection and bind the captured reasoning/generation settings so the admitted request is the dispatched request.

## Input admission

- Accepted user text, prior accepted role-preserving conversation, current authoritative state projection, protocol schema, tool schema, evidence projection, and attachment projection are counted before dispatch.
- Exact selected context and output limits are honored; code does not silently clamp, truncate, or enlarge them.
- Known tokenizer profiles use their declared counting strategy. Unknown profiles use a conservative deterministic UTF-8 upper bound.
- A captured model profile whose package, family, or display name contains the exact provider-neutral marker `gpt-oss` (case-insensitive) selects the pinned o200k/Harmony text counter plus conservative protocol reserves. This classifies tokenizer mechanics only; it cannot choose a provider, model, tool, workflow, or answer.
- If the complete call cannot fit, admission makes zero model calls and records the typed recoverable reason `model-input-not-admitted` without discarding accepted durable input.
- Interrupted turns with raw attachments require the exact attachments to be supplied again; stored identity/digests cannot manufacture missing bytes.

## Planner protocol

- A planning pass accepts one typed `submit_orchestration_decision` protocol envelope. Assistant prose is not parsed into an action.
- `Stop` with a non-empty protocol payload may be decoded. `Length`, `ContentFilter`, incomplete output, unknown finish reasons, and non-protocol tool calls are discarded before decoding.
- Native `ToolCalls` is accepted only when it is the exact orchestration protocol call; task-tool calls cannot bypass durable decision acceptance.
- Conversation ID, assistant-message ID, expected state revision, work-graph base revision, call ID, tool name, capability metadata, and schema shape must match exactly where applicable.
- Invalid drafts do not enter durable state or a future prompt.

## Work graph

- Revisions advance by exactly one from the exact authenticated parent selected by durable state.
- Work IDs, objectives, parents, dependencies, evidence IDs, status values, replacement provenance, and collection sizes must satisfy bounded canonical shape rules.
- Accepted objectives and parents are immutable. Evidence history is append-only and ordered. Terminal dependency/provenance constraints cannot be rewritten.
- Parent and dependency graphs must reference known nodes and remain acyclic. Local status/evidence changes use cached validated analysis; structural mutations retain full reference and cycle validation.
- Every evidence ID attached to every work-item upsert must resolve to accepted evidence bound to that exact work-item ID.
- A terminal work item additionally requires at least one exact-bound `Succeeded` evidence record.
- Immutable delta candidates become authoritative only when the turn journal commits their exact revision and keyed digest. Losing candidates are pruned after CAS conflict; older lineage is pruned only after an authoritative full checkpoint is selected.
- Full checkpoints occur every 64 graph revisions. Revision count is not capped; retained candidate files remain bounded between checkpoints.

## Evidence and typed tool outcomes

- Invocation status (`Returned`, `Denied`, `Threw`, and related transport states) is distinct from domain outcome (`Succeeded`, `Failed`, `Unreported`).
- A normal return, truthy-looking JSON, a property named `success`, display text, or model prose never proves domain success.
- Ali-owned production tools use an explicit versioned per-tool typed-result contract. Unknown, missing, conflicting, stale, mismatched, evicted, external, or unverifiable outcomes remain `Unreported`.
- Outcome observations bind exact durable turn identity, call ID, canonical tool name, and one typed result. They are bounded, consume-once, cleaned per turn, and failure-dominant on conflict.
- Framework file/work-memory observations exist only inside the exact active invocation lease; incidental, disposed, nested-mismatched, or background-late store calls cannot signal the current tool.
- `mode_get` succeeds only when the exact current Agent Framework session reports a nonblank mode. `mode_set` succeeds only when the structured requested mode exactly equals the subsequently observed session mode.
- Skill outcomes use the exact current-run inventory and wrapped typed skill/content/resource/script boundaries. Exact missing/null/not-found/exception results may fail; absent inventory, approval requests, malformed structured values, conflicts, and unverifiable observations remain `Unreported`. Structured arguments accept CLR strings or already-parsed JSON string elements only; result prose and JSON text are never reinterpreted.
- Stable evidence IDs bind exact invocation identity, WorkItemId, effect kind, canonical ordered artifacts, source metadata, arguments, results, normalized target/effect, permission material, outcome, and timestamps. A retry may return the existing record only when all bound fields match.
- WorkItemId and caller-derived identifiers are protected at rest and projected only as keyed digests outside the protected payload.
- Evidence journal records and protected payloads are authenticated. Disposable Bloom/exact-index accelerators may rebuild from the authoritative journal but cannot create evidence authority.

## Completion and claims

- The completion composer receives a bounded authoritative dossier through the same exact runtime capture and input-admission boundary as planning.
- The composer sends exactly its completion contract messages, exposes no task tools, and cannot reuse planner transcript or partial output.
- `Succeeded` requires every non-superseded node to be `Satisfied`.
- `ProvenImpossible` requires every non-superseded node to be terminal and at least one node to be `Impossible`; runtime, configuration, or protocol failure alone is not task impossibility.
- Every terminal work citation must bind exact successful evidence to that work item. Every declared material claim must cite accepted successful evidence.
- Required evidence selection is all-or-nothing within explicit item/character bounds; omission cannot authorize completion.
- Only a non-empty response with the explicit finish reason `Stop` can be published. A missing/null finish reason is incomplete just like length-limited, filtered, tool-call, empty, stale-binding, cancelled, or superseded output; it is discarded and cannot enter the final journal.

## Effects, permissions, recovery, and publication

- Side-effecting execution is prepared before dispatch with exact tool/capability identity, canonical arguments, permission receipt, target-state fingerprint, registry revision, reconciler, WorkItemId, and idempotency identity.
- Execution-time capability availability and permission are revalidated against the same canonical registry used for UI/discovery/model schemas.
- Public production coordinator composition requires an explicit nonblank capability-settings data root and enters the same settings, registry, and enforcement path; it cannot silently construct an unenforced test-style coordinator.
- Unknown post-crash effects are reconciled as applied, absent, or genuinely in doubt. They are not blindly replayed.
- Pause, cancellation, user resolution, interim response, and final publication transitions use exact IDs, digests, revisions, and compare-and-swap state.
- A late model/tool/completion result cannot commit across cancellation, pause, changed runtime bindings, changed permissions/capabilities, a newer revision, or an existing final-publication transition.
- Final display preparation and commit are separate durable states so recovery can reconcile uncertain display without silently duplicating it.

## Journal, indexes, protection, and bounds

- The append-only authenticated transition and evidence journals are authority. Index, Bloom, membership, and cache structures are acceleration only.
- Record chains, commit markers, heads/manifests, keyed digests/MACs, exact turn/profile bindings, file lengths, and referenced protected payloads are validated before use.
- Exact-index pages authenticate their slot bytes plus the exact key bytes referenced by those slots. The manifest, not each page, authenticates mutable global key-file length.
- Missing, stale, torn, redirected, replaced, or same-length-tampered disposable sidecars rebuild from authenticated journal records or fail closed.
- Hot turn/evidence/status caches, prompt projections, identifiers, payloads, artifacts, candidate enumeration, result sidecars, and user-visible status text have explicit bounds.
- Coordinator tool plans retain active/in-flight calls only and retire an exact call after terminal processing; they do not accumulate for an unlimited advancing turn.
- Current-user encryption protects evidence confidentiality/integrity at rest against other ordinary accounts and accidental inspection; it does not claim protection from the same running user, administrator, or a compromised process.

## User-visible activity

- Status updates are derived from accepted transitions and typed execution events, not speculative model text.
- Full paths are shortened to the relevant file name for routine display, while durable/internal records retain exact identities.
- UI text wraps within the activity surface and is bounded before display.
- The UI retains only the newest 16 current-turn execution receipts used for the next-turn projection, in exact order; older display activity remains separately bounded.

## Explicit non-rules

- No deterministic rule decides what the user meant.
- No keyword or phrase match chooses a tool, provider, workflow, answer, or work decomposition.
- No fixed global step/retry ceiling ends a task that continues to make validated material progress.
- No receipt label, status sentence, generic JSON field, or assistant assertion is accepted as proof of external truth.
- Vision capability discovery planned for checkpoint 9 is advisory. `Unknown` or a failed probe will not override the user's manual checkbox, and Vision will not replace or gate the ordinary text/tool/orchestration path.
