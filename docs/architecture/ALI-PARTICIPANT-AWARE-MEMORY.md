# Ali Participant-Aware Memory

## Status and semantic boundary

Checkpoint 11 (CP11) is a fresh participant-aware memory path. It does not migrate,
import, query, copy, or delete old Mem0 history or old Qdrant vectors. The configured
Agent Framework model still decides whether memory is relevant, chooses a tool and
query, proposes the semantic roles and mutation, interprets retrieved evidence, and
writes the answer. CP11 adds typed mechanical admission and durability; it does not
parse English names, pronouns, phrases, or keywords to make those semantic decisions.

The existing target-selection, camera, microphone, and speaker-recognition producers
remain independent latest-value producers. CP11 consumes immutable publications and
cannot queue, block, back-pressure, or change them. Explicit profile selection is the
participant authority. Recognition and presence are advisory only, never consent,
authentication, liveness, or privileged authority.

## End-to-end shape

At admission Ali captures an immutable selected-participant roster and advisory fresh
presence. Trusted adapters add the roster, provenance, permissions, consent,
authentication, access keys, embedding identity, and stable durable operation ID;
the model cannot supply those fields. Recall and inventory prefilter the backing
store by exact authorization before any candidate is scored or returned. Mutations
and explicit reconciliation enter a single-writer receipt journal and are addressed
by one stable ID, so an uncertain result is inspected rather than blindly replayed.

Coordinator durable-effect ownership is selected by exact participant tool name.
Consent, mutation, and explicit reconciliation each require a prepared intent. Their
operation IDs use the `participant-consent:`, `participant-mutation:`, and
`participant-reconcile:` domains respectively. A consent intent can reconcile only
to the process-local grant state it prepared; after restart it safely reports no
durable consent effect. A mutation or reconciliation intent never reconstructs
authority from prose.

The production MCP server no longer publishes or binds the legacy file-memory names
`recall_user_memory`, `list_current_user_memories`, or
`forget_current_user_memory`. Configuration normalization cannot re-enable them.
Participant memory remains in Ali's participant tool/service path, and legacy worker
operations are rejected from the CP11 collection.

## Immutable roster and advisory presence

`SelectedParticipantRosterAuthority` captures tenant, turn, conversation, capture
time, selection revision, presence revision, selected participant, and at most 16
normalized participants. The advisory feed accepts at most one target and one speaker:

- target publications are fresh for 3 seconds;
- speaker publications are fresh for 15 seconds;
- future-dated values are stale;
- targets require `HasTarget`;
- speaker-enrollment utterances are excluded; and
- attachment epoch, source sequence, and freshness participate in presence revision.

An exact local profile match becomes registered. An unmatched nonempty recognition
ID becomes an opaque conversation-scoped guest; a missing ID becomes an opaque
conversation-scoped unknown. Raw recognition IDs are not durable guest IDs. At most
4,096 conversation scopes are retained. Reads and consequential operations compare
the exact current selection revision, presence revision, and selected participant
before and after relevant work and fail closed when they change.

## Record, access, and authority

`ParticipantMemoryRecord` stores text, category, speaker, subjects, witnesses,
reporter, shared-event reference, claim/evidence kinds, visibility, audience,
sensitivity, attribution confidence, lifecycle state, source provenance, consent
receipts, correction/dispute lineage, timestamps, and exact embedding-space identity.
Those roles remain independent: a report by Alice about Bob is not rewritten as a
fact owned by Bob. Corrections append successors and lineage; they do not overwrite
the original text or provenance.

Exact access keys are installation general-low, participant low/sensitive, or future
team low/sensitive keys. A current exact read receipt admits general-low and the
selected principal's low key. Sensitive access requires current independent
authentication for that principal. Team/project keys fail closed until a trusted
membership authority exists.

Adds, corrections, and disputes require consent from every distinct proposed
speaker, reporter, subject, witness, participant audience member, and selected
requester. Each participant must be separately selected and approve the unchanged
typed proposal. The proposal fingerprint is SHA-256 over the exact tenant and complete
proposal. The coordinator retains at most 256 proposal fingerprints and removes
expired five-minute grants. Issued short turn-bound receipts are then atomically bound
as one consent session to the first stable mutation request ID. A same-ID retry is
allowed; a different request ID is rejected. The process-local receipt authority
retains at most 4,096 issued receipts and FIFO-evicts the oldest, which immediately
removes its authority. Consent-session bindings have their own 4,096 cap and prune
only when expired or when no current issued consent remains for the session; if safe
binding admission still has no room, it fails closed. Unknown participants cannot
consent.

Sensitive mutations and every correction, dispute, revoke, archive, delete, repair,
or explicit reconciliation require independent authentication. The production
Windows provider prompts for a credential, validates it with interactive `LogonUser`,
requires the token SID to equal Ali's current process SID, checks the exact profile
selection before and after, and clears credential buffers. The native credential
prompt is synchronous and is not separately cancellable; cancellation is observed
before and after it. Production real-profile authentication is intentionally scoped
to exactly one registered non-test profile. Installations with multiple real profiles
fail closed for these consequential paths until per-profile credential binding exists.
A narrowly admitted generated authoritative John Doe test profile may use the test
factor for maintenance coverage; a caller flag cannot turn a real profile into that
profile. Recognition never supplies authentication.

For every non-add mutation the authenticated requester must also be the exact target
record's speaker, subject, or provenance reporter. Witness or audience membership
alone is insufficient. C# validates the exact returned provenance and consent arrays,
not merely counts, as well as tenant, space, access, state, target, and lineage.

## Provider-parity embedding identity

Every identity resolution performs two live, bounded endpoint probes; there is no
five-minute or other probe cache:

1. `ali-participant-memory-embedding-identity-probe-v1`; and
2. an exact 4,096-character application-boundary input.

The request matches pinned Mem0 OpenAI-compatible behavior: input is a one-element
JSON array, newline characters are normalized to spaces, `encoding_format` is
`float`, and authorization is `Bearer ali-local-only`. Each response is bounded to
1 MiB, must contain finite numeric values, and must have exactly the configured
dimensions. Vector lengths and exact float bits are hashed in fixed order. The digest
joins provider, protocol, endpoint, configured/resolved model, dimensions, reported
context, prompt modes/prefixes, and resolution source. Storage identity additionally
binds the base collection, stdio protocol, and Qdrant connection identity. The
4,096-character probe is not a token-limit claim; an unreported token limit remains
zero. Identical probes retain the same space, but they are still performed on every
resolution.

The current root is `Memory/ParticipantAware/Mem0`; history is under
`embedding-spaces/<space-id>`; the base collection is
`ali_participant_memories_cp11`; the effective collection is
`<base>__embedding_<space-id>`; and the stdio protocol is
`ali-participant-memory-stdio-v2`. There are no version fields, migrations,
compatibility readers, old-data importers, or vector-copy paths.

The transport deadline covers identity resolution, both probes, worker startup, and
the request/response exchange once that budget begins. Recall/list default to 2.5
seconds, mutation/reconciliation to 5 seconds, health to 3 seconds, and deliberate
repair to 30 seconds, with bounded configuration ranges. The Windows credential
prompt occurs before that transport budget and, as noted above, is synchronous rather
than separately cancellable. Every stdio request owns correlation and space IDs and
requires exact echoes. An abandoned post-write exchange resets the worker; the worker
also belongs to a Windows kill-on-close Job Object. Disabled memory starts none of
these resources.

## Exact privacy-filtered recall and inventory

For each already-authorized access key, Qdrant filters exact tenant, embedding space,
confirmed state, and that key before candidate retrieval. Ali calls its own
`search_exact_hybrid`; it does not call `Memory.search` and therefore does not run
Mem0 entity extraction, entity embeddings, entity-link boosts, English lemmatization,
or an answer-finding layer.

The exact deterministic hybrid mechanics are:

- request results clamp to 1-8; dense candidate capacity is `max(topK * 4, 60)`;
- lexical tokens are Unicode NFKC plus casefolded letter/digit runs only;
- BM25 uses only those dense, already-authorized candidates, unique query terms,
  candidate-local document frequency and average length, `k1=1.5`, and `b=0.75`;
- sigmoid `(midpoint, steepness)` is `(5,.7)` for at most 3 query tokens, `(7,.6)`
  for at most 6, `(9,.5)` for at most 9, `(10,.5)` for at most 15, and `(12,.5)`
  above 15;
- dense candidates below 0.1 are discarded;
- when any candidate has a positive BM25 score, combined score is
  `(dense + BM25) / 2`; otherwise it is dense alone; combined score clamps to 1;
- within each exact-key query, candidates order by combined score descending and ID
  ascending, then truncate to the requested maximum;
- authorized keys are normalized into ordinal order; the cross-key merge keeps the
  higher score for a duplicate ID, orders by score descending, and for exact score
  ties preserves first insertion (sorted key order, then the per-key ID order); and
- C# applies its bounded numeric evidence gates and repeats correlation, roster,
  tenant, space, confirmed-state, access, and count checks before returning records.

Duplicate IDs across authorized-key queries retain the higher score. Inventory does
not score semantics: it scans every exact-filtered page for every authorized key,
deduplicates, maintains the newest bounded candidates, orders by created timestamp
then ID descending, and returns at most 8. It does not stop after the first page or
first eight points.

## Mutation journal, reconciliation, rollback, and delete

The worker accepts only add, correct, dispute, revoke, archive, and delete, with Mem0
inference disabled. A mutation fingerprint covers the stable ID, tenant, roster,
space, complete proposal, provenance, exact consent receipts, access keys, requesting
principal, authentication state, and target material. Same ID plus different material
is a conflict; same ID plus identical material inspects durable state.

Each embedding space uses a nonblocking OS file lease around write, reconcile, and
rollback. The journal admits at most 4,096 active receipt files, each at most 2 MiB.
New mutations require Ali's timestamped request-ID form, no more than five minutes in
the future and no more than 24 hours old. Files are written through an exclusive
0600 temporary file, flushed and fsynced, atomically replaced, permissions tightened,
and the containing directory fsynced where supported. Directories and receipts must
be non-reparse, regular, single-link paths; unsafe links are not followed. Hour-old
safe temporary files are cleaned. Unreadable/corrupt receipts are quarantined, not
guessed. Terminal committed/rolled-back content older than 24 hours is replaced by a
redacted hashed tombstone, and old terminal tombstones may be compacted to make room.
The bounded journal fails closed when safe admission or quarantine capacity is gone.

Before rollback side effects the journal persists `rollback_started`. Restart or a
retry resumes exact rollback from that durable state. Successor rollback preflights
exact creation owner, pending owner, last owner, and expected record digest before
changing any point, preventing an old receipt from deleting a later-owned successor.
Power-loss durability of these file and directory operations on Windows has not been
executed or proven.

Delete is two phase. Mutation first persists the exact target snapshot, deletes and
verifies absence, and returns `delete_staged`. C# then revalidates current authority,
roster, and authentication. Explicit reconciliation can finalize the committed,
zero-content tombstone or authorized rollback can restore the exact snapshot. A
worker response that skips staging and claims direct finalization is rejected.

Explicit reconciliation is not a read-only operation: it can promote a fully applied
receipt, finalize a staged delete, roll back a staged delete when authority becomes
stale, or resume rollback. For returned records C# validates bounded shape, exact
tenant, embedding space, authorized access, expected state, and the lineage fields
available in the response. It cannot validate an exact target-contract digest because
that digest is not present in the reconciliation response. An ordinary historical
committed mutation is never rolled back solely because the roster becomes stale after
inspection; the call reports stale instead. Staged delete remains eligible for exact
finalization or rollback.

## Health, repair, desktop, and verification boundary

Health reports embedding, Mem0, and Qdrant readiness as separate explicit booleans.
Hybrid inspection is read-only and reports at most 32 opaque pending IDs. Repair is a
deliberate 1-32 exact-ID, authorized, authenticated operation; it validates each point
independently and never writes an empty sparse vector.

Desktop settings adapt read, list, test, correction, delete, health, and repair into
the same participant service. The passed profile must equal current explicit
selection. Correction/delete target only an exact current bounded review-cache ID.
Automatic legacy `RememberAsync` remains retired.

The current evidence is deliberately narrower than runtime certification. The last
focused relevant Release run compiled 91 tests: 90 passed and one stale source-canary
assertion failed. That canary was corrected, but the final rerun is pending. An earlier
focused checkpoint was 46/46 green. No Python worker runtime, live provider, Mem0,
Qdrant, Windows prompt, camera, microphone, multi-person consent, crash/power-loss,
hardware, published UI, or broad-suite claim is made. See
`docs/reviews/CP11-VERIFICATION-REPORT.md` for the exact boundary and
`docs/reviews/CP11-DETERMINISTIC-RULE-DISCLOSURE.md` for the rule inventory.
