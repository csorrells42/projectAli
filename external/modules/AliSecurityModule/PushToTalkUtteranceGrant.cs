using System.Diagnostics;
using System.Threading;
using AvatarBuilder.Modules.Audio.VoiceActivity;

namespace AvatarBuilder.Modules.Security;

internal sealed class PushToTalkUtteranceGrant
{
	private sealed record Interval(
		bool IsPressed,
		long PressedAtTimestamp,
		long ReleasedAtTimestamp);

	private Interval _latest = new(false, 0, 0);

	internal void Update(bool enabled, bool pressed, long timestamp)
	{
		Interval current = Volatile.Read(ref _latest);
		bool active = enabled && pressed;
		if (active)
		{
			if (!current.IsPressed)
			{
				Volatile.Write(ref _latest,
					new Interval(true, timestamp, 0));
			}
			return;
		}
		if (current.IsPressed)
		{
			Volatile.Write(ref _latest,
				new Interval(false, current.PressedAtTimestamp, timestamp));
		}
	}

	internal bool Overlaps(UtteranceOutput utterance)
	{
		long durationTicks = (long)Math.Ceiling(
			utterance.Duration.TotalSeconds * Stopwatch.Frequency);
		return Overlaps(
			Math.Max(0, utterance.ProducedAtTimestamp - durationTicks),
			utterance.ProducedAtTimestamp);
	}

	internal bool Overlaps(long utteranceStart, long utteranceEnd)
	{
		Interval interval = Volatile.Read(ref _latest);
		if (interval.PressedAtTimestamp == 0)
		{
			return false;
		}
		long intervalEnd = interval.IsPressed
			? long.MaxValue
			: interval.ReleasedAtTimestamp;
		return interval.PressedAtTimestamp <= utteranceEnd
			&& intervalEnd >= utteranceStart;
	}
}
