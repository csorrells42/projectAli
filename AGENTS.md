# ================================================================
# HARD REPOSITORY RULE: ONE SOURCE OF TRUTH
# ================================================================

Project Ali has exactly one authoritative repository path, worktree, and branch:

- Repository and worktree: `C:\Users\clsor\Documents\Codex\ProjectAli`
- Branch: `master`

All normal work is committed directly to `master`. Do not create another branch,
worktree, clone, checkpoint lane, or alternate source directory unless Chris
explicitly requests that exact exception.

If Chris explicitly authorizes a temporary branch, integration is not complete
until the branch has been merged into `master`, `master` has been verified and
pushed, and the temporary local branch, remote branch, and worktree have all been
removed. Never leave repository debris behind after integration.

Before changing any file, verify the repository root and current branch. If the
checkout is not the canonical path on `master`, stop and report the mismatch.

# HARD BUILD RULE: ENGLISH-ONLY OUTPUT

Ali is built and distributed for English-speaking users in the United States.
Do not copy or publish satellite resource language packs. Keep
`SatelliteResourceLanguages` restricted to `en`, and do not commit generated
build, publish, release, `bin`, or `obj` output.

# Project Ali Codex completion gate

Before finishing any turn that changes this repository, perform a final scope-and-trust review. Do not return a completion message until every applicable condition below is true.

1. Scope fidelity
   - The implementation changes only what Chris requested.
   - Do not add improvements, cleanup, safeguards, refactors, policies, or adjacent behavior unless Chris explicitly requested them.
   - If an unrequested change was made while working, revert that change before finishing.

2. Existing behavior
   - Preserve existing startup, readiness waiting, retry, recovery, request coordination, permissions, endpoint protection, and other unrelated behavior unless the request explicitly changes it.
   - Do not infer permission to redesign a subsystem from permission to change one setting or bug.

3. Configuration truth
   - Any value selected by the user must remain the value saved, displayed, and sent to the applicable runtime or server.
   - Do not silently clamp, replace, normalize to a different option, shrink, expand, or otherwise govern a selected value.
   - If the selected value cannot be honored, report the failure plainly instead of substituting another value.
   - Do not hard-code a value that is exposed as a configurable option.

4. Deterministic processing disclosure
   - Model-driven interpretation and decisions remain the default.
   - If deterministic logic is added to a user-request processing path, the final response must identify the rule, its location, why it exists, and what it can affect.
   - If none was added, say so explicitly when the turn changed request-processing behavior.

5. Verification discipline
   - Review the final diff and confirm every changed file belongs to the requested scope.
   - Run only the build or tests Chris requested. Do not broaden verification into unrelated suites.
   - Project Ali builds are Release builds unless Chris explicitly requests otherwise.
   - If Ali is running and a build would interrupt her, ask before stopping or rebuilding.
   - After every successful Ali build, verify that the desktop Ali shortcut targets the canonical Release executable. Repair the shortcut if necessary, verify that its target exists, and launch that Release build unless Chris explicitly says not to launch it.
   - State exactly what was and was not verified; never imply a runtime result that was not observed.

6. Completion decision
   - The final review is binary: the requested work is satisfied without unrequested changes, or the turn is not ready to finish.
   - If the review fails, continue repairing the scoped work instead of presenting it as complete.
