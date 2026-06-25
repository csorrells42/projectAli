# Ali Coding Edition Phase Report - 2026-06-25

## Scope

This phase turns Ali from a chat-only local assistant into a guarded local coding assistant.

The goal was not unrestricted autonomy. The goal was a reliable loop:

```text
inspect
-> plan
-> preview
-> confirm
-> apply
-> build/test
-> diagnose
-> repair
```

## Completed

- Approved coding workspace policy and permissions.
- Settings surface for coding permissions, tool paths, and Git gates.
- Workspace inspection and project map.
- File open/read/search inside the approved workspace.
- Explicit file open outside the workspace when allowed by policy.
- Notepad++ and Visual Studio tool discovery with configurable paths.
- Package reference inspection.
- Confirmed dotnet build/test/restore/run.
- Dotnet diagnostic summaries.
- Open first diagnostic file from the last failed dotnet command.
- Diagnose last dotnet failure with command result, source excerpt, and next guarded commands.
- Guarded Git status/diff/log/add/commit/merge, with pull/push blocked unless intentionally enabled later.
- Coding task planner.
- Coding action receipts.
- Literal patch preview.
- Small multi-file literal patch bundle preview.
- Apply last guarded patch preview after confirmation.
- Show and discard pending patch previews.
- Stale pending patch previews are discarded instead of applied.
- Simple local text-to-PDF generation.

## Owner Commands

```text
inspect coding workspace
show project map
list packages
search workspace for WidgetFactory
read file "C:\path\to\file.cs" at line 42
plan coding task fix the build
show coding receipts
preview replace in file "C:\path\to\file.cs" "old text" with "new text"
preview patch bundle
file "C:\path\to\first.cs" replace "old text" with "new text"
file "C:\path\to\second.cs" replace "old text" with "new text"
show pending patch preview
discard pending patch preview
confirm apply last patch preview
confirm dotnet build "C:\path\to\project-or-solution"
diagnose last build failure
open build error
git status
confirm git commit "message"
generate pdf "owner-demo.pdf" with text "Ali demo ready."
```

## Validation

Latest validated state:

- `dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false`
- Full `Ali.Tests` console harness
- DevRun refreshed at `%LOCALAPPDATA%\Ali\DevRun\Ali.App.Wpf.exe`
- Build/compiler servers shut down after validation

## Recent Coding Phase Commits

```text
2ef5a95 Manage pending patch previews
1a5a12f Diagnose last dotnet failure
6fda6f0 Open last dotnet diagnostic file
1a11af0 Apply last guarded patch preview
ead1ae5 Add local text PDF generation
aad9d24 Add guarded patch preview
1900de2 Add coding action receipts
5adb0d5 Add guarded coding task planner
100d686 Add guarded coding context repair loop
aa7ac3a Open primary coding solution from workspace
ac02357 Summarize dotnet coding diagnostics
4201af1 Add coding workspace and dependency inspection
```

## Remaining Work

- Live owner soak of the coding commands in a real external project.
- Richer patch preview/apply format that can safely handle multiple edits per file.
- Better PDF/session report templates.
- Installer-managed tool discovery and repair.
- Optional project-specific build/test profiles.
- Voice is still separate from this coding phase and should be certified independently.
