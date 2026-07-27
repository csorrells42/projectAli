namespace AvatarBuilder.Modules.Vision.Attention;

internal static class AttentionPosePolicy
{
	internal const double MaximumAbsoluteYawDegrees = 22d;
	internal const double MaximumAbsolutePitchDegrees = 30d;
	internal const double MaximumAbsoluteRollDegrees = 24d;

	internal static bool IsAccepted(
		double yawDegrees,
		double pitchDegrees,
		double rollDegrees) =>
		double.IsFinite(yawDegrees)
		&& double.IsFinite(pitchDegrees)
		&& double.IsFinite(rollDegrees)
		&& Math.Abs(yawDegrees) <= MaximumAbsoluteYawDegrees
		&& Math.Abs(pitchDegrees) <= MaximumAbsolutePitchDegrees
		&& Math.Abs(rollDegrees) <= MaximumAbsoluteRollDegrees;
}
