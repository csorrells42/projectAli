using System;
using System.Collections.Generic;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public static class FaceFeatureDetectionExtensions
{
	public static FaceLandmarkFrame ToLandmarkFrame(this FaceFeatureDetection detection, DateTime capturedAtUtc)
	{
		if (!detection.HasFace)
		{
			return FaceLandmarkFrame.None;
		}
		bool flag = detection.LeftEyeBox.HasValue && detection.RightEyeBox.HasValue;
		bool flag2 = detection.MouthBox.HasValue;
		FaceLandmarkFrame obj = new FaceLandmarkFrame
		{
			HasFace = true,
			CapturedAtUtc = capturedAtUtc,
			Source = detection.Source + " landmark fallback",
			TrackingConfidence = ((detection.TrackingConfidence > 0.0) ? detection.TrackingConfidence : 0.5),
			EyeConfidence = ((detection.EyeConfidence > 0.0) ? detection.EyeConfidence : (flag ? 0.46 : 0.22)),
			MouthConfidence = ((detection.MouthConfidence > 0.0) ? detection.MouthConfidence : (flag2 ? 0.4 : 0.18)),
			EyeImageQualityAvailable = detection.EyeImageQualityAvailable,
			MouthImageQualityAvailable = detection.MouthImageQualityAvailable,
			EyeGlarePercent = detection.EyeGlarePercent,
			MouthGlarePercent = detection.MouthGlarePercent,
			EyeContrastPercent = detection.EyeContrastPercent,
			MouthContrastPercent = detection.MouthContrastPercent,
			EyeSharpnessPercent = detection.EyeSharpnessPercent,
			MouthSharpnessPercent = detection.MouthSharpnessPercent,
			EyeDarkCoveragePercent = detection.EyeDarkCoveragePercent,
			MouthDarkCoveragePercent = detection.MouthDarkCoveragePercent,
			FaceContour = ((detection.FaceContour.Count > 0) ? detection.FaceContour : CreateOvalContour(detection.FaceBox, 24))
		};
		IReadOnlyList<Point> leftEyeContour;
		Rect? leftEyeBox;
		if (detection.LeftEyeContour.Count <= 0)
		{
			leftEyeBox = detection.LeftEyeBox;
			if (leftEyeBox.HasValue)
			{
				Rect valueOrDefault = leftEyeBox.GetValueOrDefault();
				leftEyeContour = CreateEyeContour(valueOrDefault);
			}
			else
			{
				IReadOnlyList<Point> readOnlyList = Array.Empty<Point>();
				leftEyeContour = readOnlyList;
			}
		}
		else
		{
			leftEyeContour = detection.LeftEyeContour;
		}
		obj.LeftEyeContour = leftEyeContour;
		IReadOnlyList<Point> rightEyeContour;
		if (detection.RightEyeContour.Count <= 0)
		{
			leftEyeBox = detection.RightEyeBox;
			if (leftEyeBox.HasValue)
			{
				Rect valueOrDefault2 = leftEyeBox.GetValueOrDefault();
				rightEyeContour = CreateEyeContour(valueOrDefault2);
			}
			else
			{
				IReadOnlyList<Point> readOnlyList = Array.Empty<Point>();
				rightEyeContour = readOnlyList;
			}
		}
		else
		{
			rightEyeContour = detection.RightEyeContour;
		}
		obj.RightEyeContour = rightEyeContour;
		leftEyeBox = detection.LeftEyeBox;
		IReadOnlyList<Point> leftBrowContour;
		if (leftEyeBox.HasValue)
		{
			Rect valueOrDefault3 = leftEyeBox.GetValueOrDefault();
			leftBrowContour = CreateBrowContour(valueOrDefault3);
		}
		else
		{
			IReadOnlyList<Point> readOnlyList = Array.Empty<Point>();
			leftBrowContour = readOnlyList;
		}
		obj.LeftBrowContour = leftBrowContour;
		leftEyeBox = detection.RightEyeBox;
		IReadOnlyList<Point> rightBrowContour;
		if (leftEyeBox.HasValue)
		{
			Rect valueOrDefault4 = leftEyeBox.GetValueOrDefault();
			rightBrowContour = CreateBrowContour(valueOrDefault4);
		}
		else
		{
			IReadOnlyList<Point> readOnlyList = Array.Empty<Point>();
			rightBrowContour = readOnlyList;
		}
		obj.RightBrowContour = rightBrowContour;
		IReadOnlyList<Point> outerLipContour;
		if (detection.OuterLipContour.Count <= 0)
		{
			leftEyeBox = detection.MouthBox;
			if (leftEyeBox.HasValue)
			{
				Rect valueOrDefault5 = leftEyeBox.GetValueOrDefault();
				outerLipContour = CreateMouthContour(valueOrDefault5, outer: true);
			}
			else
			{
				IReadOnlyList<Point> readOnlyList = Array.Empty<Point>();
				outerLipContour = readOnlyList;
			}
		}
		else
		{
			outerLipContour = detection.OuterLipContour;
		}
		obj.OuterLipContour = outerLipContour;
		IReadOnlyList<Point> innerLipContour;
		if (detection.InnerLipContour.Count <= 0)
		{
			leftEyeBox = detection.MouthBox;
			if (leftEyeBox.HasValue)
			{
				Rect valueOrDefault6 = leftEyeBox.GetValueOrDefault();
				innerLipContour = CreateMouthContour(valueOrDefault6, outer: false);
			}
			else
			{
				IReadOnlyList<Point> readOnlyList = Array.Empty<Point>();
				innerLipContour = readOnlyList;
			}
		}
		else
		{
			innerLipContour = detection.InnerLipContour;
		}
		obj.InnerLipContour = innerLipContour;
		obj.JawContour = ((detection.JawContour.Count > 0) ? detection.JawContour : CreateJawContour(detection.FaceBox));
		return obj;
	}

	private static IReadOnlyList<Point> CreateEyeContour(Rect box)
	{
		double num = box.Left + box.Width / 2.0;
		double num2 = box.Top + box.Height / 2.0;
		double num3 = box.Width * 0.5;
		double num4 = box.Height * 0.34;
		return
		[
			new Point(num - num3, num2),
			new Point(num - num3 * 0.55, num2 - num4),
			new Point(num, num2 - num4 * 1.05),
			new Point(num + num3 * 0.55, num2 - num4),
			new Point(num + num3, num2),
			new Point(num + num3 * 0.55, num2 + num4),
			new Point(num, num2 + num4 * 1.05),
			new Point(num - num3 * 0.55, num2 + num4)
		];
	}

	private static IReadOnlyList<Point> CreateMouthContour(Rect box, bool outer)
	{
		double num = box.Left + box.Width / 2.0;
		double num2 = box.Top + box.Height * (outer ? 0.52 : 0.56);
		double num3 = box.Width * (outer ? 0.5 : 0.36);
		double num4 = box.Height * (outer ? 0.24 : 0.13);
		return
		[
			new Point(num - num3, num2),
			new Point(num - num3 * 0.5, num2 - num4),
			new Point(num, num2 - num4 * 1.1),
			new Point(num + num3 * 0.5, num2 - num4),
			new Point(num + num3, num2),
			new Point(num + num3 * 0.5, num2 + num4),
			new Point(num, num2 + num4 * 1.1),
			new Point(num - num3 * 0.5, num2 + num4)
		];
	}

	private static IReadOnlyList<Point> CreateBrowContour(Rect eyeBox)
	{
		double num = eyeBox.Left + eyeBox.Width / 2.0;
		double num2 = Math.Clamp(eyeBox.Top - eyeBox.Height * 0.5, 0.0, 1.0);
		double num3 = eyeBox.Width * 0.52;
		return
		[
			new Point(Math.Clamp(num - num3, 0.0, 1.0), num2 + eyeBox.Height * 0.05),
			new Point(Math.Clamp(num - num3 * 0.45, 0.0, 1.0), num2 - eyeBox.Height * 0.03),
			new Point(num, num2 - eyeBox.Height * 0.06),
			new Point(Math.Clamp(num + num3 * 0.45, 0.0, 1.0), num2 - eyeBox.Height * 0.03),
			new Point(Math.Clamp(num + num3, 0.0, 1.0), num2 + eyeBox.Height * 0.05)
		];
	}

	private static IReadOnlyList<Point> CreateOvalContour(Rect box, int count)
	{
		List<Point> list = new List<Point>(count);
		double num = box.Left + box.Width / 2.0;
		double num2 = box.Top + box.Height / 2.0;
		for (int i = 0; i < count; i++)
		{
			double num3 = Math.PI * 2.0 * (double)i / (double)count;
			list.Add(new Point(num + Math.Cos(num3) * box.Width * 0.5, num2 + Math.Sin(num3) * box.Height * 0.5));
		}
		return list;
	}

	private static IReadOnlyList<Point> CreateJawContour(Rect face)
	{
		return
		[
			new Point(face.Left + face.Width * 0.12, face.Top + face.Height * 0.62),
			new Point(face.Left + face.Width * 0.22, face.Top + face.Height * 0.8),
			new Point(face.Left + face.Width * 0.5, face.Bottom),
			new Point(face.Left + face.Width * 0.78, face.Top + face.Height * 0.8),
			new Point(face.Left + face.Width * 0.88, face.Top + face.Height * 0.62)
		];
	}
}
