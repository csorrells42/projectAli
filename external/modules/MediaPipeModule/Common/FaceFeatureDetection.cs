using System;
using System.Collections.Generic;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceFeatureDetection
{
	public static FaceFeatureDetection None { get; } = new FaceFeatureDetection();

	public bool HasFace { get; init; }

	public Rect FaceBox { get; init; }

	public Rect? LeftEyeBox { get; init; }

	public Rect? RightEyeBox { get; init; }

	public Rect? MouthBox { get; init; }

	public IReadOnlyList<Point> FaceContour { get; init; } = Array.Empty<Point>();

	public IReadOnlyList<Point> LeftEyeContour { get; init; } = Array.Empty<Point>();

	public IReadOnlyList<Point> RightEyeContour { get; init; } = Array.Empty<Point>();

	public IReadOnlyList<Point> OuterLipContour { get; init; } = Array.Empty<Point>();

	public IReadOnlyList<Point> InnerLipContour { get; init; } = Array.Empty<Point>();

	public IReadOnlyList<Point> JawContour { get; init; } = Array.Empty<Point>();

	public double TrackingConfidence { get; init; }

	public double EyeConfidence { get; init; }

	public double MouthConfidence { get; init; }

	public bool EyeImageQualityAvailable { get; init; }

	public bool MouthImageQualityAvailable { get; init; }

	public double EyeGlarePercent { get; init; }

	public double MouthGlarePercent { get; init; }

	public double EyeContrastPercent { get; init; }

	public double MouthContrastPercent { get; init; }

	public double EyeSharpnessPercent { get; init; }

	public double MouthSharpnessPercent { get; init; }

	public double EyeDarkCoveragePercent { get; init; }

	public double MouthDarkCoveragePercent { get; init; }

	public string Source { get; init; } = "";

	public FaceCueGuideLayout ToGuideLayout(FaceCueGuideLayout fallback)
	{
		if (!HasFace || FaceBox.Width <= 0.0 || FaceBox.Height <= 0.0)
		{
			return fallback;
		}
		return new FaceCueGuideLayout((FaceBox.Left + FaceBox.Width / 2.0) * 100.0, (FaceBox.Top + FaceBox.Height / 2.0) * 100.0, FaceBox.Height * 100.0);
	}
}
