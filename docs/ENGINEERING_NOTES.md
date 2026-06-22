# Ali Engineering Notes

## Launch Slice

This repository starts with the smallest useful slice:

```text
WPF composer
-> local runtime boundary
-> streamed answer
-> stop/cancel
-> evidence status
-> flag as incorrect
-> correction queue persistence
```

The active runtime starts as a local deterministic development stub. It does not pretend to be a real model.

The first real runtime path is now present:

```text
runtime-settings.json or ALI_OPENAI_* environment variables
-> local endpoint policy
-> OpenAI-compatible adapter
-> Check Runtime
-> health check
-> visible activation
```

Ali only switches from the stub to the configured runtime after the health check succeeds and the user clicks `Activate Runtime`.

## Current Project Shape

```text
Ali.sln
src/Ali.App.Wpf
src/Ali.Core
src/Ali.Infrastructure
tests/Ali.Tests
docs
```

## Safety Foundations Added

- `EvidenceStatus`: `Verified`, `Inferred`, `Unverified`, `Unknown`
- `ActionReceipt`: the proof object for tool/build/test/calendar claims
- `TruthfulnessPolicy`: first non-deception helpers
- `PermissionRisk`: risk groups for future execution gates
- `PermissionService`: bootstrap confirmation policy
- `CorrectionQueueService`: preserves exact Q/A when an answer is flagged
- `ILocalModelRuntime`: local model boundary
- `OpenAiCompatibleLocalModelRuntime`: first real local HTTP adapter
- `LocalEndpointPolicy`: refuses public/cloud endpoints in local-only mode
- `SafeActivatingLocalRuntime`: keeps the fallback active until health passes
- Runtime settings UI: load/save/check/activate/revert without a model library

## Bootstrap Storage

The correction queue currently uses a simple JSON file store so the first app loop can build without external packages or network access.

The final product spec calls for SQLite. Add SQLite deliberately once package installation is approved and the schema is ready.

## Installer, Repair, Backup, Restore

Keep these simple:

- Installer: one Windows installer, `Install` and `Repair` modes only.
- Repair: validate files, recreate missing folders, keep user data.
- Backup: zip Ali's local data folder plus settings/export metadata.
- Restore: stop Ali, validate backup manifest, restore local data, restart.
- Documentation: update user and engineering notes as features land.

## Package Rule

Build/test commands are executable code and package restore can run external logic. Treat restore/build/test with appropriate permission once Ali executes them herself.

For this bootstrap, no external NuGet packages are required.

## Runtime Settings

The bootstrap settings file is:

```text
%LOCALAPPDATA%\Ali\BootstrapData\runtime-settings.json
```

The app writes an example next to it:

```text
%LOCALAPPDATA%\Ali\BootstrapData\runtime-settings.example.json
```

Environment variable alternative:

```powershell
$env:ALI_OPENAI_BASE_URL = "http://127.0.0.1:11434/v1/"
$env:ALI_OPENAI_MODEL = "qwen3:14b"
```

Do not enable private LAN endpoints until pairing/authentication/encryption exists.

## Health Check Behavior

The local runtime health check verifies:

- Endpoint policy accepts the URL.
- Model/package ID is present.
- `/models` works and lists the selected model, or the prompt call proves the model is callable.
- A tiny non-streaming chat completion returns content.
- A tiny streaming chat completion returns content when streaming is enabled.
- Cancellation is honored.
- Latency, endpoint, model, context, output limit, temperature, and streaming support are recorded in the result.

Failure leaves the active runtime unchanged.

## First Real Local Heartbeat

Date: 2026-06-22

The first real local model validation used:

```text
Runtime: Ollama
Endpoint: http://127.0.0.1:11434/v1/
Model/package ID: qwen3:14b
Installed package size: 14.8B
Installed quantization: Q4_K_M
Context: 4096
Max output: 256
Temperature: 0.2
Top-p: 0.9
Streaming: enabled
```

This is the first proof model only. It is not the final Ali model decision.

Validation result:

```text
Health check: passed
Activation before explicit request: no
Explicit activation: passed
First prompt: What model are you using? Answer in one short sentence.
Streamed answer: I am using the Qwen3 model.
Stop/cancel: passed after first token
Correction queue runtime snapshot: stored
```
