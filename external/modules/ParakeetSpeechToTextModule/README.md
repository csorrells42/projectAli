# ParakeetSpeechToTextModule

Drop-in authorized-utterance speech-to-text module using sherpa-onnx and
NVIDIA Parakeet TDT 0.6B v2 int8. It implements the shared
`ISpeechToTextModule` contract and publishes the same immutable
`TranscriptionOutput` as `SpeechToTextModule` (Whisper).

The module does not capture audio, decide authorization, call another module,
or touch UI. It owns one latest-value worker and processes only outputs already
published by `AliSecurityModule`.

Model discovery order:

1. Constructor `modelFolder`
2. `ALI_PARAKEET_MODEL`
3. `dependencies/audio/parakeet/<model-name>` beside the executable
4. `models/<model-name>` beside the executable

The model files are intentionally ignored by Git.
