# Checkpoint 7 Verification Report

Date: 2026-08-03
Scope: Project Ali Orchestrator V2 checkpoint 7
Status: **CERTIFIED - final integrated Release gates passed**

## Result

Checkpoint 7 is certified from the final checkpoint candidate. The adversarial audit correctly found four legacy in-place source mutators claiming transactional reconciler coverage; those four declarations are now fail-closed as `ReconcilerUnavailable`. The production catalog remains 117 task tools plus one protocol tool; 88 task descriptors require a durable effect adapter; 44 have unique exact adapters; and 44 are deliberately unavailable with the reason projected through runtime planning and capability settings. The final outside-sandbox Release suite, clean builds, package audit, repository scope review, shortcut verification, and process-start observation all completed successfully within the claims and trust limitations recorded below.

- Full automated suite: **1,536 passed, 0 failed, 26 environmental skips, 1,562 total** in **5m11s** outside the restricted sandbox. Authoritative result: `TestResults/cp7-final-green/cp7-final-green.trx`.
- Solution-wide Release build: **0 warnings, 0 errors**, after `dotnet restore Ali.sln --runtime win-x64`.
- Test-project clean Release build: **0 warnings, 0 errors**.
- Exact effect-adapter coverage audit: **COMPLETE AND AUTOMATED** - 88 required = 44 adapter-backed + 44 deliberately unavailable; `AliProductionCompositionIntegrationTests` passed **2 of 2**.
- NuGet vulnerability audit: **no vulnerable packages reported**, including transitive packages.
- Repository diff/scope/trust review: **COMPLETE** - 24 tracked CP7 files modified; no tracked deletion, binary/mode change, whitespace error, or CP9-CP13 marker; the sole untracked participant-aware-memory document was excluded.
- Desktop shortcut target verification and Release launch: **COMPLETE AT PROCESS-START BOUNDARY** - the shortcut targets the exact CP7 worktree Release executable, the target exists, and PID 19628 was observed running. Model connectivity, runtime-service behavior, and interactive UI function were not established by that observation.

## Focused and historical development evidence

These focused results preserve the implementation history. The final integrated result is the green 1,562-test Release run recorded above; earlier rows are not presented as substitutes for it.

| Focused area | Last known result | Final status |
| --- | ---: | --- |
| Initial durable changeset/Action Deck foundation subset | 13 passed, 0 failed | Historical precursor superseded by later provider/document-delta work; final suite passed |
| Recovered interim/final redisplay claim and acknowledgment | 27 passed, 0 failed | Focused pass; final suite passed |
| Seven Roslyn semantic-query broker adapters | 3 passed, 0 failed | Focused pass; final suite and production composition passed |
| Real project compiler plus analyzer diagnostic capture | 1 passed, 0 failed | Focused Release pass, no warnings/errors reported |
| Agent Framework file-mutation broker tests | 11 passed, 0 failed | Exact-class Release pass outside the restricted sandbox |
| Existing workstation-file and Framework capability-probe compatibility | 23 passed, 0 failed | Focused pass |
| Expanded Action Deck provider/document-delta tests | 40 passed, 0 failed | Exact-class Release pass after both intermediate corrections |
| Production catalog/adapter availability inventory | 117 task + 1 protocol; 88 durable-required = 44 adapter-backed + 44 deliberately unavailable | Source inventory updated after adversarial mutation-boundary audit; final production-composition class passed 2 of 2 |
| Workstation file-tree atomicity/adversarial class | 50 passed, 0 failed, 2 environmental symlink skips | Exact-class Release pass outside the sandbox; durable fixture moved outside OneDrive after the first full-suite identity-replacement failure |
| Conversation work-memory atomicity/adversarial class | 70 passed, 0 failed, 1 environmental skip, 71 total | Exact final-suite class result, including the clean reseal-interruption recovery test |
| File-tree Windows release/rename/reseal seam | 4 passed, 0 failed, 0 skipped | Focused Release pass outside the sandbox: persistent gap drift failed closed and reconciled `Unknown`; clean interruption reconciled `Applied`; native no-replace preserved a destination created after preparation; pre-rename child injection was detected without claiming Windows blocked it. Final suite passed |
| Roslyn exact-host, staged-input, linked-document, and incomplete-workspace gate | 45 passed, 0 failed, 0 skipped; bounded-runner compatibility 2 passed | Focused Release pass; final suite passed |
| Git filter, preauthorization, provider identity, and hard-link boundary gate | 23 passed, 0 failed, 0 skipped | Exact-class outside-sandbox Release pass; final suite passed |
| Application launch output-closure gate | 37 passed, 0 failed, 3 symbolic-link fixture skips; Release compile 0 warnings/0 errors | Full sidecar and hard-link checks passed; final suite passed |
| First integrated CP7 adversarial gate | 347 passed, 4 failed, 5 symbolic-link fixture skips, 356 total | Historical failing run that exposed three stale exact-catalog assertions and one transient post-launch process-inspection race; corrected before the final green suite |
| Catalog and process-launch correction gate | 56 passed, 0 failed, 0 skipped | Exact 117-tool contracts, production reconciler enforcement, long-running launch identity, and legitimate quick-exit handling passed together |
| Source transaction and Windows namespace-security classes | 54 passed, 0 failed, 0 skipped | 30 source-transaction plus 24 namespace-security tests passed in Release outside the sandbox |
| Durable invocation store and Roslyn publication recovery | 29 passed, 0 failed, 0 skipped | 24 durable-store plus 5 publication-recovery tests passed in Release outside the sandbox |
| Post-correction 16-class execution/Roslyn integration gate | 211 passed, 0 failed, 3 environmental symlink skips | Release pass outside the sandbox |
| Complete 25-class CP7-focused gate | 482 passed, 0 failed, 11 environmental link/alias skips | Release pass outside the sandbox; includes namespace security, FileTree, WorkMemory, Git, DevOps, coding/process, Roslyn, broker, recovery, capability, and MCP gates |
| First repository-wide Release suite attempt | 1,524 passed, 4 failed, 31 environmental skips, 1,559 total | Historical failing run that exposed OneDrive-hosted FileTree/WorkMemory fixture identity replacement, normal SDK apphost build failure, and MSYS2 hard-linked G++ rejection |
| Final repository-wide Release suite | 1,536 passed, 0 failed, 26 environmental skips, 1,562 total | Certified outside-sandbox Release run in 5m11s; authoritative TRX: `TestResults/cp7-final-green/cp7-final-green.trx` |
| Pinned executable-provider hard-link gate | 6 bounded-runner tests plus 1 live GCC integration test passed; 0 failed, 0 skipped | Proves an all-inside-root alias set runs and an alias outside the pinned provider root fails closed; real Ali-managed MSYS2 G++ compiled, ran, and tested C++ successfully |
| Exact hard-link regression after diagnostic correction | 1 passed, 0 failed, 0 skipped | Confirmed the corrected regression independently before the final suite |

The two intermediate Action Deck failures were:

1. the owned `CS0246` provider resolved an annotated node from a syntax root that was not the changed document's current syntax tree;
2. exact document-delta reconstruction treated analyzer-config-derived `SyntaxTreeOptionsProvider` state as an unsupported explicit compilation-metadata change.

The implementation was subsequently changed to resolve the annotation from the changed document root and to compare durable compilation options while authenticating analyzer-config documents separately. The corrected Action Deck class passed 40 tests, and the complete 25-class focused gate passed 482 with no failures.

The analyzer gate was reported with this exact command:

```powershell
dotnet test .\tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RoslynAnalyzerDiagnosticsTests
```

It reported 1 passed, 0 failed, 0 skipped, with no build warnings or errors.

## Final gates executed and recorded

The final gates ran from the checkpoint root in Release configuration. Environment-specific skips remain recorded as skips rather than being converted into passes.

### Focused CP7 gates

The final integrated suite exercised the current versions of:

- `SourceChangeSetTransactionTests`
- `RoslynPreviewWorkspaceManagerTests`
- `RoslynActionDeckTests`
- `RoslynActionHandleStoreTests`
- `RoslynActionPublicationRecoveryTests`
- `RoslynAnalyzerDiagnosticsTests`
- `RoslynStagedBuildVerifierTests`
- `RoslynQueryBrokerTests`
- `AliExecutionBrokerTests`
- `AliFrameworkFileMutationBrokerTests`
- `AliProductionCompositionIntegrationTests`
- affected durable publication/recovery, capability, MCP publication-gate, turn-lease, and bounded-process tests

The historical focused total was **482 passed / 0 failed / 11 environmental skips**. The later repository-wide Release suite passed **1,536 tests with 0 failures and 26 environmental skips**.

### Integrated build and full suite

Recorded final command/evidence family:

```powershell
dotnet restore Ali.sln --runtime win-x64
dotnet build tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj -c Release --no-restore --no-incremental --verbosity minimal
dotnet test tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj -c Release --no-restore --no-build --logger "trx;LogFileName=cp7-final-green.trx" --results-directory TestResults\cp7-final-green
dotnet build Ali.sln -c Release --no-restore --no-incremental --verbosity minimal
dotnet list src\Ali.csproj package --vulnerable --include-transitive
git diff --check
git ls-files --deleted
```

Recorded results:

- `TestResults/cp7-final-green/cp7-final-green.trx`: **1,536 passed / 0 failed / 26 environmental skips / 1,562 total**, outside the restricted sandbox in **5m11s**.
- Clean test-project Release build: **0 warnings / 0 errors**.
- Solution-wide Release build after the solution runtime restore: **0 warnings / 0 errors**.
- Package vulnerability audit: **no vulnerable packages reported**, including transitive packages.
- Exact post-diagnostic hard-link regression: **1 passed / 0 failed / 0 skipped**.

The runtime-asset integration tests used the ignored portable bundle restored and verified through the repository's existing offline-cache procedure. The approximately multi-gigabyte runtime bundle remains outside Git.

Runtime-stage preparation on 2026-08-03 used the verified offline cache at `C:\Users\clsor\Documents\Codex\ProjectAli\artifacts\runtime-assets-cache`. The first attempt stopped before publication because the temporary `.restore-<id>` path exceeded a native spaCy DLL path limit. The identical repository script was then invoked through a temporary unused `R:` drive mapping to shorten only the temporary path; its portable-Python and offline hybrid-retrieval smoke check passed, it published the normal ignored `artifacts\runtime-assets\win-x64` stage, and the mapping was removed in `finally`. A second `-VerifyOnly` run from the real checkpoint path passed full checksums. `git check-ignore` confirms the bundle remains excluded by `artifacts/`.

### Adapter-coverage audit

The source/composition inventory now enumerates every production descriptor for which `RequiresDurableEffectAdapter` is true and compares that set with the exact composed `AliExecutionEffectAdapterRegistry`.

Current exact partition:

- Production declarations: **117 task tools + 1 protocol tool**.
- Task descriptors requiring durable effect adapters: **88**.
- Unique exact adapter tuples: **44**.
- Adapter-backed required task descriptors: **44**.
- Deliberately unavailable required task descriptors: **44**.
- Duplicate adapter tuples or unmatched adapter tool names accepted by the audit: **0**.

The 44 adapter-backed tools comprise three Agent Framework text mutations, four workstation file-tree mutations, four conversation work-memory mutations, five Action Deck tools, seven Roslyn query tools, 10 coding/process tools, five Git tools, and six DevOps tools. The exact names are recorded in `docs/reviews/CP7-DETERMINISTIC-RULE-DISCLOSURE.md`.

The exact 44 deliberately unavailable tools remain registered for truthful capability inventory but do not enter the callable planning set. Production resolution assigns each one `CapabilityAvailabilityReasonCode.ReconcilerUnavailable`, and the capability-settings projection carries the same tool-specific reason. This set includes the four legacy canonical in-place mutators `coding_format_project`, `dotnet_create_project`, `roslyn_format_project`, and `dotnet_dependency_apply`; they may return only after a real shared source-transaction publisher exists. The protocol tool remains callable and is not part of the 88-task durable-effect partition.

Source/composition inventory result: **COMPLETE**.

Final automated execution of `AliProductionCompositionIntegrationTests`, including the 117/1, 88/44/44, exact-set, duplicate-ownership, planning, and settings assertions: **2 passed / 0 failed / 0 skipped / 2 total**.

### Windows/reparse verification

Several filesystem tests use no-follow Windows handle checks that cannot exercise all link boundaries inside the restricted sandbox. The final repository-wide suite therefore ran outside that sandbox. It completed with **1,536 passes, 0 failures, and 26 environmental skips**; those environment-dependent link/reparse cases remain skips rather than being counted as passes. The exact final WorkMemory class result was **70 passed / 0 failed / 1 environmental skip / 71 total**, and the exact FileTree class result was **50 passed / 0 failed / 2 environmental skips / 52 total**.

The evidence continues to distinguish:

- a product failure;
- an environment that cannot create/test a symlink, junction, or reparse point; and
- a test run outside the sandbox that actually exercised the boundary.

No environment-dependent skip is counted as a pass in the certified totals.

### Desktop completion gate

After the final successful Ali build, the stale desktop shortcut was repaired to target:

`C:\Users\clsor\OneDrive\Documents\Project Ali\checkpoint-7-transactional-roslyn-f929bae\bin\Release\Ali\Ali.exe`

The exact target exists, and process PID **19628** was observed running from the CP7 Release build. This establishes process start only. It does not establish model connectivity, runtime-service behavior, successful user interaction, or broader desktop UI correctness.

## Final source review

- The final diff contains **24 tracked modified CP7 files**: four CP7 documents, 11 CP7 production files, and nine CP7 test files.
- No tracked deletion, binary change, mode change, or `git diff --check` whitespace error is present.
- No CP9-CP13, participant-aware, Nomic, LM Studio, vision, certification-suite, Qdrant, or Mem0 marker occurs in the tracked patch. No unrelated startup, retry, runtime-provider, model-selection, or UI behavior was introduced by that patch.
- The sole untracked file, `docs/architecture/ALI-PARTICIPANT-AWARE-MEMORY.md`, belongs to another checkpoint and is explicitly excluded from CP7 staging and certification.
- Ignored `TestResults`, `artifacts`, `bin`, and `obj` outputs remain outside the tracked patch.
- No deterministic English/keyword router or prose-to-action parser was introduced.
- Source mutation cannot fall back from a missing adapter to a direct canonical write.
- The final production catalog partitions exactly as 117 task plus one protocol descriptor, with 88 durable-required task descriptors split into 44 exact adapters and 44 `ReconcilerUnavailable` descriptors, with no duplicate target-state ownership. The final two-test production-composition class proves those assertions.
- The retired direct Roslyn rename tools are absent from the production surface, and the Action Deck outcome/permission/semantic metadata remains canonical.
- Committed recovery requires the exact persisted authorization digest before sealing applied state or emitting success evidence.

Final source review: **COMPLETE - CP7 scope and trust gate passed**.

## Claims not established by this certification

This certification does not establish live proof of:

- a real model selecting, previewing, verifying, applying, and postverifying an Action Deck change;
- a forced process crash at every real filesystem/journal durability boundary;
- a full WPF/multi-target/source-generator solution preserving every semantic input in the isolated workspace;
- hermetic execution of untrusted project files, SDK resolvers, MSBuild imports/property functions/`UsingTask` code, analyzers, generators, or child processes; CP7 treats authorized project/toolchain code as trusted local code running with Ali's account;
- OS-enforced prevention of a project-authored child process reading or writing outside the selected source root, or racing a junction replacement after Ali's own final no-follow checks;
- OS-enforced exclusion of an owner-operated same-user process during the Windows-required interval between releasing descendant file-tree or conversation work-memory handles, natively renaming the still-held root, and resealing the descendants. CP7 detects a persistent mismatched postimage and refuses success, but it does not claim to detect a mutation restored to the exact authenticated path/content snapshot before reseal;
- execution or live usefulness of the 44 deliberately unavailable effect-bearing tools; CP7 intentionally keeps them non-callable until exact adapters exist, including four legacy in-place source mutators newly withheld by this audit;
- LM Studio, GPT-OSS 20B, the 65,536/8,192 context-output selection, or rolling-window behavior;
- Nomic v1.5 prefixing, 8,192-token functional embeddings, Mem0, Qdrant, or a rebuilt vector database;
- external MCP, remote OpenAI-compatible HTTPS, Lemonade, Ollama, or llama.cpp runtime behavior;
- screenshot paste, vision detection, status wrapping, microphone/camera/speaker behavior, or interactive desktop UI operation beyond the observed process start;
- long-duration soak, million-record scale, power-loss durability, or performance on the user's hardware;
- checkpoint 8 or checkpoint 9 features listed as deferred in the read-only review brief.

Automated source tests, Git state, successful builds, shortcut-target verification, and an observed process start are not substitutes for those live claims.

## Explicit trust boundary

Before durable intent, CP7 binds the selected tool, exact arguments, selected source-root revision, Ali executor, and required host/assets without evaluating MSBuild or loading a Roslyn workspace. Project evaluation, SDK resolution, imports, property functions, `UsingTask` implementations, `dotnet`, and analyzer/generator execution (only for tools that explicitly request them) occur after authorization. They are trusted local project/toolchain code, not a hermetic sandbox. Static source binding detects selected-root drift and authorizes Ali-owned primitives; it intentionally does not claim evaluation-equivalent coverage of every transitive input. Windows file-tree and conversation work-memory directory-root publication have a narrower platform constraint: after a final authenticated full-tree snapshot, descendant handles must be released before the identity-held root can be renamed; Ali immediately reseals and re-verifies the destination before success. Persistent interposition fails closed and reconciles `Unknown`, while an exact poststate after interruption reconciles `Applied`, but owner-operated same-user mutation restored before reseal is outside CP7's detection claim. Adversarial repositories and hostile same-user writers remain outside CP7's security claim until execution moves to a low-privilege sandboxed worker with private scratch storage and an OS-enforced boundary.

## Deterministic-processing disclosure

Checkpoint 7 adds no semantic keyword/phrase router and no deterministic English interpretation, task decomposition, source-edit decision, tool choice, or answer rule. Its mechanical execution, Roslyn, transaction, file-edit, recovery, and display rules are inventoried in `docs/reviews/CP7-DETERMINISTIC-RULE-DISCLOSURE.md`.
