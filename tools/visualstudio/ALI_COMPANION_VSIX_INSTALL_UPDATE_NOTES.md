# Ali Companion VSIX Install and Update Notes

These notes cover the native `Ali Companion` Visual Studio extension. The VSIX is a local developer package, not a marketplace-published extension yet.

## Current Target

The current manifest targets:

```text
Visual Studio Community
Version [17.14, )
Architecture amd64
```

That matches the installed Visual Studio Community 2026 instance on Chris's machine.

The built VSIX is:

```text
<repo>\src\Ali.App.VisualStudioExtension\bin\Debug\net472\Ali.App.VisualStudioExtension.vsix
```

For this repo today, `<repo>` is:

```text
C:\Users\clsor\Documents\Codex\2026-06-22\i-2
```

## Install or Update Community

1. Close Visual Studio.
2. Build the VSIX:

```powershell
dotnet build .\src\Ali.App.VisualStudioExtension\Ali.App.VisualStudioExtension.csproj --no-restore -p:UseSharedCompilation=false -nr:false
```

3. Install the VSIX with Visual Studio's installer:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe" /quiet ".\src\Ali.App.VisualStudioExtension\bin\Debug\net472\Ali.App.VisualStudioExtension.vsix"
```

4. Start Visual Studio.
5. Open `View -> Ali Companion`.
6. Confirm `Tools -> Options -> Ali -> Companion` points at the helper URL, normally:

```text
http://127.0.0.1:8765
```

## How Updates Work

Visual Studio installs the extension per Visual Studio instance and extension identity:

```text
Ali.App.VisualStudioExtension
```

When the VSIX manifest version increases, installing the rebuilt VSIX updates the existing Ali Companion extension for that Visual Studio instance. If Visual Studio is reinstalled or a different Community instance is used later, install the same VSIX into that new instance.

The VSIX does not currently auto-update from a feed. Updates are local rebuilds and reinstalls.

## Community Versus Other Editions

Community is supported by the current manifest. If Ali needs to install into Visual Studio Professional or Enterprise later, add matching `InstallationTarget` entries to `source.extension.vsixmanifest`, rebuild the VSIX, and install it with that edition's `VSIXInstaller.exe`.

The extension behavior should remain the same across editions because Ali Companion talks to Ali's local loopback helper and uses Visual Studio automation for active solution, document, line, and selection context.

## Boundary

The VSIX is a cockpit, not a silent authority upgrade. File edits, package changes, builds, tests, and Git writes still go through Ali's guarded parser, preview, confirmation, and receipt flow.
