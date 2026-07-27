namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public enum SpeakerEnrollmentOutcome
{
	None,
	Capturing,
	Accepted,
	Rejected,
	Canceled
}

public sealed record SpeakerEnrollmentState(
	bool IsAvailable,
	bool IsActive,
	string PersonIdentityId,
	string DisplayName,
	int CapturedSampleCount,
	int RequiredSampleCount,
	string Prompt,
	string Status,
	SpeakerEnrollmentOutcome Outcome);

public sealed record SpeakerEnrollmentResult(
	bool Success,
	string Status);

public interface ISpeakerEnrollmentService
{
	SpeakerEnrollmentState GetSpeakerEnrollmentState();

	SpeakerEnrollmentResult BeginSpeakerEnrollment(
		string personIdentityId,
		string displayName);

	void CancelSpeakerEnrollment();

	SpeakerEnrollmentResult DeleteSpeakerEnrollment(
		string personIdentityId);
}
