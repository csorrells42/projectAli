# SpeakerRecognitionModule

Consumes complete utterances and publishes speaker evidence. Voice evidence is
never treated as sufficient authorization by itself. The inference backend is
replaceable; an unconfigured backend returns Unknown rather than fabricating a
match.

The official 3D-Speaker English CAMPPlus model is discovered from an explicit
path, `ALI_SHERPA_SPEAKER_MODEL`, or the packaged
`dependencies/audio/speaker-identification` folder. Voice enrollment is
explicit, requires three completed utterances, is keyed by the registered
UserId, and persists one averaged embedding. The module never learns unknown
speakers passively.
