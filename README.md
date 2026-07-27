# Ali

Ali is a local-first WPF assistant built in C#.

## Build

```powershell
dotnet restore .\Ali.sln --configfile .\NuGet.Config --ignore-failed-sources
dotnet build .\Ali.sln --no-restore
```

## Run

```powershell
dotnet run --project .\src\Ali.csproj --no-build
```

## Copy-Folder Publish

```powershell
dotnet publish .\src\Ali.csproj -c Release -r win-x64 --self-contained true -o .\bin\Release\Ali
```

Copy the published folder to the target computer and launch `Ali.exe`.
The publish now fails instead of producing a camera-broken folder when the local
MediaPipe runtime asset is missing from `lib\vision\mediapipe-runtime`.

## Runtime Data

Ali keeps user-facing runtime data under:

```text
%LOCALAPPDATA%\AliFiles
```

Local voice resources may exist under `lib\voice` during development or beside an installed app. Large package/runtime assets are intentionally ignored by Git.

## Current Shape

- `src`: the Ali desktop app project file and core application source.
- `src\UI`: core Ali desktop windows, view models, commands, and user-facing controls.
- `src\Modules`: portable feature code and feature-owned helper scripts grouped by feature, using `Ali.Modules.<Feature>` namespaces.
- `tools\Ali.Modules\Automation`: external UI automation harness for real app validation.

Ali is distributed as a copyable executable folder. User data, settings, API keys, memories, reminders, and receipts stay outside the executable folder under the configured Ali data root.

