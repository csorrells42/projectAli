# Checkpoint 6 Verification Report

Date: 2026-08-02
Scope: Project Ali Orchestrator V2 checkpoint 6

## Result

Checkpoint 6 passed its source, build, runtime-asset, and automated test gates in the isolated checkpoint worktree.

- Full automated suite: **1,153 passed, 0 failed, 20 skipped, 1,173 total**.
- Solution-wide Release build: **0 warnings, 0 errors**.
- Test-project clean Release build: **0 warnings, 0 errors**.
- Portable runtime assets: full manifest checksum and offline Python/import verification passed.
- NuGet vulnerability scan: no known vulnerable packages were reported for direct or transitive dependencies.
- Static P0-P2 re-audit: no remaining concrete finding after the final runtime service-discovery hardening.
- Repository integrity: `git diff --check` passed and no tracked deletion was present.

## Executed gates

The final verification used these commands from the checkpoint root:

```powershell
dotnet restore tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj --runtime win-x64 --verbosity minimal
dotnet build tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj -c Release --no-restore --no-incremental --verbosity minimal
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RestoreRuntimeAssets.ps1 -OfflineCache 'C:\Users\clsor\Documents\Codex\ProjectAli\artifacts\runtime-assets-cache'
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RestoreRuntimeAssets.ps1 -VerifyOnly
dotnet test .\tests\Ali.Framework.Tests\Ali.Framework.Tests.csproj --configuration Release --no-restore --no-build --logger "console;verbosity=minimal"
dotnet build Ali.sln -c Release --no-restore --no-incremental --verbosity minimal
dotnet list src\Ali.csproj package --vulnerable --include-transitive
git diff --check
git ls-files --deleted
```

Focused gates also passed:

- Planner, admission, completion, runtime binding, and evidence authority: 90 passed.
- Typed tool outcomes, production composition, bounded plan lifecycle, and UI receipts: 135 passed.
- Evidence journal, protected storage, and durable work graph: 136 passed and 19 skipped reparse-point cases.
- Activity, permission, and durable recovery reconciliation: 46 passed.
- Java, Node, Qdrant, ripgrep, wake-word, Parakeet, and netcoredbg integration cases: 11 passed after the verified runtime bundle was staged.

## Skipped tests

The final full run reported 20 explicit skips. They are Windows symbolic-link, junction, or reparse-point boundary tests whose setup could not create the required redirected filesystem objects under this machine's current policy. They were not counted as passes. Nearby non-link durability, authentication, tamper, restart, and boundary tests did execute and pass.

This remains a verification gap for those exact OS-policy-dependent cases. Run the same suite in an isolated Windows environment that permits test-created symbolic links/reparse points before making a stronger claim about those paths.

## Runtime-asset handling

The runtime bundle is ignored build/test material and is not part of the checkpoint commit. The canonical worktree's existing stage was not reused because full verification found it stale. The checkpoint instead rebuilt a fresh approximately 3.36 GiB stage from the complete local offline cache, validated the manifest, hashes, and imports, and then staged it into the test output.

## Claims not established by this gate

This report does not claim live proof of:

- LM Studio or another real model completing an Ali task;
- GPT-OSS 20B throughput, 65,536-token context, or 8,192-token output on the user's hardware;
- Mem0, a persistent user Qdrant database, or an external MCP server working in the user's current configuration;
- desktop UI layout, status wrapping, screenshot paste, microphone, camera, speaker, or shortcut behavior under interactive use;
- long-duration soak performance, forced process-crash recovery, or million-record scaling;
- the checkpoint 7 transactional Roslyn changeset work or checkpoint 8-9 features listed as deferred in the review brief.

Those require their own live or later-phase gates. Automated source tests and Git state are not substitutes for those claims.

## Deterministic-processing disclosure

Checkpoint 6 added no semantic keyword/phrase router and no deterministic English interpretation, tool choice, task decomposition, or answer rule. The complete mechanical rule inventory and its effect boundaries are recorded in `docs/reviews/CP6-DETERMINISTIC-RULE-DISCLOSURE.md`.
