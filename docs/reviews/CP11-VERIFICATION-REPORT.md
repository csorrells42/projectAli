# Checkpoint 11 Verification Report

Date: 2026-08-03
Scope: Project Ali participant-aware memory in the isolated checkpoint-11 worktree

## Current conclusion

CP11 has substantial focused source and simulated-transport evidence, but the final
verification gate is still pending. The most recent relevant Release test invocation
compiled 91 tests: 90 passed and one stale source-canary assertion failed. The canary
was corrected after that run; the corrected 91-test set has not yet been rerun. An
earlier focused checkpoint completed 46/46 green.

Those are the only aggregate counts asserted by this report. It does not declare the
broad solution suite green and does not claim a final build/test/audit gate.

| Gate | Current evidence | Boundary |
| --- | --- | --- |
| Latest focused relevant Release run | 91 compiled; 90 passed; 1 stale source-canary failure | The failed canary was corrected after the run, but final rerun remains pending. |
| Earlier focused checkpoint | 46 passed; 0 failed | Earlier checkpoint evidence only; it is not a substitute for the final rerun. |
| Python worker execution | Not run | Python source was inspected and covered by .NET source canaries, but no Python interpreter executed the worker or its tests in this pass. |
| Broad solution suite | No green claim | A broad aggregate is intentionally not reported as passing. |

## Implemented boundaries awaiting final aggregate rerun

The checked source and focused tests cover or assert the following CP11 boundaries.
This list describes the intended verification surface; it does not turn the pending
aggregate rerun into a pass.

- immutable admitted rosters, explicit selection, advisory target/speaker freshness,
  attachment epochs, and stale-roster rejection;
- trusted context deriving selected/test identity only from the authoritative active
  session rather than caller flags;
- exact process-issued permission, consent, and authentication receipts;
- consent prepared intent, exact proposal fingerprints, separately selected grants,
  first-stable-mutation-ID session binding, same-ID retry, bounded retention, and
  restart/no-durable-effect reconciliation;
- production credential binding to the sole selected registered real profile,
  multi-real-profile fail-closed behavior, exact current-binding rechecks, and the
  narrow authoritative generated John Doe test factor;
- full exact mutation validation of record shape/count, tenant, space, access, state,
  target, operation lineage, provenance, and consent arrays;
- no cached embedding identity resolution: two endpoint probes on every resolution,
  using pinned-Mem0 request parity (one-element input array, newline normalization,
  `encoding_format: float`, and `Bearer ali-local-only`);
- exact access filtering before recall/inventory, Ali-owned candidate-local dense+
  BM25 ranking without `Memory.search`/entity extraction/English lemmatization, and
  a full exact-filtered inventory scan retaining the newest at most eight records;
- legacy MCP file-memory names removed from catalog policy and function bindings;
- prepared mutate/reconcile intents, exact request correlation, bounded uncertain-
  outcome reconciliation, and rejection of unauthorized current bindings;
- bounded durable journal admission, safe path/link/temp handling, corrupt-receipt
  quarantine, terminal redaction/compaction, successor ownership preflight,
  `rollback_started`, and two-phase delete;
- ordinary historical committed reconcile inspection reporting stale without rolling
  back solely for a post-response roster change, while staged delete may finalize or
  roll back;
- explicit reconcile validation of bounded response shape, tenant, space, access,
  lifecycle state, and available lineage (not an unavailable target-contract digest);
- health readiness booleans, bounded hybrid inspection, deliberate exact-ID repair,
  and desktop participant-service adaptation without hidden `RememberAsync`; and
- fresh current-format storage with no migration or old-vector read path.

## Final automated gates still pending

- Rerun the corrected 91-test focused Release selection and report exact pass/fail/skip
  counts.
- Rebuild the final source after all CP11 code/test edits converge and report warnings
  and errors without extrapolating runtime behavior.
- Rerun the consent, production-boundary, participant-service integration, user-memory
  architecture/source-canary, embedding-settings, and MCP legacy-name cases included
  by the final filter.
- Run any available Python worker tests only if an intended Python runtime is present;
  until then keep the evidence explicitly source-only.
- Run the broader solution suite separately and distinguish CP11 regressions from
  unrelated repository baseline failures. Do not restate a partial selection as broad
  green.
- Complete the independent read-only final diff audit before commit, push, publish,
  shortcut work, or UI launch.

## Live and environment-dependent checks not run

No current evidence proves:

- Python syntax/import/runtime execution of `mem0_service.py` or `local_qdrant.py`;
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
- published-folder or shared-shortcut launch, or any UI launch/visual verification;
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
- Exact mutation response validation includes provenance and consent values.
- Explicit reconcile validates the returned bounded shape, tenant, space, access,
  state, and available lineage. It cannot claim target-contract-digest validation
  because that digest is absent from the response.
- An ordinary historical committed receipt is not rolled back merely because the
  roster becomes stale after inspection. Staged delete is the special two-phase state
  that may be finalized or rolled back under current authority.
- The production MCP catalog no longer exposes the three legacy file-memory names.

## Deterministic-processing statement

CP11 adds exact typed validation, freshness, receipt, consent, authentication, access,
embedding-identity, ranking, bounded-journal, reconciliation, rollback, delete, and
repair rules. It adds no English keyword/pronoun routing, deterministic semantic
interpretation, memory-relevance decision, answer selection, or response formatting.
The complete rule inventory and effect boundaries are recorded in
`docs/reviews/CP11-DETERMINISTIC-RULE-DISCLOSURE.md`.
