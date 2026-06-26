# Ali Companion Professional Plugin Backlog

This list tracks the Visual Studio plugin polish path while keeping Ali's current authority model intact. The VSIX can improve the cockpit, context, and feedback loop, but file edits, package changes, builds/tests, and Git writes still route through Ali's guarded local bridge.

## In Progress

- Native Visual Studio tool window with loopback bridge commands.
- Active solution, document, line, and selection context.
- Context command fillers for read file, build solution, search selection, plan selection, and patch selection.
- Command history in the VS panel.
- Visible progress and command state indicators.
- Pending approval and patch-preview state cues.
- Diagnostic list with double-click open for compiler-style file/line output.
- Visual Studio options page for helper URL, command history limit, and selected-text context behavior.

## Next

- Richer pending-approval view that separates requested command, risk, target path, and next confirmation command.
- Build/test progress model that can show start time, elapsed time, exit code, and recent receipt link.
- Safer selected-code packaging with explicit preview of what context will be sent to Ali.
- Dedicated output tabs for response, diagnostics, receipts, and pending patch.
- Real icon, branding pass, and marketplace-ready metadata.
- Installer/update notes for Community versus future Visual Studio editions.

## Guardrails

- Do not add silent write/build/package/Git authority in the VSIX.
- Do not send selected code anywhere except the local loopback Ali bridge.
- Keep commands deterministic and visible in the command box before running.
- Keep deeper autonomy behind explicit owner approval.
