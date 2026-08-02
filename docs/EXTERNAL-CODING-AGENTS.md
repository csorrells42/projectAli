# Historical reference: retired external coding agents

> **Status: historical and inert.** Ali is the sole coding executor. Aider and OpenHands are not selectable in the live UI, are not exposed through the live capability registry, and are not launched by the active orchestration path. This file remains in the bundle only as an architecture record.

## Retired design

An earlier prototype placed optional Aider and OpenHands implementation engines beneath Ali's Agent Framework coordinator:

- **Aider** ran in scripted architect mode against an approved project.
- **OpenHands** ran headlessly in WSL against an approved project.
- **Hybrid** asked OpenHands to implement and Aider to review the same working tree.

The prototype exposed a Programming engines setting and a `coding_agent_execute` capability. Those controls and that live capability have been retired. Persisted legacy selections normalize to Off; they do not reactivate an engine.

## Historical safety boundaries

The retired design required approved workstation mounts, process-tree cancellation, private task files, bounded local-model settings, and direct Ali build/test/diff/runtime evidence before any completion claim. External-agent output was treated as evidence rather than proof, and source-control actions remained separate protected tools.

These notes describe the former boundary only. They are not active configuration or setup instructions. Ali's active orchestration path does not configure or launch Aider or OpenHands; dormant compatibility and provisioning assets may remain in the deployment until a later package-size cleanup.

Historical upstream references:

- [OpenHands CLI installation](https://docs.openhands.dev/openhands/usage/cli/installation)
- [OpenHands CLI command reference](https://docs.openhands.dev/openhands/usage/cli/command-reference)
- [Aider scripted mode](https://aider.chat/docs/scripting.html)
- [Aider options](https://aider.chat/docs/config/options.html)
