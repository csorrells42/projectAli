---
name: software-engineering-delivery
description: Inspect, design, implement, build, test, debug, review, and verify software deliverables across Ali's supported languages. Use for substantial programming work rather than simple explanations.
license: MIT
---

# Software engineering delivery

Use Ali's live coding capability inventory instead of describing limitations from model memory.

1. Inspect the target and select its registered language provider.
2. Build bounded project context around the user's actual request.
3. State the intended change and the evidence that will prove it works.
4. Make the smallest coherent implementation through approved file tools.
5. Analyze, format, build, and test with the provider's registered tools.
6. Read diagnostics, repair failures, and repeat only the failed verification stages.
7. When the user asks for execution, run only after a successful build.
8. Report changed artifacts, verification evidence, remaining uncertainty, and any approval-gated next action.

For code investigation, prefer the shared ripgrep, Tree-sitter, LSP, semantic index, and architecture tools before loading large files wholesale. Select the language provider from project evidence rather than the wording of the request.

For Python, inspect the live provider before claiming that Ruff, basedpyright, pytest, debugpy, coverage, or py-spy is available. Use only toolchains that the capability report marks available. For Java, use the shipped Eclipse Temurin JDK and report Eclipse JDT Language Server or Eclipse IDE integration as available only when the live inspection actually finds it.

For a new Arduino sketch, prefer the registered `arduino_create_and_compile` operation: supply the complete source, an approved virtual or absolute `.ino` path whose filename matches the parent folder, and an explicit board FQBN. The operation creates a missing sketch folder itself. Use the returned compiler result and firmware artifacts as evidence. Never split the job through generic file writing, invent an Arduino Agent Skill, or claim the compiler is unavailable without checking the live capability registry.

Never claim a build, test, launch, file mutation, or external action occurred without a successful tool result. Preserve existing modular boundaries and avoid unrelated cleanup.
