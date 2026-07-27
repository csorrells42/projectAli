# SpeechToTextModule

Consumes only `AuthorizedInteractionOutput`; unauthorized audio can never reach
the transcription backend. The backend is replaceable. This first integration
uses Ali's configured local Whisper CLI and deletes transient WAV/transcript
files after each utterance. A Parakeet backend can replace it without changing
security or UI contracts.
