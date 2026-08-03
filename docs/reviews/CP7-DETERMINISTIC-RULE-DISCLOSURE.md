# Checkpoint 7 Deterministic-Rule Disclosure

## Boundary

Ali still leaves request meaning, task decomposition, tool selection, source-edit intent, answer construction, and the decision to continue, pause, or finish to the configured model. Checkpoint 7 adds no English keyword router, phrase-list classifier, fixed prose-to-tool mapping, deterministic answer finder, or semantic rule that chooses what code the user wants changed.

The rules below are mechanical authority, identity, syntax, version, permission, durability, recovery, and resource-bound checks. A failed rule blocks the exact call, retains the last authenticated state, rolls a source transaction back when that can be proven, or records an in-doubt result. It does not reinterpret the user's request.

## Exact execution-broker authority

- An effect adapter is registered by the exact tuple of canonical tool name, capability ID, and reconciler ID. Prefixes, categories, descriptions, or similar names grant no authority.
- Calls classified as a mutation, local write, process start, system-state change, or non-idempotent network operation require a matching durable effect adapter. If no exact adapter is composed, the capability fails closed as unavailable before its inner function runs.
- Preparation binds the durable turn, call ID, WorkItemId, tool/capability/reconciler identities, canonical arguments digest, action identity, target-version digest, permission-receipt digest, capability-registry revision, live registry identity, preparation artifact, and source-root binding.
- The adapter must reproduce the accepted target-version digest. Its preparation is journaled before the invocation receives authority.
- The inner invocation receives one AsyncLocal-scoped, exact-arguments grant. The grant can be entered once, consumed once, and cannot be reused after the invocation scope is disposed.
- Durable authorization digests use separate domains for Roslyn source publication and Agent Framework file publication. They bind the persisted call, arguments, target, permission, registry, reconciler, prepared artifact, and root. Recovery computes from the persisted intent; it does not recreate or trust an ephemeral grant.
- The production catalog contains 117 task descriptors plus one required protocol descriptor. Exactly 88 task descriptors require a durable effect adapter. Production composition owns 44 unique exact adapter tuples; the other 44 are deliberately unavailable and are projected with `ReconcilerUnavailable` in runtime planning and capability settings rather than being silently callable.

## Production effect-availability partition

The 44 adapter-backed task tools are an exact closed set:

- Agent Framework text publication (3): `file_access_write`, `file_access_replace`, `file_access_replace_lines`.
- Workstation file-tree mutation (4): `file_access_delete`, `file_access_move`, `file_access_copy`, `file_access_create_directory`.
- Agent Framework conversation work memory (4): `file_memory_write`, `file_memory_replace`, `file_memory_replace_lines`, `file_memory_delete`.
- Roslyn Action Deck (5): `roslyn_inspect_target`, `roslyn_list_actions`, `roslyn_preview_action`, `roslyn_verify_changeset`, `roslyn_apply_action`.
- Roslyn semantic query (7): `roslyn_analyze_project`, `roslyn_find_symbol`, `roslyn_get_completions`, `roslyn_inspect_solution`, `roslyn_inspect_document`, `roslyn_inspect_position`, `roslyn_find_references`.
- Coding/process execution (10): `coding_analyze_project`, `coding_build_project`, `coding_test_project`, `coding_run_project`, `dotnet_build_project`, `dotnet_test_project`, `dotnet_verify_project`, `dotnet_run_project`, `dotnet_stop_project`, `dotnet_dependency_inspect`.
- Git (5): `git_status`, `git_diff`, `git_create_branch`, `git_commit`, `git_push`.
- DevOps (6): `architecture_inspect`, `architecture_check_boundaries`, `dotnet_quality_scan`, `dotnet_application_verify`, `dotnet_release_publish`, `dotnet_delivery_verify`.

The 44 deliberately unavailable task tools are also an exact closed set. They remain declared so capability settings can explain their absence, but planning cannot call them while their exact reconciler is missing:

- Archives (3): `archive_create`, `archive_list`, `archive_extract`.
- Arduino (8): `arduino_inspect`, `arduino_search_libraries`, `arduino_install_core`, `arduino_install_library`, `arduino_create_and_compile`, `arduino_compile`, `arduino_upload`, `arduino_open_ide`.
- User memory, web/local retrieval, calendar, mode, and skill execution (9): `recall_user_memory`, `forget_current_user_memory`, `list_current_user_memories`, `search_current_web`, `research_web`, `search_local_library`, `create_calendar_event`, `mode_set`, `run_skill_script`.
- Visual Studio, GNU native, and Raspberry Pi integration (9): `visual_studio_inspect`, `visual_studio_build`, `visual_studio_open`, `native_gnu_inspect`, `native_gnu_execute`, `raspberry_pi_probe`, `raspberry_pi_inspect_libraries`, `raspberry_pi_search_packages`, `raspberry_pi_deploy`.
- Debugging, performance, and architecture-report operations (9): `dotnet_debug_launch`, `dotnet_debug_attach`, `dotnet_debug_evaluate`, `dotnet_debug_set_breakpoints`, `dotnet_debug_control`, `dotnet_debug_stop`, `dotnet_performance_measure`, `dotnet_performance_trace`, `dotnet_architecture_report`.
- Remaining Git inspection (2): `git_history`, `git_blame`.
- Legacy in-place source mutations awaiting transactional publication (4): `coding_format_project`, `dotnet_create_project`, `roslyn_format_project`, `dotnet_dependency_apply`.

This is an availability decision, not a semantic router. The model still chooses among the tools that planning truthfully exposes. A missing exact adapter can only remove a tool from the callable set; it cannot select a substitute.

## Roslyn semantic-query tools

- `roslyn_analyze_project`, `roslyn_find_symbol`, `roslyn_get_completions`, `roslyn_inspect_solution`, `roslyn_inspect_document`, `roslyn_inspect_position`, and `roslyn_find_references` parse only their exact declared argument names.
- Their target-state adapters resolve an existing project/solution and, where required, an exact document. A physical document path must identify exactly one loaded project/document pair; a linked/shared file that is semantically ambiguous across projects fails closed. One-based line and column values must be positive 32-bit integers.
- Target and document identities are SHA-256 hashes of ordinary local files no larger than 16 MiB. Direct file reparse points are rejected.
- Each query requires and consumes a one-use broker grant whose source-root binding matches the resolved target.
- An exact query requires zero live `MSBuildWorkspace` warnings and a non-null compilation for every loaded project both before and after semantic execution. A partial/incomplete workspace cannot produce an exact-success query result.
- `roslyn_analyze_project` counts all compiler and loaded project-analyzer errors/warnings, then returns at most 200 diagnostic details in deterministic severity/path/position/ID/message order. The detail bound cannot reduce the reported total counts or convert an error result to success.
- These seven operations are read-only. After an interrupted prepared query, reconciliation returns `Absent`/safe-to-repeat only when the exact adapter identity is intact; it does not assert that a prior response reached the user.

## Roslyn Action Deck discovery and identity

- The production Action Deck provider set is explicit: the built-in semantic rename provider plus a production-owned catalog containing one unambiguous `CS0246` namespace-import `CodeFixProvider` and one whole-document formatting `CodeRefactoringProvider`. Provider discovery and action invocation perform no assembly scan, MEF discovery, reflection invocation, or internal Roslyn service lookup. Separate semantic-fingerprint and analyzer-config reconstruction code intentionally inspects a pinned Roslyn 5.6 internal surface and fails closed if that exact surface changes.
- Provider identity binds the concrete provider type, assembly name/version, and SHA-256 of the physical provider assembly. Missing, non-file, empty, or over-1-GiB provider assemblies fail closed.
- At most 64 total providers and 256 flattened actions are accepted. Nested actions are traversed by exact ordinal/equivalence-key path, with a maximum depth of 32 and cycle rejection.
- An action identity binds the solution fingerprint, exact document text hash, provider identity/version/assembly hash, nullable equivalence key, title, sorted diagnostic IDs, project/document identities, source span, and every nested path segment. Title text alone is never executable authority.
- Provider failures are isolated per provider. Actions partially registered by a provider that then fails are removed, and a bounded failure receipt is returned; other exact providers may still contribute actions.
- The namespace-import provider registers only for `CS0246` at the exact requested span when exactly one accessible top-level type namespace resolves the missing name. It inspects at most 65,536 namespaces and registers nothing for zero or multiple candidates.
- The formatting provider registers only when Roslyn's exact whole-document formatted text differs from the source.
- Semantic rename validates its requested identifier through Roslyn semantics. Provider actions that need no value may receive an empty requested value; all requested values are bounded to 4,096 characters.

## Canonical and isolated workspaces

- `MSBuildWorkspace` remains the canonical solution loader. Any canonical workspace warning prevents construction of either the semantic preview gate or an exact semantic-query result.
- A short-lived `AdhocWorkspace` clones exact project/document identities, regular/additional/analyzer-config documents, project references, parse and compilation options, output paths, default namespaces, physical metadata references, project analyzers, and solution analyzers.
- Metadata/analyzer references must be existing absolute non-reparse files. Their bytes are hashed before and after rehydration; a missing, unresolved, unloadable, or changing reference fails closed.
- Preview reference resolution never manufactures references from assemblies merely loaded in Ali's process.
- The canonical and isolated solutions must produce the same full semantic fingerprint before discovery or preview can proceed. The fingerprint includes project files, semantic options, document checksums/encoding, project references, metadata references, analyzer references, and analyzer-config/additional documents.

## Preview and durable Roslyn document deltas

- `roslyn_inspect_target`, `roslyn_list_actions`, `roslyn_preview_action`, `roslyn_verify_changeset`, and `roslyn_apply_action` each require their exact one-use broker grant and source-root binding.
- The former public `roslyn_preview_rename` and `roslyn_apply_rename` capabilities are retired from production composition. The retained lower-level direct-apply method returns a typed refusal rather than publishing source.
- Preview re-discovers actions against the current solution/document/span and executes only the supplied 64-hex action identity. A stale or no-longer-applicable identity is rejected.
- A provider result is accepted only when it contains exactly one `ApplyChangesOperation` and no other operation.
- The durable Roslyn delta supports regular, additional, and analyzer-config documents with exact `Add`, `Replace`, `Delete`, `Rename`, and `RenameAndReplace` shapes. Every delta entry binds to one or two exact source-manifest operation sequences.
- A provider action that changes project identity, project/reference metadata, independent compilation metadata, parse metadata, metadata references, analyzer references, or an unrepresented document metadata-only field is rejected. The one exception is mechanical state Roslyn derives from a represented analyzer-config document delta: only when such a delta exists, CP7 removes `SyntaxTreeOptionsProvider` from both compilation-option values through the exact pinned Roslyn 5.6 surface and compares every remaining option. The analyzer-config document remains authenticated in the durable delta and solution fingerprint. A type mismatch, missing pinned member, invocation failure, or any accompanying independent option change fails closed.
- Preview creates a protected handle for two hours and changes no canonical source. The handle binds the provider/action, target/root, project/document/span, requested value, canonical fingerprint, authenticated source manifest, and exact Roslyn document delta.

## Shared source changesets

- A changeset contains 1-256 typed `Add`, `Replace`, `Delete`, or `Rename` operations. Each path is a bounded normalized relative path contained by one approved canonical source root; duplicate/colliding paths, unsafe components, escapes, devices, and traversed reparse points are rejected.
- Each operation authenticates exact existence, byte length, SHA-256 preimage and postimage, and destination state where applicable. Adds require absence, replacements/deletes/renames require presence, rename destinations require absence, and a zero-effect replacement is rejected.
- One file is limited to 16 MiB and aggregate captured pre/post/backup data to 128 MiB. The manifest, receipt, journal line, relative path, and error collections have separate explicit bounds.
- Text changes preserve supported strict UTF-8/16/32 encodings and their BOM state. BOM-less text must be strict UTF-8; binary changes use byte requests.
- Before publication, `.cs` is syntax-parsed, `.json` is parsed with comments/trailing commas disabled and depth 256, and XML/XAML/MSBuild XML is parsed with DTDs and external resolution disabled. Other file types receive byte/hash validation but no invented semantic validator.
- Manifests and transaction documents are authenticated. Postimages and backups are current-user protected and associated with the exact changeset, manifest, sequence, purpose, hash, and length. This protects ordinary at-rest confidentiality/integrity; it does not claim protection from the same running user, administrator, or compromised process.
- Source-file rename uses a handle-relative native no-replace operation after authenticating both namespace spines and the held source identity. The destination must remain absent, and the destination name must resolve to the same held file identity immediately afterward; Ali never substitutes a copy/delete sequence that could overwrite an unexpected destination.

## Staged verification

- Verification reloads the exact handle and manifest, rejects expired or non-preview state, reloads canonical source, and requires the canonical semantic fingerprint to remain unchanged.
- The staged Roslyn solution is reconstructed only from the authenticated document delta and protected source postimages. Every durable source operation must be claimed by that delta.
- Compiler diagnostics and all exact project analyzer diagnostics are captured. Diagnostic identity includes project, ID, severity, warning level, source location/span, and message identity; equal counts with different identities do not compare equal. Any added diagnostic is a regression; removed diagnostics are allowed.
- A temporary source tree is materialized outside canonical source. It rejects reparse points, overlap with canonical source, excluded-path changes, more than 100,000 copied entries, or more than 4 GiB under the default policy. Default exclusions are `.git`, `.vs`, `bin`, `obj`, `.ali`, `TestResults`, `artifacts`, and `node_modules`, and the exact policy digest enters the receipt.
- After materialization, verification binds a bounded no-follow manifest of every non-generated staged input, including the actual project/solution bytes rather than merely their path text. It revalidates that manifest after graph evaluation, after each external restore/build/test step, and before recording successful verification; a project-authored step that rewrites staged source cannot become a success receipt.
- The absolute `.NET` host path and bytes are captured during durable preparation, carried into staged verification, and revalidated inside the bounded runner immediately before every `Process.Start`. Staged verification has no mutable `PATH` or `DOTNET_HOST_PATH` fallback.
- The staged MSBuild graph is evaluated with the exact configuration and registered toolset. Targets must remain inside the staged root and be `.csproj`, `.sln`, or `.slnx`; affected projects must be exact `.csproj` nodes in the graph.
- Verification selects the smallest build roots covering affected projects and test projects whose evaluated project-reference graph depends on them. Limits are 4,096 evaluated projects, 256 affected projects, and 512 selected test projects.
- The runner performs only fixed restore/build and test-without-build/restore commands. Each operation is bounded to 15 minutes by default, process trees are terminated on timeout, output is capped at 30,000 characters in the receipt, TRX is bounded to 64 MiB, and a selected test project that reports zero tests fails verification.
- A successful preverification receipt requires no Roslyn diagnostic regressions, successful staged build evidence, and every selected test project passing. It expires after 30 minutes or when the parent preview expires, whichever comes first.

## Publication and crash recovery

- Apply accepts only an unexpired `Verified` handle, the exact manifest, unchanged canonical fingerprint, and an exact broker grant bound to that handle/change set/root.
- Handle transitions are revision-checked and one-way: `Previewed` -> `Verified` -> `Applying` -> `Applied`, with bounded `Failed` or `Expired` terminal alternatives. Protected handle writes serialize under a store writer lock; a verified handle can enter apply only once.
- The source publication grant is one-use and manifest-bound. A per-source-root publication lease serializes canonical publication.
- Publication durably records `Prepared`, per-operation intent, mutation, applied, and hash-verified journal entries before a terminal `Committed` receipt. Add/replace/delete/rename use exact staged files and hash checks.
- Durable invocation and receipt publication writes a flushed temporary file, verifies that temporary file's exact identity and bytes, performs the authenticated no-replace/replace transition, then verifies that the destination name resolves to the published identity before advancing durable state.
- On failure, rollback records its own intent/applied/verified sequence and restores authenticated preimages. Failure is reported `RolledBack` only when every preimage is hash-proven; otherwise it is `InDoubt`.
- Restart reconciliation compares the authenticated journal/receipt and every canonical pre/postimage. All-postimage is committed, all-preimage is rolled back, a recognized mixture is rolled back under the root lease, and an unknown image is never overwritten automatically.
- Roslyn recovery additionally requires the committed receipt's authorization digest to match the exact persisted prepared intent before it can seal an `Applied` handle or emit success evidence. A missing/mismatched durable authorization, handle, manifest, transaction ID, or root remains `Unknown`.
- A committed source transaction whose canonical Roslyn fingerprint matches the staged verified fingerprint is sealed as applied/postverified without republishing. A committed mutation that cannot be postverified is reported applied-needs-review, never absent and never silently replayed.
- A proven rolled-back source transaction is absent/safe only after the handle is closed as failed or expired.

## Agent Framework file mutations

- The production Agent Framework file store brokers only `file_access_write`, `file_access_replace`, and `file_access_replace_lines`. Reads/list/search/existence remain read-only pass-through operations.
- Each mutation accepts only the Framework's exact live schema; unknown, duplicate, missing, aliased, or wrongly typed properties are rejected.
- `file_access_write` uses exact `fileName`, `content`, and optional Boolean `overwrite` (default false). It refuses an existing file unless overwrite is true.
- `file_access_replace` uses ordinal text matching. Empty `oldString` is rejected; zero matches fail; multiple matches require `replaceAll=true`; otherwise exactly one occurrence is replaced.
- `file_access_replace_lines` uses unique positive one-based physical line numbers. It preserves untouched `CRLF`, `LF`, lone `CR`, and final unterminated-line bytes; each supplied replacement is the exact complete replacement text for that physical line.
- Preparation captures the exact ordinary-file preimage and exact UTF-8-without-BOM postimage. The Framework call must supply that authenticated postimage exactly; a changed argument or content consumes no second authority.
- Committed crash recovery emits success evidence only after manifest and authorization-digest checks. Rolled-back is absent; in-doubt or mismatched durable identity remains unknown and is not replayed.

## Workstation file-tree mutations

- `file_access_delete`, `file_access_move`, `file_access_copy`, and `file_access_create_directory` each have an exact adapter and exact live schema. Delete accepts only `fileName`; move/copy accept only `sourcePath` and `destinationPath`; directory creation accepts only `path`. Unknown, duplicate, missing, empty, or wrongly typed fields fail preparation.
- The accepted source, destination, recoverable-trash, and copy-staging states are captured as one immutable preparation snapshot, verified by two stable SHA-256 file/tree passes, and used for both the accepted target digest and durable domain plan. No later recapture can silently substitute a different source or destination. Direct or traversed reparse/device entries fail closed. One tree is bounded to 100,000 entries and 8 GiB.
- Delete moves the exact source to an invocation-specific recoverable-trash target. Move and copy require an absent destination and never overwrite it. Copy writes to an invocation-specific staging target before moving that staged file/tree into place. Directory creation is idempotent only when the authenticated target was already a directory.
- The one-use grant binds the exact virtual paths, physical roots, preimages, expected postimages, domain-plan digest, and durable invocation identity. Every preimage is recaptured immediately before the effect.
- Windows does not permit a directory root to be renamed while Ali keeps its descendant handles open. For a directory-root publication, Ali therefore verifies the complete no-follow tree while those handles are held, releases only the descendant handles, retains the identity-bound root and authenticated parent-spine handles, performs one native no-replace rename, and immediately reopens every descendant and verifies the complete expected tree before reporting success or exposing a post-rename checkpoint. A destination created before the native rename wins; Ali does not replace it.
- This Windows-mandated release/rename/reseal interval is not an OS-enforced exclusion boundary against another process running as the same owner. A persistent nested mutation during the interval is detected by reseal/postimage verification, the operation cannot report success, and reconciliation remains `Unknown`; an interruption with the exact authenticated poststate is later classified `Applied`. CP7 does not claim it can detect an owner-operated process that changes the tree and restores the exact authenticated path/content snapshot before reseal. Closing that residual same-user race requires a future private low-privilege publication worker or another OS-enforced write boundary.
- Holding directory handles does not itself prevent another same-user process from creating a new child. Ali's final authenticated snapshot before rename detects a persistent injected child and refuses canonical publication, but this is fail-closed detection rather than a claim that Windows blocked the write.
- Recovery classifies only the complete authenticated poststate as applied and the complete authenticated prestate as absent. Any other source/destination/trash/staging combination is unknown and is not automatically replayed or overwritten.

## Agent Framework conversation work memory

- `file_memory_write`, `file_memory_replace`, `file_memory_replace_lines`, and `file_memory_delete` are bound to the active conversation scope and one flat, non-reserved file name. Absolute paths, path separators, `.`/`..`, `memories.md`, and generated description-file names are rejected.
- Each operation has an exact schema. Replacement is ordinal and requires one match unless `replaceAll=true`; line edits use unique positive one-based line numbers and preserve untouched physical line endings.
- Preparation reads the active conversation scope exactly once, snapshots that exact scoped workspace and target into one immutable target/domain record, and validates replacement semantics or required delete presence before creating invocation-specific staging and recoverable-backup paths. If any later preparation step fails, the per-invocation staging tree is removed through bounded no-follow cleanup. The Framework receives a restricted staging store that permits only the authenticated main-file, optional description-file, and mechanically regenerated memory-index effects owned by that exact mutation; nested directory creation and unrelated writes/deletes are rejected.
- A work-memory mutation without its exact durable grant is rejected. While any other durable grant is active, the store cannot fall back to the canonical conversation workspace. Read-only legacy access is retained only when no durable grant is active.
- Work-memory publication, recovery quarantine, and restore use the same Windows directory-root release/rename/reseal rule as workstation file-tree mutation: authenticate the complete tree while descendants are held; release only descendant handles; retain the root and both parent-spine identities; perform native no-replace rename; and reseal and verify the destination before success. A failed native rename reseals the original path. A successful rename whose destination cannot be resealed remains durably `Unknown` rather than being rolled back through an unauthenticated tree. Exact authenticated poststate after interruption reconciles `Applied`; persistent gap mutation cannot become success. The same residual owner-operated same-user race remains outside CP7's exclusion claim.
- Completion publishes only the authenticated staged poststate and records exact terminal evidence. A delete result must be the exact typed value `true`; missing-target or `false` delete results cannot publish or become `Completed`. Recovery compares the scoped canonical workspace, staging state, and backup state; an unrecognized combination remains unknown instead of being replayed.

## Coding and process execution adapters

- Fourteen typed coordinator entrypoints exist for five language-provider operations and nine .NET/Roslyn project operations. Ten read/build/test/run/stop/inspect operations have production-registered exact adapters. The four canonical in-place mutators `coding_format_project`, `dotnet_create_project`, `roslyn_format_project`, and `dotnet_dependency_apply` remain declared but are mechanically withheld as `ReconcilerUnavailable` until they publish through the shared transactional source engine. Without an exact durable grant, each typed entrypoint fails before its inner delegate. There is no generic executable, command-line, or tool-name dispatch surface in these adapters.
- Preparation binds the already-selected tool's exact target/project path, resolved physical root, fixed command identity, resolved language provider or .NET/Roslyn executor identity, normalized typed arguments, target-version digest, and fixed per-operation timeout.
- Pre-intent capture does not evaluate the MSBuild graph, imports, SDK resolvers, property functions, analyzers, or generators. It binds the selected static source root plus the exact executor/assets; project/toolchain evaluation can begin only after the durable grant.
- Coding inputs are captured as bounded, no-follow file/tree fingerprints. The default source-input bound is 12,000 files, 512 MiB aggregate, and 128 MiB per file; fixed generated/cache roots such as `.git`, `.vs`, `.idea`, `.ali`, `bin`, `obj`, `node_modules`, `artifacts`, `release`, and `TestResults` are excluded from that source identity.
- An executable is normally required to have one hard-link name. Windows-serviced system executables and G++ selected from Ali's managed `msys64` installation may instead use one explicit pinned provider root: capture and every live lease revalidation enumerate and authenticate the complete alias set, and any alias outside that root fails closed. An externally configured G++ path retains the strict single-link rule.
- The adapter re-resolves and re-hashes the exact binding after consuming its one-use grant and before invoking the existing typed implementation. A changed tool, root, command/executor identity, arguments digest, or target version fails without selecting another operation.
- C# and .NET run bind the complete output directory containing the selected executable or DLL, not only the principal artifact. The final launch boundary revalidates the principal, the managed host when applicable, and then the complete bounded no-follow directory immediately before `Process.Start`; any added, removed, renamed, or changed sidecar, reparse/device entry, or multiply linked file prevents launch.
- After `Process.Start`, Ali allows at most one second, polling every 25 milliseconds, for a still-running process to expose its executable identity. That identity must match the authorized native artifact or managed host before the process is tracked. If a legitimately short-lived process exits before that identity is readable, Ali accepts its exit result only after revalidating the complete artifact/host/output closure again and never records the exited process as a running target.
- Mechanical operation timeouts range from one minute to 30 minutes according to the exact registered operation. They bound one process call; they are not a global attempt, task-horizon, or reasoning limit.
- A typed result records exactly one terminal durable receipt. A missing completion participant, interrupted started call, authorization mismatch, or unproven process effect reconciles as unknown and is never automatically rerun.

## Git execution adapters

- The Git surface is closed to five recipes: `status --short --branch`, staged or unstaged `diff --stat --patch`, `switch -c <branchName>`, `commit -m <message>`, and `push <remote> <branchName>`. The adapters expose no arbitrary Git command or shell argument array.
- Arguments must match the exact per-tool schema. Branch and remote values are bounded Git-ref-shaped strings, reject `..` and `.lock`, and are at most 128 characters. Commit messages are one line and at most 200 characters.
- Standard repositories and linked worktrees are resolved beneath the approved coding mount without following reparse points. Preparation binds the repository/worktree/common-Git layout, the hash-identified Git executable, fixed recipe, typed arguments, and exact HEAD/refs/index/worktree/config/hooks state, including relevant ambient/global Git configuration.
- Preparation performs no Git or helper process launch. It statically resolves and parses bounded repository, worktree, common, system, global, and explicitly included Git configuration, then revalidates the statically pinned provider executable immediately before the authorized Git executor starts.
- Every active clean/smudge/process filter command is rejected unless it is one exact canonical `git-lfs` recipe with the fixed allowed arguments. Single-token shell or interpreter names such as `cmd`, `sh`, or `powershell` are not an exception.
- Bound regular-file identity includes Windows volume serial, file ID, hard-link count, and a stable alias-set digest. Coding/DevOps inputs reject every multiply linked file. Git/provider assets permit multiple links only when complete twice-stable alias enumeration proves every alias remains inside the approved repository or statically pinned Git installation root.
- Repository capture is bounded to 25,000 worktree files/1 GiB, 50,000 metadata files/512 MiB, and 256 MiB per captured file. A repository that cannot be proven within those bounds is unavailable rather than weakly identified.
- Every Git call has a fixed two-minute-ten-second adapter limit. The exact binding is recaptured after grant consumption. Because no protected intended poststate exists for an arbitrary branch, commit, push, or lost read response, an interrupted started Git call remains unknown and is never automatically repeated.

## DevOps execution adapters

- Six explicit adapters own architecture inspection, architecture boundary checking, quality scanning, application verification, Release publishing, and the composed delivery verification. Each entrypoint binds one fixed typed implementation and cannot select a different operation.
- Preparation validates the exact tool schema, approved project/test roots, fixed operation identity, relevant options, and the physical Roslyn, MSBuild/.NET host, built-application, or delivery executor identity. Architecture boundary input is limited to 1-256 explicit rules, with each namespace value bounded to 1,024 characters.
- DevOps pre-intent capture is likewise static and no-follow. It does not expand authority from an evaluated project graph or declared output path. MSBuild and `dotnet` evaluation remain post-grant trusted-toolchain work.
- Application health checks, when requested, accept only absolute loopback HTTP/HTTPS URLs. Release publishing accepts only `win-x64` or `win-arm64`, always binds `Release`, and delivery's existing publish recipe binds `win-x64` plus self-contained output when publishing is selected.
- Direct application verification requires an already-built artifact whose exact bytes, launch host, and complete bounded no-follow output directory are bound before intent. Composed delivery instead binds a typed post-build policy containing one statically derived literal output root and one or two statically parsed literal artifact candidates; only after build-and-test succeeds may it select the produced candidate and bind that exact artifact plus its complete output directory. Immediately before `Process.Start`, Ali revalidates the principal, the `.NET` host for a DLL, and the output directory last, so sidecar additions, deletions, renames, content drift, reparse/device entries, and multiply linked files fail closed.
- Delivery stops immediately when architecture inspection reports failure, before quality, build/test, application, or release stages can run. This rule can only prevent a false-success delivery receipt after a failed prerequisite; it cannot choose a project, edit, tool, or answer.
- Delivery normalizes an omitted configuration to `Release` and accepts only case-insensitive `Debug` or `Release`, because the durable binding and the actual build must use the same existing delivery configuration contract. This is mechanical configuration consistency on an already-selected tool, not request interpretation.
- Source/execution inputs are no-follow fingerprints bounded to 16,000 entries, 12,000 files, 512 MiB aggregate, and 128 MiB per file. Executor identity files are also bounded to 128 MiB. Generated/cache roots are excluded unless an exact built application/output tree is itself an execution input.
- Fixed per-operation limits are 15 minutes for architecture inspection/checking and Release publish, 20 minutes for quality scan, two minutes for application verification, and 60 minutes for delivery verification. These are single-operation resource bounds, not global task limits.
- Only the exact typed result contract enters the durable terminal receipt. Collections and strings have explicit bounds, including an 8,000,000-character aggregate and 1,000,000-character single-string limit. Unknown, oversized, interrupted, or authorization-mismatched results remain in doubt and are not replayed.

## Trusted local toolchain boundary

- CP7 authorizes the selected tool, arguments, selected source-root revision, Ali executor, and required external host/assets before any project evaluation or process start.
- `MSBuildWorkspace`, `dotnet`, custom SDK resolvers, project imports, property functions, `UsingTask` implementations, explicitly requested analyzers, generators, and their child processes execute only after that durable authorization. They are trusted local project/toolchain code running with Ali's process-account authority, not a hermetic sandbox.
- CP7 does not claim to enumerate every transitive MSBuild input or prevent project-authored child code from reading or writing outside the selected root with permissions already held by Ali's account.
- Ali-owned file/output primitives reject reparse points immediately before access. That check does not prevent an external MSBuild/`dotnet` child process from racing a later junction replacement. Adversarial repositories require a future low-privilege sandboxed worker with a private scratch tree and an OS-enforced boundary.

## Publication-display, process, path, and MCP hardening

- Before redisplaying a recovered interim or final response, Ali durably marks the exact publication/digest and current revision as display-in-doubt. Acknowledgment must match that claim revision and digest. A crash after the claim does not blindly redisplay again.
- Suppressed `ExecutionContext` loses coordinator-turn authority. The sole process-wide active turn is no longer used as a fallback identity.
- The shared bounded process runner validates a positive timeout no greater than 24 hours, distinguishes caller cancellation from timeout, and attempts to terminate the entire process tree on timeout or failure.
- Workstation target-state capture hashes only ordinary local files and rejects reparse/device/non-regular targets or parents before authorization.
- The non-interactive outgoing MCP server publishes no mutation, local-write, process-start, or system-state-changing capability because that path has no durable turn/call grant or staged publication scope.

## Explicit non-rules

- No deterministic rule decides what the user meant, what source change would best satisfy the request, or which tool/provider/action should be chosen.
- No keyword, phrase, extension, filename, diagnostic message, or provider title routes an English request to an action. File extensions select only mechanical syntax validators after the model has proposed an exact mutation.
- The built-in `CS0246` provider operates only after Roslyn reports that exact diagnostic at an exact span; it does not infer user intent from words.
- No action title, status text, receipt summary, normal return, or generic JSON field proves success. Authority comes from exact identities, authenticated durable state, hash-verified effects, and typed outcomes.
- No source mutation is permitted to fall back from a missing adapter to a direct canonical write.
- Checkpoint 7 does not implement checkpoint 8 context/evidence paging, critic/shadow isolation/soak work, or checkpoint 9 runtime-provider, embedding, vision, deployment, and final-reference deliverables.
