using System;
using System.Diagnostics;
using System.Threading;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Per-module timing state. The owning module calls WorkStarted when it accepts
/// a frame and FrameMovedOut when it publishes that frame. No controller or
/// neighboring module writes these measurements.
/// </summary>
internal sealed class FrameModuleTiming
{
	private long _previousFrameOutTimestamp;

	private long _workStartedTimestamp;

	private long _lastWaitTimestampTicks;

	private long _lastWorkTimestampTicks;

	internal TimeSpan TimeWaited =>
		ToTimeSpan(Interlocked.Read(ref _lastWaitTimestampTicks));

	internal TimeSpan TimeWorked =>
		ToTimeSpan(Interlocked.Read(ref _lastWorkTimestampTicks));

	/// <summary>
	/// Called by the owning module at the instant it accepts its next frame.
	/// </summary>
	internal void WorkStarted(long currentTimestamp)
	{
		long previousFrameOut =
			Volatile.Read(ref _previousFrameOutTimestamp);
		long waited =
			previousFrameOut == 0L
				? 0L
				: Math.Max(0L, currentTimestamp - previousFrameOut);
		Volatile.Write(ref _workStartedTimestamp, currentTimestamp);
		Interlocked.Exchange(ref _lastWaitTimestampTicks, waited);
	}

	/// <summary>
	/// Called by the owning module at the instant it moves the completed frame
	/// into its output slot.
	/// </summary>
	internal void FrameMovedOut(long currentTimestamp)
	{
		long workStarted =
			Volatile.Read(ref _workStartedTimestamp);
		if (workStarted == 0L)
		{
			throw new InvalidOperationException(
				"FrameMovedOut requires a matching WorkStarted call.");
		}

		long worked = Math.Max(0L, currentTimestamp - workStarted);
		Interlocked.Exchange(ref _lastWorkTimestampTicks, worked);
		Volatile.Write(
			ref _previousFrameOutTimestamp,
			currentTimestamp);
		Volatile.Write(ref _workStartedTimestamp, 0L);
	}

	internal static TimeSpan ToTimeSpan(long timestampTicks)
	{
		return timestampTicks <= 0L
			? TimeSpan.Zero
			: TimeSpan.FromSeconds(
				(double)timestampTicks / Stopwatch.Frequency);
	}
}
