namespace AvatarBuilder.Modules.Vision.IdentityEnrollment;

public sealed record IdentityEnrollmentGuidanceSelfTestResult(
	bool Succeeded,
	string Detail);

public static class IdentityEnrollmentGuidanceSelfTest
{
	public static IdentityEnrollmentGuidanceSelfTestResult Run()
	{
		bool passed =
			EnrollmentPoseMatcher.Matches(0, 0d, 0d, 0d)
			&& !EnrollmentPoseMatcher.Matches(0, 15d, 0d, 0d)
			&& EnrollmentPoseMatcher.Matches(1, 18d, 0d, 0d)
			&& !EnrollmentPoseMatcher.Matches(1, -18d, 0d, 0d)
			&& EnrollmentPoseMatcher.Matches(2, -18d, 0d, 0d)
			&& EnrollmentPoseMatcher.Matches(3, 0d, -16d, 0d)
			&& EnrollmentPoseMatcher.Matches(4, 0d, 16d, 0d)
			&& !EnrollmentPoseMatcher.Matches(4, 0d, -16d, 0d)
			&& EnrollmentPoseMatcher.PromptFor(0).Contains("camera")
			&& EnrollmentPoseMatcher.CountdownFor(3) == "Three."
			&& EnrollmentPoseMatcher.CountdownFor(2) == "Two."
			&& EnrollmentPoseMatcher.CountdownFor(1) == "One."
			&& EnrollmentPoseMatcher.PromptFor(5).Contains("complete");
		return new IdentityEnrollmentGuidanceSelfTestResult(
			passed,
			passed
				? "All five official-pose acceptance regions are distinct."
				: "A guided-enrollment pose acceptance rule failed.");
	}
}
