# Ali

Ali is a local-first WPF assistant built in C#.

Original Project Ali source is available under the [MIT License](LICENSE).
Bundled dependencies and model assets retain their upstream licenses; see
[Third-Party Notices](THIRD-PARTY-NOTICES.md) and `runtime-assets.json`.

## Build

```powershell
dotnet restore .\Ali.sln --configfile .\NuGet.Config --ignore-failed-sources
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\RestoreRuntimeAssets.ps1
dotnet build .\Ali.sln --no-restore
```

The restore script provisions the ignored canonical staging tree at
`artifacts\runtime-assets\win-x64`. It accepts `-VerifyOnly` for full checksum
validation, `-VerifyOnly -Fast` for the Build-time existence/size check, and
`-OfflineCache <folder>` to restore without network access from a previously
populated `downloads` and `wheels` cache.

## Run

```powershell
dotnet run --project .\src\Ali.csproj --no-build
```

## Copy-Folder Publish

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\PublishAliRelease.ps1
```

This is the canonical Ali release command. It always rebuilds the self-contained
bundle directly into `bin\Release\Ali`, validates every required asset, refreshes
`Ali Dev Run.lnk` on the current user's Desktop, and removes compiler intermediates.
Copy or zip that one published folder for a demo or transfer, then launch `Ali.exe`.
Build fails when a required staged asset is absent. Publish performs the full
model checksum pass before producing a copy-folder bundle, so a camera-, voice-,
speech-, or FFmpeg-broken folder cannot be produced accidentally. Non-English
.NET satellite-resource folders are removed from the finished English bundle.

## Runtime Data

Ali keeps user-facing runtime data under:

```text
%LOCALAPPDATA%\AliFiles
```

Agent Framework file memory is stored beneath `Data\AgentWorkspaces` and is
isolated by active user and conversation. It is Ali's private working notebook
for intermediate notes and drafts—not personal Mem0 memory, the Qdrant document
library, or a user-visible output folder. File-memory actions are metadata-audited
under `Data\Logs`; deletes and overwritten working notes remain recoverable under
`Data\RecoverableTrash\AgentWorkMemory`.

Large package/runtime assets are intentionally ignored by Git. Their exact
versions, licenses, immutable sources, destinations, sizes, and model checksums
are tracked in `runtime-assets.json`; no machine-bound virtual environment is
copied into a build.

## Current Shape

- `src`: the Ali desktop app project file and core application source.
- `src\UI`: core Ali desktop windows, view models, commands, and user-facing controls.
- `src\Modules`: portable feature code and feature-owned helper scripts grouped by feature, using `Ali.Modules.<Feature>` namespaces.
- `src\Modules\Coordinator`: the model-controlled `Microsoft.Extensions.AI` coordinator, semantic router, authoritative capability catalog, and focused memory, web/RAG, reminder, and identity/time tool implementations.
- `tools\Ali.Modules\Automation`: external UI automation harness for real app validation.

Ali is distributed as a copyable executable folder. User data, settings, API keys, memories, reminders, and receipts stay outside the executable folder under the configured Ali data root.

## Model-Controlled Tools

The desktop conversation entry point is a thin handoff to the Extensions.AI
coordinator. Ali receives the complete user request before choosing tools; there
is no rule-based English source or memory interceptor in front of the model.
Connectors without native function calls use a compact routing envelope as a
transport adapter, but the local model still makes the semantic routing decision.

The authoritative callable catalog is defined once in `AliCapabilityCatalog`.
It includes Ali's native memory, web/RAG, reminder, identity/time, and inventory
tools; all seven Microsoft Agent Framework `file_access_*` tools; all seven
Microsoft Agent Framework `file_memory_*` tools; and enabled external MCP tools.
Call `list_available_tools` at runtime for the complete current inventory and
source labels.

Voice playback remains an application output setting rather than a model tool.
Ali must not claim direct calendar, email, arbitrary file-system, shell, camera,
or generic browser-control access unless a future module actually registers it.

