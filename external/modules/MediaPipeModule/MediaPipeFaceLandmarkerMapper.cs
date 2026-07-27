using System;
using System.Collections.Generic;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal static class MediaPipeFaceLandmarkerMapper
{
	private static readonly int[] FaceOval = new int[36]
	{
		10, 338, 297, 332, 284, 251, 389, 356, 454, 323,
		361, 288, 397, 365, 379, 378, 400, 377, 152, 148,
		176, 149, 150, 136, 172, 58, 132, 93, 234, 127,
		162, 21, 54, 103, 67, 109
	};

	private static readonly int[] EyeA = new int[16]
	{
		33, 246, 161, 160, 159, 158, 157, 173, 133, 155,
		154, 153, 145, 144, 163, 7
	};

	private static readonly int[] EyeB = new int[16]
	{
		362, 398, 384, 385, 386, 387, 388, 466, 263, 249,
		390, 373, 374, 380, 381, 382
	};

	private static readonly int[] OuterLip = new int[20]
	{
		61, 185, 40, 39, 37, 0, 267, 269, 270, 409,
		291, 375, 321, 405, 314, 17, 84, 181, 91, 146
	};

	private static readonly int[] InnerLip = new int[20]
	{
		78, 191, 80, 81, 82, 13, 312, 311, 310, 415,
		308, 324, 318, 402, 317, 14, 87, 178, 88, 95
	};

	private static readonly int[] Jaw = new int[21]
	{
		234, 93, 132, 58, 172, 136, 150, 149, 176, 148,
		152, 377, 400, 378, 379, 365, 397, 288, 361, 323,
		454
	};

	public static FaceLandmarkTrackingResult ToTrackingResult(
		MediaPipeSidecarResponse response,
		DateTime capturedAtUtc,
		string backendName,
		int frameWidth,
		int frameHeight,
		FaceTrackingRegion? trackingRegion = null)
	{
		if (!response.Ok)
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = backendName,
				BackendStatus = response.Status
			};
		}
		if (!response.HasFace || response.Landmarks.Count < 468)
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = backendName,
				BackendStatus = (string.IsNullOrWhiteSpace(response.Status) ? "MediaPipe sidecar searching" : response.Status)
			};
		}
		Point[] readOnlyList = Select(response.Landmarks, FaceOval);
		Point[] first = Select(response.Landmarks, EyeA);
		Point[] second = Select(response.Landmarks, EyeB);
		(Point[] Left, Point[] Right) tuple = SortEyesByFramePosition(first, second);
		Point[] item = tuple.Left;
		Point[] item2 = tuple.Right;
		Point[] first2 = Select(response.Landmarks, MediaPipeBrowOutlineGeometry.BrowAIndices);
		Point[] second2 = Select(response.Landmarks, MediaPipeBrowOutlineGeometry.BrowBIndices);
		(Point[] Left, Point[] Right) tuple2 = SortEyesByFramePosition(first2, second2);
		Point[] item3 = tuple2.Left;
		Point[] item4 = tuple2.Right;
		Point[] readOnlyList2 = Select(response.Landmarks, OuterLip);
		Point[] readOnlyList3 = Select(response.Landmarks, InnerLip);
		Point[] jawContour = Select(response.Landmarks, Jaw);
		Rect? rect = GetOfficialFaceBox(response);
		IReadOnlyList<double> facialTransformationMatrix =
			response.FacialTransformationMatrix.Count >= 16
				? response.FacialTransformationMatrix
				: Array.Empty<double>();
		(double, double, double) tuple3 = EstimatePoseDegrees(
			facialTransformationMatrix);
		double officialDetectionConfidence = Math.Clamp(
			response.FaceDetectionScore ?? 0d,
			0d,
			1d);
		FaceLandmarkFrame faceLandmarkFrame = new FaceLandmarkFrame
		{
			HasFace = true,
			Source = "MediaPipe Face Landmarker sidecar",
			CapturedAtUtc = capturedAtUtc,
			TrackingConfidence = officialDetectionConfidence,
			EyeConfidence = officialDetectionConfidence,
			MouthConfidence = officialDetectionConfidence,
			HeadYawDegrees = tuple3.Item1,
			HeadPitchDegrees = tuple3.Item2,
			HeadRollDegrees = tuple3.Item3,
			MediaPipeEyeBlinkLeftScore = response.EyeBlinkLeftScore,
			MediaPipeEyeBlinkRightScore = response.EyeBlinkRightScore,
			MediaPipeJawOpenScore = response.JawOpenScore,
			MediaPipeMouthCloseScore = response.MouthCloseScore,
			DenseMeshTopology = "MediaPipeFaceMesh468",
			DenseMeshPoints = CreateDenseMeshPoints(response.Landmarks),
			FacialTransformationMatrix = facialTransformationMatrix,
			FaceContour = readOnlyList,
			LeftEyeContour = item,
			RightEyeContour = item2,
			LeftBrowContour = item3,
			RightBrowContour = item4,
			OuterLipContour = readOnlyList2,
			InnerLipContour = readOnlyList3,
			JawContour = jawContour
		};
		FaceFeatureDetection featureDetection = new FaceFeatureDetection
		{
			HasFace = true,
			Source = faceLandmarkFrame.Source,
			FaceBox = (rect ?? new Rect(0.0, 0.0, 0.0, 0.0)),
			LeftEyeBox = BoundingRect(item),
			RightEyeBox = BoundingRect(item2),
			MouthBox = BoundingRect((readOnlyList2.Length > 0) ? readOnlyList2 : readOnlyList3),
			TrackingConfidence = faceLandmarkFrame.TrackingConfidence,
			EyeConfidence = faceLandmarkFrame.EyeConfidence,
			MouthConfidence = faceLandmarkFrame.MouthConfidence,
			FaceContour = faceLandmarkFrame.FaceContour,
			LeftEyeContour = faceLandmarkFrame.LeftEyeContour,
			RightEyeContour = faceLandmarkFrame.RightEyeContour,
			OuterLipContour = faceLandmarkFrame.OuterLipContour,
			InnerLipContour = faceLandmarkFrame.InnerLipContour,
			JawContour = faceLandmarkFrame.JawContour
		};
		return new FaceLandmarkTrackingResult
		{
			BackendName = backendName,
			BackendStatus = (string.IsNullOrWhiteSpace(response.Status) ? "MediaPipe dense landmark lock" : response.Status),
			FeatureDetection = featureDetection,
			TrackingRegion = trackingRegion,
			LandmarkFrame = faceLandmarkFrame,
			Diagnostics = response.Diagnostics
		};
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> CreateDenseMeshPoints(IReadOnlyList<MediaPipeSidecarLandmark> landmarks)
	{
		FaceMeshLandmarkPoint[] points = new FaceMeshLandmarkPoint[landmarks.Count];
		for (int i = 0; i < landmarks.Count; i++)
		{
			MediaPipeSidecarLandmark mediaPipeSidecarLandmark = landmarks[i];
			points[i] = new FaceMeshLandmarkPoint
			{
				Index = i,
				X = Math.Clamp(mediaPipeSidecarLandmark.X, 0.0, 1.0),
				Y = Math.Clamp(mediaPipeSidecarLandmark.Y, 0.0, 1.0),
				Z = mediaPipeSidecarLandmark.Z
			};
		}
		return points;
	}

	private static Point[] Select(IReadOnlyList<MediaPipeSidecarLandmark> landmarks, IReadOnlyList<int> indices)
	{
		Point[] points = new Point[indices.Count];
		int count = 0;
		for (int i = 0; i < indices.Count; i++)
		{
			int index = indices[i];
			if (index >= 0 && index < landmarks.Count)
			{
				MediaPipeSidecarLandmark mediaPipeSidecarLandmark = landmarks[index];
				points[count++] = new Point(Math.Clamp(mediaPipeSidecarLandmark.X, 0.0, 1.0), Math.Clamp(mediaPipeSidecarLandmark.Y, 0.0, 1.0));
			}
		}
		if (count == points.Length)
		{
			return points;
		}
		Array.Resize(ref points, count);
		return points;
	}

	private static (Point[] Left, Point[] Right) SortEyesByFramePosition(Point[] first, Point[] second)
	{
		double num = ((first.Length == 0) ? 0.0 : MeanX(first));
		double num2 = ((second.Length == 0) ? 1.0 : MeanX(second));
		if (!(num <= num2))
		{
			return (Left: second, Right: first);
		}
		return (Left: first, Right: second);
	}

	private static (double YawDegrees, double PitchDegrees, double RollDegrees) EstimatePoseDegrees(
		IReadOnlyList<double> facialTransformationMatrix)
	{
		if (TryEstimatePoseFromMatrix(
			facialTransformationMatrix,
			out (double, double, double) pose))
		{
			return pose;
		}
		return (
			YawDegrees: 0d,
			PitchDegrees: 0d,
			RollDegrees: 0d);
	}

	private static bool TryEstimatePoseFromMatrix(IReadOnlyList<double> values, out (double YawDegrees, double PitchDegrees, double RollDegrees) pose)
	{
		pose = default((double, double, double));
		if (values.Count < 16)
		{
			return false;
		}
		for (int i = 0; i < values.Count; i++)
		{
			if (!double.IsFinite(values[i]))
			{
				return false;
			}
		}
		double y = values[2];
		double y2 = values[4];
		double x = values[5];
		double num = values[6];
		double x2 = values[10];
		double value = Math.Atan2(y, x2) * 180.0 / Math.PI;
		double value2 = Math.Asin(Math.Clamp(0.0 - num, -1.0, 1.0)) * 180.0 / Math.PI;
		double value3 = Math.Atan2(y2, x) * 180.0 / Math.PI;
		if (Math.Abs(value) > 80.0 || Math.Abs(value2) > 70.0 || Math.Abs(value3) > 80.0)
		{
			return false;
		}
		pose = (YawDegrees: Math.Clamp(value, -55.0, 55.0), PitchDegrees: Math.Clamp(value2, -45.0, 45.0), RollDegrees: Math.Clamp(value3, -55.0, 55.0));
		return true;
	}

	private static Rect? BoundingRect(IReadOnlyList<Point> points)
	{
		if (points.Count == 0)
		{
			return null;
		}
		Point point = points[0];
		double num = point.X;
		double num2 = point.X;
		double num3 = point.Y;
		double num4 = point.Y;
		for (int i = 1; i < points.Count; i++)
		{
			Point point2 = points[i];
			num = Math.Min(num, point2.X);
			num2 = Math.Max(num2, point2.X);
			num3 = Math.Min(num3, point2.Y);
			num4 = Math.Max(num4, point2.Y);
		}
		if (!(num2 <= num) && !(num4 <= num3))
		{
			return new Rect(num, num3, num2 - num, num4 - num3);
		}
		return null;
	}

	private static Rect? GetOfficialFaceBox(
		MediaPipeSidecarResponse response)
	{
		if (!response.FaceBoxX.HasValue
			|| !response.FaceBoxY.HasValue
			|| !response.FaceBoxWidth.HasValue
			|| !response.FaceBoxHeight.HasValue)
		{
			return null;
		}
		double left = Math.Clamp(response.FaceBoxX.Value, 0d, 1d);
		double top = Math.Clamp(response.FaceBoxY.Value, 0d, 1d);
		double right = Math.Clamp(
			response.FaceBoxX.Value + response.FaceBoxWidth.Value,
			0d,
			1d);
		double bottom = Math.Clamp(
			response.FaceBoxY.Value + response.FaceBoxHeight.Value,
			0d,
			1d);
		return right > left && bottom > top
			? new Rect(left, top, right - left, bottom - top)
			: null;
	}

	private static double EstimateRollDegrees(IReadOnlyList<Point> leftEye, IReadOnlyList<Point> rightEye)
	{
		if (leftEye.Count == 0 || rightEye.Count == 0)
		{
			return 0.0;
		}
		Point point = Center(leftEye);
		Point point2 = Center(rightEye);
		return Math.Atan2(point2.Y - point.Y, point2.X - point.X) * 180.0 / Math.PI;
	}

	private static Point Center(IReadOnlyList<Point> points)
	{
		double num = 0.0;
		double num2 = 0.0;
		foreach (Point point in points)
		{
			num += point.X;
			num2 += point.Y;
		}
		return new Point(num / (double)points.Count, num2 / (double)points.Count);
	}

	private static double MeanX(IReadOnlyList<Point> points)
	{
		double num = 0.0;
		foreach (Point point in points)
		{
			num += point.X;
		}
		return num / (double)points.Count;
	}

}
