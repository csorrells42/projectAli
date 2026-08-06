# Project Ali — Complete Architecture Review: Keep, Fix, Cut

**Method:** This is not a fresh investigation — it synthesizes direct, adversarial, evidence-based findings from an extensive review conducted across this codebase's evolution (CP6 through the current live checkout), plus the implementation work just completed (participant-memory recall wiring). Every claim below traces to something actually read in the source, not inferred. Evaluated against the three stated non-negotiable goals: **fast, reliable (always completes unless impossible), honest (never lies).**

## Executive verdict

**The architecture does not need a rebuild. It needs its own already-written plan finished, with a specific, named set of real defects fixed along the way.** `docs/ALI_CORE_FIRST_REINTEGRATION_PLAN.md` (Sol's staged 13A-13G plan) is genuinely sound — disciplined budgets, honest rollback criteria, and it has already proven itself on real live runs. The pattern that's actually caused the frustration, based on everything visible in this repo, is **repeated restarts that never finished a checkpoint**, not a flawed design. This review's job is to say precisely what's good enough to keep untouched, what's good but has a real bug, and what's genuinely questionable — so the next attempt finishes instead of restarting again.

---

## Layer-by-layer verdict

### 1. The conversational entry loop (`RunCoreAssistantAsync` / `AliMinimumMessage`)
**KEEP the foundation.** Raw Agent Framework tool-calling, no decisionJson protocol wrapper, no completion critic in the loop — this is architecturally the right shape for speed, and it's real, working code, not a stub.

**FIX — highest priority in this entire review:** there is no real token-level streaming anywhere in the model-client layer. Confirmed directly: `OpenAiCompatibleLocalModelRuntime`'s payload construction hardcodes `stream = false` unconditionally, and `GetStreamingResponseAsync` at three separate layers (`OpenAiCompatibleLocalModelRuntime`, the orchestration planning client, `SafeActivatingLocalRuntime`) all just `await` the complete non-streaming response and fake a stream from the finished text afterward. **The model finishes generating the entire answer before the user sees a single character.** This is almost certainly the single biggest cause of "10 seconds to say hello" and the single highest-leverage fix available — it's well-understood, localized to a known set of files, and doesn't require any architecture change, just wiring real `stream: true` through and reusing the SSE reader that already exists in the codebase but is currently dead code on this path.

**Needs finishing, not fixing:** whether the user's own configured persona/system prompt is actually preserved during the real answer-generation call in this path was not fully verified — only confirmed that two small side-classifiers (tool-routing, behavior-verification) suppress persona, which is reasonable for narrow yes/no judgments. Worth closing this out explicitly, since "does Ali sound like herself" matters for the stated "becomes a friend over time" goal, not just correctness.

### 2. Tool inventory / capability system
**KEEP, untouched.** The capability registry, cross-path permission consistency, and JSON-schema validation hardening (the CP13-era `CapabilityJsonSchemaValidator`, MCP schema admission) were both independently adversarially audited and came back clean — no defects found across two separate passes, real DoS bounding verified by tracing the actual recursion budget, real duplicate-property rejection verified. This is some of the strongest engineering in the codebase. Do not rewrite it.

**STAGE, don't rush.** Broader tool categories beyond the current weather + C# + (now) memory allowlist should come in one at a time, exactly as Sol's plan already specifies (Checkpoint 13F) — this narrow-allowlist approach is itself a lesson already learned, not something to abandon for speed.

### 3. Durable evidence/journal architecture (evidence ledger, turn journal, execution grants, changesets)
**KEEP the design.** Exact evidence-to-work-item binding, write-ahead ordering, idempotency-key handling, and the changeset system's pre-image-backed rollback were all confirmed sound under dedicated adversarial audit tracing real crash-interruption scenarios. This is genuine, valuable engineering — the kind that makes "does not lie" actually true for anything that uses it, not just a policy statement.

**FIX (already designed, ready to implement):**
- The changeset reconciler misclassifies the single most common crash point — right after a file write succeeds, before the journal entry confirming it lands — as "in doubt" even though the file's true state is cryptographically provable right there. A real, if narrow, gap between "provably fine" and "reported as stuck."
- The execution broker throws a hard error on a legitimate duplicate/idempotent prepare instead of adopting it like the rest of the codebase's established pattern — a spurious failure on retry, not a correctness hole.

**STAGE.** This whole layer is currently "preserved but bypassed" for speed on the fast path (Checkpoint 13D territory), and that's correct — ordinary chat shouldn't pay journal-write latency. It should come back for real file mutations and builds before it comes back for plain conversation.

### 4. Completion critic / answer composition (CP8)
**FIX before reintroducing, don't just re-enable.** A real bug: if the process is interrupted mid-review and later regenerates the identical answer (realistic for local, low-temperature models — the exact profile this product targets), the turn can get **permanently stuck** with no automatic recovery path. A concrete fix already exists (bind the review lease to a process-run token backed by an OS-level exclusive lock, reclaim on a provably-dead prior run — no wall-clock guessing) and is ready to implement when this checkpoint comes up.

**Open question, not yet answered — flagging for you, not deciding it myself:** does Ali need a fully separate critic implementation at all, or could the lightweight fast-path verifier (`CoreAssistantCompletionGate`'s single "did this actually do what was asked" check, already live and already caught a real false-completion case in Sol's own test log) be extended incrementally instead of maintaining two parallel critic systems long-term? I don't have a strong verdict here yet.

### 5. Participant memory (Mem0)
**KEEP, and now partially working** — recall is wired into the live fast path as of this session, build verified clean. Along the way this surfaced a real, generalizable hazard: `AliParticipantMemoryTools` assumed full-orchestration-path plumbing (an active-call-id tracker, a permission receipt) that the fast path never populates, so it would have failed silently every time without the fix. **Worth a deliberate check for the same trap in anything else pulled from `CoreRecoveryToolNames` before wiring more tools in.**

**Real, honest gap, not yet closed:** automatic memory *formation* — Ali noticing and remembering something on her own, without an explicit "remember this" — does not exist anywhere in the codebase, dormant or otherwise. Confirmed by direct search. This is genuinely new work, and it's probably the single most valuable next step toward "becomes a friend over time," since recall alone only returns what was manually stored before.

### 6. MCP (external tool servers)
**KEEP, audited twice, clean.** The one real issue is latency-shaped, not correctness-shaped: settings get re-read and content-hashed up to five times per turn with no caching (unlike the permission store, which already caches correctly), and per-server connection attempts are sequential rather than parallel. Both are cheap, already-specified, low-risk fixes.

### 7. The heavy decisionJson orchestrator (`AliOrchestrationPlanningClient`) — the real open decision
This is the one place that genuinely deserves a "keep or cut" conversation rather than just staging. It duplicates work the fast path already does (tool execution, evidence recording) through a substantially heavier, slower mechanism, with a rigid protocol-only system prompt that doesn't preserve persona for any conversational turn that goes through it. As the fast path gets staged up through 13D/13E and picks up durability and evidence properties of its own, there's a real possibility this entire implementation becomes redundant — at which point it should be deleted outright, not left dormant forever as dead weight.

**I'm not recommending cutting it today** — the fast path doesn't have durability/evidence yet, so the heavy path is still the only thing that provides those properties at all. But this should be an explicit, planned decision point later in the plan, not something that just quietly lingers.

---

## What I'd actually do next

Finish Checkpoint 13A properly before anything else: fix the no-streaming bug. It's the highest-impact, best-understood, lowest-risk item in this entire review, it directly attacks "quick on the draw" — the thing you keep coming back to — and it requires zero architecture rethinking, just wiring real streaming through a client layer that already has the pieces (an SSE reader already exists, just isn't reachable from this path).

Everything else above is real and worth doing, but doing it all at once is the same failure pattern that's already been tried. One finished checkpoint beats five started ones.
