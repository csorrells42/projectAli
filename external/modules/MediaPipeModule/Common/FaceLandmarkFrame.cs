using System;
using System.Collections.Generic;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceLandmarkFrame
{
	private static readonly IReadOnlyDictionary<string, double> EmptyBlendshapeScores = new Dictionary<string, double>(0, StringComparer.OrdinalIgnoreCase);

	public static FaceLandmarkFrame None { get; } = new FaceLandmarkFrame();

	public bool HasFace { get; init; }

	public string Source { get; init; } = "";

	public DateTime CapturedAtUtc { get; init; }

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

	public bool LeftEyeReconstructed { get; init; }

	public bool RightEyeReconstructed { get; init; }

	public bool MouthReconstructed { get; init; }

	public bool EyeArtifactSuppressed { get; init; }

	public double HeadYawDegrees { get; init; }

	public double HeadPitchDegrees { get; init; }

	public double HeadRollDegrees { get; init; }

	public IReadOnlyDictionary<string, double> BlendshapeScores { get; init; } = EmptyBlendshapeScores;

	public double? MediaPipeEyeBlinkLeftScore { get; init; }

	public double? MediaPipeEyeBlinkRightScore { get; init; }

	public double? MediaPipeJawOpenScore { get; init; }

	public double? MediaPipeMouthCloseScore { get; init; }

	public string DenseMeshTopology { get; init; } = "";

	public IReadOnlyList<FaceMeshLandmarkPoint> DenseMeshPoints { get; init; } = Array.Empty<FaceMeshLandmarkPoint>();

	public IReadOnlyList<double> FacialTransformationMatrix { get; init; } = Array.Empty<double>();

	public IReadOnlyList<Point> FaceContour { get; init; } = Array.Empty<Point>();

	public IReadOnlyList<Point> LeftEyeContour { get; set; } = Array.Empty<Point>();

	public IReadOnlyList<Point> RightEyeContour { get; set; } = Array.Empty<Point>();

	public IReadOnlyList<Point> LeftBrowContour { get; set; } = Array.Empty<Point>();

	public IReadOnlyList<Point> RightBrowContour { get; set; } = Array.Empty<Point>();

	public IReadOnlyList<Point> OuterLipContour { get; set; } = Array.Empty<Point>();

	public IReadOnlyList<Point> InnerLipContour { get; set; } = Array.Empty<Point>();

	public IReadOnlyList<Point> JawContour { get; set; } = Array.Empty<Point>();

	public bool HasEyeContours
	{
		get
		{
			if (LeftEyeContour.Count >= 4)
			{
				return RightEyeContour.Count >= 4;
			}
			return false;
		}
	}

	public bool HasBrowContours
	{
		get
		{
			if (LeftBrowContour.Count >= 3)
			{
				return RightBrowContour.Count >= 3;
			}
			return false;
		}
	}

	public bool HasMouthContours
	{
		get
		{
			if (InnerLipContour.Count < 4)
			{
				return OuterLipContour.Count >= 4;
			}
			return true;
		}
	}

	public bool HasDenseMesh => DenseMeshPoints.Count >= 100;

	public string ConfidenceLabel
	{
		get
		{
			double num = Math.Min(TrackingConfidence, Math.Min(EyeConfidence, MouthConfidence));
			if (num >= 0.75)
			{
				return "strong";
			}
			if (num >= 0.45)
			{
				return "usable";
			}
			return "limited";
		}
	}
}
