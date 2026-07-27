using System;
using System.Collections.Generic;
using System.Linq;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public static class MediaPipeBrowGeometrySelfTest
{
	public static MediaPipeBrowGeometrySelfTestResult Run()
	{
		try
		{
			VerifyGeneratedOutline("left", CreateBrow(mirror: false));
			VerifyGeneratedOutline("right", CreateBrow(mirror: true));
			Require(!MediaPipeBrowOutlineGeometry.TryValidateClosedOutline(CreatePoints((0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)), [0, 1, 2, 3], closed: false, out string _), "An open eyebrow outline passed validation.");
			Require(!MediaPipeBrowOutlineGeometry.TryValidateClosedOutline(CreatePoints((0.0, 0.0), (1.0, 1.0), (0.0, 1.0), (1.0, 0.0)), [0, 1, 2, 3], closed: true, out string failureReason2) && failureReason2.Contains("cross", StringComparison.OrdinalIgnoreCase), "A self-crossing eyebrow outline passed validation.");
			return new MediaPipeBrowGeometrySelfTestResult(Succeeded: true, "PASS: mirrored brow hulls are closed and simple; open and crossing outlines are rejected.");
		}
		catch (Exception ex)
		{
			return new MediaPipeBrowGeometrySelfTestResult(Succeeded: false, "FAIL: " + ex.Message);
		}
	}

	private static void VerifyGeneratedOutline(string name, IReadOnlyList<FaceMeshLandmarkPoint> points)
	{
		int[] candidateIndices = points.Select((FaceMeshLandmarkPoint point) => point.Index).ToArray();
		IReadOnlyList<int> readOnlyList = MediaPipeBrowOutlineGeometry.BuildClosedOutlineIndices(points, candidateIndices);
		Require(readOnlyList.Count >= 3, "The " + name + " brow did not produce a polygon.");
		Require(MediaPipeBrowOutlineGeometry.TryValidateClosedOutline(points, readOnlyList, closed: true, out string failureReason), "The " + name + " brow failed validation: " + failureReason);
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> CreateBrow(bool mirror)
	{
		return new(double, double)[10]
		{
			(0.05, 0.58),
			(0.18, 0.28),
			(0.38, 0.1),
			(0.62, 0.08),
			(0.88, 0.3),
			(0.92, 0.52),
			(0.68, 0.43),
			(0.45, 0.4),
			(0.24, 0.48),
			(0.1, 0.62)
		}.Select(delegate((double X, double Y) point, int index)
		{
			return new FaceMeshLandmarkPoint
			{
				Index = index,
				X = mirror ? 1.0 - point.X : point.X,
				Y = point.Y
			};
		}).ToArray();
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> CreatePoints(params (double X, double Y)[] points)
	{
		return points.Select(delegate((double X, double Y) point, int index)
		{
			return new FaceMeshLandmarkPoint
			{
				Index = index,
				X = point.X,
				Y = point.Y
			};
		}).ToArray();
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}
}
