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
15. Process evidence collector: `collect process evidence <name-or-pid>`.
16. Port owner diagnostic: `diagnose port <port>`.
17. File/build lock diagnostics: `diagnose file lock <path>` and `diagnose build lock`.
18. Service/startup inspector: `inspect services and startup`.
19. Event-log triage: `triage event logs`.
20. Approved process stop executor: `confirm stop process <pid>`.
21. Build/install intelligence: `classify last build failure`, `show roadmap step checklist`, and `show install doctor`.
22. Builder goal interpreter: `interpret build goal <goal>`.
23. Architecture option cards: `show architecture options <goal>`.
24. Acceptance/test planning: `write acceptance criteria <goal>` and `suggest tests for <goal>`.
25. Codebase pattern and feature file planning: `detect codebase patterns` and `plan feature files <goal>`.
26. Refactor safety review: `show refactor safety checklist <goal>`.
27. Dependency install planning: `plan dependency install packet <goal>`.
28. Scaffold apply planning: `plan scaffold apply <goal>`.
29. Post-edit validation loop: `plan post edit validation`.
30. Builder command surfaces: `show coding skill command index` and `show coding session summary`.
31. Phase 1 coding companion stopping-point polish: WebHelper and VSIX command groups now surface the guided builder flow first, then deeper planning, execution, diagnostics, and reports.
32. Start Here discoverability lane: WebHelper and VSIX now surface What Can Ali Do, Plan A Build, Fix A Build, Install Check, VS Setup, and Windows Help before the deeper command groups.
33. Audio setup source catalog: official Focusrite Scarlett Solo/2i2, Audio-Technica AT2040, TritonAudio FetHead, and Shure SH-BROADCAST2 sources are cataloged for source-backed setup guidance.

## Approved Next Road Completed

The second approved sprint moved Windows troubleshooting and build readiness into deterministic Ali commands while preserving the same approval gates. The third approved sprint moved builder planning into deterministic Ali commands without adding uncontrolled execution authority. The final polish pass made the current coding helper and Visual Studio integration a good Phase 1 stopping point before installer, dependencies, and model installation work resumes.

## Guardrails

- Read-only diagnostics first.
- Stop, disable, delete, repair, registry, firewall, PATH, trust-store, and signing changes need explicit owner approval.
- If Ali is not sure, she should present options instead of acting.
