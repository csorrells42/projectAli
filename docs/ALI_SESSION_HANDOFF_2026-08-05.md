# Project Ali — Session Handoff, 2026-08-05

**Read this first if picking up cold.** This was one long session (started with a "review the orchestrator" ask, ended after midnight with a real memory-write feature). Everything below is verified true as of this write-up. The companion document, `ALI_ONE_PATH_CONSOLIDATION_PLAN.md`, has more granular phase-by-phase detail and a fuller session log if you need it — this doc is the fast-orientation summary.

Ali is currently **running** (just rebuilt and relaunched with every fix in this doc live).

---

## The mandate this session worked against

The owner's own words, still the standard for every decision: **one execution path, judged only by truthful / reliable / fast, for a single consumer desktop (14700k / 32GB RAM / 5070Ti) — not enterprise scale.** Cut anything overboard for that; keep anything that actually helps.

---

## What's done and verified (highest confidence first)

### 1. Real token streaming — DONE, verified multiple ways
- Root cause: `stream: false` hardcoded, plus fake-streaming shims that awaited the full response and replayed it. Fixed in `OpenAiCompatibleLocalModelRuntime.ExtensionsAI.cs` (real SSE/NDJSON reader) and `SafeActivatingLocalRuntime.ExtensionsAI.cs` (same bug, second layer).
- Verified with a real backend measurement: 50 real chunks over an 803ms span from LM Studio, using the actual production code.
- Verified live by the owner watching it in the running app ("i saw the streaming 123 and the poem... fantastic").

### 2. Completion-reliability gate — DONE, verified against a real failure
- `CoreAssistantCompletionGate` wired into `AliMinimumMessage.RunAsync`. Catches unverified/false "done" claims (stale build, failed tool, unresolved tool call) and forces a bounded retry instead of publishing the lie.
- Found and fixed a real gap in this same work: the failed-mutation blocker was gated behind a classifier flag (`gate.Require()`) that the fast path never sets, so a **failed file write produced zero blocker**. Fixed to be unconditional, like every other terminal-failure check.
- Found and fixed a second real gap: stopping a running app (`dotnet_stop_project`) didn't force re-verification unless source was also edited. Added a dedicated `run-stopped` blocker.
- Verified end-to-end with a real integration test: genuine file-already-exists failure → model falsely claims success → claim rejected across 5 real round-trips → truthful answer published → file confirmed never actually overwritten. Temporary test deleted after use.

### 3. Empty-response hard-fail → bounded retry — DONE, verified live
- `AliMinimumMessage` used to hard-throw the instant a model returned neither a tool call nor text — zero retries. Now retries up to 2 times with a corrective nudge before giving up.
- Confirmed live in the running app: an "Attention: Empty model response" recovery fired and the turn completed successfully instead of hanging (owner directly observed this: "she was hung on the last operation and that popped out... but this time she is actually doing what i asked").

### 4. Streaming paragraph/newline display bugs — DONE, two separate real bugs found and fixed
- **Bug A (mine, introduced by the retry logic above):** on a gate-blocked retry, the live display channel (`turn.PublishResponseText`) is never cleared the way the internal answer buffer is, so the rejected attempt's text ran directly into the next attempt's text with zero separator. Fixed: publish a real blank-line break before the retry's text starts.
- **Bug B (pre-existing):** the normal "new text after a tool call" separator only inserted a single `\n`, which doesn't read as a paragraph break. Upgraded to a full blank line, consistent with the retry-boundary fix.
- Both are code-level fixes, not prompt tweaks — deliberately, since the boundary itself (retry happened / tool call happened) is precisely known in code.

### 5. Two real prompt-instruction fixes
- **String-escaping instruction:** she was inserting literal newlines inside C# string constants instead of `\n`, causing a `CS1010` reject-loop. Roslyn's pre-write validation was already correctly catching this (working as designed) — she just wasn't reliably self-correcting. Added an explicit instruction. *(Not yet independently re-confirmed live after the fix — worth watching for recurrence.)*
- **Narration formatting instruction:** she was running distinct planning/narration sentences together ("Let me do X. Now let me do Y.") with no break at all — this is model-generated text with no code-level boundary to hook, so it's a prompt-level fix, not a code fix.

### 6. Real, structural bug: output-token cap crash — DONE, likely the biggest single reliability fix tonight
- The fast path's `MaxOutputTokens` was capped at 2,048 — too small for a single tool call whose argument is a full multi-method C# file rewrite. When the model's JSON got cut off mid-string by the cap, `BuildStreamedFunctionCall`'s `JsonDocument.Parse` threw an **uncaught** `JsonException`, crashing the entire agent turn with an opaque "Command failed safely: JsonReaderException..." — this is very likely the real explanation for the "she's stuck in a loop rediscovering the same duplicate-method mess" pattern observed live tonight, not a model-capability problem.
- Fixed two ways: (a) raised the cap to 4,096 for real headroom, (b) wrapped the parse in try/catch so a still-truncated call now gets dropped and retried gracefully (reusing fix #3 above) instead of crashing the turn.
- **Not yet re-tested live after this specific fix** — this is the single most important thing to verify next: retry the mergesort/foo2 task and see if the stuck-loop pattern is actually gone.

### 7. Real memory-write capability — DONE, NOT yet tested live
- Previously Ali could only *read* memory (`recall_user_memory`, `list_current_user_memories`); no write tool was ever wired into the fast path. She was hallucinating "I'll save this" with literally nothing to back the claim — confirmed via the activity log (no write-tool call ever appeared).
- Owner explicitly chose **auto-approve, no interactive prompt** for memory writes over building a real confirmation-dialog flow (asked via a direct tradeoff question, this was their pick).
- Built honestly: the permission receipt is labeled `Source: "auto-policy"`, never `"interactive-user"` — it does not and must not claim a human clicked approve. `AliParticipantMemoryTools.TryGetTrustedPermission` was extended with a second, distinctly-labeled acceptance path (`IsCoreAssistantAutoApproval`) alongside the original, **completely untouched** `IsInteractiveOnceApproval` path that the heavy orchestrator still requires for real.
- `mutate_participant_memory` added to the fast path's active tool list; instructions added telling her exactly how to populate the (fairly complex, 15-field) proposal object with sensible single-user defaults.
- All 98 memory/gate-related regression tests pass after this change.
- **Real risk flagged, not yet resolved:** this tool's parameter shape is complex (built for a full multi-person speaker-identity system, not a simple note). A small model may not construct it correctly on the first few tries. This needs live testing before trusting it.

---

## What's queued/known but NOT done

- **Phase 2 of the architecture plan (deleting the dead heavy orchestrator)** — logically proven dead (zero live callers anywhere), but no deletion has happened. See `ALI_ONE_PATH_CONSOLIDATION_PLAN.md` for the full reasoning chain.
- **Model comparison (devstral-small-24b vs qwen2.5-coder-14b)** — explicitly deferred until after the token-cap fix is retested, since that fix alone plausibly explains tonight's struggles. Don't conclude anything about model quality until that retest happens.
- **Changeset/rollback durability wiring into the fast path** — not evaluated this session, still open per the consolidation plan.

---

## Immediate next steps, in priority order

1. **Retry the mergesort/foo2 task** with devstral now that the token cap and crash are fixed — this is the real test of tonight's biggest fix.
2. **Test the new memory-write tool** with a simple "remember that I like X" request — watch whether she constructs the proposal correctly and whether the write actually lands (check via `list_current_user_memories` in a fresh chat afterward).
3. **Watch for CS1010 recurrence** (newline-in-string-constant) — the instruction fix hasn't been independently re-confirmed live yet.
4. Only after 1–3: decide whether qwen2.5-coder-14b is worth a real side-by-side comparison.

---

## Files touched this session

- `src/Modules/Runtime/OpenAiCompatibleLocalModelRuntime.ExtensionsAI.cs` — real streaming, truncated-JSON graceful handling
- `src/Modules/Runtime/SafeActivatingLocalRuntime.ExtensionsAI.cs` — real streaming (second layer)
- `src/Modules/Coordinator/AliMinimumMessage.cs` — completion gate wiring, bounded retries (blocked-claim and empty-response), paragraph-break fixes
- `src/Modules/Coordinator/CoreAssistantCompletionGate.cs` — unconditional mutation-failure check, `run-stopped` blocker
- `src/Modules/Coordinator/CoordinatorToolContracts.cs` — `TryEnterLightweightToolInvocation`
- `src/Modules/Coordinator/AliCoreMemoryReadReceiptMiddleware.cs` — extended to cover the mutate tool, honestly labeled
- `src/Modules/Coordinator/AliParticipantMemoryTools.cs` — second, honestly-labeled auto-approval path alongside the untouched interactive one
- `src/Modules/Coordinator/AliAgentHarnessRunner.cs` — output-token cap raised, memory-write tool added to active list, four new instruction lines (string-escaping, memory-honesty/capability, narration formatting)
- `tests/Ali.Framework.Tests/CoreAssistantCompletionGateTests.cs` — 4 pre-existing test bugs fixed (unrealistic mock data), all 9 now pass
- `docs/ALI_ONE_PATH_CONSOLIDATION_PLAN.md` — the fuller running log, updated throughout tonight

Full regression suite (1751 tests) was run once mid-session: 51 pre-existing failures, all traced to either dead code or missing external tools (git/gcc/Arduino/LM-Studio-adjacent), none caused by this session's changes. Not rerun after the later changes tonight — worth a fresh full run next session if time allows, though the targeted subsets (gate: 9/9, memory: 98/98) all pass.
