# Ali Local Voice Resources

This folder holds local-only speech assets for Ali.

Large binary resources are intentionally not tracked by Git:

- `python-venv/`: local Python speech runtime copied from the older Olivia project.
- `piper/*.onnx` and `piper/*.onnx.json`: local Piper voice models.
- `whisper/models--Systran--faster-whisper-*/`: local Faster-Whisper model caches.

Current local install summary:

- Piper en_US voices installed: 26 model/config pairs.
- Faster-Whisper caches installed: tiny.en, base.en, small.en, medium.en, large-v3.
- Default proof voice: `en_US-hfc_female-medium`.

These are local resources only. Ali should not use cloud STT or cloud TTS.
