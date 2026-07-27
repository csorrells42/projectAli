using System;
using System.Collections.Generic;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Vision.Overlays;

/// <summary>
/// Pure geometry conversion from an immutable tracking result to immutable
/// renderer data. It has no camera, tracker, viewport, or thread knowledge.
/// </summary>
public static class TrackingOverlayFactory
{
	/// <summary>
	/// Anchors the single tracked-person label to the topmost point of
	/// MediaPipe's reconstructed left eyebrow. The detector box is used only
	/// when the measured brow is unavailable.
	/// </summary>
	public static PreviewOverlayRect CreateTrackedPersonLabelAnchor(
		FaceLandmarkFrame landmarkFrame,
		Rect fallbackFaceBox)
	{
		ArgumentNullException.ThrowIfNull(landmarkFrame);
		IReadOnlyList<Point> brow = landmarkFrame.LeftBrowContour;
		Point top = default;
		bool found = false;
		for (int index = 0; index < brow.Count; index++)
		{
			Point point = brow[index];
			if (!double.IsFinite(point.X)
				|| !double.IsFinite(point.Y))
			{
				continue;
			}
			if (!found
				|| point.Y < top.Y
				|| (point.Y == top.Y && point.X < top.X))
			{
				top = point;
				found = true;
			}
		}
		if (found)
		{
			return new PreviewOverlayRect(
				top.X,
				top.Y,
				top.X,
				top.Y).Clamp();
		}
		return new PreviewOverlayRect(
			fallbackFaceBox.Left,
			fallbackFaceBox.Top,
			fallbackFaceBox.Right,
			fallbackFaceBox.Bottom).Clamp();
	}

	public static PreviewTrackingOverlay Create(
		FaceFeatureDetection featureDetection,
		FaceLandmarkFrame landmarkFrame,
		long sourceTimestamp,
		TimeSpan maximumAge,
		FaceTrackingRegion? trackingRegion = null,
		bool includeFaceBox = true)
	{
		if (!featureDetection.HasFace || !landmarkFrame.HasFace)
		{
			return PreviewTrackingOverlay.Empty;
		}

		IReadOnlyList<Point> leftBrow =
			CreateBrowDisplayOutline(landmarkFrame.LeftBrowContour);
		IReadOnlyList<Point> rightBrow =
			CreateBrowDisplayOutline(landmarkFrame.RightBrowContour);
		return new PreviewTrackingOverlay
		{
			FaceBox = !includeFaceBox || trackingRegion.HasValue
				? null
				: ToPreviewOverlayRect(featureDetection.FaceBox),
			TrackingRegion = ToPreviewTrackingRegion(trackingRegion),
			FaceContour = ToPreviewOverlayPolyline(
				landmarkFrame.FaceContour,
				closed: true),
			JawContour = ToPreviewOverlayPolyline(
				landmarkFrame.JawContour,
				closed: false),
			LeftEyeContour = ToPreviewOverlayPolyline(
				landmarkFrame.LeftEyeContour,
				closed: true,
				landmarkFrame.LeftEyeReconstructed),
			RightEyeContour = ToPreviewOverlayPolyline(
				landmarkFrame.RightEyeContour,
				closed: true,
				landmarkFrame.RightEyeReconstructed),
			LeftBrowContour = ToPreviewOverlayPolyline(
				leftBrow,
				closed: true),
			RightBrowContour = ToPreviewOverlayPolyline(
				rightBrow,
				closed: true),
			OuterLipContour = ToPreviewOverlayPolyline(
				landmarkFrame.OuterLipContour,
				closed: true,
				landmarkFrame.MouthReconstructed),
			InnerLipContour = ToPreviewOverlayPolyline(
				landmarkFrame.InnerLipContour,
				closed: true,
				landmarkFrame.MouthReconstructed),
			SourceTimestamp = sourceTimestamp,
			MaximumAge = maximumAge
		};
	}

	private static PreviewOverlayPolyline? ToPreviewOverlayPolyline(
		IReadOnlyList<Point> points,
		bool closed,
		bool inferred = false)
	{
		if (points.Count < 2)
		{
			return null;
		}
		PreviewOverlayPoint[] converted =
			new PreviewOverlayPoint[points.Count];
		int count = 0;
		foreach (Point point in points)
		{
			if (double.IsFinite(point.X) && double.IsFinite(point.Y))
			{
				converted[count++] =
					new PreviewOverlayPoint(point.X, point.Y).Clamp();
			}
		}
		if (count < 2)
		{
			return null;
		}
		if (count != converted.Length)
		{
			Array.Resize(ref converted, count);
		}
		return new PreviewOverlayPolyline(converted, closed, inferred);
	}

	private static PreviewOverlayPolyline? ToPreviewTrackingRegion(
		FaceTrackingRegion? region)
	{
		if (!region.HasValue || !region.Value.IsValid)
		{
			return null;
		}
		IReadOnlyList<Point> corners = region.Value.GetCorners();
		if (corners.Count != 4)
		{
			return null;
		}
		PreviewOverlayPoint[] converted =
			new PreviewOverlayPoint[corners.Count];
		for (int index = 0; index < corners.Count; index++)
		{
			Point point = corners[index];
			if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
			{
				return null;
			}
			converted[index] = new PreviewOverlayPoint(point.X, point.Y);
		}
		return new PreviewOverlayPolyline(
			converted,
			Closed: true);
	}

	private static IReadOnlyList<Point> CreateBrowDisplayOutline(
		IReadOnlyList<Point> points)
	{
		if (points.Count < 3)
		{
			return points;
		}
		Point[] sorted = new Point[points.Count];
		int count = 0;
		foreach (Point point in points)
		{
			if (double.IsFinite(point.X) && double.IsFinite(point.Y))
			{
				sorted[count++] = point;
			}
		}
		if (count < 3)
		{
			Array.Resize(ref sorted, count);
			return sorted;
		}
		Array.Resize(ref sorted, count);
		Array.Sort(sorted, static (left, right) =>
		{
			int x = left.X.CompareTo(right.X);
			return x == 0 ? left.Y.CompareTo(right.Y) : x;
		});
		int unique = 1;
		for (int index = 1; index < sorted.Length; index++)
		{
			if (sorted[index] != sorted[unique - 1])
			{
				sorted[unique++] = sorted[index];
			}
		}
		if (unique < 3)
		{
			Array.Resize(ref sorted, unique);
			return sorted;
		}

		Point[] hull = new Point[unique * 2];
		int hullCount = 0;
		for (int index = 0; index < unique; index++)
		{
			AppendHullPoint(hull, ref hullCount, sorted[index]);
		}
		int lowerCount = hullCount;
		for (int index = unique - 2; index >= 0; index--)
		{
			while (hullCount > lowerCount
				&& hullCount >= 2
				&& Cross(
					hull[hullCount - 2],
					hull[hullCount - 1],
					sorted[index]) <= 0d)
			{
				hullCount--;
			}
			hull[hullCount++] = sorted[index];
		}
		hullCount--;
		Array.Resize(ref hull, hullCount);
		return hull;
	}

	private static void AppendHullPoint(
		Point[] hull,
		ref int count,
		Point point)
	{
		while (count >= 2
			&& Cross(hull[count - 2], hull[count - 1], point) <= 0d)
		{
			count--;
		}
		hull[count++] = point;
	}

	private static double Cross(Point origin, Point a, Point b)
	{
		return (a.X - origin.X) * (b.Y - origin.Y)
			- (a.Y - origin.Y) * (b.X - origin.X);
	}

	private static PreviewOverlayRect? ToPreviewOverlayRect(Rect? region)
	{
		if (!region.HasValue)
		{
			return null;
		}
		Rect value = region.Value;
		if (value.IsEmpty || value.Width <= 0d || value.Height <= 0d)
		{
			return null;
		}
		return new PreviewOverlayRect(
			value.Left,
			value.Top,
			value.Right,
			value.Bottom).Clamp();
	}
}
