namespace AvatarBuilder.Modules.Security;

[Flags]
public enum AttentionGrantSource
{
	None = 0,
	Visual = 1,
	WakeWord = 2,
	PushToTalk = 4
}

public sealed record TargetAuthorizationEvidence(
	bool IsVisible,
	bool IsAuthorized,
	string PersonIdentityId,
	string DisplayName,
	double VisualIdentityConfidence = 0d)
{
	public static TargetAuthorizationEvidence None { get; } =
		new(false, false, "", "Unknown");
}

public sealed record LoginEvidence(
	bool IsAvailable,
	bool IsAuthenticated,
	string PersonIdentityId,
	string DisplayName)
{
	public static LoginEvidence Unavailable { get; } =
		new(false, false, "", "Unknown");
}

public sealed record PushToTalkEvidence(
	bool IsEnabled,
	bool IsPressed);

public sealed record SecurityDecision(
	bool AllowSpeechToText,
	string Route,
	string PersonIdentityId,
	string ParticipantDisplayName,
	bool IdentityKnown,
	string Reason,
	AttentionGrantSource AttentionSource = AttentionGrantSource.None,
	string VisualPersonIdentityId = "",
	string VoicePersonIdentityId = "",
	double VisualIdentityConfidence = 0d,
	double VoiceIdentityConfidence = 0d,
	bool IdentitySignalsAgree = false);
