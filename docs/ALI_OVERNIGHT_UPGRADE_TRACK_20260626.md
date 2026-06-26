# Ali Overnight Upgrade Track 2026-06-26

This is the approved build-skills path for the overnight sprint.

## Completed Or Implemented This Pass

1. Packet execution console: `show packet commands`, `run packet item N`, and `confirm run packet item N`.
2. Package/library lookup approval flow: `plan package lookup <goal>` with risk cards and approval path.
3. Better failure handling path: existing diagnosis plus deterministic patch suggestions and `resume build plan`.
4. Project creation/scaffolding planner: `preview project scaffold <goal>`.
5. Visual Studio quality pass: current commands remain available through the helper/VS bridge while preserving gates.
6. Packet run ledger: `show packet ledger`.
7. Scaffold preview bundles: scaffold previews list proposed project/test/file shape without writing files.
8. Dependency risk cards: package lookup output includes fit, license, maintenance, security, and integration risks.
9. Crash resume command: `resume build plan`.
10. Morning report export: `generate morning report`.
11. Project installer completion: `docs\ALI_PROJECT_INSTALLER_COMPLETION.md` and PDF.
12. Project installation instructions PDF: `docs\ALI_PROJECT_INSTALLATION_INSTRUCTIONS.md` and PDF.
13. PowerShell/CMD troubleshooting toolkit: `show windows troubleshooting toolkit`.
14. Rogue process hunt planner: `plan rogue process hunt <target>`.

## Approved Next Road

15. Process evidence collector: gather process, command line, parent PID, path, CPU, memory, and start time through a read-only receipt.
16. Port owner diagnostic: map listening ports to owning processes and suggested next questions.
17. File-lock diagnostic: detect common build locks and recommend safe process-specific actions.
18. Service/startup inspector: read services and startup entries without changing them.
19. Event-log triage: summarize recent System/Application errors without clearing logs.
20. Approved repair executor: stop a named PID/service only after explicit approval, with a receipt and rollback note when applicable.

## Guardrails

- Read-only diagnostics first.
- Stop, disable, delete, repair, registry, firewall, PATH, trust-store, and signing changes need explicit owner approval.
- If Ali is not sure, she should present options instead of acting.
