namespace AvatarBuilder.Modules.Vision.IdentityEnrollment;

public sealed record IdentityEnrollmentGuidanceState(
	bool IsActive,
	bool HasFace,
	bool PoseConfirmed,
	int CapturedPoseCount,
	int RequiredPoseCount,
	double HeadYawDegrees,
	double HeadPitchDegrees,
	double HeadRollDegrees,
	string Prompt,
	string Status,
	string CompletedIdentityId)
{
	public static IdentityEnrollmentGuidanceState Waiting { get; } = new(
		false,
		false,
		false,
		0,
		0,
		0d,
		0d,
		0d,
		"",
		"Guided enrollment is waiting.",
		"");
}
