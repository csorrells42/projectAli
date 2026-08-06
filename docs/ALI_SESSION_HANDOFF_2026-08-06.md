# Project Ali — Session Handoff, 2026-08-06

## Status: 5 commits ahead of origin/master, ready to push

`22aeb8b`, `a73a17b`, `93ab404`, `c4aef88`, `2e8ade3` — all on top of Codex's Serena integration (`755c197`).

## What shipped this session

1. **Persona-suppression regression fixed** — ordinary chat no longer routed through the persona-stripped internal classifier's own answer.
2. **Redundant classifier round-trip removed** — was paying a full extra model call before every single turn, chat included.
3. **Native coding-tool fallback restored** — Ali no longer has zero coding capability if Serena fails to start; falls back to her own file tools with a visible warning.
4. **Live textual tool-call leakage fixed** — the `[TOOL_CALLS]tool[ARGS]{...}` promotion logic Codex wrote lived in a class (`AliToolCallingChatClient`) that's never constructed anywhere in the app. Confirmed via a real bridge-driven test that the exact leak it was meant to prevent still happened live. Reimplemented in `CoreAssistantContextCompactingChatClient` (the class that's actually in the live streaming chain), working against the real token stream.
5. **Serena sandbox escape found and fixed** — Serena keeps a machine-global project registry independent of Ali's configured Workspace. A same-named-project collision let the model activate a project outside the sandbox during live testing (confirmed via Serena's own logs). Immediate fix: removed the stray registry entry. Structural fix: `AliSerenaWorkspaceGuardMiddleware` now checks every `activate_project` result against the configured Workspace root and rejects anything outside it, fails closed if it can't confidently parse the path.
6. **Brace-cascade guidance added** — instruction telling her that many simultaneous compiler errors are usually one structural break (missing/misplaced brace), not many independent mistakes; fix the earliest one first.
7. **Dead code removed** — the `#if false` legacy core-assistant path (never compiled, confirmed zero live callers).

## The big finding tonight: live test verdict

Ran a real, controlled mergesort-visualizer test via the Conversation Bridge (not screenshots) with every fix above actually live. Result: **not a success.** She got stuck in a genuine loop — 5+ straight minutes of "Editing MainWindow.xaml" with zero build attempts or diagnostics checks — then hit a 15-minute timeout unfinished.

Separately reviewed a second model's attempt (Gemma-4-12B-QAT) on the same kind of task. Same underlying failure pattern, different manifestation: when asked to organize existing files into a proper project, it scaffolded a **brand-new blank project** via `dotnet new wpf` instead of incorporating the real implementation, then claimed success because the blank scaffold built and ran — a hollow completion claim, not a lie exactly, but not what was asked either.

**Conclusion (owner's own, and I agree it's well-evidenced): this is a genuine model capability ceiling for this task's complexity, not an infrastructure problem anymore.** The infrastructure got real, verified fixes tonight and the failures persisted anyway. Two different local models, same core failure mode: defaulting to greenfield scaffolding instead of incorporating/editing existing work, sometimes paired with a technically-true-but-substantively-false success claim.

## Recommended next steps

1. **Don't keep throwing bigger one-shot specs at local models.** Decompose: scaffold + verify it builds and shows the 20 boxes → verify shuffle works → verify stop/restore → only then add the merge-sort animation. Verify at each stage before moving on, regardless of which model is used.
2. **Add an explicit instruction (not yet done):** never re-scaffold a project when files already exist in the target location; if asked to organize/incorporate existing work, move/edit the actual files, don't create fresh ones. Also: never report success until the *specific* requested content is verified present, not just that *something* built and ran.
3. The Serena workspace guard is real but should get a live retest to confirm it actually blocks a cross-boundary activation attempt, not just code-reviewed.
4. Full regression suite hasn't been rerun since the last few commits — worth a fresh `dotnet test` pass next session.

## Files touched this session (beyond what's in the commits above)

See `git log 755c197..HEAD --stat` for the exact diff. Key new file: `src/Modules/Coordinator/AliSerenaWorkspaceGuardMiddleware.cs`.
