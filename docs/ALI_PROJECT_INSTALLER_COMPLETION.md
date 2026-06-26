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
- Keep voice resources under Ali-owned local folders.

This is not yet a production installer. It is a repeatable developer install/repair checklist.

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
- Ollama for local model runtime, with `qwen3-vl:8b` installed for the current certified local proof path.
- Local voice assets under Ali's `lib\voice` folder when voice testing resumes.
- Optional Notepad++ for file-opening convenience.

## Install Receipts

The installer/repair path should write a receipt that includes:

- Timestamp.
- Source repo path.
- Build result.
- DevRun target path.
- Copied file count.
- VSIX path and install result.
- WebHelper launch/status result when requested.
- Skipped actions and why they were skipped.

## Open Work

- Build a scriptable install/repair command.
- Add a signed production packaging path.
- Add a dependency checker command inside Ali.
- Add a repair command that can explain exactly what it plans to do before doing it.
- Add backup/restore after install/repair is stable.
