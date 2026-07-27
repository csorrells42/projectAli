using System;
using System.Diagnostics;

namespace AvatarBuilder.Modules.Vision.Attention;

public static class AttentionModuleSelfTest
{
	public static string Run()
	{
		var debouncer = new AttentionStateDebouncer(
			TimeSpan.FromMilliseconds(250));
		long start = Stopwatch.GetTimestamp();
		long Ms(double value) => (long)Math.Ceiling(
			value / 1000d * Stopwatch.Frequency);
		if (debouncer.Update(true, start)
			|| debouncer.Update(true, start + Ms(249))
			|| !debouncer.Update(true, start + Ms(250)))
		{
			throw new InvalidOperationException(
				"Attention did not require 250 ms of stable evidence.");
		}
		if (!AttentionPosePolicy.IsAccepted(0d, 30d, 0d)
			|| !AttentionPosePolicy.IsAccepted(0d, -30d, 0d)
			|| AttentionPosePolicy.IsAccepted(0d, 30.01d, 0d)
			|| AttentionPosePolicy.IsAccepted(22.01d, 0d, 0d)
			|| AttentionPosePolicy.IsAccepted(0d, 0d, 24.01d))
		{
			throw new InvalidOperationException(
				"Attention pose limits do not match the approved policy.");
		}
		return "PASS: official MediaPipe head pose accepts a 30 degree monitor pitch while preserving yaw, roll, and the 250 ms debounce.";
	}
}
