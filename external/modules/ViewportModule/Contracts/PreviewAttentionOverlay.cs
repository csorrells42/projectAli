using System;
using System.Diagnostics;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Viewports.Contracts;

/// <summary>
/// Immutable always-on attention-indicator render layer.
/// </summary>
public sealed record PreviewAttentionOverlay :
	IPreviewOverlay,
	IFramePipelineSnapshot,
	IDisposable
{
	public static PreviewAttentionOverlay Empty { get; } = new();

	public long FrameId { get; init; }

	public long CapturedAtTimestamp { get; init; }

	public DateTime CapturedAtUtc { get; init; }

	public PreviewAttentionIndicator? Indicator { get; init; }

	public TimeSpan MaximumAge { get; init; }

	public bool IsFresh =>
		MaximumAge <= TimeSpan.Zero
		|| (CapturedAtTimestamp != 0L
			&& Stopwatch.GetElapsedTime(CapturedAtTimestamp) <= MaximumAge);

	public bool HasContent => Indicator.HasValue;

	public void Dispose()
	{
	}
}
