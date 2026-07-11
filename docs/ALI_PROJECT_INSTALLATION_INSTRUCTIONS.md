# Ali Project Installation Instructions

Generated: 2026-06-26

These instructions cover the current developer installation path. They are intentionally explicit so tomorrow's install pass can be verified step by step.

## Required Dependencies

- Windows 11.
- PowerShell 5.1 or newer.
- .NET SDK `10.0.301` or compatible SDK for the repo.
- Visual Studio Community 2026 / Visual Studio 18 Community.
- Visual Studio workloads:
  - .NET desktop development.
  - Visual Studio extension development.
  - ASP.NET and web development is useful for the WebHelper project.
- Git for Windows.
- Ollama installed locally if model-backed chat will be used.
- Current saved coding/chat model: `ali-deepseek-coder-v2:16b-low`.
- Optional vision model for image reasoning: `qwen3-vl:8b` or `ali-qwen3-vl:8b-low`.
- Optional Notepad++.
- Voice dependencies only when voice work resumes:
  - Ali-owned `lib\voice\python-venv`.
  - Faster-Whisper model cache.
  - Piper executable and voice models.

## Source Repo

Current developer repo:

```text
C:\Users\clsor\Documents\Codex\2026-06-22\i-2
```

Current branch:

```text
phase-1c-voice-hardening
```

## Build

From the repo root:

```powershell
dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false
```

Expected result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

If the build reports locked WebHelper DLLs, stop only `Ali.App.WebHelper`, rebuild, and restart the helper afterward. Ollama does not need to be stopped for ordinary coding updates.

## Test

From the repo root:

```powershell
dotnet run --project .\tests\Ali.Tests\Ali.Tests.csproj --no-build
```

Expected result: all tests print `PASS` and the process exits with code 0.

## Refresh DevRun

Current target:

```text
%LOCALAPPDATA%\Ali\DevRun\Ali.App.Wpf.exe
```

Do not create new `DevRun-*` folders for this pass. Refresh the existing DevRun folder only after a clean build/test pass.

Use a self-contained Windows publish for DevRun so `Ali.App.Wpf.exe` does not require a separate .NET Desktop Runtime install:

```powershell
dotnet publish .\src\Ali.App.Wpf\Ali.App.Wpf.csproj -c Debug -r win-x64 --self-contained true -o "$env:LOCALAPPDATA\Ali\DevRun"
```

## Start WebHelper

From the repo root:

```powershell
dotnet run --project .\src\Ali.App.WebHelper\Ali.App.WebHelper.csproj --no-build
```

Default URL:

```text
http://127.0.0.1:8765/
```

The WebHelper provides the browser companion and the loopback coding bridge used by the Visual Studio extension.

## Install Visual Studio Extension

Build output:

```text
src\Ali.App.VisualStudioExtension\bin\Debug\net472\Ali.App.VisualStudioExtension.vsix
```

Visual Studio Community installer path:

```text
C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe
```

Install/update command:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe" /quiet ".\src\Ali.App.VisualStudioExtension\bin\Debug\net472\Ali.App.VisualStudioExtension.vsix"
```

Open Visual Studio Community, then use:

```text
View -> Ali Companion
```

or:

```text
Tools -> Ali Companion
```

The extension is installed per Visual Studio instance. If Community is reinstalled later, reinstall the same VSIX into that new instance.

## Runtime Models

Ali's current saved coding/chat runtime uses Ollama:

```text
Endpoint: http://127.0.0.1:11434/v1/
Model: ali-deepseek-coder-v2:16b-low
Display name: Ali tuned DeepSeek-Coder-V2 16B low
Context: 4096
Output: 768
Temperature: 0.1
Top-p: 0.9
Streaming: enabled
```

Vision-capable models such as `qwen3-vl:8b` and `ali-qwen3-vl:8b-low` are optional install assets for image reasoning. They are not the current saved coding runtime. Ali should not claim model-backed chat is active unless the runtime check passes.

Verify installed Ollama models with:

```powershell
ollama list
```

The install target is healthy when `ali-deepseek-coder-v2:16b-low` is listed for coding/chat. Vision models can be added later if screenshot or image reasoning is part of the installed feature set.

## Install Doctor

Inside Ali or the helper/VS companion, run:

```text
show install doctor
show pdf tool status
show visual studio integration
show coding skill command index
```

The install doctor is read-only. It checks DevRun, Visual Studio discovery, VSIX build artifact, WebHelper bridge URL, runtime settings, saved model, PDF workspace, .NET runtime, OS, and manual dependency commands. It does not install, repair, sign, edit registry, change firewall, change PATH, or modify trust stores.

## Internet And Local Sources

Ali's internet backend settings are created at startup or repair when missing:

```text
%LOCALAPPDATA%\Ali\BootstrapData\Sources\internet_backends.json
```

Configure Tavily and Firecrawl keys in Ali Settings -> Internet, or set the configured environment variables:

```text
TAVILY_API_KEY
FIRECRAWL_API_KEY
```

Ali's source planner decides when source lookup is needed. Internet lookups use Tavily search first, Firecrawl page extraction for retrieved pages, Firecrawl search fallback when Tavily returns no usable results, and Firecrawl direct scrape for exact URL prompts.

Local reference documents are managed through Maintenance -> Local Library. Put user-approved manuals or reference files in the configured local library root and rebuild the local index there. Do not place legacy JSON source lists in `%LOCALAPPDATA%\Ali\BootstrapData\Sources`; that path now belongs to internet backend settings and Local Library indexes.

## Coding Companion Checks

After WebHelper and Visual Studio are running, verify these commands:

```text
show visual studio integration
show coding skill command index
interpret build goal Visual Studio companion upgrade
show architecture options Visual Studio companion upgrade
write acceptance criteria Visual Studio companion upgrade
suggest tests for Visual Studio companion upgrade
draft implementation roadmap Visual Studio companion upgrade
show next coding action
show execution packet
plan post edit validation
plan package lookup Visual Studio tool window
plan dependency install packet Visual Studio tool window
show install doctor
generate morning report
```

## Safety Notes

- File edits still require preview and confirmation.
- Package installs, restore, build, test, and run commands still require confirmation.
- Git write commands still require confirmation.
- Windows troubleshooting commands are read-only guidance unless Chris explicitly approves a named repair action.
- Do not change signing, trust stores, Smart App Control, firewall, registry, global PATH, or service startup without explicit approval.
