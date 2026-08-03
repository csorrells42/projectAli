# Project Ali Orchestrator V2 - Checkpoint 7 Read-Only Review Brief

## Review target

Review only the exact pushed checkpoint-7 commit hash supplied with this brief. Do not review a moving branch tip.

Use a separate clone or detached read-only worktree. The active Project Ali checkout is shared with the implementation agent. Do not edit, format, restore, generate, commit, reset, clean, build, test, publish, launch Ali, or start/stop local model, Mem0, Qdrant, or MCP services from that shared checkout. If an experiment is necessary, reproduce the supplied commit in an isolated clone and report every command and environmental assumption.

This is an adversarial architecture/correctness review, not authorization to change code. Codex remains the sole implementation owner. Return findings and suggested corrections only.

## Product and design intent

Ali is a Windows/.NET desktop coding assistant with one model-facing identity and one authoritative Microsoft Agent Framework orchestration loop. The configured model owns request interpretation, decomposition, tool/action choice, and source-edit intent. Deterministic code may enforce mechanical identity, syntax, permissions, versioning, bounds, durability, and recovery; it must not interpret English or use keywords to substitute a fixed decision for the model.

Checkpoint 7's central property is: a model-proposed code or file mutation cannot directly become a canonical write. It must pass an exact durable execution grant, isolated preview or exact postimage construction, authenticated changeset, staged validation, one-use publication authority, and all-or-reconciled recovery.

## Implemented scope through checkpoint 7

Treat the checkpoint-6 behavior in `docs/reviews/CP6-EXTERNAL-READ-ONLY-REVIEW-BRIEF.md` as the inherited baseline. Scrutinize whether checkpoint 7 preserves it while adding the following executable boundaries.

1. **Exact durable execution broker.** Effect adapters are keyed by exact tool/capability/reconciler identity. Preparation binds the turn, call, WorkItemId, canonical arguments, action/target versions, permission, registry, prepared artifact, and root before one AsyncLocal-scoped, single-use grant can enter the inner function.
2. **Fail-closed adapter requirement.** Mutations, durable writes, process/system effects, and non-idempotent network effects cannot execute without an exact adapter. Do not assume the existence of `AliExecutionBroker` means every legacy tool is adapted: enumerate the final composition and compare every effect-bearing descriptor with the adapter registry.
3. **Shared transactional source engine.** Typed add/replace/delete/rename changesets bind exact pre/postimages, encodings, relative paths, protected blobs/backups, authenticated manifests, per-root publication leases, a write-ahead operation/rollback journal, terminal receipts, and deterministic committed/rolled-back/in-doubt reconciliation.
4. **Roslyn Action Deck.** `roslyn_inspect_target`, `roslyn_list_actions`, `roslyn_preview_action`, `roslyn_verify_changeset`, and `roslyn_apply_action` replace the retired direct preview/apply rename surface. Action title alone is not executable identity.
5. **Canonical-versus-preview workspaces.** `MSBuildWorkspace` loads canonical state. A separate `AdhocWorkspace` clones exact projects, documents, options, references, analyzers, and analyzer-config data. Physical references are hash-bound; an exact semantic fingerprint mismatch blocks preview.
6. **Public provider bridge.** Production explicitly supplies public `CodeFixProvider` and `CodeRefactoringProvider` instances; no MEF scan, reflection invocation, or internal Roslyn service is used. Provider concrete type, assembly version, and assembly-file hash enter action identity. Nested actions have deterministic ordinal/equivalence-key identities and hard bounds.
7. **Explicit provider set.** The Action Deck always adds the built-in semantic rename provider, while the production-owned public catalog supplies an unambiguous `CS0246` namespace-import fix and exact whole-document formatting. Provider exceptions are isolated and reported without partially retaining that provider's actions.
8. **Exact durable Roslyn delta.** Regular, additional, and analyzer-config document add/replace/delete/rename/rename-and-replace changes are reconstructable from protected source operations. Unrepresented project/reference/compilation metadata changes fail closed rather than being silently omitted.
9. **Staged semantic/build/test gate.** Verification compares compiler plus project-analyzer diagnostic identities, materializes a bounded no-follow temporary tree, evaluates the staged MSBuild graph, builds the smallest affected roots, and runs dependency-related test projects. Timeout, failed build/test, selected zero-test result, missing reference, stale fingerprint, or diagnostic regression prevents verification.
10. **Protected expiring handles.** Preview and verification state, requested values, paths, exact document deltas, and receipts are stored in current-user-protected handles with revision-checked one-way state transitions. Preview lasts two hours; successful preverification lasts at most 30 minutes.
11. **All-or-reconciled Roslyn publication.** Apply requires the exact verified handle and unchanged canonical fingerprint. Canonical source is published through the shared transaction, then reloaded and compared with the verified staged semantic fingerprint. Recovery never republishes; it classifies the source receipt and protected handle, then postverifies or reports applied-needs-review.
12. **Authorization-bound recovery.** A committed source/file receipt is accepted as the prepared call's effect only when its authorization digest matches the exact persisted durable intent, including registry identity and root binding. Missing or altered authorization remains unknown and cannot generate success evidence.
13. **Brokered Agent Framework text edits.** The production Framework store sends `file_access_write`, `file_access_replace`, and `file_access_replace_lines` through exact-schema, exact-pre/postimage transactions. Framework delete and directory creation are explicitly unavailable through that store; ordinary Framework reads remain read-only.
14. **Seven brokered Roslyn semantic queries.** Analyze, symbol search, completions, solution/document/position inspection, and references resolve exact target/document hashes and consume a one-use root-bound grant. Their recovery classification is safe-to-repeat/absent because they publish no source or durable domain state.
15. **Recovery-display claim.** Recovered interim/final text is marked display-in-doubt durably before redisplay; the UI acknowledgment must match its digest and claim revision. A crash after the claim cannot silently trigger another automatic display.
16. **Adjacent boundary hardening.** Suppressed `ExecutionContext` no longer inherits a process-wide turn fallback; process timeouts kill the tree and remain distinct from caller cancellation; workstation target hashes reject reparse paths; outgoing non-interactive MCP publication rejects every mutating/system-changing capability.

## Exact adapter-coverage release gate

This is a required review, not an assumption. The CP7 source introduces a broad `RequiresDurableEffectAdapter` predicate. The explicitly implemented adapter families visible during development were:

- five Action Deck tools;
- seven Roslyn semantic-query tools;
- three Agent Framework file-mutation tools.

For the supplied final commit, enumerate every descriptor for which the predicate is true and prove exactly one of these outcomes:

1. it has the correct exact adapter and target-state path;
2. it is deliberately unavailable and that limitation is truthful in settings/model/UI surfaces; or
3. it is an unintended regression that disables an existing core capability.

Examples to scrutinize include builds/tests, process control, formatters, package changes, Git mutations, multi-language tools, Skills, and MCP paths. A fail-closed result is safer than an unbrokered mutation, but unintentionally withholding core tools is still a release defect. Do not characterize the finished V2 toolbelt as complete without this enumeration.

## Deliberately deferred after checkpoint 7

Do not report these as checkpoint-7 defects unless the code claims they are complete, regresses inherited behavior, or makes the planned work impossible.

### Checkpoint 8

- full bounded evidence/context paging and hash-linked multi-pass final-answer composition;
- completion critic integration without a second autonomous execution loop;
- isolated `Channel<ShadowToolObservation>` observation lane with no back-pressure into the authoritative loop;
- fault-injection matrices, recorded regression traces, property checks, long-running soak, and larger-scale verification;
- explicit prompt-window policy that keeps every request under the selected context budget. LM Studio rolling-window mode may be a runtime fallback, but Ali must not knowingly depend on silent middle/edge truncation or lose immutable request/evidence/tool-pairing boundaries.

### Checkpoint 9

- provider-neutral runtime selection across LM Studio, Lemonade, Ollama, llama.cpp, and an explicitly configured secure remote OpenAI-compatible HTTPS endpoint;
- settings-driven model inventory/load/unload/ownership behavior with no hardcoded loaded instance;
- GPT-OSS 20B profile support for a user-selected 65,536 context and 8,192 output reserve, including rolling-window identity in captured generation settings;
- `text-embedding-nomic-embed-text-v1.5@q8_0` at the selected 8,192 context, query/document prefix roles, resolved model/protocol fingerprinting, functional long-context health probing, and fresh Mem0/Qdrant indexing;
- provider-neutral cleanup of Lemonade-named classes/tests;
- advisory vision-capability detection with manual override, attachment picker, and pasted-screenshot previews without replacing the text/tool path;
- final status/UI cleanup, optional server warnings, runtime/deployment cleanup, desktop shortcut verification, architecture PDF, and live release verification.

## Highest-risk review questions

### Broker, capability, and permission authority

- Does every effect-bearing descriptor have one exact adapter, or is any important tool silently unavailable after CP7?
- Can any compatibility path, direct AIFunction, Framework store, outgoing MCP method, Skill, language provider, or lower-level service bypass broker preparation and write canonical source?
- Can an adapter be selected by name similarity, stale registry state, or a mismatched capability/reconciler identity?
- Can a captured invocation scope be entered twice, consumed twice, survive disposal, cross async flows, or execute changed arguments?
- Is the persisted `ExecutionRegistryIdentityDigest`/`RootBinding` actually derived from the exact live authorization and authenticated on recovery?

### Source transaction and filesystem integrity

- At every injected boundary, can restart prove all-postimage, all-preimage, recognized mixed, or truly unknown without duplicate publication or overwriting an unrecognized user edit?
- Can a copied/replayed/tampered manifest, blob, backup, receipt, journal line, authorization digest, or handle bind to another changeset/call/root?
- Can case-insensitive path collisions, alternate separators, `..`, ADS/device names, symlinks, junctions, hard links, or a parent swapped after validation escape the approved root?
- Are add/replace/delete/rename and rollback operations durable in their real Windows crash model, including directory-entry durability and locked files?
- Can materialization exclusions omit a changed file yet still authorize verification?

### Roslyn semantic fidelity

- Does the `AdhocWorkspace` preserve every semantic input that can affect compilation/analyzers, including target framework, WPF references, generated/editorconfig state, source generators, project aliases, output refs, and multi-target projects?
- Is the solution fingerprint stable but complete? Can equal fingerprints hide a meaningful resolver, generator, analyzer-config, additional-file, project XML, or reference change?
- Can a provider action return operations or solution metadata that the durable document delta silently drops?
- Does the public provider bridge isolate exceptions, nested action cycles, duplicate identities, null equivalence keys, stale diagnostics, and asynchronous registration safely?
- Is the owned `CS0246` namespace fix semantically unambiguous at the changed solution, including aliases, generics, accessibility, nested types, and conflicting imports?

### Verification and publication

- Does preverification reconstruct exactly the solution represented by the protected source manifest, not an in-memory changed solution that canonical publication cannot reproduce?
- Are compiler and analyzer diagnostics compared by stable exact identity rather than count/message text only?
- Can project/analyzer metadata change after preview but before build/apply without invalidating the handle?
- Does the MSBuild graph choose every affected project and every relevant dependent test while avoiding arbitrary targets/properties supplied by the model?
- Can a timeout, malformed/missing TRX, zero discovered tests, bounded-output truncation, or child process escape become false success?
- Can canonical publication commit while handle/evidence recovery reports absent, or can a rolled-back publication report applied?
- Does authorization mismatch always remain unknown without sealing `Applied` or appending successful evidence?

### Framework file semantics

- Do the exact parser and actual Agent Framework provider produce byte-for-byte identical postimages for write, ordinal replace, replace-all, and physical-line replacement across CRLF/LF/lone-CR/final-empty-line inputs?
- Can a changed path/content call consume or reuse a preparation created for another accepted argument set?
- Can Framework delete/directory creation reach the raw store despite the brokered production store?
- Is the lower-level store exposure intentional, permission-bound, and incapable of becoming an Agent Framework bypass?

### Recovery, display, and concurrency

- Can a crash before/after the display-in-doubt transition cause duplicate or lost recovered UI text, and is user resolution available for genuinely in-doubt display?
- Can `ExecutionContext.SuppressFlow`, background callbacks, nested invocations, or concurrent users inherit another turn/grant?
- Can root leases deadlock or serialize unrelated roots; can cancellation strand a lease or temporary staged tree?

### Deterministic-processing boundary

- Identify every `.Contains()`, keyword/phrase list, filename/extension branch, diagnostic-ID branch, and status switch on a user-request path.
- Distinguish permitted mechanical validation/provider behavior from any rule that interprets English, chooses a tool from prose, or decides the requested source change.
- Treat extension-based syntax parsing and the exact `CS0246` provider as mechanical only if they run after an exact model-selected tool/action and cannot choose the user's goal.

## Primary code areas

- `src/Modules/Orchestration/AliExecutionBroker.cs`
- `src/Modules/Orchestration/AliExecutionAuthorizationDigest.cs`
- `src/Modules/Orchestration/AliDurablePlanningCoordinator.cs`
- `src/Modules/Coding/Changesets/`
- `src/Modules/Coding/RoslynActions/`
- `src/Modules/Coding/RoslynQueries/`
- `src/Modules/Coding/AliCodingModule.cs`
- `src/Modules/FileAccess/AliBrokeredFrameworkFileStore.cs`
- `src/Modules/FileAccess/AliFrameworkFileExecutionAdapter.cs`
- `src/Modules/FileAccess/AliFrameworkFileMutationPlan.cs`
- `src/Modules/FileAccess/AliFrameworkFileMutationTransaction.cs`
- `src/Modules/Coordinator/AliProductionCapabilityCatalog.cs`
- `src/Modules/Coordinator/AliToolCoordinator.cs`
- `src/Modules/Mcp/McpCapabilityPublicationGate.cs`
- `src/Modules/Orchestration/State/`
- `tests/Ali.Framework.Tests/Coding/`
- `tests/Ali.Framework.Tests/OrchestrationV2/AliExecutionBrokerTests.cs`
- `tests/Ali.Framework.Tests/OrchestrationV2/AliFrameworkFileMutationBrokerTests.cs`

Use `docs/architecture/ALI-ORCHESTRATION-LOOP-V2.md` as the design contract, but report executable-code/document mismatches rather than assuming either is correct. Read `docs/reviews/CP7-DETERMINISTIC-RULE-DISCLOSURE.md` for the intended mechanical rule inventory and `docs/reviews/CP7-VERIFICATION-REPORT.md` for exactly what was and was not run.

## Required finding format

Return findings first, ordered by severity. For every finding provide:

1. **Severity:** `P0`, `P1`, `P2`, or `P3`.
2. **Classification:** correctness/security defect, missing verification, architecture drift, performance/scaling risk, or preference/alternative.
3. **Exact location:** repository-relative file and tight line range at the supplied commit.
4. **Concrete failure scenario:** initial state, exact action/interruption/adversarial input, and observable wrong result.
5. **Why existing validation does not prevent it.**
6. **Minimal suggested correction:** guidance only; do not edit.
7. **Confidence and evidence:** distinguish source proof from inference or an unrun experiment.

Severity meanings:

- `P0`: credible data loss, authority bypass, secret disclosure, or broadly unrecoverable source corruption.
- `P1`: duplicate/unauthorized mutation, false success/completion, cross-turn authority, unintended loss of a core tool class, or unrecoverable normal workflow.
- `P2`: bounded recovery, semantic-fidelity, scalability, observability, or operational flaw with material impact.
- `P3`: localized hardening or clarity improvement.

Label design preferences and alternative architectures explicitly. If no actionable defects are found, say so and list residual risks and missing tests separately.

## Claims this review must not make without evidence

- Do not claim real LM Studio/GPT-OSS/Nomic, Mem0/Qdrant, MCP, hardware, GPU, latency, soak, shortcut, or desktop-UI success from source inspection.
- Do not claim a staged build proves canonical publication, or a canonical build proves crash recovery at every journal boundary.
- Do not claim current-user protection withstands the same logged-in user, administrator, debugger, injected process, or compromised runtime.
- Do not infer universal tool availability from catalog registration; follow final capability resolution and exact adapter composition.
- Do not infer task truth or mutation success from action titles, summaries, status text, normal returns, or generic JSON fields.
- Do not report CP8/CP9 features as implemented unless they are actually present and verified in the supplied checkpoint hash.
