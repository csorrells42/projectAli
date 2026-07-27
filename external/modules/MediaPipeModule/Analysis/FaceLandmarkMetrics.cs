using System;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkMetrics
{
	public static FaceLandmarkMetrics None { get; } = new FaceLandmarkMetrics();

	public bool HasFace { get; init; }

	public string Source { get; init; } = "";

	public string ConfidenceLabel { get; init; } = "none";

	public DateTime CapturedAtUtc { get; init; }

	public double TrackingConfidence { get; init; }

	public double EyeConfidence { get; init; }

	public double MouthConfidence { get; init; }

	public double EyeMeasurementQualityPercent { get; init; }

	public double MouthMeasurementQualityPercent { get; init; }

	public double BrowMeasurementQualityPercent { get; init; }

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

	public double? RawEyeAsymmetryPercent { get; init; }

	public double? EyeAsymmetryPercent { get; init; }

	public double EyeAgreementPercent
	{
		get
		{
			double? eyeAsymmetryPercent = EyeAsymmetryPercent;
			if (eyeAsymmetryPercent.HasValue)
			{
				double valueOrDefault = eyeAsymmetryPercent.GetValueOrDefault();
				return Math.Clamp(100.0 - valueOrDefault, 0.0, 100.0);
			}
			return 0.0;
		}
	}

	public bool PossibleOneEyeArtifact { get; init; }

	public bool LeftEyeReconstructed { get; init; }

	public bool RightEyeReconstructed { get; init; }

	public bool MouthReconstructed { get; init; }

	public bool EyeArtifactSuppressed { get; init; }

	public bool AnyEyeReconstructed
	{
		get
		{
			if (!LeftEyeReconstructed)
			{
				return RightEyeReconstructed;
			}
			return true;
		}
	}

	public double OverallMeasurementQualityPercent
	{
		get
		{
			if (!HasFace)
			{
				return 0.0;
			}
			bool hasValue = AverageEyeOpeningRatio.HasValue;
			bool hasValue2 = MouthOpeningRatio.HasValue;
			if (hasValue)
			{
				if (hasValue2)
				{
					return EyeMeasurementQualityPercent * 0.72 + MouthMeasurementQualityPercent * 0.28;
				}
				return EyeMeasurementQualityPercent;
			}
			if (hasValue2)
			{
				return MouthMeasurementQualityPercent * 0.75;
			}
			return 0.0;
		}
	}

	public bool IsEyeMeasurementUsable
	{
		get
		{
			if (AverageEyeOpeningRatio.HasValue)
			{
				return EyeMeasurementQualityPercent >= 42.0;
			}
			return false;
		}
	}

	public bool IsMouthMeasurementUsable
	{
		get
		{
			if (MouthOpeningRatio.HasValue)
			{
				return MouthMeasurementQualityPercent >= 40.0;
			}
			return false;
		}
	}

	public bool IsJawDroopMeasurementUsable
	{
		get
		{
			if (JawDroopRatio.HasValue && MouthMeasurementQualityPercent >= 38.0)
			{
				return TrackingConfidence >= 0.35;
			}
			return false;
		}
	}

	public bool IsBrowMeasurementUsable
	{
		get
		{
			if (AverageBrowHeightRatio.HasValue)
			{
				return BrowMeasurementQualityPercent >= 42.0;
			}
			return false;
		}
	}

	public string MeasurementQualityLabel
	{
		get
		{
			double overallMeasurementQualityPercent = OverallMeasurementQualityPercent;
			if (overallMeasurementQualityPercent >= 75.0)
			{
				return "strong";
			}
			if (overallMeasurementQualityPercent >= 55.0)
			{
				return "usable";
			}
			if (overallMeasurementQualityPercent >= 35.0)
			{
				return "limited";
			}
			return "low";
		}
	}

	public double? RawLeftEyeOpeningRatio { get; init; }

	public double? RawRightEyeOpeningRatio { get; init; }

	public double? RawAverageEyeOpeningRatio { get; init; }

	public double? RawMouthOpeningRatio { get; init; }

	public double? RawJawDroopRatio { get; init; }

	public double? RawLeftBrowHeightRatio { get; init; }

	public double? RawRightBrowHeightRatio { get; init; }

	public double? RawAverageBrowHeightRatio { get; init; }

	public double? LeftEyeOpeningRatio { get; init; }

	public double? RightEyeOpeningRatio { get; init; }

	public double? AverageEyeOpeningRatio { get; init; }

	public double? MouthOpeningRatio { get; init; }

	public double? MouthOpeningVelocityPerSecond { get; init; }

	public double? JawDroopRatio { get; init; }

	public double? JawDroopVelocityPerSecond { get; init; }

	public double? LeftBrowHeightRatio { get; init; }

	public double? RightBrowHeightRatio { get; init; }

	public double? AverageBrowHeightRatio { get; init; }

	public double? BrowHeightVelocityPerSecond { get; init; }

	public double? BrowAsymmetryPercent { get; init; }

	public double? MediaPipeLeftEyeBlinkPercent { get; init; }

	public double? MediaPipeRightEyeBlinkPercent { get; init; }

	public double? MediaPipeAverageEyeBlinkPercent { get; init; }

	public double? MediaPipeJawOpenPercent { get; init; }

	public double? MediaPipeMouthClosePercent { get; init; }

	public double? MediaPipeEyeOpeningCorrectionRatio { get; init; }

	public double? MediaPipeMouthOpeningCorrectionRatio { get; init; }

	public bool MediaPipeEyeOpeningCorrected => MediaPipeEyeOpeningCorrectionRatio.HasValue;

	public bool MediaPipeMouthOpeningCorrected => MediaPipeMouthOpeningCorrectionRatio.HasValue;

	public bool HasMediaPipeBlendshapeEvidence
	{
		get
		{
			if (!MediaPipeAverageEyeBlinkPercent.HasValue && !MediaPipeJawOpenPercent.HasValue)
			{
				return MediaPipeMouthClosePercent.HasValue;
			}
			return true;
		}
	}

	public double HeadYawDegrees { get; init; }

	public double HeadPitchDegrees { get; init; }

	public double HeadRollDegrees { get; init; }

	public string Status
	{
		get
		{
			if (!HasFace)
			{
				return "landmarks waiting";
			}
			double? averageEyeOpeningRatio = AverageEyeOpeningRatio;
			object obj;
			if (averageEyeOpeningRatio.HasValue)
			{
				double valueOrDefault = averageEyeOpeningRatio.GetValueOrDefault();
				obj = $"eyes {valueOrDefault * 100.0:0}%";
			}
			else
			{
				obj = "eyes --";
			}
			string value = (string)obj;
			averageEyeOpeningRatio = MouthOpeningRatio;
			object obj2;
			if (averageEyeOpeningRatio.HasValue)
			{
				double valueOrDefault2 = averageEyeOpeningRatio.GetValueOrDefault();
				obj2 = $"mouth {valueOrDefault2 * 100.0:0}%";
			}
			else
			{
				obj2 = "mouth --";
			}
			string value2 = (string)obj2;
			averageEyeOpeningRatio = JawDroopRatio;
			object obj3;
			if (averageEyeOpeningRatio.HasValue)
			{
				double valueOrDefault3 = averageEyeOpeningRatio.GetValueOrDefault();
				obj3 = $", jaw drop {valueOrDefault3 * 100.0:0}%";
			}
			else
			{
				obj3 = "";
			}
			string value3 = (string)obj3;
			averageEyeOpeningRatio = AverageBrowHeightRatio;
			object obj4;
			if (averageEyeOpeningRatio.HasValue)
			{
				double valueOrDefault4 = averageEyeOpeningRatio.GetValueOrDefault();
				obj4 = $", brow {valueOrDefault4 * 100.0:0}%";
			}
			else
			{
				obj4 = "";
			}
			string value4 = (string)obj4;
			string value5 = ((EyeAsymmetryPercent is double) ? $", eye agreement {EyeAgreementPercent:0}%" : "");
			string value6 = (PossibleOneEyeArtifact ? ", possible one-eye artifact" : "");
			string value7 = ((AnyEyeReconstructed || MouthReconstructed || EyeArtifactSuppressed) ? ", reconstruction used" : "");
			string value8 = (HasMediaPipeBlendshapeEvidence ? $", mp blink {MediaPipeAverageEyeBlinkPercent?.ToString("0") ?? "--"}%, jaw {MediaPipeJawOpenPercent?.ToString("0") ?? "--"}%" : "");
			string value9 = ((MediaPipeEyeOpeningCorrected || MediaPipeMouthOpeningCorrected) ? (", mp correction eye " + FormatSigned(MediaPipeEyeOpeningCorrectionRatio) + ", mouth " + FormatSigned(MediaPipeMouthOpeningCorrectionRatio)) : "");
			return $"landmarks {MeasurementQualityLabel}: {value}, {value2}{value3}{value4}, q {OverallMeasurementQualityPercent:0}%{value5}{value6}{value7}{value8}{value9}";
		}
	}

	private static string FormatSigned(double? value)
	{
		if (!value.HasValue)
		{
			return "--";
		}
		return value.GetValueOrDefault().ToString("+0.###;-0.###;0");
	}
}
