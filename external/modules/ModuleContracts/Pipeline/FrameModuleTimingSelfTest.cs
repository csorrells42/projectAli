using System;

namespace AvatarBuilder.Modules.Pipeline;

public sealed record FrameModuleTimingSelfTestResult(
	bool Succeeded,
	string Detail);

public static class FrameModuleTimingSelfTest
{
	public static FrameModuleTimingSelfTestResult Run()
	{
		var timing = new FrameModuleTiming();
		timing.WorkStarted(100L);
		timing.FrameMovedOut(140L);
		timing.WorkStarted(200L);
		timing.FrameMovedOut(260L);

		bool passed =
			timing.TimeWaited
				== FrameModuleTiming.ToTimeSpan(60L)
			&& timing.TimeWorked
				== FrameModuleTiming.ToTimeSpan(60L);

		return new FrameModuleTimingSelfTestResult(
			passed,
			passed
				? "PASS: each module publicly exposes only its newest time waited and time worked."
				: "FAIL: module-owned waited/worked timing arithmetic regressed.");
	}
}
