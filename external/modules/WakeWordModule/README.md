# WakeWordModule

Consumes utterances and emits dynamic `Hey <assistant name>` evidence. The
assistant name can change at runtime. An unavailable model reports no wake
event; it never guesses from unrelated audio.

The module owns the packaged Sherpa open-vocabulary keyword-spotting model,
loads the official English pronunciation lexicon, and converts the current
`Hey <assistant name>` phrase into the phoneme-token format required by Sherpa.
No machine-specific environment variables are required.
