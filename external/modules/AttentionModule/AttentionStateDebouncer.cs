using System;
using System.Diagnostics;

namespace AvatarBuilder.Modules.Vision.Attention;

internal sealed class AttentionStateDebouncer
{
	private readonly long _stableDurationTicks;
	private bool _publishedState;
	private bool? _pendingState;
	private long _pendingSinceTimestamp;

	public AttentionStateDebouncer(TimeSpan stableDuration)
	{
		if (stableDuration < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(stableDuration));
		}
		_stableDurationTicks = (long)Math.Ceiling(
			stableDuration.TotalSeconds * Stopwatch.Frequency);
	}

	public bool Update(bool observedState, long timestamp)
	{
		if (_publishedState == observedState)
		{
			_pendingState = null;
			_pendingSinceTimestamp = 0;
			return _publishedState;
		}
		if (_pendingState != observedState)
		{
			_pendingState = observedState;
			_pendingSinceTimestamp = timestamp;
			return _publishedState;
		}
		if (Math.Max(0, timestamp - _pendingSinceTimestamp)
			< _stableDurationTicks)
		{
			return _publishedState;
		}
		_publishedState = observedState;
		_pendingState = null;
		_pendingSinceTimestamp = 0;
		return _publishedState;
	}
}
