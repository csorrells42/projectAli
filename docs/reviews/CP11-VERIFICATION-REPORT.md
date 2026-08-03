# Checkpoint 11 Verification Report

Date: 2026-08-03
Scope: Project Ali participant-aware memory in the isolated checkpoint-11 worktree

## Current conclusion

CP11 has current focused source and simulated-transport evidence. The current expanded
six-class Release selection is 104/104 green; it includes the previously documented
five-class selection plus participant-presence tests. The two exact legacy-MCP boundary
tests are 2/2 green. The current Release app build succeeds with zero warnings and zero
errors. The broad suite was attempted and is not green. Python-runtime, live-provider,
and final independent-audit gates remain separate and are not implied by focused passes.

Those are the only aggregate counts asserted by this report at this checkpoint. It
does not declare the broad solution suite green or claim live worker/provider behavior.

| Gate | Current evidence | Boundary |
| --- | --- | --- |
| Current expanded focused Release run | 104 passed; 0 failed; 0 skipped | Six CP11 classes: policy, service integration, production boundary, architecture/source canaries, embedding settings, and participant presence. |
| Exact legacy-MCP boundary run | 2 passed; 0 failed; 0 skipped | Exact production catalog-policy and binding cases. |
| Current Release app build | 0 warnings; 0 errors | Compilation only; no app or shortcut was launched. |
| Python worker execution | Syntax-only pass | Installed Python 3.12 parsed `mem0_service.py` and `local_qdrant.py` through `ast.parse` successfully. Imports, dependencies, providers, and worker operations were not executed. |
| Broad solution suite | Attempted; non-green | Failures include pre-existing Roslyn capability-catalog drift, protected-directory access denials, and unavailable external toolchains/hardware. Output truncation prevents a trustworthy aggregate count, so none is invented. |

## Implemented boundaries covered by focused evidence

The checked source and focused tests cover or assert the following CP11 boundaries.
This list describes the focused verification surface; it does not turn it into a live
provider or broad-suite pass.

- immutable admitted rosters, explicit selection, advisory target/speaker freshness,
  attachment epochs, and stale-roster rejection;
- a CP11 read-only identity boundary over the frozen active-user session, with stable
  immutable selection/registry sampling, authority-owned roster generation capture,
  and selected/test identity derived from authority rather than caller flags;
- exact process-issued permission, consent, and authentication receipts;
- consent prepared intent, exact proposal fingerprints, separately selected grants,
  first-stable-mutation-ID session binding, same-ID retry, bounded retention, and
  restart/no-durable-effect reconciliation;
- production credential binding to the sole selected registered real profile,
  multi-real-profile fail-closed behavior, exact current-binding rechecks, and the
  narrow authoritative generated John Doe test factor;
- exact add/correct/dispute validation of record shape/count, proposal metadata,
  tenant, space, access, state, target, operation lineage, provenance, and consent
  arrays, with separate exact lifecycle/delete receipt checks;
- no cached embedding identity resolution: two endpoint probes on every resolution,
  using pinned-Mem0 request parity (one-element input array, newline normalization,
  `encoding_format: float`, and `Bearer ali-local-only`);
- exact access filtering before recall/inventory, Ali-owned candidate-local dense+
  BM25 ranking without `Memory.search`/entity extraction/English lemmatization, and
  a full exact-filtered inventory scan retaining the newest at most eight records;
- legacy MCP file-memory names removed from catalog policy and function bindings, and
  model-visible recall/list fail closed instead of falling back to the legacy store;
- prepared mutate/reconcile intents, exact request correlation, bounded uncertain-
  outcome reconciliation, and rejection of unauthorized current bindings;
- bounded durable journal admission, safe path/link/temp handling, global fail-closed
  structural classification of every receipt plus corrupt/temp/unsafe artifacts,
  terminal redaction/compaction, successor ownership preflight, monotonic
  `rollback_started`, and resumable two-phase delete finalization;
- exact worker-side pre-effect parity for UTF-16 string bounds, Unicode controls,
  record/provenance/consent shape, timestamps, lineage, and finite scores so malformed
  stored targets cannot be changed before C# would reject the returned record;
- ordinary historical committed reconcile inspection reporting stale without rolling
  back solely for a post-response roster change, while staged delete may finalize or
  roll back;
- explicit reconcile validation of bounded response shape, tenant, space, access,
  lifecycle state, and available lineage (not an unavailable target-contract digest);
- health readiness booleans, bounded hybrid inspection, deliberate exact-ID repair,
  and desktop participant-service adaptation without hidden `RememberAsync`; and
- fresh current-format storage with no migration or old-vector read path.

## Remaining automated gate

- Commit and push the independently audited delta. Python evidence remains syntax-only,
  and the attempted broad suite remains explicitly non-green rather than being restated
  as a focused success.

## Live and environment-dependent checks not run

No current evidence proves:

- Python import/runtime execution of `mem0_service.py` or `local_qdrant.py` beyond the
  successful syntax-only AST parse;
- a live local OpenAI-compatible provider completing both live probes with the pinned
  Mem0-compatible envelope, or the installed model's true quantization/template/
  truncation/token-limit behavior;
- live Mem0 imports, Qdrant startup/collection creation, dense/BM25 search, exact-point
  updates, snapshot restore, two-phase delete recovery, or repair against installed
  package versions;
- Windows credential UI behavior, interactive `LogonUser`, current-process SID match,
  synchronous prompt cancellation timing, or secret clearing under a real account;
- per-profile credential binding for a multi-real-profile install (the current design
  intentionally fails closed instead);
- multiple real people separately selecting and approving one exact proposal inside
  the grant window;
- restart recovery at every journal transition, two-process lease contention, corrupt
  file recovery, or actual Windows power-loss durability of file/directory fsync and
  atomic replace;
- a live camera, target track, microphone, speaker recognition, freshness accuracy,
  enrollment exclusion, liveness, or simultaneous multi-person roster behavior;
- real model tool selection, memory relevance, answer quality, or long-running
  conversation behavior;
- real CPU, memory, disk, network, GPU, webcam, microphone, or latency characteristics;
- published-folder, Ali app, or shared-shortcut launch, and any UI visual verification;
- encryption, backup/restore, authentication security beyond the exact implemented
  checks, privileged authority, medical validity, or identity/liveness assurance; or
- a green broad repository test suite.

These require explicit live passes on the intended deployment and must remain separate
from compilation, fake transports, and source-canary evidence.

## Important interpretation notes

- Every embedding identity resolution probes twice. Any document or test referring to
  a five-minute probe cache is stale.
- The Windows credential prompt happens before the transport budget, but the native
  prompt itself is synchronous and not separately cancellable.
- Reconcile is not read-only. Consent, mutate, and reconcile all use prepared intents.
- Exact add/correct/dispute response validation includes provenance and consent values;
  lifecycle and delete responses use their separate target/state/zero-content checks.
- Explicit reconcile validates the returned bounded shape, tenant, space, access,
  state, and available lineage. It cannot claim target-contract-digest validation
  because that digest is absent from the response.
- An ordinary historical committed receipt is not rolled back merely because the
  roster becomes stale after inspection. Staged delete is the special two-phase state
  that may be finalized or rolled back under current authority.
- The production MCP catalog no longer exposes the three legacy file-memory names.
- Delete finalization persists `finalization_started` plus the exact receipt scrub set
  before the first irreversible redaction. Retry/restart resumes that scrub; rollback
  cannot cross the marker.

## Deterministic-processing statement

CP11 adds exact typed validation, freshness, receipt, consent, authentication, access,
embedding-identity, ranking, bounded-journal, reconciliation, rollback, delete, and
repair rules. It adds no English keyword/pronoun routing, deterministic semantic
interpretation, memory-relevance decision, answer selection, or response formatting.
The complete rule inventory and effect boundaries are recorded in
`docs/reviews/CP11-DETERMINISTIC-RULE-DISCLOSURE.md`.
