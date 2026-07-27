# InteractionConfidenceModule

Independent latest-value subscriber for the exact `TranscriptionOutput` that
Ali consumes. It writes one current-format SQLite row per Security-approved
utterance, including the transcript, immutable bounded audio as a PCM16 WAV
blob, attention sources, and separate visual and voice identity confidence.

The module owns its database worker and one-slot subscription. It cannot call,
wait for, modify, or back-pressure Security, speech-to-text, Ali, camera, or
vision producers.
