# VoiceActivityModule

Owns the only bounded in-memory utterance buffer: 300 ms pre-roll, 500 ms end
silence, and a hard 30-second cap. It consumes current microphone blocks and
emits complete immutable utterances. It never stores an unbounded queue.
