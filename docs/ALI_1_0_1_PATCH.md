# Ali 1.0.1 Patch

Date: 2026-06-27

Ali 1.0.1 repairs first-field-install issues found after Ali 1.0.

- Voice resources now repair the bundled `python-venv` to use a voice-local `python-runtime` under `DevRun\lib\voice`, not a user-specific Python install path.
- The installer accepts a small `Ali.VoicePatch.zip` sidecar for existing installs, so broken voice resources can be repaired without copying the full multi-GB voice pack again.
- KittenTTS and Whisper bridge scripts are installed under `lib\voice` and are also published with the app payload.
- Existing voice settings are repaired to prefer installed DevRun voice resources while preserving microphone/output preferences.
- Curated Sources & Topics now merge missing built-in starter sources into partial or old catalogs without removing user-added sources.
