using AvatarBuilder.Modules.Security;

namespace AvatarBuilder.Modules.Confidence;

public sealed record InteractionConfidenceRecord(
	long SequenceId,
	DateTime UtteranceCapturedAtUtc,
	DateTime TranscribedAtUtc,
	AttentionGrantSource AttentionSources,
	string Transcript,
	string TranscriptionProvider,
	string TranscriptionStatus,
	string ParticipantIdentityId,
	string ParticipantDisplayName,
	string VisualIdentityId,
	double VisualIdentityConfidence,
	string VoiceIdentityId,
	double VoiceIdentityConfidence,
	bool IdentitySignalsAgree,
	string SecurityReason,
	int AudioSampleRate,
	TimeSpan AudioDuration,
	byte[] AudioWav);
