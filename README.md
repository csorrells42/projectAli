# Ali

Ali is a local-first, WPF-based C# assistant.

Current status: Phase 0 / Phase 1A bootstrap.

## What Works Now

- Classic `Ali.sln` with four projects:
  - `Ali.App.Wpf`
  - `Ali.Core`
  - `Ali.Infrastructure`
  - `Ali.Tests`
- WPF shell with left navigation and Chat home base.
- ChatGPT-style composer with Enter to send and Shift+Enter for newline.
- Streaming bootstrap response from a local deterministic runtime stub.
- Optional local OpenAI-compatible runtime adapter with explicit health check activation.
- Runtime Settings panel with load, save, check, activate, stub revert, and last-known-good revert.
- Stop/cancel support for active response streaming.
- Evidence status labels.
- `Flag as incorrect` button on assistant answers.
- File-backed correction queue for bootstrap validation.
- Permission risk classes for command, network, package, calendar, model, LAN, and destructive actions.
- Truthfulness policy helpers for receipt-backed action claims.

## Build

```powershell
dotnet restore .\Ali.sln --configfile .\NuGet.Config --ignore-failed-sources
dotnet build .\Ali.sln --no-restore
```

## Run Bootstrap Tests

```powershell
dotnet run --project .\tests\Ali.Tests\Ali.Tests.csproj --no-build
```

## Run The App

```powershell
dotnet run --project .\src\Ali.App.Wpf\Ali.App.Wpf.csproj --no-build
```

## Optional Local Runtime Setup

Ali starts on the safe bootstrap stub. To test a local OpenAI-compatible runtime, copy:

```text
%LOCALAPPDATA%\Ali\BootstrapData\runtime-settings.example.json
```

to:

```text
%LOCALAPPDATA%\Ali\BootstrapData\runtime-settings.json
```

Then edit the model name and endpoint if needed, or use the Runtime Settings panel in Ali. The Ollama OpenAI-compatible default is:

```text
http://127.0.0.1:11434/v1/
```

After launching Ali:

1. Open the Runtime Settings panel on the right.
2. Enter the endpoint and model/package ID.
3. Click `Save settings`.
4. Click `Check Runtime`.
5. Click `Activate Runtime` only after the check passes.

Ali only activates the configured runtime after the health check succeeds. `Revert to Stub` returns to the deterministic local stub.

Reference: https://docs.ollama.com/api/openai-compatibility

## KISS Rules

- Keep Ali local-first.
- Keep WPF as the user interface.
- Keep commands behind deterministic C# validation.
- Keep wrong answers preserved in the correction queue.
- Keep runtime activation explicit after health checks.
- Keep dependencies boring and justified.
- Add packages only when a feature truly needs them.
