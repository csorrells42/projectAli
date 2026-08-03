# Project Ali Orchestrator V2 - Checkpoint 6 Read-Only Review Brief

## Review target

Review only the exact pushed commit hash supplied with this document. Do not review a moving branch tip. Use a separate clone or detached read-only worktree when possible.

The active Project Ali checkout is shared with the implementation agent. Do not edit, format, restore, generate, commit, reset, clean, build, test, publish, or launch the application from that shared checkout. If runtime verification is useful, first reproduce the supplied commit in an isolated clone and report every command and environmental assumption.

This is an adversarial architecture and correctness review, not an authorization to change code. Codex remains the sole implementation owner.

## Product and design intent

Ali is a Windows/.NET desktop coding assistant with one user-facing identity and one authoritative Microsoft Agent Framework execution loop. The configured model decides request meaning, decomposition, tool choice, and whether useful work remains. Deterministic code is restricted to mechanical integrity, security, availability, durability, and schema checks; it must not interpret English, infer intent from keywords, or silently replace a model decision.

The central safety property is not that a probabilistic model can never be wrong. It is that an invalid or unsupported model assertion cannot silently become authoritative turn state, evidence, a completed outcome, or proof that an external effect occurred.

The loop has no fixed global task-step limit while validated state continues to advance. An exact action repeated against unchanged state is a no-progress signal that requires a fresh decision, not an excuse to accumulate an unbounded retry transcript.

## Implemented boundary through checkpoint 6

Treat the following as intended current behavior and scrutinize whether the implementation actually enforces it:

1. **One loop and one coding executor.** Aider, OpenHands, Open Interpreter, specialist group chat, sequential workflows, and Magentic orchestration do not own nested planning loops. Agent Framework remains the single outer execution harness.
2. **Fresh state-backed planning.** Each planning pass is rebuilt from accepted durable state. Rejected drafts, malformed protocol output, raw tool payloads, compacted Framework transcript, and critic prose cannot seed a later decision.
3. **Role-safe conversation recovery.** Exact accepted prior User, Assistant, and System roles are retained durably. Prior non-steering transcript is projected as explicitly non-authoritative referential data; current user steering remains a distinct authoritative user message.
4. **Native typed decisions.** The model proposes one `submit_orchestration_decision` envelope. Structural validation either accepts the whole transition or discards it. Assistant prose is not parsed to recover actions.
5. **Immutable evidence authority.** Tool invocation status and domain outcome are separate. A normal return, generic JSON field such as `success`, or model statement cannot prove work succeeded. The production catalog has an explicit versioned outcome contract for every canonical task tool: exact typed result classifiers for Ali-owned tools and exact provider-boundary signals for otherwise ambiguous Framework tools. File/work-memory, Agent Framework mode, and Agent Skill signals are accepted only inside the exact durable turn/call/tool invocation lease and verified at their typed provider/session/source boundary. Missing, mismatched, evicted, late, external, or unverifiable results remain `Unreported`.
6. **Evidence and work identity.** A stable evidence ID cannot be rebound to a different invocation, WorkItemId, effect kind, canonical artifact list, source, payload, permission, outcome, or time. Every evidence citation on every work upsert - including `Pending` and `Active` - must resolve to evidence bound to that exact work-item ID. Terminal work additionally requires explicit `Succeeded` evidence.
7. **Evidence-backed completion.** `Succeeded` requires every non-superseded work node to be satisfied. `ProvenImpossible` requires every non-superseded node to be terminal and at least one to be impossible. Every terminal node and every material completion claim must bind to exact successful evidence.
8. **Durable turn recovery.** Accepted inputs, transitions, work-graph references, action intents, evidence, interim publications, final-publication state, pause/cancel state, and runtime bindings survive interruption. In-doubt effects are reconciled or surfaced to the user; they are not blindly replayed.
9. **Write-ahead effects.** Side-effecting calls bind the live capability, canonical arguments, permission receipt, target state, registry revision, effect identity, WorkItemId, and idempotency identity before execution.
10. **Scalable journals.** Authenticated append-only transition and evidence journals are authoritative. Disk-backed exact indexes, membership filters, and caches are disposable acceleration structures. Missing, stale, same-length-tampered, or torn sidecars rebuild from authenticated journal data. Cold exact lookups do not require retaining or replaying an unbounded journal in ordinary operation.
11. **Scalable work graph.** Immutable graph snapshots expose validated cached analysis for status buckets, dependency relationships, counts, and digests. Local status/evidence changes avoid whole-graph reconstruction; structural mutations retain full reference and cycle validation. Full checkpoints occur every 64 revisions; only after the state journal selects a checkpoint may older lineage be pruned, keeping retained candidates bounded without capping revision count.
12. **Exact runtime capture and context admission.** Each pass captures the concrete client/profile/model/settings used for both admission and dispatch. A runtime switch after capture cannot redirect the call. A shared dispatch/switch lease prevents physical unload or activation from overlapping an in-flight pinned response/stream, and a stale captured client fails before its inner call. The selected context and output limits are never clamped, rewritten, or silently relaxed. Admission accounts for projected messages, protocol/tool schemas, and attachment data before a model call. An impossible or oversized request makes zero model calls, changes no runtime setting, preserves the accepted durable text, roles, state, evidence, and attachment identity, and produces a typed recoverable `model-input-not-admitted` state. Raw attachment bytes remain current-turn material and must be reattached exactly before an interrupted request can resume.
13. **Completion publication safety.** Completion uses a separately admitted, tool-free, exact-bound model call. Only explicit `Stop` plus nonempty text is complete. Missing/null, `Length`, `ContentFilter`, tool-call, unknown, empty, stale-binding, cancelled, or superseded completion output is discarded before publication; partial text never enters the final journal. Compatibility planner JSON likewise requires explicit `Stop`; native `ToolCalls` is accepted only for the sole orchestration protocol call.
14. **Bounded model projection.** Completion evidence is selected in a bounded batch and fails closed if the authoritative dossier cannot fit its explicit item/character limits. Full evidence remains retrievable outside the prompt.
15. **Canonical availability and permission boundaries.** The same capability metadata must govern settings, model-visible schemas, semantic discovery, direct/native tools, incoming MCP, execution-time revalidation, effects, permissions, reconciliation, and typed outcome availability. Disabled, stale, or uncontracted capability paths cannot execute or prove success.
16. **User-visible activity from accepted transitions.** Status messages describe accepted work and the next step. Internal full paths and low-level payloads should not become the default user-facing explanation. Completed per-call plans retire after exact terminal processing, and the next-turn UI projection retains only the newest 16 execution receipts, so an advancing turn is not given an artificial step cap and also does not accumulate unbounded coordinator/UI history.

## Deliberately deferred after checkpoint 6

Do not report these as checkpoint-6 regressions unless current code falsely claims they are already complete or the checkpoint-6 implementation makes them impossible:

- **Checkpoint 7:** transactional multi-file source changesets, the Roslyn Action Deck, temporary semantic verification, journaled all-or-reconciled publication, and source-mutation broker enforcement.
- **Checkpoint 8:** full bounded evidence/context paging, hash-chained final-answer composition, completion critic integration, isolated shadow-observation lane, fault injection, regression traces, and long-running soak/property verification.
- **Checkpoint 9:** final provider cutover for LM Studio, Lemonade, Ollama, llama.cpp, and an explicitly selected secure remote OpenAI-compatible HTTPS endpoint; provider-neutral settings cleanup; advisory vision capability discovery with manual override; conditional image picker and pasted-screenshot previews; runtime/deployment cleanup; shortcut repair; final architecture PDF and release verification.

The planned Vision checkbox is additive. Capability detection may notify on a verified result, but `Unknown` or a failed probe must not disable the user's checkbox. Merely enabling Vision must not replace the text/tool/orchestration path; actual attached images may consume additional model context and latency.

## Future-plan review

After reviewing the implemented checkpoint, separately review the checkpoint 7-9 plan in
`docs/architecture/ALI-ORCHESTRATION-LOOP-V2.md`. Identify any missing boundary that would make the
finished design unable to provide transactional source publication, Roslyn semantic verification,
bounded completion/critic behavior, isolated shadow observation, provider-neutral local/remote
runtime selection, additive vision, simple copy-folder deployment, or honest live verification.
Label these as **future-plan risks or suggestions**, not defects in checkpoint-6 code. Do not propose
a second autonomous agent loop, deterministic English routing, a fixed global task-step limit, or
provider-specific core orchestration state.

For checkpoint 7 specifically, scrutinize the planned split between canonical `MSBuildWorkspace`
loading and an isolated `AdhocWorkspace` preview/compilation manager with explicit target-framework,
WPF, and package references. The preview gate must not depend on incidental assemblies already loaded
inside Ali, and failure to establish exact references must prevent publication.

## Highest-risk review questions

### Authority and loop ownership

- Can any retained agent, client, workflow, callback, middleware path, or compatibility adapter start a second autonomous planning loop?
- Can Framework transcript, rejected model output, raw tool payload, assistant prose, or UI text re-enter authoritative planning state?
- Can two callbacks or users cross turn, conversation, active-user, assistant-message, or revision boundaries?

### Effects, recovery, and publication

- Is every externally visible mutation either prepared before execution or explicitly classified non-mutating?
- At every crash boundary, can Ali distinguish proven-applied, proven-absent, and genuinely in-doubt without duplicating an effect?
- Can a late completion commit after cancel, pause, runtime-binding change, permission change, or a newer revision?
- Can a final answer be displayed twice, displayed without its durable publication record, or marked committed when display is uncertain?

### Evidence and completion

- Can malformed, empty, partial, stale, failed, permission-denied, or generic `{ "success": true }` output satisfy a node without a typed tool-owned success classifier?
- Can a stable EvidenceId be retried with a changed WorkItemId, effect kind, artifact, source, permission, payload, result, or timestamp and silently return the older record?
- Can evidence bound to work A be attached to Pending/Active work B and poison its append-only evidence history before terminal validation runs?
- Can completion omit a non-superseded node, terminal evidence, declared material claim, or exact evidence binding?
- Can `ProvenImpossible` be used for a runtime/protocol/configuration failure rather than conclusive task evidence?
- Can bounded evidence selection silently omit a required proof and still allow completion?

### Scale and denial of service

- Are hot-path journal operations exact and near-linear without RAM growing with record count?
- Can a cold positive evidence lookup or batch degrade into a full authenticated replay per ID after the hot cache evicts it?
- Do sidecars remain non-authoritative under deletion, stale metadata, same-length tampering, truncation, or replacement?
- Does a one-node status/evidence update on a 5,000-node graph avoid full serialization, reconstruction, sorting, cycle traversal, and digest rebuilding?
- Are graph snapshots, mutation tickets, and store-local provenance unforgeable outside their intended assembly/store boundary?
- Are prompt, schema, attachment, evidence, journal-record, identifier, and user-visible status sizes bounded before expensive allocation or model execution?

### Context and trust boundaries

- Does admission count the actual message/protocol shape used by the selected backend, including role framing, tool schemas, and attachments?
- Is the 65,536-context/8,192-output GPT-OSS configuration admitted exactly when valid, without assuming it is a permanent or exclusive model choice?
- For unknown models, is the deterministic UTF-8 upper bound fail-closed without silently discarding or truncating accepted content?
- Are recovered Assistant/System messages clearly data rather than steering, while exact current user steering remains authoritative?
- Can untrusted web, MCP, command, document, or tool content inject instructions, forge evidence, disclose secrets, or alter capability metadata?

### Deterministic-processing boundary

- Identify every phrase list, `.Contains()` intent test, keyword route, fixed English decomposition, or deterministic answer rule on a user-request path.
- Distinguish permitted mechanical checks - schema, digest, identity, bounds, availability, permission, status, evidence binding, cancellation, and CAS - from prohibited semantic interpretation.

## Primary code areas

- `src/Modules/Coordinator/AliAgentHarnessRunner.cs`
- `src/Modules/Orchestration/AliDurablePlanningCoordinator.cs`
- `src/Modules/Orchestration/AliDurablePlanningResume.cs`
- `src/Modules/Orchestration/Planning/`
- `src/Modules/Orchestration/State/`
- `src/Modules/Orchestration/Work/`
- `src/Modules/Orchestration/Evidence/`
- `src/Modules/Capabilities/`
- `src/Modules/Coordinator/AliToolCoordinator.cs`
- `src/Modules/Coordinator/AliProductionCapabilityCatalog.cs`
- `src/Modules/Permissions/AliToolPermissionPolicy.cs`
- `tests/Ali.Framework.Tests/OrchestrationV2/`

Use `docs/architecture/ALI-ORCHESTRATION-LOOP-V2.md` as the design contract, but report mismatches between that document and executable code rather than assuming either side is correct.

Read `docs/reviews/CP6-VERIFICATION-REPORT.md` for the exact automated gates, pass/skip totals, runtime-asset procedure, and unverified live-behavior boundary. Treat it as reported evidence to scrutinize, not as a substitute for source review.

## Required finding format

Return findings first, ordered by severity. For every finding provide:

1. **Severity:** `P0`, `P1`, `P2`, or `P3`.
2. **Classification:** correctness/security defect, missing verification, architecture drift, performance/scaling risk, or preference/alternative.
3. **Exact location:** repository-relative file and tight line range at the supplied commit.
4. **Concrete failure scenario:** initial state, action/interruption/adversarial input, and observable wrong result.
5. **Why existing validation does not prevent it.**
6. **Minimal suggested correction:** guidance only; do not make the edit.
7. **Confidence and evidence:** distinguish source proof from inference or an unrun experiment.

Severity meanings:

- `P0`: credible data loss, security-boundary bypass, secret disclosure, or broadly unrecoverable corruption.
- `P1`: duplicate side effect, false completion, cross-turn authority failure, unrecoverable normal workflow, or major correctness flaw.
- `P2`: bounded edge-case correctness, recovery, scalability, observability, or maintainability flaw with material operational impact.
- `P3`: localized hardening or clarity improvement.

Label design preferences and alternative architectures explicitly; do not present them as correctness defects. If no actionable defects are found, say so and list residual risks and missing tests separately.

## Claims this review must not make without evidence

- Do not claim real-model, LM Studio, hardware, latency, soak, crash-recovery, or UI behavior solely from source inspection.
- Do not claim encryption protects against a compromised Windows account, administrator, or running process. The current-user protection boundary is narrower.
- Do not treat Git status or passing unit tests as proof that an external side effect, desktop shortcut, local model runtime, MCP server, Mem0/Qdrant service, or deployment works live.
- Do not infer task truth from assistant wording, status text, or receipt labels; follow the authoritative record and evidence path.
