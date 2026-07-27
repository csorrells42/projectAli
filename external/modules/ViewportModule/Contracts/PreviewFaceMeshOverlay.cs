using System;
using System.Diagnostics;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Viewports.Contracts;

/// <summary>
/// Immutable face-mesh render layer. It is independent of the standard
/// tracking and identity layer.
/// </summary>
public sealed record PreviewFaceMeshOverlay :
	IPreviewOverlay,
	IFramePipelineSnapshot,
	IDisposable
{
	public long FrameId { get; init; }

	public long CapturedAtTimestamp { get; init; }

	public DateTime CapturedAtUtc { get; init; }

	public PreviewOverlayMesh? FaceMesh { get; init; }

	public TimeSpan MaximumAge { get; init; }

	public bool IsFresh =>
		MaximumAge <= TimeSpan.Zero
		|| (CapturedAtTimestamp != 0L
			&& Stopwatch.GetElapsedTime(CapturedAtTimestamp)
				<= MaximumAge);

	public bool HasContent =>
		FaceMesh is { Points.Count: > 1 };

	public void Dispose()
	{
	}
}
