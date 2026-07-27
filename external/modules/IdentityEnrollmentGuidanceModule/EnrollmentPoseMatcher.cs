using System;

namespace AvatarBuilder.Modules.Vision.IdentityEnrollment;

internal static class EnrollmentPoseMatcher
{
	public const int RequiredPoseCount = 5;

	public static bool Matches(
		int poseIndex,
		double yawDegrees,
		double pitchDegrees,
		double rollDegrees)
	{
		if (!double.IsFinite(yawDegrees)
			|| !double.IsFinite(pitchDegrees)
			|| !double.IsFinite(rollDegrees)
			|| Math.Abs(rollDegrees) > 15d)
		{
			return false;
		}

		return poseIndex switch
		{
			0 => Math.Abs(yawDegrees) <= 9d
				&& Math.Abs(pitchDegrees) <= 9d,
			1 => yawDegrees >= 13d
				&& yawDegrees <= 38d
				&& Math.Abs(pitchDegrees) <= 15d,
			2 => yawDegrees <= -13d
				&& yawDegrees >= -38d
				&& Math.Abs(pitchDegrees) <= 15d,
			3 => pitchDegrees <= -10d
				&& pitchDegrees >= -30d
				&& Math.Abs(yawDegrees) <= 15d,
			4 => pitchDegrees >= 10d
				&& pitchDegrees <= 30d
				&& Math.Abs(yawDegrees) <= 15d,
			_ => false
		};
	}

	public static string PromptFor(int poseIndex) => poseIndex switch
	{
		0 => "Please look at the camera.",
		1 => "Turn your head slightly toward your left shoulder.",
		2 => "Turn your head slightly toward your right shoulder.",
		3 => "Raise your chin slightly.",
		4 => "Lower your chin slightly.",
		_ => "Enrollment complete."
	};

	public static string CountdownFor(int value) => value switch
	{
		3 => "Three.",
		2 => "Two.",
		1 => "One.",
		_ => ""
	};
}
