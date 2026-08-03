# Checkpoint 11 Deterministic-Rule Disclosure

Date: 2026-08-03
Scope: Project Ali checkpoint 11 participant-aware memory

## Semantic boundary

CP11 adds no English keyword router, phrase matcher, pronoun resolver, name parser,
deterministic intent classifier, semantic target selector, answer finder, or answer
formatter. The configured Agent Framework model decides whether memory is relevant,
chooses recall/list/consent/mutate/reconcile, writes the recall query, proposes all
semantic record fields and exact mutation target, interprets results, and composes the
answer. Mechanical code may reject or bound a typed proposal but never rewrites it
from prose.

## Capability and prepared-intent rules

- Durable ownership uses exact ordinal participant tool names and matching adapter/
  reconciler identities. Descriptions, prose, and argument keywords do not confer it.
- Consent, mutate, and explicit reconcile all require a coordinator-prepared intent.
  Their stable operation-ID domains are `participant-consent:`,
  `participant-mutation:`, and `participant-reconcile:`. Recall/list are reads and do
  not receive prepared intents.
- Intent IDs bind durable turn/call identity, canonical arguments, authority context,
  and exact tool domain. Different material under the same ID is rejected.
- Consent reconciliation reports whether the exact process-local grant was recorded;
  restart cannot recreate it and safely resolves it as absent/no durable effect.
- Reconciliation is not mechanically classified as read-only: worker reconciliation
  can promote exact complete state, finalize or restore a staged delete, or resume a
  durable rollback.
- The production MCP catalog removes and cannot bind the legacy file-memory names
  `recall_user_memory`, `list_current_user_memories`, and
  `forget_current_user_memory`; configuration normalization drops them.
- If participant trust dependencies are absent, the model-visible recall/list tools
  return unavailable and never fall back to the legacy memory store.

These rules affect capability admission and recovery only. They do not infer intent.

## Roster and advisory-presence rules

- A roster normalizes and binds exact tenant, turn, conversation, selection revision,
  presence revision, selected reference, capture time, and at most 16 participants.
- A CP11 read-only boundary double-samples the frozen Identity session's immutable
  selection and registry state. An unstable sample fails closed; any observed material
  change advances an opaque participant generation. Roster authority captures its own
  selection and generation together rather than trusting a caller generation.
- Target freshness is 3 seconds and speaker freshness is 15 seconds. Future values,
  targets without `HasTarget`, and speaker enrollment utterances are excluded.
- At most one fresh target and one fresh speaker are projected. Camera attachment
  epoch, material target identity/track, material speaker identity, and fresh-versus-
  absent state enter the presence revision. Per-frame source sequence/timestamp changes
  intentionally do not churn it; attach/detach or unavailable source changes it.
- Exact local membership maps a recognized ID to a registered profile. Other nonempty
  IDs map to opaque conversation guests; missing IDs map to opaque conversation
  unknowns. At most 4,096 conversation scopes are retained.
- Consequential calls and returned reads require exact current selection revision,
  presence revision, and selected reference. The code rejects stale state rather than
  guessing another participant.

These rules can admit or invalidate identity context. They do not authenticate,
recognize liveness, or decide a semantic role.

## Proposal, receipt, permission, consent, and authentication rules

- Memory text is 1-4,096 characters; category is 1-128; CR, LF, and tab are permitted
  content while other control characters are rejected before dispatch and on returned
  records. Participant/reference lists are normalized, deduplicated, and bounded.
  Operations, claim/evidence kinds,
  visibility, sensitivity, states, IDs, provenance, and target requirements use closed
  typed sets and exact comparisons.
- Permission, consent, and authentication authority accepts only the exact immutable
  object the process issuer retains, with matching ID, principal, operation, turn,
  visibility/audience, source, issuance, and expiry. Receipt-shaped model JSON has no
  authority.
- Receipt lifetimes are positive and at most 10 minutes. Model permissions are five
  minutes, desktop permissions two minutes, and production authentication two minutes.
  The issuer retains at most 4,096 receipts and FIFO-evicts the oldest, immediately
  removing its authority. Consent-session bindings have a separate 4,096 cap and
  prune expired bindings or bindings with no current issued consent; inability to
  admit a binding after pruning fails closed.
- The requester must be in the admitted roster and not unknown. Exact selection is
  participant authority, but it is not an independent authentication factor.
- Adds, corrections, and disputes require consent from every distinct speaker,
  reporter, subject, witness, participant audience member, and selected requester.
  Revoke/archive/delete do not stage proposal consent but remain consequential.
- Consent fingerprint is SHA-256 over exact tenant plus the complete typed proposal as
  supplied before policy normalization.
  Any field change changes the fingerprint. At most 256 proposal
  fingerprints are retained and expired five-minute grants are removed.
- Each required participant is separately selected and approves the unchanged
  proposal. Unknowns cannot consent. The resulting short turn/operation/visibility/
  audience receipts are atomically bound to the first stable mutation request ID;
  same-ID retry succeeds and a different ID fails.
- Sensitive add plus every correct, dispute, revoke, archive, delete, repair, and
  explicit reconcile requires current independent authentication for the exact
  principal and operation.
- Production authentication is allowed only when the selected real profile is the
  sole registered non-test profile. Two or more real profiles fail closed until
  per-profile Windows credential binding exists.
- The Windows provider uses credential UI plus interactive `LogonUser`, requires the
  authenticated SID to equal the current Ali process SID, rechecks the same exact sole
  profile before and after, and clears the credential buffers. The native prompt is
  synchronous, not separately cancellable; cancellation is observed around it.
- `TrustedTestFactor` is accepted only for the authoritative generated John Doe test
  profile under the narrow test/maintenance boundary. A caller's `IsTestProfile`
  field cannot promote a real profile. Face/speaker recognition and passive presence
  are never authentication factors.
- Every non-add target mutation repeats exact tenant/space/access/state checks and
  requires the authenticated requester to be the target's speaker, subject, or
  provenance reporter. Witness or audience membership alone does not authorize it.
- Team/project or caller-supplied team audience keys fail closed without a trusted
  membership authority.

These rules can require another approval or credential and reject copied authority.
They cannot infer who intended a statement.

## Access, exact hybrid ranking, and inventory rules

- Access keys are exact installation general-low, participant low/sensitive, or team
  low/sensitive strings. Current read permission admits general-low and selected
  participant-low. Participant-sensitive additionally requires current independent
  authentication. Team keys are unavailable today.
- Each authorized key is queried separately with exact tenant, embedding space,
  confirmed state, and key in the Qdrant filter before retrieval/scoring. Duplicate
  IDs retain only the higher score.
- Ali calls `search_exact_hybrid`, not `Memory.search`; entity extraction, entity
  embeddings, entity-link boosts, answer finding, and English lemmatization are absent.
- Result maximum clamps to 1-8. Dense candidate capacity is
  `max(requestedMaximum * 4, 60)`.
- Lexical tokens are Unicode NFKC, casefolded, contiguous letter/digit runs. Punctuation
  splits runs; there is no language dictionary, stemming, synonym, or stopword rule.
- Candidate-local BM25 considers only the dense authorized pool. It uses unique query
  terms, candidate document frequencies, candidate average token length (minimum 1),
  `k1=1.5`, `b=0.75`, and IDF
  `ln(1 + (N - df + .5) / (df + .5))`.
- Positive raw BM25 is sigmoid-normalized. `(midpoint, steepness)` is `(5,.7)` for
  1-3 query tokens, `(7,.6)` for 4-6, `(9,.5)` for 7-9, `(10,.5)` for 10-15, and
  `(12,.5)` above 15. Empty query has no BM25 score.
- Non-finite dense scores are discarded; dense and BM25 components clamp to 0-1.
  Dense score below 0.1 is discarded. If any candidate has positive BM25, every
  combined score is `(dense + candidateBM25) / 2`; otherwise combined score is dense.
  The result clamps to 0-1, sorts score descending then ID ascending within that exact-
  key query, and truncates.
- Authorized keys are normalized in ordinal order. Cross-key merge retains the higher
  score for a duplicate ID and sorts by score descending; an exact score tie preserves
  first insertion, which follows sorted key order and then per-key ID order.
- C# requires numeric scores. Default evidence gates require top normalized score at
  least .30. Keyword score at least .08 admits the keyword-support branch; otherwise
  semantic score must be at least .50 and, when below .65 with a runner-up, lead by at
  least .08. Returned records remain within .05 of top and at least .30. Thresholds
  clamp to 0-1 and the top window to 0-.25.
- C# repeats exact request/response correlation, result count, roster revision,
  tenant, space, confirmed state, and authorized-access intersection, then checks live
  roster freshness before exposing results.
- Inventory performs no semantic scoring. It scans every exact-filtered Qdrant page
  for every authorized key, deduplicates, retains the newest bounded candidates,
  orders created timestamp then ID descending, and returns at most 8. It never treats
  the first eight scanned points as a complete inventory.

These rules mechanically rank and bound already-private candidates. They do not decide
whether the query is relevant or whether a memory answers it.

## Embedding identity, namespace, and transport rules

- Every resolution sends two live probes; no probe cache exists. Inputs are the fixed
  identity string and exactly 4,096 `x` characters.
- Both requests match pinned Mem0: JSON `input` is a one-item array, newline is
  replaced with space, `encoding_format` is `float`, and Authorization is
  `Bearer ali-local-only`.
- Each response is at most 1 MiB, nonempty numeric/finite, and exactly the configured
  dimensions. Any failure prevents worker/Qdrant use for that operation.
- SHA-256 covers vector lengths and exact little-endian float bits in fixed probe
  order. The fingerprint additionally binds provider, protocol, endpoint, configured
  and resolved model, dimensions, reported context, query/document modes and prefixes,
  and resolution source. Probe time is health evidence, not stable identity.
- The 4,096-character probe proves only Ali's application input boundary and never
  becomes a token claim. Unreported maximum tokens remain zero.
- Space identity also binds base collection, stdio protocol, embedding fingerprint,
  and Qdrant host/ports/TLS/API-key environment-variable identity. Its SHA-256 prefix
  selects per-space history and collection.
- Transport-owned request and space IDs must echo exactly. A post-write abandoned
  exchange resets the private worker. Disabled memory does not probe or start storage.
- Recall/list deadline defaults to 2,500 ms and clamps 250-5,000; mutation/reconcile
  defaults 5,000 and clamps 500-15,000; health defaults 3,000 and clamps 250-5,000;
  repair defaults 30,000 and clamps 1,000-60,000. Identity resolution, probes, worker
  startup, and exchange share the operation budget once it begins. Credential UI is
  before that budget and is synchronous, not separately cancellable.

These rules can reject unverifiable vectors or choose a new namespace. They do not
interpret vector meaning.

## Mutation, receipt journal, reconciliation, rollback, and delete rules

- Accepted mutations are add, correct, dispute, revoke, archive, and delete. Mem0
  inference is disabled. Adds create one confirmed record; correction/dispute create
  one candidate successor, preserve audience/security, transition the exact target,
  then confirm exact lineage; revoke/archive transition the target out of confirmed
  state.
- Fingerprint covers request ID, tenant, roster, space, complete proposal, exact
  provenance and consent arrays, record/access keys, requester, authentication, and
  the exact target ID carried by the proposal. Target snapshot and successor-contract
  digest are separate receipt/preflight fields. Same ID/different fingerprint
  conflicts; same ID/same fingerprint inspects existing exact receipt/points.
- C# add/correct/dispute validation checks bounded count and shape, exact proposal
  metadata, tenant, space, access, state, target, operation lineage, and exact returned
  provenance and consent arrays. Revoke/archive require exact target ID plus lifecycle
  state; staged delete requires the exact target, while finalized delete returns zero
  records.
- One nonblocking embedding-space file lease covers every write/reconcile/rollback.
  Contention returns a retryable conflict.
- The journal is bounded to 4,096 active receipts and 2 MiB per receipt. New mutation
  IDs must use an admitted Ali timestamped domain, be at most five minutes future and
  no more than 24 hours old.
- Journal paths must be safe non-reparse directories and single-link regular files.
  Writes use exclusive safe 0600 temporaries, file flush/fsync, atomic replace,
  permission tightening, and supported directory fsync. Unsafe links are never
  followed. On the next under-lease writer maintenance after one hour, safe stale
  temporary files are cleaned; any remaining temporary blocks that writer.
- Unreadable receipts are quarantined. Any `.corrupt`, remaining `.tmp-*`, or unsafe
  `.json` artifact blocks every writer, reconcile/rollback, delete scrub, and global
  lineage/reference decision until deliberate local repair. On the next under-lease
  writer maintenance after 24 hours, terminal committed/rolled-back receipt content
  crosses to a redacted hashed `recovery_expired` tombstone. Old terminal tombstones
  are compacted only as needed for bounded admission; active or recoverable receipts
  are not pruned. Full,
  unclassified, or quarantine-full state fails closed.
- Successor creation saves exact created IDs and authorization snapshot before later
  transitions. Rollback preflights creation request, pending owner, last owner, and
  expected digest for all successors before changing any, preventing an old receipt
  from deleting later-owned state.
- Before any rollback side effect, status `rollback_started` is persisted. Retry or
  restart resumes from that exact state; completion records `rolled_back`.
- Delete is two-phase. Worker persists snapshot, deletes and verifies absence, then
  returns `delete_staged`. C# revalidates authority/roster/authentication. Exact
  reconcile then finalizes a zero-content committed tombstone or authorized rollback
  restores the snapshot. Direct delete finalization that skips staging is rejected.
- Before its first irreversible associated-receipt redaction, delete persists
  `finalization_started` and the exact bounded scrub receipt IDs. Retry/restart resumes
  only that set; rollback is rejected once this monotonic marker exists.
- Uncertain write outcomes trigger one bounded same-ID inspection. Reconcile can mark
  fully applied state committed, finalize/restore staged delete, or resume rollback;
  it never fills arbitrary missing mutation work.
- Explicit reconcile requires prepared intent, exact bounded request ID, interactive
  permission, current authentication, and live authority. Its response is checked for
  bounded shape/count, exact tenant, space, access, expected lifecycle state, and
  available correction/dispute lineage. The response does not carry the target
  contract digest, so C# does not claim to validate that digest on reconcile.
- If roster state becomes stale after inspecting an ordinary historical committed
  receipt, C# reports stale and does not roll it back merely because of that later
  change. Staged delete may be exactly finalized or rolled back. A newly committed
  in-flight mutation may still enter same-ID authorized rollback under its mutation
  path.
- Journal file/fsync logic is present, but actual Windows power-loss durability is
  unproven because no power-loss run was executed.

These rules make exact retries attributable and bounded. They do not select a mutation
or semantic target.

## Health, repair, desktop, and current-format rules

- Health requires explicit embedding, Mem0, and Qdrant booleans. Missing does not
  mean ready. Hybrid inspection is read-only and reports at most 32 pending IDs.
- Repair requires 1-32 unique bounded exact IDs, Repair permission, matching current
  roster/space, authorized access, and independent authentication. Each ID is isolated;
  only missing sparse vectors are installed and empty sparse vectors are rejected.
- Desktop calls require passed profile equal current selected profile. Review cache is
  bounded; correction/delete must target an exact cached authorized ID and enter the
  participant mutation path. Hidden automatic `RememberAsync` remains retired.
- CP11 uses only `Memory/ParticipantAware/Mem0`, per-space history, the
  `ali_participant_memories_cp11` collection family, and stdio v2. No schema version,
  migration, compatibility reader, old importer, or vector-copy rule exists.

## Evidence boundary

These are source rules, not proof of a live deployment. The current expanded focused
Release selection is 104/104 green, the two exact legacy-MCP boundary tests are 2/2
green, and the current Release app build has zero warnings and zero errors. Python
worker runtime, live Mem0/Qdrant/provider, UI, Windows credential, multi-person,
crash/power-loss, hardware, and broad-suite-green behavior are not claimed; the
attempted broad suite was non-green.
