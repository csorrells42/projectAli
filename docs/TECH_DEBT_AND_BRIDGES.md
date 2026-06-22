# Ali Technical Debt And Temporary Bridges

This file tracks places where Ali uses a practical bridge, shortcut, or temporary implementation so we can get the app rolling without forgetting to replace it later.

## Local Resource Rule

Status: standing rule

Rule:

- Always copy runtime resources Ali depends on into Ali's own `lib` folder.
- Do not leave Ali pointing at old project folders, Downloads, temporary paths, or another app's environment.

Reason:

- Older projects may be deleted.
- Install/repair/backup/restore need a single predictable local resource root.
- Ali must be able to explain exactly what local assets she is using.

Current examples:

- `lib/voice/python-venv`
- `lib/voice/piper`
- `lib/voice/whisper`

## Voice Runtime Bridge

Status: active temporary bridge

Current implementation:

- Ali is a C# WPF app, but Phase 1C uses a copied local Python virtual environment under `lib/voice/python-venv`.
- The Python environment runs `faster_whisper`, `ctranslate2`, `piper`, and `onnxruntime`.
- C# remains in control through local process adapters.
- No cloud STT or TTS is used.

Reason:

- This is the fastest honest path to live local voice certification using speech engines already proven on this PC.

Replace with:

- Ali-owned packaged speech runtime under `lib/voice/bin`, or
- stable .NET bindings for Whisper/Piper if they prove reliable, or
- a local service with explicit lifecycle management.

Do not forget:

- Remove dependency on a Python venv as the final production shape.
- Preserve local-only behavior.
- Preserve model/voice selection.
- Preserve voice metadata in correction reports.

## Windows Audio Capture And Playback

Status: improved, still needs UI polish

Current implementation:

- Microphone recording uses the VoiceWorkbench-proven NAudio capture path.
- Capture writes processed mono WAV audio for Whisper.
- The DSP path includes high-pass filtering, noise gate, noise suppression, de-popper, compressor, makeup gain, and limiter.
- Speech playback uses NAudio WAV playback.

Remaining work:

- Keep the NAudio microphone picker wired to settings persistence.
- Add full playback-device enumeration; speech currently uses Windows default output.
- Add a live input level meter.
- Tune DSP defaults after live testing.

## Environment Variable Speech Configuration

Status: active temporary bridge

Current implementation:

- `ALI_WHISPER_EXE`
- `ALI_WHISPER_MODEL`
- `ALI_WHISPER_ARGS`
- `ALI_PIPER_EXE`
- `ALI_PIPER_MODEL`
- `ALI_PIPER_VOICE`
- `ALI_PIPER_ARGS`

Reason:

- Keeps Phase 1C simple and avoids a premature full voice settings system.

Replace with:

- WPF voice settings panel with local path pickers, voice picker, STT model picker, test microphone, test speaker, and health diagnostics.

## Local Voice Assets Outside Git

Status: intentional local install

Current implementation:

- Piper `.onnx` voices, Faster-Whisper caches, and the copied speech venv are under `lib/voice`.
- Large binaries are ignored by Git.
- Manifests document what is installed.

Reason:

- The copied local assets total several GB and should not be committed to ordinary Git history.

Replace with:

- Installer-managed local asset install/repair/backup flow.
- Optional local model/voice import flow.

## Faster-Whisper Model Choice

Status: acceptable bridge and likely near-term default

Current implementation:

- Faster-Whisper `small.en` is the current live-gate default.
- The local wrapper defaults to no VAD for short push-to-talk clips, then rejects segments with high no-speech probability or weak average log probability.

Reason:

- It is open source, local, fast, and already installed on this PC.
- The first live Focusrite captures were rejected by VAD even though no-VAD transcription could recover speech.
- The first no-VAD pass produced a suspicious phrase with high no-speech probability, so Ali must filter suspicious STT output before it can become a command.

Revisit:

- Evaluate `medium.en` and `large-v3` for accuracy on this desktop.
- Tune VAD only after microphone gain/device selection is stable.
- Evaluate newer local ASR options such as NVIDIA Parakeet/Canary/Nemotron Speech when they are practical for Windows/local packaging.
- Keep the final choice swappable.
