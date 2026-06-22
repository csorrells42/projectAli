# Ali User Guide

## Current Bootstrap

Ali currently opens as a WPF desktop app with Chat as the home base.

You can:

- Type in the composer.
- Press Enter to send.
- Press Shift+Enter for a new line.
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

The current first proof model is `qwen3:14b`. It proves the local text path works; it is not the final Ali model selection.

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

The original answer is not rewritten.

## Coming Next

- Real local model endpoint setup
- Screenshot paste and local vision
- Push-to-talk voice
- Source/search controls
- Memory controls
- Backup and restore
- Simple installer with repair mode
