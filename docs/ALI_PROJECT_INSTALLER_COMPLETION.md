# Ali Project Installer Completion

Generated: 2026-06-26

This document tracks the current developer installer path and the remaining work needed before Ali has a polished one-click installer.

## Current Completion Status

Developer install path is ready for tomorrow's manual install pass:

- Build the solution from source.
- Refresh `%LOCALAPPDATA%\Ali\DevRun`.
- Keep DevRun at `%LOCALAPPDATA%\Ali\DevRun\Ali.App.Wpf.exe`.
- Install the local Ali Companion VSIX into Visual Studio Community.
- Start `Ali.App.WebHelper` on `http://127.0.0.1:8765/` for browser/VS companion commands.
- Keep Ollama running when desired; Ali should not need to stop/start it during ordinary coding updates.
- Use `ali-deepseek-coder-v2:16b-low` as the current saved coding/chat model.
- Treat `qwen3-vl:8b` or `ali-qwen3-vl:8b-low` as optional vision assets, not the default coding model.
- Keep voice resources under Ali-owned local folders.

This is not yet a production installer. It is a repeatable developer install/repair checklist.

## Installer Executable Lane

The repo now includes the first executable installer project:

```powershell
src\Ali.App.Installer\Ali.App.Installer.csproj
```

Running `Ali.Setup.exe` with no arguments opens the GUI installer. The GUI is a wizard with setup mode, assistant, dependency/model, Visual Studio, shortcut, review, and finish steps. It installs Ali by default and exposes optional selections for repair mode, model pulls, launching after install, shortcuts, and the Ali Companion Visual Studio extension.

The installer executable deploys an Ali app payload into:

```text
%LOCALAPPDATA%\Ali\DevRun
```

It does not copy chats, memories, reminders, correction queues, session audio, images, speech output, or assistant profile files from the payload. Fresh personal data lives under profile-specific folders:

```text
%LOCALAPPDATA%\Ali\Profiles\<profileId>
```

The assistant display name has one persisted source of truth:

```text
%LOCALAPPDATA%\Ali\BootstrapData\assistant-profile.json
```

By default, the installer does not create that profile file. First app launch asks the user to name the assistant. If an automated package intentionally wants to preseed the profile, it can pass `--assistant-name <name>`; that still writes only `assistant-profile.json` and does not duplicate the name into runtime, chat, memory, or installer settings.

Build the packaged setup executable with:

```powershell
.\tools\installer\build-ali-setup.ps1
```

That script publishes the WPF app, creates `src\Ali.App.Installer\Payload\ali-payload.zip`, embeds that payload into a single-file `Ali.Setup.exe`, and writes the result under:

```text
outputs\installer\setup-publish
```

The packaging script also builds the Ali Companion VSIX and places it in the payload under:

```text
extras\visualstudio\Ali.App.VisualStudioExtension.vsix
```

Developer install from an explicit payload folder or zip:

```powershell
.\Ali.Setup.exe --payload "C:\path\to\app-publish"
.\Ali.Setup.exe --payload "C:\path\to\ali-payload.zip"
```

Repair mode refreshes Ali app binaries and optional components while preserving chats, memories, reminders, correction queues, assistant profile, and profile folders:

```powershell
.\Ali.Setup.exe --repair
```

First-launch convenience remains explicit. The GUI can create desktop and Start menu shortcuts; command-line installs can request them with:

```powershell
.\Ali.Setup.exe --desktop-shortcut --start-menu-shortcut
```

Ollama installation remains explicit. In the GUI, use the `Install Ollama if missing` checkbox. For command-line installs:

```powershell
.\Ali.Setup.exe --install-ollama
.\Ali.Setup.exe --install-ollama --ollama-installer "C:\path\to\OllamaSetup.exe"
.\Ali.Setup.exe --install-ollama --ollama-installer-url "https://ollama.com/download/OllamaSetup.exe"
```

If Ollama is missing and install is requested, the installer runs the local installer path when supplied; otherwise it downloads the official Windows installer from Ollama and waits for it to complete. It then rechecks for `ollama.exe` before model pulls.

Model installation remains explicit. The installer will not pull Ollama models unless requested:

```powershell
.\Ali.Setup.exe --pull-runtime-model --runtime-model "ali-deepseek-coder-v2:16b-low"
.\Ali.Setup.exe --pull-vision-model --vision-model "qwen3-vl:8b"
```

Visual Studio Companion installation is optional and explicit. In the GUI, use the Visual Studio Companion checkbox. For command-line installs:

```powershell
.\Ali.Setup.exe --install-vsix
.\Ali.Setup.exe --install-vsix-only
.\Ali.Setup.exe --vsix "C:\path\to\Ali.App.VisualStudioExtension.vsix" --vsix-installer "C:\path\to\VSIXInstaller.exe"
```

`--install-vsix-only` can be used later without reinstalling the Ali app payload, seeding an assistant profile, copying chats or memories, pulling models, or launching Ali. If Visual Studio or `VSIXInstaller.exe` is not found, the installer records the skip in the receipt instead of making system changes.

If Ollama is not found, requested model pulls are skipped and the install receipt records that fact. The installer does not change PATH, registry, firewall, signing, trust stores, Windows services, or drivers.

## Installer Scope

Install mode should:

- Verify prerequisites.
- Build or copy known-good binaries.
- Create `%LOCALAPPDATA%\Ali\DevRun`.
- Copy app binaries into DevRun.
- Preserve `%LOCALAPPDATA%\Ali\BootstrapData`.
- Preserve generated documents, memories, reminders, conversations, and coding state.
- Install or update the Ali Companion VSIX for the selected Visual Studio Community instance.
- Write a clear install receipt.

Repair mode should:

- Stop Ali app/helper processes only after owner approval.
- Rebuild or recopy missing app files.
- Recreate required folders.
- Reinstall the VSIX if missing or stale.
- Leave user data untouched.
- Report exactly what changed.

Out of scope without explicit approval:

- Trust-store changes.
- Smart App Control changes.
- Registry repair.
- Firewall changes.
- Global PATH changes.
- Deleting user data.

## Dependencies To Validate

- Windows 11 development machine.
- .NET SDK `10.0.301` or compatible current SDK for this repo.
- Visual Studio Community 2026 / Visual Studio 18 Community with .NET desktop and extension tooling installed.
- Git for Windows.
- Ollama for local model runtime, with `ali-deepseek-coder-v2:16b-low` installed for the current coding/chat path.
- Optional Ollama vision model: `qwen3-vl:8b` or `ali-qwen3-vl:8b-low`.
- Local voice assets under Ali's `lib\voice` folder when voice testing resumes.
- Optional Notepad++ for file-opening convenience.
- PDF workspace configured under Ali Settings -> Coding / Permissions.
- WebHelper loopback bridge available at `http://127.0.0.1:8765/` before using VS companion commands.

## Install Receipts

The installer/repair path should write a receipt that includes:

- Timestamp.
- DevRun target path.
- Target app path.
- Assistant profile path and whether it exists.
- Copied file count.
- VSIX path and install result.
- Ollama install settings and model pull selections.
- Shortcut selections.
- Repair mode selection.
- Readiness snapshot for payload, DevRun, assistant profile, shortcuts, Ollama/models, and VSIX.
- Skipped actions and why they were skipped.

## Open Work

- Build a scriptable install/repair command.
- Add a signed production packaging path.
- Keep `show install doctor` as the read-only dependency checker and extend it as dependencies are added.
- Add a repair command that can explain exactly what it plans to do before doing it.
- Add backup/restore after install/repair is stable.

## Backup And Restore Follow-Up

After the one-shot installer flow is complete, add a user-facing backup button and restore path for Ali state. This should be implemented carefully as a zip-backed snapshot, not as a loose file copy.

Backup should capture the important per-user state needed to restore Ali exactly as she was at that moment:

- `%LOCALAPPDATA%\Ali\BootstrapData\assistant-profile.json`
- profile-specific folders under `%LOCALAPPDATA%\Ali\Profiles\<profileId>`
- conversations, memories, reminders, correction queues, local settings, runtime settings, model selection settings, and user-facing app preferences
- install receipts and diagnostic state that are useful for repair/support
- a manifest that records schema version, created timestamp, Ali app version, profile id, assistant name, selected runtime model id, selected vision model id, and source paths

Backup package shape:

- gather all selected state into one staging folder
- write the manifest at the staging root as `ali-backup-manifest.json`
- copy captured data under stable relative folders, for example `BootstrapData`, `Profiles`, and `Receipts`
- zip the staging folder into a single timestamped backup file
- do not write machine-specific absolute paths into restored file locations except as manifest metadata for troubleshooting

Restore should:

- stop or block active Ali UI/runtime operations before writing restored state
- unzip the selected backup into a staging folder first, validate the manifest and required files, then swap into place
- preserve a pre-restore backup automatically before overwriting current state
- update the single assistant profile source of truth only from the restored profile file
- restore model names/settings atomically so runtime settings, UI selections, and install receipts do not disagree
- never start model pulls during restore; only report missing models after restore
- require explicit confirmation before replacing current user data

Race-condition guardrails:

- do not restore while Ali is actively writing conversations, memories, reminders, correction queues, runtime settings, or profile data
- use one restore lock file or mutex around backup/restore operations
- write restored JSON/settings files with temp-file-and-rename semantics where possible
- treat the selected model id as a setting value, not as proof that the model exists locally
- after restore, run a read-only readiness check to report missing Ollama/models/VSIX without mutating state
