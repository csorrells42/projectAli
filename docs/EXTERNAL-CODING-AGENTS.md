# Ali external coding agents

Ali remains the only user-facing assistant. Aider and OpenHands are modular implementation engines beneath Ali's existing Agent Framework coordinator.

## Modes

- **Aider**: runs Aider 0.86.2 in scripted architect mode against the approved project.
- **OpenHands**: runs OpenHands 1.16.0 headlessly in WSL against the approved project.
- **Hybrid**: OpenHands performs the implementation pass, then Aider reviews and refines the current working tree. Ali must inspect direct build, test, diff, or runtime evidence before claiming completion.

The mode is selected under **Settings > Agents > Programming engines**. It is persisted in `agent-orchestration-settings.json`.

Enable **Always use the selected coding agent for programming work** to require Ali's model to call `coding_agent_execute` for every request it semantically identifies as code creation, modification, debugging, building, testing, running, or packaging. This changes the model instruction; it does not add an English keyword router.

## Boundaries

- The model selects the registered `coding_agent_execute` tool from its semantic description. No English keyword router selects these engines.
- The target is resolved through Ali's approved workstation mounts before either engine starts.
- `coding_agent_execute` passes through the same Agent Framework approval, denial, audit, activity, and MCP exposure path as Ali's other coding tools.
- The worker processes receive the approved project directory and are cancelled as a complete process tree when the user cancels the turn.
- OpenHands receives the active Lemonade endpoint and model plus Ali's full context window, separate output cap, temperature, top-p, and reasoning effort. A module-owned launcher uses OpenHands' public `LLM` and `AgentStore` APIs because the CLI's temporary environment override supports only API key, model, and endpoint. The isolated OpenHands settings contain only the local dummy API key and current local-runtime configuration.
- Both providers receive the objective through a private UTF-8 task file rather than embedding a potentially long request in a Windows command line. Ali deletes these transport files after the provider exits.
- Aider receives a private per-run model metadata file with Ali's exact input/output budget and a model-settings file carrying the selected temperature, optional top-p, and Lemonade `chat_template_kwargs.reasoning_effort`. OpenHands receives the same connector settings through its module-owned launcher.
- OpenHands is successful only when its structured event stream contains a finish observation. A zero process exit after a model or conversation error is rejected. Aider's known zero-exit command-failure output is rejected as well.
- External-agent output is evidence, not proof of delivery. Ali's direct build, test, source-control, application-verification, and delivery tools remain authoritative.
- Aider and OpenHands do not commit or push on Ali's behalf. Source-control actions remain separate protected tools.

## Runtime provisioning

Aider is a pinned package group in `runtime-assets.json` and is restored into `runtime/aider-packages` by `tools/RestoreRuntimeAssets.ps1`. Ali's Aider launcher places that package group ahead of the shared portable-Python packages, preventing another feature's NumPy or other transitive dependency from overriding Aider's verified versions.

OpenHands officially requires WSL on Windows. Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\SetupOpenHands.ps1 -Distribution Ubuntu-24.04
```

If the Windows WSL feature is missing, the script first runs the supported `wsl --install` bootstrap and may require administrator approval and a restart. Rerun it afterward; if Ubuntu 24.04 is still missing, the script then requests that distribution. The script provisions `python3.12-venv` when necessary and uses OpenHands' recommended `uv tool install` path to install the pinned CLI in an isolated per-user tool directory. Ubuntu 24.04 is pinned because OpenHands 1.16.0 requires Python 3.12.

Lemonade binds to Windows loopback. OpenHands therefore requires WSL mirrored networking so Linux can reach that same `127.0.0.1` endpoint without exposing Lemonade to the LAN. When `%USERPROFILE%\.wslconfig` is absent, the setup script installs the tracked `tools\wslconfig.openhands` template and restarts WSL. If a custom `.wslconfig` already exists without mirrored networking, the script leaves it untouched and reports the exact setting to merge.

References:

- [OpenHands CLI installation](https://docs.openhands.dev/openhands/usage/cli/installation)
- [OpenHands CLI command reference](https://docs.openhands.dev/openhands/usage/cli/command-reference)
- [Aider scripted mode](https://aider.chat/docs/scripting.html)
- [Aider options](https://aider.chat/docs/config/options.html)
