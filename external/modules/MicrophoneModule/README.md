# MicrophoneModule

Captures 16 kHz mono PCM and publishes immutable 20 ms sample blocks. It has no
VAD, wake-word, speaker, security, transcription, UI, or Ali knowledge.

It exposes active Windows capture endpoints, nonblocking latest signal level,
capture status, and explicit input selection. Switching remains entirely
inside this module and preserves the same immutable output contract.
