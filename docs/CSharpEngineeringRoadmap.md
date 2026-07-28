# Ali C# Engineering Roadmap

Ali's C# engineering environment is divided into independently testable modules. The coordinator composes these modules; the Agent Framework and MCP server expose their bounded tools. No layer receives a general-purpose shell.

## Layers 1-5: Roslyn language environment

1. **Compiler model** — C# syntax, semantic compilations, exact diagnostics, and supported language versions.
2. **Workspace model** — `.csproj`, `.sln`, and `.slnx` loading, multi-project graphs, project references, documents, and target frameworks.
3. **Language intelligence** — declarations, completions, hover information, invocation signatures, definitions, and solution-wide references.
4. **Safe transformations** — formatting and semantic rename with exact preview, bounded writes, audit records, and user approval for mutation.
5. **Editor services** — document outline, live diagnostics, semantic classification, and source-position inspection.

Acceptance requires zero-warning Release builds, direct module tests, cross-project semantic tests, permission tests, and MCP discovery/invocation tests.

## Layers 6-7: execution and debugging

6. **Engineering loop** — solution build, test discovery and execution, structured failure parsing, bounded edit/build/test repair cycles, cancellation, timeouts, and stable result artifacts.
7. **Debugger environment** — launch/attach, breakpoints, stepping, threads, stack frames, locals, watches, exception stops, process termination, coverage, and profiler handoff through a dedicated debugger module.

## Layers 8-15: complete project delivery

8. **Dependency engineering** — NuGet search, package compatibility, vulnerability/license visibility, restore, add/update/remove preview, lock-file awareness, and rollback.
9. **Source control** — repository status, diffs, branches, commits, blame/history, merge-conflict inspection, and guarded push/PR workflows.
10. **Codebase architecture** — ripgrep, tree-sitter, Roslyn symbols, dependency graphs, call graphs, dead-code candidates, and architecture-boundary checks.
11. **Quality and security** — analyzer discovery/execution, `.editorconfig`, warnings-as-errors policy, code metrics, secrets checks, dependency audit, and SARIF results.
12. **Performance engineering** — benchmarks, traces, allocation/CPU summaries, regression comparison, and bounded profiling sessions.
13. **Application verification** — console, service, WPF, and web smoke tests; UI automation; accessibility inspection; screenshots; logs; and crash capture.
14. **Documentation and release** — XML/API documentation, architecture reports, changelogs, publish profiles, installers/portable bundles, checksums, licenses, and release validation.
15. **Autonomous delivery loop** — turn a user request into a visible plan, implement in bounded changes, inspect diffs, build, test, debug, verify the actual application, package it, and report evidence without claiming unfinished work is complete.

Each layer remains useful independently and is committed only after its module, Agent Framework surface, MCP exposure, permissions, and tests agree.
