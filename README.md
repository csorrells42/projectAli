# Ali

Ali is a local-first, WPF-based C# assistant.

Current status: Phase 0 / Phase 1C bootstrap.

## What Works Now

- Classic `Ali.sln` with four projects:
  - `Ali.App.Wpf`
  - `Ali.Core`
  - `Ali.Infrastructure`
  - `Ali.Tests`
- WPF shell with left navigation and Chat home base.
- ChatGPT-style composer with Enter to send and Shift+Enter for newline.
- Push-to-talk voice controls in the WPF chat surface.
- Temporary local WAV recording through NAudio with selectable input device support.
- Live input level meter with silence, too-quiet, usable, and clipping states.
- Persisted microphone/speaker selection and simple voice input presets.
- Local-only Faster-Whisper STT wrapper with no-speech confidence filtering.
- Editable transcript review before sending a voice prompt.
- Local-only Piper TTS wrapper using copied `lib\voice` resources.
- Stop speaking control for NAudio WAV playback.
- Speech response cleanup so URLs, code blocks, logs, and markdown clutter are not read aloud.
- Streaming bootstrap response from a local deterministic runtime stub.
- Optional local OpenAI-compatible runtime adapter with explicit health check activation.
- OpenAI-compatible image payloads for local vision-capable models.
- Runtime Settings panel with load, save, check, activate, stub revert, and last-known-good revert.
- Stop/cancel support for active response streaming.
- Evidence status labels.
- `Flag as incorrect` button on assistant answers.
- File-backed correction queue for bootstrap validation, including screenshot misread category routing.
- Voice-origin correction metadata for transcript, STT provider/mode, TTS provider/voice, and raw-audio retention state.
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

Current first proof model:

```text
qwen3:14b
```

This proves Ali's local text runtime path. It is not the final Ali model decision.

Current first proof vision model:

```text
qwen3-vl:8b
```

This proves Ali's local vision-model configuration path. It is not the final Ali vision model decision.

## Optional Local Voice Setup

Phase 1C does not use cloud speech. Ali records temporary WAV files locally and only calls local executables configured by environment variables.

Developer speech resources are copied under:

```text
lib\voice
```

Use the local helper script as the starting point:

```powershell
.\tools\voice\ALI_LOCAL_VOICE_ENV.example.ps1
```

Installed proof resources include 26 en-US Piper voices and Faster-Whisper caches for `tiny.en`, `base.en`, `small.en`, `medium.en`, and `large-v3`.

Whisper-style STT:

```powershell
$env:ALI_WHISPER_EXE = "C:\path\to\whisper-cli.exe"
$env:ALI_WHISPER_MODEL = "C:\path\to\model.bin"
```

Optional STT argument template:

```powershell
$env:ALI_WHISPER_ARGS = "-m ""{model}"" -f ""{audio}"" -otxt -of ""{outputBase}"""
```

Piper-style TTS:

```powershell
$env:ALI_PIPER_EXE = "C:\path\to\piper.exe"
$env:ALI_PIPER_MODEL = "C:\path\to\voice.onnx"
$env:ALI_PIPER_VOICE = "local-piper-voice"
```

Optional TTS argument template:

```powershell
$env:ALI_PIPER_ARGS = "--model ""{model}"" --output_file ""{output}"""
```

If those tools are not configured, Ali shows that honestly in the voice status area and skips local STT/TTS rather than pretending.

Voice status note: the local mic -> STT -> qwen3:14b -> Piper -> stop-speaking -> correction-metadata chain has been proven mechanically. Guarded voice-command reliability still needs microphone path tuning before it is considered field-certified.

Voice settings are stored in:

```text
%LOCALAPPDATA%\Ali\BootstrapData\voice-settings.json
```

Input presets are intentionally simple: `Raw`, `Quiet Room`, `Noisy Room`, `Broadcast Mic / Close Mic`, and `Headset Mic`. The meter should move when the selected microphone receives speech, drop in quiet rooms, warn when the signal is too quiet, and warn if input clips.

## KISS Rules

- Keep Ali local-first.
- Keep WPF as the user interface.
- Keep commands behind deterministic C# validation.
- Keep wrong answers preserved in the correction queue.
- Keep runtime activation explicit after health checks.
- Keep dependencies boring and justified.
- Add packages only when a feature truly needs them.
