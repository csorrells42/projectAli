# Checkpoint 7 Verification Report

Date: 2026-08-03
Scope: Project Ali Orchestrator V2 checkpoint 7
Status: **DRAFT - final integrated gates pending**

## Result

Checkpoint 7 is not certified by this draft. The exact production adapter expansion and source/composition availability audit is being revalidated after the adversarial audit correctly found four legacy in-place source mutators claiming transactional reconciler coverage. Those four declarations are now fail-closed as `ReconcilerUnavailable`: 117 task tools plus one protocol tool remain declared; 88 task descriptors require a durable effect adapter; 44 have unique exact adapters; and 44 are deliberately unavailable with the reason projected through runtime planning and capability settings. Focused development gates have exercised portions of the transactional source engine, Roslyn adapters/providers, Framework file transactions, and recovery-display changes, but the final solution-wide Release build, full automated suite, binary scope review, shortcut verification, and interactive launch have not yet been recorded here.

- Full automated suite: **PENDING** passed, **PENDING** failed, **PENDING** skipped, **PENDING** total.
- Solution-wide Release build: **PENDING** warnings, **PENDING** errors.
- Test-project clean Release build: **PENDING** warnings, **PENDING** errors.
- Exact effect-adapter coverage audit: **SOURCE INVENTORY UPDATED; AUTOMATED REVALIDATION PENDING** - 88 required = 44 adapter-backed + 44 deliberately unavailable; final execution of the integrated audit test remains pending.
- Repository diff/scope/trust review: **PENDING**.
- Desktop shortcut target verification and Release launch: **PENDING**.

Do not replace these markers with estimates. Fill them only from the final commands run against the exact checkpoint commit candidate.

## Known focused development evidence

These are provisional development results reported during implementation. They are useful regression evidence but are not substitutes for the final integrated run because source continued changing after some of them.

| Focused area | Last known result | Final status |
| --- | ---: | --- |
| Initial durable changeset/Action Deck foundation subset | 13 passed, 0 failed | Superseded by later provider/document-delta work; rerun pending |
| Recovered interim/final redisplay claim and acknowledgment | 27 passed, 0 failed | Focused pass; full-suite confirmation pending |
| Seven Roslyn semantic-query broker adapters | 3 passed, 0 failed | Focused pass; production composition confirmation pending |
| Real project compiler plus analyzer diagnostic capture | 1 passed, 0 failed | Focused Release pass, no warnings/errors reported |
| Agent Framework file-mutation broker tests | 8 passed, 0 failed | Focused pass outside the restricted sandbox |
| Existing workstation-file and Framework capability-probe compatibility | 23 passed, 0 failed | Focused pass |
| Expanded Action Deck provider/document-delta tests | 12 passed, 2 failed in an intermediate run | Both failures received code fixes; required rerun remains pending in this draft |
| Production catalog/adapter availability inventory | 117 task + 1 protocol; 88 durable-required = 44 adapter-backed + 44 deliberately unavailable | Source inventory updated after adversarial mutation-boundary audit; integrated test execution pending |
| File-tree and conversation work-memory atomicity/adversarial gate | 24 passed, 0 failed, 2 symlink-policy skips; Release test build 0 warnings/0 errors | Focused outside-sandbox pass; combined/full confirmation pending |
| Roslyn exact-host, staged-input, linked-document, and incomplete-workspace gate | 45 passed, 0 failed, 0 skipped; bounded-runner compatibility 2 passed | Focused Release pass; combined/full confirmation pending |
| Git filter, preauthorization, provider identity, and hard-link boundary gate | 20 passed, 0 failed, 0 skipped | Focused outside-sandbox Release pass; combined/full confirmation pending |
| Application launch output-closure gate | 37 passed, 0 failed, 3 symbolic-link fixture skips; Release compile 0 warnings/0 errors | Full sidecar and hard-link checks passed; combined/full confirmation pending |
| First integrated CP7 adversarial gate | 347 passed, 4 failed, 5 symbolic-link fixture skips, 356 total | Exposed three stale exact-catalog assertions and one transient post-launch process-inspection race; corrections applied or in progress, clean rerun required |
| Catalog and process-launch correction gate | 56 passed, 0 failed, 0 skipped | Exact 117-tool contracts, production reconciler enforcement, long-running launch identity, and legitimate quick-exit handling passed together |

The two intermediate Action Deck failures were:

1. the owned `CS0246` provider resolved an annotated node from a syntax root that was not the changed document's current syntax tree;
2. exact document-delta reconstruction treated analyzer-config-derived `SyntaxTreeOptionsProvider` state as an unsupported explicit compilation-metadata change.

The implementation was subsequently changed to resolve the annotation from the changed document root and to compare durable compilation options while authenticating analyzer-config documents separately. This draft makes no passing claim for those fixes until the focused suite is rerun.

The analyzer gate was reported with this exact command:

```powershell
dotnet test .\tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RoslynAnalyzerDiagnosticsTests
```

It reported 1 passed, 0 failed, 0 skipped, with no build warnings or errors.

## Final gates to execute and record

Run from the checkpoint root in Release configuration. Record the exact command, result, duration when useful, and any environment-specific skip rather than converting a skip into a pass.

### Focused CP7 gates

At minimum, rerun the current versions of:

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

Record final focused total: **PENDING passed / PENDING failed / PENDING skipped**.

### Integrated build and full suite

Expected final command family:

```powershell
dotnet restore tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj --runtime win-x64 --verbosity minimal
dotnet build tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj -c Release --no-restore --no-incremental --verbosity minimal
dotnet test tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj -c Release --no-restore --no-build --logger "console;verbosity=minimal"
dotnet build Ali.sln -c Release --no-restore --no-incremental --verbosity minimal
dotnet list src\Ali.csproj package --vulnerable --include-transitive
git diff --check
git ls-files --deleted
```

If runtime-asset integration tests require the ignored portable bundle, restore and verify it through the repository's existing offline-cache procedure before the full run, and record exactly which stage was used. Do not copy the approximately multi-gigabyte runtime bundle into Git.

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

Final automated execution of `AliProductionCompositionIntegrationTests`, including the 117/1, 88/44/44, exact-set, duplicate-ownership, planning, and settings assertions: **PENDING**. A failing final run reopens this audit and blocks checkpoint certification.

### Windows/reparse verification

Several filesystem tests use no-follow Windows handle checks and may fail inside a restricted sandbox with `AccessDenied`. The focused Framework broker run passed outside that restricted sandbox. The final report must distinguish:

- a product failure;
- an environment that cannot create/test a symlink, junction, or reparse point; and
- a test run outside the sandbox that actually exercised the boundary.

Do not count an environment-dependent skip as a pass.

### Desktop completion gate

After the final successful Ali build:

1. verify the desktop Ali shortcut targets the canonical Release executable;
2. repair the shortcut if and only if it is stale;
3. verify the target exists; and
4. launch that exact Release build unless the user explicitly says not to.

Record the resolved shortcut target and observed launch result without claiming model/runtime connectivity that was not tested.

## Required source review before certification

- Inspect the final diff and confirm every changed file belongs to checkpoint 7.
- Confirm no unrelated startup, retry, model-runtime, memory, provider-selection, or UI behavior changed.
- Confirm no deterministic English/keyword router or prose-to-action parser was introduced.
- Confirm source mutation cannot fall back from a missing adapter to a direct canonical write.
- Confirm the final production catalog still partitions exactly as 117 task plus one protocol descriptor, with 88 durable-required task descriptors split into the documented 44 exact adapters and 44 `ReconcilerUnavailable` descriptors, with no duplicate target-state ownership.
- Confirm the retired direct Roslyn rename tools are absent from the production surface and the Action Deck outcome/permission/semantic metadata is canonical.
- Confirm committed recovery requires the exact persisted authorization digest before sealing applied state or emitting success evidence.
- Confirm no tracked deletion and no build/runtime artifact entered the commit.

Final source review: **PENDING**.

## Claims not established by this draft

This draft does not establish live proof of:

- a real model selecting, previewing, verifying, applying, and postverifying an Action Deck change;
- a forced process crash at every real filesystem/journal durability boundary;
- a full WPF/multi-target/source-generator solution preserving every semantic input in the isolated workspace;
- hermetic execution of untrusted project files, SDK resolvers, MSBuild imports/property functions/`UsingTask` code, analyzers, generators, or child processes; CP7 treats authorized project/toolchain code as trusted local code running with Ali's account;
- OS-enforced prevention of a project-authored child process reading or writing outside the selected source root, or racing a junction replacement after Ali's own final no-follow checks;
- execution or live usefulness of the 44 deliberately unavailable effect-bearing tools; CP7 intentionally keeps them non-callable until exact adapters exist, including four legacy in-place source mutators newly withheld by this audit;
- LM Studio, GPT-OSS 20B, the 65,536/8,192 context-output selection, or rolling-window behavior;
- Nomic v1.5 prefixing, 8,192-token functional embeddings, Mem0, Qdrant, or a rebuilt vector database;
- external MCP, remote OpenAI-compatible HTTPS, Lemonade, Ollama, or llama.cpp runtime behavior;
- screenshot paste, vision detection, status wrapping, microphone/camera/speaker behavior, shortcut correctness, or interactive desktop UI operation;
- long-duration soak, million-record scale, power-loss durability, or performance on the user's hardware;
- checkpoint 8 or checkpoint 9 features listed as deferred in the read-only review brief.

Automated source tests, Git state, and a successful build are not substitutes for those live claims.

## Explicit trust boundary

Before durable intent, CP7 binds the selected tool, exact arguments, selected source-root revision, Ali executor, and required host/assets without evaluating MSBuild or loading a Roslyn workspace. Project evaluation, SDK resolution, imports, property functions, `UsingTask` implementations, `dotnet`, and analyzer/generator execution (only for tools that explicitly request them) occur after authorization. They are trusted local project/toolchain code, not a hermetic sandbox. Static source binding detects selected-root drift and authorizes Ali-owned primitives; it intentionally does not claim evaluation-equivalent coverage of every transitive input. Adversarial repositories remain outside CP7's security claim until execution moves to a low-privilege sandboxed worker with private scratch storage and an OS-enforced boundary.

## Deterministic-processing disclosure

Checkpoint 7 adds no semantic keyword/phrase router and no deterministic English interpretation, task decomposition, source-edit decision, tool choice, or answer rule. Its mechanical execution, Roslyn, transaction, file-edit, recovery, and display rules are inventoried in `docs/reviews/CP7-DETERMINISTIC-RULE-DISCLOSURE.md`.
