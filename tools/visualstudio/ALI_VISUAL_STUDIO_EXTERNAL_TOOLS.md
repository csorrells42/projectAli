# Ali Visual Studio External Tools

This is the optional Visual Studio External Tools bridge. It works with Visual Studio Community stable or Insiders because Visual Studio only launches the bridge executable.

Ali also has a native developer VSIX named `Ali Companion`. Use `tools\visualstudio\ALI_COMPANION_VSIX_INSTALL_UPDATE_NOTES.md` for the native tool window. This External Tools path remains useful as a simple fallback because it calls `Ali.App.VisualStudioBridge.exe`, which sends deterministic commands to Ali's loopback coding bridge.

## Prerequisites

Build the solution:

```powershell
dotnet restore .\Ali.sln
dotnet build .\Ali.sln --no-restore
```

Bridge executable:

```text
<repo>\src\Ali.App.VisualStudioBridge\bin\Debug\net10.0\Ali.App.VisualStudioBridge.exe
```

The bridge will try to start `Ali.App.WebHelper` on loopback if it is not already running.

External Tools entries are stored per Visual Studio instance/profile. If you switch Visual Studio instances later, add the same External Tools entries in the new instance.

## Suggested External Tools

In Visual Studio:

```text
Tools -> External Tools... -> Add
```

### Ali Coding Status

Command:

```text
<repo>\src\Ali.App.VisualStudioBridge\bin\Debug\net10.0\Ali.App.VisualStudioBridge.exe
```

Arguments:

```text
--status
```

### Ali Integration Handoff

Command:

```text
<repo>\src\Ali.App.VisualStudioBridge\bin\Debug\net10.0\Ali.App.VisualStudioBridge.exe
```

Arguments:

```text
--handoff
```

### Ali Read Current File

Command:

```text
<repo>\src\Ali.App.VisualStudioBridge\bin\Debug\net10.0\Ali.App.VisualStudioBridge.exe
```

Arguments:

```text
--read-current-file --file "$(ItemPath)" --line "$(CurLine)"
```

### Ali Analyze Solution

Command:

```text
<repo>\src\Ali.App.VisualStudioBridge\bin\Debug\net10.0\Ali.App.VisualStudioBridge.exe
```

Arguments:

```text
--command "analyze solution architecture"
```

### Ali Search Workspace

Command:

```text
<repo>\src\Ali.App.VisualStudioBridge\bin\Debug\net10.0\Ali.App.VisualStudioBridge.exe
```

Arguments:

```text
--command "search workspace for WidgetFactory"
```

## Boundary

All commands still pass through Ali's parser, workspace policy, confirmation gates, and receipts. Build/test/run, file writes, and Git write actions do not become automatic because Visual Studio launched the bridge.

For this repo today, `<repo>` is:

```text
C:\Users\clsor\Documents\Codex\2026-06-22\i-2
```
