# Ali external coding agents

Ali remains the only user-facing assistant. Aider and OpenHands are modular implementation engines beneath Ali's existing Agent Framework coordinator.

## Modes

- **Aider**: runs Aider 0.86.2 in scripted architect mode against the approved project.
- **OpenHands**: runs OpenHands 1.16.0 headlessly in WSL against the approved project.
- **Hybrid**: OpenHands performs the implementation pass, then Aider reviews and refines the current working tree. Ali must inspect direct build, test, diff, or runtime evidence before claiming completion.

The mode is selected under **Settings > Agents > Programming engines**. It is persisted in `agent-orchestration-settings.json`.

## Boundaries

- The model selects the registered `coding_agent_execute` tool from its semantic description. No English keyword router selects these engines.
- The target is resolved through Ali's approved workstation mounts before either engine starts.
- `coding_agent_execute` passes through the same Agent Framework approval, denial, audit, activity, and MCP exposure path as Ali's other coding tools.
- The worker processes receive the approved project directory and are cancelled as a complete process tree when the user cancels the turn.
- External-agent output is evidence, not proof of delivery. Ali's direct build, test, source-control, application-verification, and delivery tools remain authoritative.
- Aider and OpenHands do not commit or push on Ali's behalf. Source-control actions remain separate protected tools.

## Runtime provisioning

Aider is a pinned package group in `runtime-assets.json` and is restored into `runtime/aider-packages` by `tools/RestoreRuntimeAssets.ps1`.

OpenHands officially requires WSL on Windows. Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\SetupOpenHands.ps1 -Distribution Ubuntu
```

If WSL or Ubuntu is missing, the script starts the official Windows WSL installation and may require a restart. Rerun it afterward to install the pinned OpenHands CLI inside the selected distribution.

References:

- [OpenHands CLI installation](https://docs.openhands.dev/openhands/usage/cli/installation)
- [OpenHands CLI command reference](https://docs.openhands.dev/openhands/usage/cli/command-reference)
- [Aider scripted mode](https://aider.chat/docs/scripting.html)
- [Aider options](https://aider.chat/docs/config/options.html)
