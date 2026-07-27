using System;
using System.Diagnostics;
using System.Windows;
using AvatarBuilder.Modules.Viewports.Contracts;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Overlays;

public sealed record PreviewOverlayStackSelfTestResult(
	bool Succeeded,
	string Detail);

public static class PreviewOverlayStackSelfTest
{
	public static PreviewOverlayStackSelfTestResult Run()
	{
		long timestamp =
			Stopwatch.GetTimestamp()
			- 10L * Stopwatch.Frequency;
		DateTime capturedAtUtc = DateTime.UtcNow;
		TimeSpan maximumAge = TimeSpan.Zero;

		PreviewTrackingOverlay tracking = new()
		{
			FrameId = 42,
			CapturedAtUtc = capturedAtUtc,
			SourceTimestamp = timestamp,
			MaximumAge = maximumAge,
			FaceBox = new PreviewOverlayRect(
				0.2d,
				0.2d,
				0.8d,
				0.8d),
			TrackedPersonBounds = new PreviewOverlayRect(
				0.2d,
				0.2d,
				0.8d,
				0.8d),
			TrackedPersonLabel = "Unknown",
			IsTrackedPersonIdentified = false
		};
		PreviewFaceMeshOverlay faceMesh = new()
		{
			FrameId = 42,
			CapturedAtTimestamp = timestamp,
			CapturedAtUtc = capturedAtUtc,
			MaximumAge = maximumAge,
			FaceMesh = new PreviewOverlayMesh(
				[
					new PreviewOverlayPoint(0.3d, 0.3d),
					new PreviewOverlayPoint(0.7d, 0.7d)
				],
				[new PreviewOverlayEdge(0, 1)],
				Array.Empty<PreviewOverlayIndexedPath>(),
				[false, false])
		};
		PreviewAttentionOverlay attention = new()
		{
			FrameId = 42,
			CapturedAtTimestamp = timestamp,
			CapturedAtUtc = capturedAtUtc,
			MaximumAge = maximumAge,
			Indicator = new PreviewAttentionIndicator(true)
		};

		PreviewOverlayStack stack = PreviewOverlayStack.Empty
			.Decorate(tracking)
			.Decorate(faceMesh)
			.Decorate(attention);
		PreviewOverlayStack faceMeshOnly =
			PreviewOverlayStack.Empty.Decorate(faceMesh);
		PreviewOverlayStack attentionOnly =
			PreviewOverlayStack.Empty.Decorate(attention);
		var selection = new PreviewOverlaySelection(
			PreviewOverlayLayers.Tracking);
		PreviewOverlayLayers capturedSelection =
			selection.EnabledLayers;
		selection.Set(
			PreviewOverlayLayers.Tracking,
			enabled: false);
		selection.Set(
			PreviewOverlayLayers.FaceMesh,
			enabled: true);

		bool commonContract =
			tracking is IPreviewOverlay
			&& faceMesh is IPreviewOverlay
			&& attention is IPreviewOverlay;
		bool decoratorOrder =
			ReferenceEquals(stack.Layer, attention)
			&& ReferenceEquals(
				stack.Next?.Layer,
				faceMesh)
			&& ReferenceEquals(
				stack.Next?.Next?.Layer,
				tracking);
		bool lookups =
			stack.TryGet(out PreviewTrackingOverlay? foundTracking)
			&& stack.TryGet(
				out PreviewFaceMeshOverlay? foundFaceMesh)
			&& stack.TryGet(out PreviewAttentionOverlay? foundAttention)
			&& ReferenceEquals(foundTracking, tracking)
			&& ReferenceEquals(foundFaceMesh, faceMesh)
			&& ReferenceEquals(foundAttention, attention);
		bool emptyWorks =
			!PreviewOverlayStack.Empty.HasContent
			&& PreviewOverlayStack.Empty.IsBoundToFrame(42)
			&& !PreviewOverlayStack.Empty.TryGet(
				out PreviewTrackingOverlay? _);
		bool frameBinding =
			stack.IsBoundToFrame(42)
			&& !stack.IsBoundToFrame(43);
		bool synchronousLayersDoNotExpire =
			tracking.IsFresh
			&& faceMesh.IsFresh
			&& attention.IsFresh;
		bool selectionIsAtomicAndFrameCapturable =
			capturedSelection
				== PreviewOverlayLayers.Tracking
			&& selection.EnabledLayers
				== PreviewOverlayLayers.FaceMesh;
		bool independentCombinations =
			faceMeshOnly.TryGet(
				out PreviewFaceMeshOverlay? faceMeshOnlyResult)
			&& ReferenceEquals(faceMeshOnlyResult, faceMesh)
			&& !faceMeshOnly.TryGet(
				out PreviewTrackingOverlay? _)
			&& attentionOnly.TryGet(
				out PreviewAttentionOverlay? attentionOnlyResult)
			&& ReferenceEquals(attentionOnlyResult, attention)
			&& !attentionOnly.TryGet(
				out PreviewTrackingOverlay? _);
		PreviewOverlayRect eyebrowAnchor =
			TrackingOverlayFactory.CreateTrackedPersonLabelAnchor(
				new FaceLandmarkFrame
				{
					HasFace = true,
					LeftBrowContour =
					[
						new Point(0.42d, 0.31d),
						new Point(0.39d, 0.27d),
						new Point(0.45d, 0.29d)
					]
				},
				new Rect(0.2d, 0.2d, 0.6d, 0.6d));
		bool labelUsesMediaPipeEyebrow =
			Math.Abs(eyebrowAnchor.Left - 0.39d) < 1e-9
			&& Math.Abs(eyebrowAnchor.Top - 0.27d) < 1e-9;
		PreviewTrackingOverlay eyeTracking = TrackingOverlayFactory.Create(
			new FaceFeatureDetection
			{
				HasFace = true,
				FaceBox = new Rect(0.2d, 0.2d, 0.6d, 0.6d)
			},
			new FaceLandmarkFrame
			{
				HasFace = true,
				LeftEyeContour =
				[
					new Point(0.3d, 0.4d),
					new Point(0.35d, 0.38d),
					new Point(0.4d, 0.4d),
					new Point(0.35d, 0.42d)
				],
				RightEyeContour =
				[
					new Point(0.6d, 0.4d),
					new Point(0.65d, 0.38d),
					new Point(0.7d, 0.4d),
					new Point(0.65d, 0.42d)
				]
			},
			42,
			TimeSpan.FromSeconds(1));
		bool standardOverlayIncludesEyeTracking =
			eyeTracking.LeftEyeContour?.Points.Count == 4
			&& eyeTracking.RightEyeContour?.Points.Count == 4;
		bool passed =
			commonContract
			&& decoratorOrder
			&& lookups
			&& emptyWorks
			&& frameBinding
			&& synchronousLayersDoNotExpire
			&& selectionIsAtomicAndFrameCapturable
			&& independentCombinations
			&& labelUsesMediaPipeEyebrow
			&& standardOverlayIncludesEyeTracking
			&& stack.HasContent;

		return new PreviewOverlayStackSelfTestResult(
			passed,
			passed
				? "PASS: exact FrameId binding keeps synchronous layers valid regardless of analysis latency; the standard overlay includes both MediaPipe eye contours; the person label uses MediaPipe's topmost reconstructed left-eyebrow point; one atomic selection controls optional layers and every stack preserves render order."
				: "FAIL: overlay selection, eye contours, label anchoring, FrameId binding, ordering, lookup, combinations, or empty-stack behavior regressed.");
	}
}
