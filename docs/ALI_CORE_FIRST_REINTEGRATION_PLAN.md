# Project Ali Core-First Reintegration Plan

Status: authoritative execution plan for checkpoints 13A through 13G
Mission: **truth, reliability, speed**
Release objective: restore Ali as a fast, dependable assistant first, then re-enable the preserved hardened systems one bounded layer at a time.

## 1. Non-negotiable mission

Ali's core contract is:

1. Give the most truthful answer available from the model and proven tool results.
2. Carry an achievable request to completion; when completion is impossible, explain the exact reason truthfully and promptly.
3. Add as little latency as physics and the selected model/runtime allow.
4. Manipulate files only inside the configured workspace during this reintegration.
5. Preserve all hardened code and tests. A system that is not yet fast or reliable enough remains dormant behind an explicit boundary; it is not deleted or weakened in place.

No checkpoint may improve a secondary subsystem by degrading ordinary chat, current weather, or the essential C#/Roslyn workflow. A nonessential subsystem that violates the core contract is bypassed immediately and returned to its last dormant state.

## 2. Measurement definitions and universal release budgets

All checkpoints use the same definitions so results cannot be improved by changing the measurement.

### 2.1 Timing boundaries

- **Model round trip:** time from sending the final request bytes to the selected model endpoint until the final response byte is received. Tool execution is measured separately.
- **Orchestration overhead:** all synchronous Ali work between accepting the user message and model dispatch, plus synchronous Ali work between the final model byte and visible completion, excluding explicit tool execution.
- **Security overhead:** the subset of orchestration overhead spent on endpoint validation, workspace validation, permission evaluation, capability filtering, approval handling, integrity checks, and security evidence.
- **Critical path:** work the user must wait for before the model receives the request, before the next model/tool step can run, or before the final answer is visible and usable.
- **Explicit tool work:** the operation the user requested, such as reading or writing a project file, running Roslyn, building, testing, or contacting a weather provider. Necessary I/O performed by that tool is not classified as orchestration overhead. Incidental settings, history, receipt, journal, audit, or cache I/O is orchestration overhead.

### 2.2 Universal budgets

- Ordinary orchestration overhead must be **no more than 25% of the measured model round trip** for that turn.
- Security overhead must independently be **no more than 25% of the measured model round trip** for that turn.
- No settings read, history write, journal write, receipt write, audit write, index rebuild, or configuration probe may be awaited on the critical path.
- Tools, permissions, runtime settings, Internet settings, memory settings, and local embedding settings are loaded into RAM at startup. They change only after an explicit Save or Reload publishes a new immutable snapshot.
- Warm ordinary chat must show useful visible output within two seconds on the primary local test model unless the direct model baseline itself exceeds two seconds.
- A failure that can be reported locally must become visible within 250 milliseconds after it is known. Ali must not spend additional seconds constructing a failure explanation.
- Optional-service failure must not block ordinary chat. It must produce one concise truthful status and continue with the capabilities that remain available.
- Every measured gate records p50, p95, maximum, model time, orchestration time, security time, tool time, first-visible-output time, completion time, and failure count. Timing records stay in RAM during the turn and are exported only after the run or on explicit request.

### 2.3 Baseline protocol

Every checkpoint compares Ali with a direct request to the same loaded model, endpoint, model settings, prompt, context state, and output limit. Run at least 20 warmed repetitions for short chat and at least 10 repetitions for each tool scenario. Report both absolute times and overhead ratios. A faster model does not excuse slower Ali infrastructure.

## 3. Preservation and enablement policy

The repository remains the source of truth for all hardened systems. Reintegration changes whether a system participates in the live request path; it does not erase completed work.

| System | Initial state | Enablement checkpoint | Rule before enablement |
|---|---:|---:|---|
| Direct model chat and streaming UI | Enabled | 13A | Must meet baseline and truth gates |
| Startup tool and settings snapshots | Enabled | 13A | No per-turn disk reload |
| Current weather/search | Dormant until proven | 13B | One cached declaration and bounded provider path |
| Workspace C# tools and Roslyn | Dormant until proven | 13B | Semantic operations use Roslyn; workspace only |
| Participant-aware Mem0 and Qdrant/RAG | Dormant until warmed | 13C | Green means ready, warmed, and immediately queryable |
| Conversation persistence, receipts, evidence journals | Dormant on critical path | 13D | Background-only, bounded, truthful durability state |
| Full permission and durable recovery layers | Dormant except workspace boundary | 13E | RAM-resident and measured below budget |
| Vision, attachments, incoming MCP, calendar, navigation, specialists, workflows, certification | Individually dormant | 13F | One capability at a time with independent bypass |
| Full integrated release | Not declared | 13G | All one-taco gates pass on the shipped build |

## 4. Checkpoint 13A — Baseline: a fast truthful assistant

### Scope

Establish the smallest production path from typed text to visible model answer. Capture a trustworthy direct-model baseline and add in-memory timing attribution. Eliminate protocol-only pauses, mandatory journals, repeated disk settings reads, and final-answer disk acknowledgment from ordinary chat.

### Preserved systems

- **Enabled:** selected runtime, model streaming, assistant identity, UI message rendering, startup settings snapshots, startup tool inventory, workspace-root boundary.
- **Dormant:** weather tools, coding tools, memory/RAG injection, durability, recovery journals, incoming MCP, vision/attachments, specialists, workflows, broad permissions, semantic retrieval.
- **Preserved but bypassed:** all CP7/CP8 durability and integrity code, recovery state, receipts, evidence storage, and optional capability systems.

### Implementation order

1. Bind runtime, endpoint, model, credential, and generation settings once at startup.
2. Bind tool inventory and permission profile once at startup; ordinary chat receives an empty active-tool subset without rebuilding inventory.
3. Use the direct native Agent Framework response path and exact in-memory display acknowledgment.
4. Add in-memory timing spans for ingress, orchestration, security, model, first output, final output, and teardown.
5. Remove or bypass every critical-path disk read/write discovered by the trace.
6. Make ordinary completion terminal and visible; ordinary turns do not enter a paused state.

### Acceptance tests

- 100 sequential `hello` turns: 100 useful answers, zero empty answers, zero protocol errors, zero pauses, zero disk accesses on the traced critical path.
- 20 warmed direct-model calls and 20 equivalent Ali calls: both overhead ratios satisfy the universal budgets.
- Switch from the configured 65K model to a configured 8K model: the next new turn uses the selected limits without source substitution, stale bindings, or application restart.
- Stop the runtime: Ali reports the runtime failure promptly and truthfully without claiming an answer was produced.
- Restore the runtime: the next user request succeeds without clearing history or repairing a journal.

### Rollback or bypass criteria

Bypass any nonessential middleware immediately if it causes one protocol rejection, one unexplained pause, one empty successful response, one critical-path disk access, or a budget violation in two consecutive warmed runs. Roll back to the direct runtime/UI boundary; do not debug secondary middleware while the baseline is failing.

### Commit, push, and live-test gate

Commit and push only after the focused Release checks pass. Launch the exact Release artifact and complete the 100-turn live test through the visible UI or bridge. Record the commit, executable path, runtime binding, direct baseline, Ali measurements, and observed result before starting 13B.

## 5. Checkpoint 13B — Essential weather and C#/Roslyn tools

### Scope

Enable only the two essential tool families: current web/weather evidence and workspace-scoped C# engineering. Tools remain loaded in RAM from startup; a turn receives only the declarations selected by the model/tool-cabinet boundary. Roslyn is authoritative for C# syntax, symbols, references, diagnostics, previews, and post-edit verification.

### Preserved systems

- **Enabled:** 13A baseline, current-search tool, bounded provider fallback, workspace file inspection, Roslyn semantic analysis, preview/apply, build, test, and focused failure recovery.
- **Dormant:** personal memory/RAG, durability journals, broad permission prompts, incoming MCP, vision, calendar, specialists, broad language toolchains.
- **Preserved but bypassed:** complex Action Deck durability and recovery layers until 13D/13E. The semantic Roslyn primitives remain available without their foreground journals.

### Implementation order

1. Publish the cached current-search and C#/Roslyn declarations at startup.
2. Prove the model can choose current search and supply the exact required schema arguments.
3. Make provider order and endpoints come only from the captured Internet-settings snapshot.
4. Enable read-only Roslyn inspection before mutation.
5. Enable workspace-scoped preview/apply, build, and test as one explicit engineering loop.
6. Keep retry state in RAM and let the model continue after a failed action with the exact diagnostic.

### Acceptance tests

- Ask for current weather in Andalusia, Alabama 20 times: every turn either returns sourced current conditions or one truthful provider-unavailable answer; zero generic `I do not have real-time data` answers when a provider succeeded.
- Disable each provider in turn: configured fallback order is honored exactly and no hardcoded endpoint is substituted.
- Provide a small C# project with one compile error: Ali uses Roslyn, explains the actual diagnostic, previews the smallest repair, applies it inside the workspace, builds, and runs the focused test.
- Inject one failed edit/build action: Ali consumes the error, chooses a corrected next action, and completes without asking the user to resume a paused turn.
- Attempt a path outside the workspace: the action is refused immediately and truthfully while the conversation remains usable.
- Static trace proves tool schemas and permissions were not reread from disk per turn.

### Latency and error budget

Tool discovery/selection infrastructure must stay within the universal orchestration budget. Weather provider time and explicit Roslyn/build/test time are reported separately. After a tool result arrives, Ali must dispatch the next model step within 100 milliseconds p95, excluding model queue time.

### Rollback or bypass criteria

Weather and coding have separate enable switches. Bypass only the failing family. Any wrong tool execution, edit outside the workspace, false claim of a completed change, repeated identical action without new evidence, or two consecutive schema failures blocks that family from the checkpoint release.

### Commit, push, and live-test gate

Commit/push the weather and coding gates only after focused Release tests and real UI/bridge tests pass. The checkpoint handoff includes the exact weather source receipts, Roslyn diagnostics, changed project, successful build/test evidence, latency table, and disabled-system list.

## 6. Checkpoint 13C — RAM-first Mem0, Qdrant, and RAG

### Scope

Enable participant-aware memory and local knowledge only after their configuration, indexes, and hot working set are ready in RAM. Memory enhances the answer but never prevents ordinary chat. Memory relevance remains model/evidence driven; transient trivia is not promoted merely because it was mentioned.

### Preserved systems

- **Enabled:** immutable UserMemory, runtime, embedding, and vector settings snapshots; Qdrant; Mem0 participant memory; local RAG; semantic tool catalog when healthy.
- **Dormant:** foreground durability, full permission/recovery stack, incoming MCP, attachments, broad capabilities.
- **Preserved but bypassed:** any cold-start or repair path that would block an ordinary turn. It runs before the green state or in an independent background lane.

### Implementation order

1. Load all memory, runtime, embedding, and collection settings once at startup.
2. Start Qdrant and Mem0, verify the exact embedding-space identity, and warm the configured participant and local-library collections.
3. Publish a green Memory/RAG state only after a bounded in-memory probe proves immediate recall; orange means unavailable or still warming.
4. Add bounded relevance retrieval that can be omitted on deadline without blocking the model.
5. Enable explicit remember/correct/forget operations through participant-aware identity.
6. Keep memory mutation and index persistence off the response critical path; publish their truthful pending/confirmed state separately.

### Acceptance tests

- Save a durable C# preference, open a new chat, and recall it for the correct participant.
- Mention a transient paperclip-direction fact without asking Ali to remember it; verify it is not returned as durable personal memory.
- Two participants share a room: store and recall each person's fact without cross-participant leakage; shared experience remains explicitly attributed.
- Restart memory services and verify orange status during warmup, green only after the exact collection is ready, and no interruption to `hello`.
- Disable Mem0, Qdrant, or the embedding runtime independently: ordinary chat and weather continue; Ali states exactly which memory capability is unavailable.
- 100 warmed memory recalls: zero settings-file reads, zero collection-configuration substitutions, and zero wrong-participant results.

### Latency and error budget

Warmed memory/RAG retrieval must return within 250 milliseconds p95. Its orchestration/security portions remain subject to the universal ratios. A missed retrieval deadline omits memory for that turn and emits one concise status; it never delays model dispatch beyond the deadline.

### Rollback or bypass criteria

Immediately bypass memory injection on wrong-participant data, embedding-space mismatch, stale collection identity, unexpected disk wait, or two consecutive warmed deadline misses. Keep the service available for diagnostics, but restore the 13B answer path until repaired.

### Commit, push, and live-test gate

Commit/push only after fresh-database ingestion, participant separation, relevance rejection, warm-restart, and optional-service-offline live tests pass. The handoff records collection IDs, embedding identity, preload proof, recall latency, and exact green-state criteria.

## 7. Checkpoint 13D — Asynchronous durability without foreground drag

### Scope

Re-enable conversations, receipts, evidence, audit, and recovery material only through isolated asynchronous lanes. The visible answer is acknowledged as displayed in memory. Persistence completion is a separate truthful state and never a prerequisite for ordinary response completion.

### Preserved systems

- **Enabled:** conversation snapshots, receipts, evidence integrity, audit records, and recovery checkpoints behind bounded background channels.
- **Dormant:** full approval/recovery behavior that can interrupt ordinary work; broader capabilities.
- **Preserved:** CP7/CP8 integrity, identity, hash, reconciliation, and tamper-detection code. The checkpoint changes scheduling and admission, not evidence meaning.

### Implementation order

1. Create immutable persistence payloads after visible completion or tool-result publication.
2. Send payloads to independent bounded channels with one consumer per storage boundary.
3. Coalesce superseded conversation snapshots and prevent producer backpressure.
4. Publish `displayed`, `persistence pending`, `persisted`, or `persistence failed` truthfully; never label an in-memory answer durable.
5. Drain gracefully on normal shutdown within a bounded interval; preserve honest loss semantics on power failure.
6. Enable recovery consumption only after written artifacts pass their existing integrity gates.

### Acceptance tests

- Inject 5-second disk latency and answer 100 short turns: model dispatch and visible completion stay within the 13A budget.
- Deny storage access: answers and essential tools continue; one bounded warning reports that restart recovery is unavailable.
- Power off immediately after visible completion: on restart, Ali neither invents a persisted answer nor duplicates a confirmed one.
- Flood 1,000 status/receipt events: memory remains bounded, the response lane stays responsive, and coalescing/drop counts are reported.
- Tamper with a persisted evidence artifact: existing integrity detection rejects it without blocking a new ordinary chat.

### Rollback or bypass criteria

Any background writer that blocks a producer, grows without bound, writes on the UI/model critical path, falsely reports persistence, or delays two warmed turns is disabled as a unit. Revert live behavior to 13C while keeping its data for diagnosis.

### Commit, push, and live-test gate

Commit/push only after slow-disk, denied-disk, saturation, shutdown, restart, and tamper tests pass. Live-test the shipped Release while observing both response latency and background queue health.

## 8. Checkpoint 13E — RAM-resident permissions and continuous recovery

### Scope

Re-enable the full permission and recovery model without turning Ali into a permission planner or a paused workflow player. Permission profiles are captured in RAM. Already-allowed workspace operations proceed immediately. Only a genuinely missing user decision blocks that exact effect; Ali continues every other safe step toward the goal.

### Preserved systems

- **Enabled:** standing permissions, exact-effect approval where required, execution grants, changeset recovery, power-loss reconciliation, durable workflow recovery.
- **Dormant:** broader optional capabilities not yet admitted in 13F.
- **Preserved:** all hardened authorization, stale-state, file-identity, integrity, and reconciliation boundaries.

### Implementation order

1. Load and compile the permission profile at startup; Save/Reload atomically republishes it.
2. Classify existing core tool effects by exact registered metadata, never by user prose or keywords.
3. Make allowed workspace effects zero-prompt and RAM-only at decision time.
4. Re-enable approval only for the exact effect that lacks authority.
5. Replace generic `turn paused` behavior with `needs user decision` for true human blockers and automatic advancement for recoverable model/tool failures.
6. Re-enable power-loss recovery one effect class at a time, starting with C# changesets.

### Acceptance tests

- Run 100 allowed workspace reads/edits: zero permission disk reads and zero unnecessary prompts.
- Attempt an outside-workspace mutation: zero writes, immediate truthful refusal, conversation remains active.
- Reject one approval: only that effect is skipped; Ali explains it and continues any remaining safe work.
- Crash after a file mutation and before its journal transition: restart reconciliation neither overwrites external work nor claims an unknown effect succeeded.
- Submit malformed tool output or stale approval: it is rejected, then Ali replans from the new evidence without an ordinary `resume` message.
- Security and orchestration attribution independently satisfy their 25% limits.

### Rollback or bypass criteria

Bypass the full permission/recovery layer and return to the workspace-only 13D policy on an unnecessary prompt rate above 1%, any false authorization, any lost external change, any no-progress pause, or repeated budget violation. Security correctness is never traded for silent execution; an over-budget implementation is made dormant until refactored.

### Commit, push, and live-test gate

Commit/push after focused Release tests and live allowed/denied/crash/recovery scenarios pass. The checkpoint handoff names each enabled effect class and every still-dormant recovery adapter.

## 9. Checkpoint 13F — Broader capabilities, one independent lane at a time

### Scope

Reintroduce nonessential capabilities individually: vision/attachments, incoming MCP, calendar/reminders, navigation, additional providers, specialist agents, multi-agent workflows, other languages, certification, and optional integrations. No capability receives implicit permission to alter the core path.

### Preserved systems

- **Enabled:** 13A-E plus only the capability currently under test.
- **Dormant:** every other 13F capability until its own gate passes.
- **Preserved:** all completed capability implementations and settings; enablement is controlled by independent switches and immutable snapshots.

### Implementation order

For each capability:

1. Capture configuration and tool declarations at startup or explicit reload.
2. Prove absence has zero effect on ordinary chat.
3. Enable read-only behavior first, then bounded effects.
4. Run its functional, truth, latency, offline, cancellation, and restart tests.
5. Keep it enabled only if all gates pass; otherwise return it to dormant and continue with the next capability.

Recommended order: vision input, incoming MCP, calendar/reminders, navigation, additional language toolchains, specialists/workflows, certification suite, remaining integrations.

### Acceptance tests

- Every capability has at least one successful real live scenario, one offline/unavailable scenario, one cancellation scenario, and one assertion that ordinary `hello` latency is unchanged when the capability is idle.
- Vision model detection may recommend enablement but never prevents manual enablement after an inconclusive probe.
- Incoming MCP schemas are cached, bounded, and unavailable tools are omitted without delaying the model.
- Calendar/navigation results distinguish created handoffs from verified external effects.
- Specialist/workflow output returns to Ali as evidence; Ali remains the only user-facing voice and final answer owner.
- Certification results are reproducible and distinguish real builds/tests from scripted mocks.

### Latency and error budget

An idle capability adds no measurable critical-path work beyond RAM lookup noise. Active capability orchestration/security still obey the universal ratios; external service and explicit tool time are separated. One offline capability may consume at most its configured bounded deadline and may not serialize unrelated capabilities behind it.

### Rollback or bypass criteria

Every capability has an independent kill switch. Disable it on false success, unbounded wait, context explosion, critical-path disk I/O, privacy boundary failure, or two consecutive warmed budget failures. A failed 13F capability cannot block progression of already-passing core checkpoints.

### Commit, push, and live-test gate

Use one commit and push per capability gate. The commit message names the capability and evidence. Do not combine two newly enabled capabilities in one commit. Live-test the exact Release executable after each enablement.

## 10. Checkpoint 13G — Final one-taco release

### Scope

Integrate the proven checkpoints into one release candidate, complete a backward review from 13F to 13A, remove only proven dead wiring, publish the architecture/reference documents, and verify the exact shipped desktop artifact. `One taco` means one coherent Ali executable in which every enabled layer has passed its own gate and every dormant layer is explicitly listed.

### Preserved systems

- **Enabled:** only systems with recorded passing checkpoint evidence.
- **Dormant:** any system that missed a functional, truth, latency, reliability, or security gate.
- **Removed:** only unreachable duplicate wiring proven by static reference analysis and a focused regression test. Hardened implementations are not deleted merely because they remain dormant.

### Implementation order

1. Merge only passing checkpoint commits in dependency order.
2. Run a reverse code review from 13F through 13A for accidental critical-path reintroduction.
3. Re-run the direct-model baseline and every checkpoint's live acceptance matrix.
4. Verify configuration truth across runtime, embedding, memory, Internet, MCP, bridge, and optional providers.
5. Verify fresh install, normal restart, abrupt power-loss recovery, optional-service outage, and model switch.
6. Publish the Release folder, repair/verify the desktop shortcut, launch the exact Release executable, and perform final human/bridge testing.
7. Produce the architecture reference, operational runbook, known-dormant list, rollback instructions, and measured scorecard.

### Final acceptance matrix

- 100 ordinary chats: 100 usable completions, zero pauses, zero empty successes.
- 20 current-weather turns: sourced success or concise truthful provider failure.
- 10 C#/Roslyn repair projects: correct semantic primitive, valid preview, workspace-only mutation, successful focused verification or truthful impossibility.
- Stable preference remembered and recalled; transient trivia rejected; participant separation proven.
- Slow/offline disk does not affect visible response latency.
- Allowed permissions proceed; denied/outside-workspace effects do not occur; recovery survives the defined crash windows.
- Each enabled 13F capability passes its recorded live scenario.
- Ordinary orchestration and security remain independently within 25% of model round-trip time.
- Zero incidental disk waits are observed on the critical path.
- Shipped file hashes, executable path, shortcut target, branch, commit, and remote push all match the release record.

### Rollback or bypass criteria

There is no waiver at 13G. Any P0/P1 defect, false completion claim, critical-path disk wait, unexplained pause, wrong workspace mutation, participant-memory leak, or repeated budget failure removes the responsible subsystem from the release candidate. If ownership cannot be isolated safely, roll back to the latest fully passing checkpoint.

### Commit, push, and live-test gate

Create the final release commit only after the entire one-taco matrix passes. Push it, verify the remote commit, publish the exact Release artifact, verify the shortcut, launch it, and record the observed live results. Tag or otherwise mark the release only after the running artifact matches the pushed commit.

## 11. Ownership and scope boundaries for lower-effort Codex execution

Each executor receives one checkpoint or one named capability within 13F. The assignment must include:

- exact branch/worktree and starting commit;
- exact files/modules it owns;
- explicit files/modules it may inspect read-only but may not edit;
- enabled and dormant system list;
- required focused tests and live scenarios;
- latency/error budgets;
- expected commit and push boundary;
- exact handoff format.

Execution rules:

1. Read the repository `AGENTS.md` before editing.
2. Do not merge, checkout, reset, clean, delete, or alter another checkpoint lane.
3. Do not add deterministic keyword/prose routing. The model owns English interpretation, relevance, tool choice, and answer construction.
4. Do not add a feature, refactor, cleanup, migration, compatibility layer, or security policy outside the assigned checkpoint.
5. Preserve unrelated dirty work. Stop and escalate an overlapping edit instead of overwriting it.
6. Use current-format configuration only. Do not add legacy schemas or migrations unless separately authorized.
7. Test only the assigned checkpoint before handoff. The integration owner performs cross-checkpoint validation.
8. Never report a build, runtime, hardware, security, persistence, or recovery result that was not directly observed.
9. If a system fails its checkpoint budget, bypass it and report the exact failure. Do not expand scope trying to save it.
10. Return control after the checkpoint commit/push/live-test gate with no uncommitted checkpoint work.

## 12. Required checkpoint handoff

Every handoff contains:

1. checkpoint and objective;
2. starting and ending commits;
3. changed files grouped by owned module;
4. enabled, dormant, and bypassed systems;
5. functional scenarios executed and exact outcomes;
6. direct-model and Ali timing table with overhead ratios;
7. disk-I/O trace result for the critical path;
8. truth/reliability failures found and disposition;
9. tests/builds/live app runs performed and not performed;
10. rollback commit or bypass switch;
11. deterministic-processing disclosure;
12. remaining work that belongs strictly to the next checkpoint.

This plan is complete only when Ali can truthfully answer, reliably act, and respond near the speed of the selected model. Everything else is valuable only after it preserves that contract.
