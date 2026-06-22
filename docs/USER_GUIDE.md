# Ali User Guide

## Current Bootstrap

Ali currently opens as a WPF desktop app with Chat as the home base.

You can:

- Type in the composer.
- Press Enter to send.
- Press Shift+Enter for a new line.
- Paste a copied screenshot or image with `Ctrl+V`.
- Use `Paste Image` when the clipboard contains an image.
- Use `Capture Screen` to attach a full-screen screenshot.
- Preview and remove image attachments before sending.
- Check `Retain` on an attachment only when you want Ali to keep it after the current send.
- Use `Push to Talk` to start local voice recording.
- Use `Stop Recording` to stop recording and ask Ali to transcribe locally.
- Review or edit the transcript before clicking `Send Transcript`.
- Use `Stop Speaking` to stop local spoken playback.
- Stop an active response.
- Flag an assistant answer as incorrect.

The first runtime is a safe local bootstrap stub. It exists to prove the app flow and correction queue before a real local model is activated.

## Local Runtime Check

Ali can now be pointed at a local OpenAI-compatible runtime, such as Ollama running on this PC.

Ali does not silently switch. Use the Runtime Settings panel:

1. Check `Enable local OpenAI-compatible runtime`.
2. Set the endpoint, usually `http://127.0.0.1:11434/v1/`.
3. Set the model/package ID exactly as the local runtime reports it.
4. Keep context at `4096` for the safe first run.
5. Click `Save settings`.
6. Click `Check Runtime`.
7. Click `Activate Runtime` only after the check passes.

If the check fails, Ali keeps the safe bootstrap stub active and reports the failure.

Ali refuses public/cloud runtime endpoints in local-only mode.

Use `Revert to Stub` any time you want to return to the deterministic local test runtime.

The current first proof text model is `qwen3:14b`. It proves the local text path works; it is not the final Ali model selection.

The current first proof vision model is `qwen3-vl:8b`. It proves the local screenshot/image path works; it is not the final Ali vision model selection.

## Voice

Phase 1C voice is local-only.

Ali records a temporary WAV file from the Windows default microphone. The raw audio is deleted after transcription unless a future retention setting is explicitly added.

Ali can use a local Whisper-style command-line transcriber when configured:

```powershell
$env:ALI_WHISPER_EXE = "C:\path\to\whisper-cli.exe"
$env:ALI_WHISPER_MODEL = "C:\path\to\model.bin"
```

Ali can use a local Piper-style command-line speaker when configured:

```powershell
$env:ALI_PIPER_EXE = "C:\path\to\piper.exe"
$env:ALI_PIPER_MODEL = "C:\path\to\voice.onnx"
```

If local STT or TTS is not configured, Ali says so in the voice status area. She does not use cloud speech and does not pretend a transcript or spoken response succeeded.

Voice can ask ordinary chat questions. Voice cannot yet run commands, change models, edit calendars, install things, delete memories, or do destructive actions. Those spoken requests are blocked in this phase and require visible typed confirmation later.

## Important Truth Rule

Ali must not claim a model, command, build, test, reminder, calendar event, or file change succeeded unless there is evidence.

If Ali does not know, she should say so.

## Correction Queue

Use `Flag as incorrect` when an answer is wrong or unsupported.

Ali preserves:

- The exact question
- The exact answer
- Model profile metadata
- Evidence status
- The correction category
- Voice transcript and local STT/TTS metadata when the answer came from voice

The original answer is not rewritten.

If the flagged answer used a screenshot or image attachment, Ali routes it as a screenshot/image misread correction.

Raw voice audio is not stored in the correction queue.

## Coming Next

- Real local model endpoint setup
- Real local Whisper/Piper install picker
- Source/search controls
- Memory controls
- Backup and restore
- Simple installer with repair mode
