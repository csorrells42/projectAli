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

1. Set the endpoint, usually `http://127.0.0.1:11434/v1/`.
2. Select the installed model/package ID exactly as the local runtime reports it.
3. Keep conservative development settings unless deliberately testing a larger profile.
4. Click `Check`.
5. Click `Activate` only after the check passes.

If the check fails, Ali keeps the safe bootstrap stub active and reports the failure.

Ali refuses public/cloud runtime endpoints in local-only mode.

Use `Revert to Stub` any time you want to return to the deterministic local test runtime.

The current certified local proof model is `qwen3-vl:8b` through Ollama's OpenAI-compatible endpoint. The development profile used for the latest certification was:

- Endpoint: `http://127.0.0.1:11434/v1/`
- Model: `qwen3-vl:8b`
- Quantization: Ollama package default / lowest Ali runtime settings
- Context: `2048`
- Output: `128`
- Temperature: `0`
- Top-p: `0.1`
- Streaming: enabled

`qwen3-vl:8b` can emit a separate Ollama reasoning stream before final answer content. Ali hides that reasoning in normal chat. If the model spends the whole low output budget on hidden reasoning, Ali reports that no visible assistant content arrived instead of exposing the reasoning text.

`qwen3:14b` was removed from this development machine to keep the system responsive.

## Voice

Phase 1C voice is local-only.

Ali records a temporary WAV file from the selected local microphone. The raw audio is deleted after transcription unless a retention setting or validation flag explicitly keeps it.

The current local voice resources live under Ali's own `lib\voice` folder:

- `lib\voice\python-venv`
- `lib\voice\whisper`
- `lib\voice\piper`

For this developer build, configure the local speech environment from:

```powershell
.\tools\voice\ALI_LOCAL_VOICE_ENV.example.ps1
```

Ali uses a local Faster-Whisper wrapper for STT and a local Piper wrapper for TTS. The STT wrapper writes confidence metadata and rejects suspicious no-speech/low-confidence segments instead of turning noise into a command.

Installed local proof resources: 26 en-US Piper voices and Faster-Whisper caches for `tiny.en`, `base.en`, `small.en`, `medium.en`, and `large-v3`.

If local STT or TTS is not configured, Ali says so in the voice status area. She does not use cloud speech and does not pretend a transcript or spoken response succeeded.

The voice settings popup includes:

- Microphone picker
- Channel picker
- Gain control
- Live input meter
- Capture diagnostics
- Piper voice picker and sample playback

The main chat surface is chat-first: conversation list on the left, conversation content in the center, and one bottom composer for typed text, image attachment, mic dictation, hands-free voice mode, stop response, and send.

The composer mic records local speech and places the accepted transcript into the chat bar. It does not send until Enter or Send is pressed. The voice mode button toggles hands-free behavior, where accepted transcripts are sent automatically. Risky command transcripts still require visible confirmation and are blocked in this phase.

Meter states:

- `No speech signal detected`: selected mic is silent, muted, or not receiving signal.
- `Input is too quiet`: Ali hears something, but STT may not be reliable.
- `Input level looks usable`: good candidate for live certification.
- `Input is clipping`: lower gain or choose a calmer preset.

Input presets:

- `Raw`: minimal processing.
- `Quiet Room`: light cleanup and moderate gain.
- `Noisy Room`: stronger gate/noise suppression.
- `Broadcast Mic / Close Mic`: close microphone shaping.
- `Headset Mic`: practical boosted default for headset-style mics.

Voice settings persist in `%LOCALAPPDATA%\Ali\BootstrapData\voice-settings.json`. If a saved mic disappears, Ali shows a warning and falls back visibly instead of silently pretending the same mic is still active.

Current voice certification status: live voice, microphone, Piper playback, and Stop Speaking are not certified in the current V1 runtime pass. Chris must be present for that hardware/audio certification. Logic-level safety tests exist, but that is not the same as live voice certification.

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

- Installer-managed signing/repair so Smart App Control accepts refreshed Ali binaries without manual certificate work
- Owner visual review
- Live voice/mic/Piper certification with Chris present
- Real local Whisper/Piper install picker
- Source/search controls
- Memory controls
- Backup and restore
- Simple installer with repair mode
