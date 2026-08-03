# CP12 engineer certification verification report

Date: 2026-08-03
Branch: `codex/checkpoint-12-engineer-certification`
Direct parent: pushed CP10 commit `d5e05c1eec333dc58c7c38abce339f77b4f9e244`

## Delivered

- Versioned `engineering-certification-v1` catalog with 100 stable C# engineering fixtures.
- Dynamic configured-runtime `/models` discovery with no candidate-family/name branches.
- Identical ordered tasks and exact suite digest for every candidate.
- Typed authoritative-agent-loop boundary; no benchmark-specific model planner/router.
- Mechanical scoring for build, primitive, hallucinated APIs, Roslyn diagnostics, unit tests, tool choice, failure recovery, time, and tokens.
- Independent Release build/test verifier using process exit codes and compiler diagnostic ids rather than answer prose.
- Per-run suite and candidate snapshots; per-task raw/JSON evidence; per-candidate JSON/Markdown reports; JSON/Markdown comparison.
- Fresh isolated fixture workspace for each candidate/task/attempt, safe path validation, reparse refusal, compact Windows paths, cancellation, bounded retries/results/transcripts, and exact resume checks.

## Verification

Release application build:

```text
dotnet build src\Ali.csproj --configuration Release --no-restore --nologo
Build succeeded.
0 Warning(s)
0 Error(s)
```

Focused CP12 Release tests:

```text
dotnet test tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj --configuration Release --no-restore --nologo --filter "FullyQualifiedName~EngineeringCertificationTests"
Failed: 0, Passed: 12, Skipped: 0, Total: 12
```

The focused gate covers:

- exact 100-task catalog construction and stable digest;
- suite count/identity rejection;
- arbitrary dynamic model inventory, including a discovered DeepSeek-named candidate treated like every other id;
- disabled/unreachable runtime handling;
- all disclosed typed score components and failure cases;
- isolated fixture materialization and bounded raw evidence;
- a complete 100-task fake-authoritative-loop run followed by a 100-task zero-reexecution resume;
- caller cancellation before model execution;
- refusal of candidate-inventory drift under the same run id;
- distinct Roslyn diagnostic counting;
- real generated-fixture Release build and real unit-test execution after a controlled fixture repair.

`git diff --check` is required again immediately before commit.

## Adversarial findings corrected

1. The first generated test fixture exposed a missing standalone xUnit namespace import. The test project template now declares it explicitly.
2. The real Windows test host exposed an overlong safe workspace hierarchy. Internal directory labels and readable hash segments were compacted while retaining the same boundary and digest checks.
3. Resume originally refreshed the candidate inventory. It now freezes the original exact bindings and refuses drift so a resumed report cannot silently compare a different population.

## Not claimed

- No live local or remote model was certified in this checkpoint.
- No live runtime inventory was queried during verification; dynamic discovery used controlled HTTP evidence.
- No actual 100-task model benchmark, GPU measurement, provider token-usage validation, or candidate comparison result is claimed.
- The production app does not auto-run certification. A caller must explicitly compose the runner with Ali's authoritative agent-loop adapter and configured run-storage root.
- The suite score is not a routing policy or a guarantee of general intelligence, safety, or unrelated engineering performance.
- Full-repository tests were not used as the CP12 gate because the inherited CP7 Roslyn catalog failures remain outside this checkpoint and were intentionally not repaired.
- No app, camera, shared shortcut, Identity module, Viewport module, CP7, CP8, or CP11 worktree was launched or changed.

## Scope audit

Production changes are confined to the new `src/Modules/EngineeringCertification` feature. One focused test file and these two CP12 review receipts were added. No existing production module, shared contract, project file, external module, or UI file was modified.
