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
dotnet publish .\src\Ali.csproj -c Release -r win-x64 --self-contained true -o .\bin\Release\Ali
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\TestPublishedBundle.ps1 -PublishRoot .\bin\Release\Ali
```

Copy the published folder to the target computer and launch `Ali.exe`.
Build fails when a required staged asset is absent. Publish performs the full
model checksum pass before producing a copy-folder bundle, so a camera-, voice-,
speech-, or FFmpeg-broken folder cannot be produced accidentally. Non-English
.NET satellite-resource folders are removed from the finished English bundle.

## Runtime Data

Ali keeps user-facing runtime data under:

```text
%LOCALAPPDATA%\AliFiles
```

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

The authoritative callable catalog is defined once in `AliCapabilityCatalog`:

- `list_available_tools`
- `search_memory`
- `remember_fact`
- `search_current_web`
- `search_local_library`
- `create_reminder`
- `get_assistant_identity`
- `get_current_local_time`

Voice playback remains an application output setting rather than a model tool.
Ali must not claim direct calendar, email, arbitrary file-system, shell, camera,
or generic browser-control access unless a future module actually registers it.

