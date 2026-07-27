using System;
using System.Collections.Generic;
using System.Diagnostics;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Viewports.Contracts;

public sealed record PreviewTrackingOverlay :
	IPreviewOverlay,
	IFramePipelineSnapshot,
	IDisposable
{
	public static PreviewTrackingOverlay Empty { get; } = new PreviewTrackingOverlay();

	public PreviewOverlayRect? FaceBox { get; init; }

	public PreviewOverlayPolyline? TrackingRegion { get; init; }

	public PreviewOverlayPolyline? FaceContour { get; init; }

	public PreviewOverlayPolyline? JawContour { get; init; }

	public PreviewOverlayPolyline? LeftEyeContour { get; init; }

	public PreviewOverlayPolyline? RightEyeContour { get; init; }

	public PreviewOverlayPolyline? LeftBrowContour { get; init; }

	public PreviewOverlayPolyline? RightBrowContour { get; init; }

	public PreviewOverlayPolyline? OuterLipContour { get; init; }

	public PreviewOverlayPolyline? InnerLipContour { get; init; }

	public PreviewOverlayMesh? FaceMesh { get; init; }

	/// <summary>
	/// The single label associated with MediaPipe's tracked face. Identity
	/// contributes only the confirmed name; the bounds remain MediaPipe's.
	/// </summary>
	public string TrackedPersonLabel { get; init; } = "";

	public PreviewOverlayRect? TrackedPersonBounds { get; init; }

	public bool IsTrackedPersonIdentified { get; init; }

	public IReadOnlyList<PreviewOverlayDiagnosticMesh> DiagnosticMeshes { get; init; } = Array.Empty<PreviewOverlayDiagnosticMesh>();

	public long FrameId { get; init; }

	public long CapturedAtTimestamp => SourceTimestamp;

	public DateTime CapturedAtUtc { get; init; }

	public long SourceTimestamp { get; init; }

	public TimeSpan MaximumAge { get; init; }

	public bool IsFresh
	{
		get
		{
			return MaximumAge <= TimeSpan.Zero
				|| (SourceTimestamp != 0L && Stopwatch.GetElapsedTime(SourceTimestamp) <= MaximumAge);
		}
	}

	public bool HasContent
	{
		get
		{
			if (!FaceBox.HasValue && TrackingRegion is null && FaceContour is null && JawContour is null && LeftEyeContour is null && RightEyeContour is null && LeftBrowContour is null && RightBrowContour is null && OuterLipContour is null && InnerLipContour is null && FaceMesh is null && (!TrackedPersonBounds.HasValue || string.IsNullOrWhiteSpace(TrackedPersonLabel)))
			{
				return DiagnosticMeshes.Count > 0;
			}
			return true;
		}
	}

	public void Dispose()
	{
	}
}
