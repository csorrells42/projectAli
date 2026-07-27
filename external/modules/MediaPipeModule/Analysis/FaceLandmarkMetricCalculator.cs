using System;
using System.Collections.Generic;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkMetricCalculator
{
	private double? _previousMouthOpeningRatio;

	private double? _previousJawDroopRatio;

	private double? _previousAverageBrowHeightRatio;

	private double? _smoothedLeftEyeOpeningRatio;

	private double? _smoothedRightEyeOpeningRatio;

	private double? _smoothedAverageEyeOpeningRatio;

	private double? _smoothedMouthOpeningRatio;

	private double? _smoothedJawDroopRatio;

	private double? _smoothedLeftBrowHeightRatio;

	private double? _smoothedRightBrowHeightRatio;

	private double? _smoothedAverageBrowHeightRatio;

	private double? _mediaPipeOpenEyeReferenceRatio;

	private double? _mediaPipeClosedMouthReferenceRatio;

	private DateTime? _previousCapturedAtUtc;

	public FaceLandmarkMetrics Update(FaceLandmarkFrame frame)
	{
		if (!frame.HasFace)
		{
			Reset();
			return FaceLandmarkMetrics.None;
		}
		bool highFidelityLandmarkSource = IsHighFidelityLandmarkSource(frame.Source);
		double? num = ContourOpeningEstimator.CalculateOpeningRatio(frame.LeftEyeContour, ShouldUsePairedAverage(frame, frame.LeftEyeContour, isEye: true, highFidelityLandmarkSource));
		double? num2 = ContourOpeningEstimator.CalculateOpeningRatio(frame.RightEyeContour, ShouldUsePairedAverage(frame, frame.RightEyeContour, isEye: true, highFidelityLandmarkSource));
		double? num3 = Average(num, num2);
		double? num4 = ScorePercent(frame.MediaPipeEyeBlinkLeftScore) ?? BlendshapePercent(frame, "eyeBlinkLeft");
		double? num5 = ScorePercent(frame.MediaPipeEyeBlinkRightScore) ?? BlendshapePercent(frame, "eyeBlinkRight");
		double? num6 = Average(num4, num5);
		UpdateMediaPipeOpenEyeReference(frame, num3, num6, highFidelityLandmarkSource);
		double? rawEyeAsymmetryPercent = CalculateAsymmetryPercent(num, num2, num3);
		bool possibleOneEyeArtifact = IsPossibleOneEyeArtifact(frame, rawEyeAsymmetryPercent, highFidelityLandmarkSource);
		double? num7 = StabilizeEyeOpeningWithMediaPipe(num, num4 ?? num6);
		double? num8 = StabilizeEyeOpeningWithMediaPipe(num2, num5 ?? num6);
		double? averageEyeOpening = Average(num7, num8) ?? StabilizeEyeOpeningWithMediaPipe(num3, num6);
		(double? Left, double? Right, double? Average) tuple = ApplyLowFidelityEyeOpeningGuard(frame, num7, num8, averageEyeOpening, num6, highFidelityLandmarkSource);
		num7 = tuple.Left;
		num8 = tuple.Right;
		averageEyeOpening = tuple.Average;
		double? mediaPipeEyeOpeningCorrectionRatio = CalculateCorrection(averageEyeOpening, num3);
		IReadOnlyList<Point> contour = ((frame.InnerLipContour.Count >= 4) ? frame.InnerLipContour : frame.OuterLipContour);
		double? num9 = ContourOpeningEstimator.CalculateOpeningRatio(contour, ShouldUsePairedAverage(frame, contour, isEye: false, highFidelityLandmarkSource));
		double? mediaPipeJawOpenPercent = ScorePercent(frame.MediaPipeJawOpenScore) ?? BlendshapePercent(frame, "jawOpen");
		double? mediaPipeMouthClosePercent = ScorePercent(frame.MediaPipeMouthCloseScore) ?? BlendshapePercent(frame, "mouthClose");
		UpdateMediaPipeClosedMouthReference(frame, num9, mediaPipeJawOpenPercent, mediaPipeMouthClosePercent, highFidelityLandmarkSource);
		double? num10 = StabilizeMouthOpeningWithMediaPipe(num9, mediaPipeJawOpenPercent, mediaPipeMouthClosePercent);
		double? mediaPipeMouthOpeningCorrectionRatio = CalculateCorrection(num10, num9);
		double? num11 = CalculateJawDroopRatio(frame, mediaPipeJawOpenPercent);
		(double?, double?, double?, double?) tuple2 = CalculateBrowHeight(frame);
		double eyeMeasurementQualityPercent = CalculateEyeMeasurementQuality(frame, num7, num8, averageEyeOpening, possibleOneEyeArtifact);
		double mouthMeasurementQualityPercent = CalculateMouthMeasurementQuality(frame, num10);
		double browMeasurementQualityPercent = CalculateBrowMeasurementQuality(frame, tuple2.Item3, tuple2.Item4);
		double amount = CalculateSmoothingFactor(frame);
		_smoothedLeftEyeOpeningRatio = Smooth(_smoothedLeftEyeOpeningRatio, num7, amount);
		_smoothedRightEyeOpeningRatio = Smooth(_smoothedRightEyeOpeningRatio, num8, amount);
		_smoothedAverageEyeOpeningRatio = Smooth(_smoothedAverageEyeOpeningRatio, averageEyeOpening, amount);
		_smoothedMouthOpeningRatio = Smooth(_smoothedMouthOpeningRatio, num10, amount);
		_smoothedJawDroopRatio = Smooth(_smoothedJawDroopRatio, num11, amount);
		_smoothedLeftBrowHeightRatio = Smooth(_smoothedLeftBrowHeightRatio, tuple2.Item1, amount);
		_smoothedRightBrowHeightRatio = Smooth(_smoothedRightBrowHeightRatio, tuple2.Item2, amount);
		_smoothedAverageBrowHeightRatio = Smooth(_smoothedAverageBrowHeightRatio, tuple2.Item3, amount);
		double? mouthOpeningVelocityPerSecond = CalculateVelocity(frame.CapturedAtUtc, _smoothedMouthOpeningRatio, _previousMouthOpeningRatio);
		double? jawDroopVelocityPerSecond = CalculateVelocity(frame.CapturedAtUtc, _smoothedJawDroopRatio, _previousJawDroopRatio);
		double? browHeightVelocityPerSecond = CalculateVelocity(frame.CapturedAtUtc, _smoothedAverageBrowHeightRatio, _previousAverageBrowHeightRatio);
		double? eyeAsymmetryPercent = CalculateAsymmetryPercent(_smoothedLeftEyeOpeningRatio, _smoothedRightEyeOpeningRatio, _smoothedAverageEyeOpeningRatio);
		double? browAsymmetryPercent = CalculateAsymmetryPercent(_smoothedLeftBrowHeightRatio, _smoothedRightBrowHeightRatio, _smoothedAverageBrowHeightRatio);
		_previousMouthOpeningRatio = _smoothedMouthOpeningRatio;
		_previousJawDroopRatio = _smoothedJawDroopRatio;
		_previousAverageBrowHeightRatio = _smoothedAverageBrowHeightRatio;
		_previousCapturedAtUtc = frame.CapturedAtUtc;
		return new FaceLandmarkMetrics
		{
			HasFace = true,
			Source = frame.Source,
			ConfidenceLabel = frame.ConfidenceLabel,
			CapturedAtUtc = frame.CapturedAtUtc,
			TrackingConfidence = frame.TrackingConfidence,
			EyeConfidence = frame.EyeConfidence,
			MouthConfidence = frame.MouthConfidence,
			EyeMeasurementQualityPercent = eyeMeasurementQualityPercent,
			MouthMeasurementQualityPercent = mouthMeasurementQualityPercent,
			BrowMeasurementQualityPercent = browMeasurementQualityPercent,
			EyeImageQualityAvailable = frame.EyeImageQualityAvailable,
			MouthImageQualityAvailable = frame.MouthImageQualityAvailable,
			EyeGlarePercent = frame.EyeGlarePercent,
			MouthGlarePercent = frame.MouthGlarePercent,
			EyeContrastPercent = frame.EyeContrastPercent,
			MouthContrastPercent = frame.MouthContrastPercent,
			EyeSharpnessPercent = frame.EyeSharpnessPercent,
			MouthSharpnessPercent = frame.MouthSharpnessPercent,
			EyeDarkCoveragePercent = frame.EyeDarkCoveragePercent,
			MouthDarkCoveragePercent = frame.MouthDarkCoveragePercent,
			RawEyeAsymmetryPercent = rawEyeAsymmetryPercent,
			EyeAsymmetryPercent = eyeAsymmetryPercent,
			PossibleOneEyeArtifact = possibleOneEyeArtifact,
			LeftEyeReconstructed = frame.LeftEyeReconstructed,
			RightEyeReconstructed = frame.RightEyeReconstructed,
			MouthReconstructed = frame.MouthReconstructed,
			EyeArtifactSuppressed = frame.EyeArtifactSuppressed,
			RawLeftEyeOpeningRatio = num,
			RawRightEyeOpeningRatio = num2,
			RawAverageEyeOpeningRatio = num3,
			RawMouthOpeningRatio = num9,
			RawJawDroopRatio = num11,
			RawLeftBrowHeightRatio = tuple2.Item1,
			RawRightBrowHeightRatio = tuple2.Item2,
			RawAverageBrowHeightRatio = tuple2.Item3,
			LeftEyeOpeningRatio = _smoothedLeftEyeOpeningRatio,
			RightEyeOpeningRatio = _smoothedRightEyeOpeningRatio,
			AverageEyeOpeningRatio = _smoothedAverageEyeOpeningRatio,
			MouthOpeningRatio = _smoothedMouthOpeningRatio,
			MouthOpeningVelocityPerSecond = mouthOpeningVelocityPerSecond,
			JawDroopRatio = _smoothedJawDroopRatio,
			JawDroopVelocityPerSecond = jawDroopVelocityPerSecond,
			LeftBrowHeightRatio = _smoothedLeftBrowHeightRatio,
			RightBrowHeightRatio = _smoothedRightBrowHeightRatio,
			AverageBrowHeightRatio = _smoothedAverageBrowHeightRatio,
			BrowHeightVelocityPerSecond = browHeightVelocityPerSecond,
			BrowAsymmetryPercent = browAsymmetryPercent,
			MediaPipeLeftEyeBlinkPercent = num4,
			MediaPipeRightEyeBlinkPercent = num5,
			MediaPipeAverageEyeBlinkPercent = num6,
			MediaPipeJawOpenPercent = mediaPipeJawOpenPercent,
			MediaPipeMouthClosePercent = mediaPipeMouthClosePercent,
			MediaPipeEyeOpeningCorrectionRatio = mediaPipeEyeOpeningCorrectionRatio,
			MediaPipeMouthOpeningCorrectionRatio = mediaPipeMouthOpeningCorrectionRatio,
			HeadYawDegrees = frame.HeadYawDegrees,
			HeadPitchDegrees = frame.HeadPitchDegrees,
			HeadRollDegrees = frame.HeadRollDegrees
		};
	}

	public void Reset()
	{
		_previousMouthOpeningRatio = null;
		_previousJawDroopRatio = null;
		_previousAverageBrowHeightRatio = null;
		_smoothedLeftEyeOpeningRatio = null;
		_smoothedRightEyeOpeningRatio = null;
		_smoothedAverageEyeOpeningRatio = null;
		_smoothedMouthOpeningRatio = null;
		_smoothedJawDroopRatio = null;
		_smoothedLeftBrowHeightRatio = null;
		_smoothedRightBrowHeightRatio = null;
		_smoothedAverageBrowHeightRatio = null;
		_mediaPipeOpenEyeReferenceRatio = null;
		_mediaPipeClosedMouthReferenceRatio = null;
		_previousCapturedAtUtc = null;
	}

	private static double CalculateSmoothingFactor(FaceLandmarkFrame frame)
	{
		double num = Math.Min(frame.TrackingConfidence, Math.Min(frame.EyeConfidence, frame.MouthConfidence));
		if (!(num >= 0.65))
		{
			if (!(num >= 0.35))
			{
				return 0.32;
			}
			return 0.46;
		}
		return 0.62;
	}

	private static double CalculateEyeMeasurementQuality(FaceLandmarkFrame frame, double? leftEyeOpening, double? rightEyeOpening, double? averageEyeOpening, bool possibleOneEyeArtifact)
	{
		double valueOrDefault;
		double num2;
		double num;
		if (averageEyeOpening.HasValue)
		{
			valueOrDefault = averageEyeOpening.GetValueOrDefault();
			num = Math.Clamp(frame.EyeConfidence, 0.0, 1.0) * 100.0;
			num2 = 0.0;
			if (leftEyeOpening.HasValue)
			{
				double valueOrDefault2 = leftEyeOpening.GetValueOrDefault();
				if (rightEyeOpening.HasValue)
				{
					double valueOrDefault3 = rightEyeOpening.GetValueOrDefault();
					double value = Math.Abs(valueOrDefault2 - valueOrDefault3) / Math.Max(Math.Abs(valueOrDefault), 0.025);
					num2 = 1.0 - Math.Clamp(value, 0.0, 1.0);
					num *= 0.58 + num2 * 0.42;
					goto IL_00dc;
				}
			}
			num *= 0.72;
			goto IL_00dc;
		}
		return 0.0;
		IL_00dc:
		if (valueOrDefault < 0.012 || valueOrDefault > 0.7)
		{
			num *= 0.7;
		}
		num *= CalculateImageQualityMultiplier(frame.EyeImageQualityAvailable, frame.EyeGlarePercent, frame.EyeContrastPercent, frame.EyeSharpnessPercent);
		if (possibleOneEyeArtifact)
		{
			num *= 0.7;
		}
		if (frame.LeftEyeReconstructed || frame.RightEyeReconstructed)
		{
			bool flag = frame.LeftEyeReconstructed && frame.RightEyeReconstructed && !frame.EyeArtifactSuppressed && !possibleOneEyeArtifact && num2 >= 0.82;
			num *= ((!frame.LeftEyeReconstructed || !frame.RightEyeReconstructed) ? 0.88 : (flag ? 0.84 : 0.78));
		}
		if (frame.EyeArtifactSuppressed)
		{
			num *= 0.82;
		}
		num *= CalculateSourceQualityMultiplier(frame.Source, isEye: true);
		return Math.Clamp(num, 0.0, 100.0);
	}

	private static double CalculateMouthMeasurementQuality(FaceLandmarkFrame frame, double? mouthOpening)
	{
		if (mouthOpening.HasValue)
		{
			double valueOrDefault = mouthOpening.GetValueOrDefault();
			double num = Math.Clamp(frame.MouthConfidence, 0.0, 1.0) * 100.0;
			if (frame.InnerLipContour.Count < 4)
			{
				num *= 0.74;
			}
			if (valueOrDefault < 0.008 || valueOrDefault > 0.95)
			{
				num *= 0.72;
			}
			num *= CalculateImageQualityMultiplier(frame.MouthImageQualityAvailable, frame.MouthGlarePercent, frame.MouthContrastPercent, frame.MouthSharpnessPercent);
			if (frame.MouthReconstructed)
			{
				num *= 0.88;
			}
			num *= CalculateSourceQualityMultiplier(frame.Source, isEye: false);
			return Math.Clamp(num, 0.0, 100.0);
		}
		return 0.0;
	}

	private static double CalculateBrowMeasurementQuality(FaceLandmarkFrame frame, double? averageBrowHeight, double? browAsymmetryPercent)
	{
		if (averageBrowHeight.HasValue)
		{
			double valueOrDefault = averageBrowHeight.GetValueOrDefault();
			double num = Math.Min(Math.Clamp(frame.TrackingConfidence, 0.0, 1.0), Math.Clamp(frame.EyeConfidence, 0.0, 1.0)) * 100.0;
			if (frame.LeftBrowContour.Count < 5 || frame.RightBrowContour.Count < 5)
			{
				num *= 0.72;
			}
			if (valueOrDefault < 0.025 || valueOrDefault > 0.38)
			{
				num *= 0.74;
			}
			if (browAsymmetryPercent.HasValue)
			{
				double valueOrDefault2 = browAsymmetryPercent.GetValueOrDefault();
				if (valueOrDefault2 > 95.0)
				{
					num *= 0.82;
				}
			}
			num *= CalculateSourceQualityMultiplier(frame.Source, isEye: true);
			return Math.Clamp(num, 0.0, 100.0);
		}
		return 0.0;
	}

	private static double CalculateImageQualityMultiplier(bool available, double glarePercent, double contrastPercent, double sharpnessPercent)
	{
		if (!available)
		{
			return 1.0;
		}
		double num = Math.Clamp((glarePercent - 6.0) / 32.0, 0.0, 0.45);
		double num2 = ((contrastPercent < 24.0) ? Math.Clamp((24.0 - contrastPercent) / 60.0, 0.0, 0.25) : 0.0);
		double num3 = ((sharpnessPercent < 18.0) ? Math.Clamp((18.0 - sharpnessPercent) / 55.0, 0.0, 0.22) : 0.0);
		return Math.Clamp(1.0 - num - num2 - num3, 0.45, 1.04);
	}

	private static double CalculateSourceQualityMultiplier(string source, bool isEye)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return 1.0;
		}
		double num = 1.0;
		if (source.Contains("temporal face hold", StringComparison.OrdinalIgnoreCase) || source.Contains("temporal hold", StringComparison.OrdinalIgnoreCase) || source.Contains("landmark hold", StringComparison.OrdinalIgnoreCase))
		{
			num *= (isEye ? 0.93 : 0.92);
		}
		if (source.Contains("temporal reconstruction", StringComparison.OrdinalIgnoreCase))
		{
			num *= (isEye ? 0.95 : 0.96);
		}
		if (source.Contains("fused", StringComparison.OrdinalIgnoreCase))
		{
			num *= 1.04;
		}
		return Math.Clamp(num, 0.25, 1.08);
	}

	private static double? Smooth(double? previous, double? current, double amount)
	{
		if (current.HasValue)
		{
			double valueOrDefault = current.GetValueOrDefault();
			double value;
			if (previous.HasValue)
			{
				double valueOrDefault2 = previous.GetValueOrDefault();
				value = valueOrDefault2 + (valueOrDefault - valueOrDefault2) * amount;
			}
			else
			{
				value = valueOrDefault;
			}
			return value;
		}
		return previous;
	}

	private double? CalculateVelocity(DateTime capturedAtUtc, double? currentValue, double? previousValue)
	{
		if (currentValue.HasValue)
		{
			double valueOrDefault = currentValue.GetValueOrDefault();
			if (previousValue.HasValue)
			{
				double valueOrDefault2 = previousValue.GetValueOrDefault();
				DateTime? previousCapturedAtUtc = _previousCapturedAtUtc;
				if (previousCapturedAtUtc.HasValue)
				{
					DateTime valueOrDefault3 = previousCapturedAtUtc.GetValueOrDefault();
					double totalSeconds = (capturedAtUtc - valueOrDefault3).TotalSeconds;
					if (totalSeconds <= 0.05)
					{
						return null;
					}
					return (valueOrDefault - valueOrDefault2) / totalSeconds;
				}
			}
		}
		return null;
	}

	private (double? Left, double? Right, double? Average) ApplyLowFidelityEyeOpeningGuard(FaceLandmarkFrame frame, double? leftEyeOpening, double? rightEyeOpening, double? averageEyeOpening, double? mediaPipeAverageBlink, bool highFidelityLandmarkSource)
	{
		if (averageEyeOpening.HasValue)
		{
			double valueOrDefault = averageEyeOpening.GetValueOrDefault();
			double? smoothedAverageEyeOpeningRatio = _smoothedAverageEyeOpeningRatio;
			if (smoothedAverageEyeOpeningRatio.HasValue)
			{
				double valueOrDefault2 = smoothedAverageEyeOpeningRatio.GetValueOrDefault();
				if (!(valueOrDefault <= valueOrDefault2) && !mediaPipeAverageBlink.HasValue)
				{
					if (!frame.LeftEyeReconstructed && !frame.RightEyeReconstructed && !frame.EyeArtifactSuppressed && highFidelityLandmarkSource && frame.BlendshapeScores.Count != 0)
					{
						return (Left: leftEyeOpening, Right: rightEyeOpening, Average: averageEyeOpening);
					}
					DateTime? previousCapturedAtUtc = _previousCapturedAtUtc;
					double num;
					if (previousCapturedAtUtc.HasValue)
					{
						DateTime valueOrDefault3 = previousCapturedAtUtc.GetValueOrDefault();
						num = Math.Clamp((frame.CapturedAtUtc - valueOrDefault3).TotalSeconds, 0.1, 3.0);
					}
					else
					{
						num = 0.5;
					}
					double num2 = num;
					double num3 = 0.006 * num2;
					double num4 = Math.Min(valueOrDefault, valueOrDefault2 + num3);
					if (num4 >= valueOrDefault - 1E-06)
					{
						return (Left: leftEyeOpening, Right: rightEyeOpening, Average: averageEyeOpening);
					}
					double scale = ((valueOrDefault <= 1E-06) ? 1.0 : (num4 / valueOrDefault));
					return (Left: ScaleOpening(leftEyeOpening, scale), Right: ScaleOpening(rightEyeOpening, scale), Average: num4);
				}
			}
		}
		return (Left: leftEyeOpening, Right: rightEyeOpening, Average: averageEyeOpening);
	}

	private static double? CalculateJawDroopRatio(FaceLandmarkFrame frame, double? mediaPipeJawOpenPercent)
	{
		if (frame.JawContour.Count < 3)
		{
			return null;
		}
		Point? point = Center(frame.LeftEyeContour);
		Point? point2 = Center(frame.RightEyeContour);
		Point? point3 = AveragePoint(point, point2);
		if (point3.HasValue)
		{
			Point valueOrDefault = point3.GetValueOrDefault();
			Vector axis = CreateFaceHorizontalAxis(point, point2);
			Vector axis2 = new Vector(0.0 - axis.Y, axis.X);
			if (axis2.Y < 0.0)
			{
				axis2 = new Vector(0.0 - axis2.X, 0.0 - axis2.Y);
			}
			if (frame.FaceContour.Count + frame.JawContour.Count + frame.LeftEyeContour.Count + frame.RightEyeContour.Count + frame.OuterLipContour.Count + frame.InnerLipContour.Count < 4)
			{
				return null;
			}
			double minimum = double.PositiveInfinity;
			double maximum = double.NegativeInfinity;
			ExpandProjectedRange(frame.FaceContour, axis, ref minimum, ref maximum);
			ExpandProjectedRange(frame.JawContour, axis, ref minimum, ref maximum);
			ExpandProjectedRange(frame.LeftEyeContour, axis, ref minimum, ref maximum);
			ExpandProjectedRange(frame.RightEyeContour, axis, ref minimum, ref maximum);
			ExpandProjectedRange(frame.OuterLipContour, axis, ref minimum, ref maximum);
			ExpandProjectedRange(frame.InnerLipContour, axis, ref minimum, ref maximum);
			double num = maximum - minimum;
			if (num <= 0.001)
			{
				return null;
			}
			double num2 = Dot(valueOrDefault, axis2);
			double num3 = Math.Clamp(((MaximumProjection(frame.JawContour, axis2) - num2) / num - 0.92) * 0.42, 0.0, 0.35);
			if (mediaPipeJawOpenPercent.HasValue)
			{
				double valueOrDefault2 = mediaPipeJawOpenPercent.GetValueOrDefault();
				if (!double.IsNaN(valueOrDefault2) && !double.IsInfinity(valueOrDefault2))
				{
					double val = Math.Clamp(valueOrDefault2 / 100.0 * 0.35, 0.0, 0.35);
					return Math.Max(num3, val);
				}
			}
			return num3;
		}
		return null;
	}

	private static (double? Left, double? Right, double? Average, double? AsymmetryPercent) CalculateBrowHeight(FaceLandmarkFrame frame)
	{
		if (frame.LeftBrowContour.Count < 2 || frame.RightBrowContour.Count < 2 || frame.LeftEyeContour.Count < 2 || frame.RightEyeContour.Count < 2)
		{
			return (Left: null, Right: null, Average: null, AsymmetryPercent: null);
		}
		Point? point = Center(frame.LeftBrowContour);
		Point? point2 = Center(frame.RightBrowContour);
		Point? leftEyeCenter = Center(frame.LeftEyeContour);
		Point? rightEyeCenter = Center(frame.RightEyeContour);
		if (point.HasValue)
		{
			Point valueOrDefault = point.GetValueOrDefault();
			if (point2.HasValue)
			{
				Point valueOrDefault2 = point2.GetValueOrDefault();
				if (leftEyeCenter.HasValue)
				{
					Point valueOrDefault3 = leftEyeCenter.GetValueOrDefault();
					if (rightEyeCenter.HasValue)
					{
						Point valueOrDefault4 = rightEyeCenter.GetValueOrDefault();
						Vector horizontal = CreateFaceHorizontalAxis(leftEyeCenter, rightEyeCenter);
						Vector axis = new Vector(0.0 - horizontal.Y, horizontal.X);
						if (axis.Y < 0.0)
						{
							axis = new Vector(0.0 - axis.X, 0.0 - axis.Y);
						}
						double num = CalculateProjectedFaceWidth(frame, horizontal);
						if (num <= 0.001)
						{
							return (Left: null, Right: null, Average: null, AsymmetryPercent: null);
						}
						double num2 = Math.Clamp((Dot(valueOrDefault3, axis) - Dot(valueOrDefault, axis)) / num, 0.0, 0.6);
						double num3 = Math.Clamp((Dot(valueOrDefault4, axis) - Dot(valueOrDefault2, axis)) / num, 0.0, 0.6);
						double value = (num2 + num3) / 2.0;
						return new ValueTuple<double?, double?, double?, double?>(item4: CalculateAsymmetryPercent(num2, num3, value), item1: num2, item2: num3, item3: value);
					}
				}
			}
		}
		return (Left: null, Right: null, Average: null, AsymmetryPercent: null);
	}

	private static double CalculateProjectedFaceWidth(FaceLandmarkFrame frame, Vector horizontal)
	{
		if (frame.FaceContour.Count + frame.JawContour.Count + frame.LeftBrowContour.Count + frame.RightBrowContour.Count + frame.LeftEyeContour.Count + frame.RightEyeContour.Count + frame.OuterLipContour.Count + frame.InnerLipContour.Count < 2)
		{
			return 0.0;
		}
		double minimum = double.PositiveInfinity;
		double maximum = double.NegativeInfinity;
		ExpandProjectedRange(frame.FaceContour, horizontal, ref minimum, ref maximum);
		ExpandProjectedRange(frame.JawContour, horizontal, ref minimum, ref maximum);
		ExpandProjectedRange(frame.LeftBrowContour, horizontal, ref minimum, ref maximum);
		ExpandProjectedRange(frame.RightBrowContour, horizontal, ref minimum, ref maximum);
		ExpandProjectedRange(frame.LeftEyeContour, horizontal, ref minimum, ref maximum);
		ExpandProjectedRange(frame.RightEyeContour, horizontal, ref minimum, ref maximum);
		ExpandProjectedRange(frame.OuterLipContour, horizontal, ref minimum, ref maximum);
		ExpandProjectedRange(frame.InnerLipContour, horizontal, ref minimum, ref maximum);
		return Math.Max(0.0, maximum - minimum);
	}

	private static Vector CreateFaceHorizontalAxis(Point? leftEyeCenter, Point? rightEyeCenter)
	{
		if (leftEyeCenter.HasValue)
		{
			Point valueOrDefault = leftEyeCenter.GetValueOrDefault();
			if (rightEyeCenter.HasValue)
			{
				Point valueOrDefault2 = rightEyeCenter.GetValueOrDefault();
				Vector result = new Vector(valueOrDefault2.X - valueOrDefault.X, valueOrDefault2.Y - valueOrDefault.Y);
				if (result.Length >= 0.001)
				{
					result.Normalize();
					return result;
				}
			}
		}
		return new Vector(1.0, 0.0);
	}

	private static Point? Center(IReadOnlyList<Point> contour)
	{
		if (contour.Count == 0)
		{
			return null;
		}
		double num = 0.0;
		double num2 = 0.0;
		foreach (Point item in contour)
		{
			num += item.X;
			num2 += item.Y;
		}
		return new Point(num / (double)contour.Count, num2 / (double)contour.Count);
	}

	private static Point? AveragePoint(Point? first, Point? second)
	{
		if (first.HasValue && second.HasValue)
		{
			Point value = first.Value;
			Point value2 = second.Value;
			return new Point((value.X + value2.X) / 2.0, (value.Y + value2.Y) / 2.0);
		}
		return first ?? second;
	}

	private static double Dot(Point point, Vector axis)
	{
		return point.X * axis.X + point.Y * axis.Y;
	}

	private static double? CalculateAsymmetryPercent(double? leftEyeOpening, double? rightEyeOpening, double? averageEyeOpening)
	{
		if (leftEyeOpening.HasValue)
		{
			double valueOrDefault = leftEyeOpening.GetValueOrDefault();
			if (rightEyeOpening.HasValue)
			{
				double valueOrDefault2 = rightEyeOpening.GetValueOrDefault();
				double num = Math.Max(Math.Abs(averageEyeOpening ?? ((valueOrDefault + valueOrDefault2) / 2.0)), 0.025);
				return Math.Clamp(Math.Abs(valueOrDefault - valueOrDefault2) / num * 100.0, 0.0, 300.0);
			}
		}
		return null;
	}

	private static bool IsPossibleOneEyeArtifact(FaceLandmarkFrame frame, double? rawEyeAsymmetryPercent, bool highFidelityLandmarkSource)
	{
		if (rawEyeAsymmetryPercent.HasValue)
		{
			double valueOrDefault = rawEyeAsymmetryPercent.GetValueOrDefault();
			bool flag = frame.EyeImageQualityAvailable && (frame.EyeGlarePercent >= 5.0 || frame.EyeContrastPercent < 35.0 || frame.EyeSharpnessPercent < 25.0);
			bool flag2 = frame.EyeArtifactSuppressed || frame.LeftEyeReconstructed || frame.RightEyeReconstructed;
			bool flag3 = highFidelityLandmarkSource && frame.EyeConfidence >= 0.72 && !flag2;
			if ((!(valueOrDefault >= 85.0) || flag3 || (!(flag || flag2) && !(frame.EyeConfidence < 0.58))) && !(valueOrDefault >= 55.0 && flag))
			{
				if (valueOrDefault >= 45.0 && flag2)
				{
					return frame.EyeConfidence < 0.72;
				}
				return false;
			}
			return true;
		}
		return false;
	}

	private static double? Average(double? first, double? second)
	{
		if (first.HasValue)
		{
			double valueOrDefault = first.GetValueOrDefault();
			if (second.HasValue)
			{
				double valueOrDefault2 = second.GetValueOrDefault();
				return (valueOrDefault + valueOrDefault2) / 2.0;
			}
		}
		return first ?? second;
	}

	private static double? ScaleOpening(double? opening, double scale)
	{
		if (opening.HasValue)
		{
			double valueOrDefault = opening.GetValueOrDefault();
			return Math.Clamp(valueOrDefault * scale, 0.0, 0.85);
		}
		return null;
	}

	private static double? BlendshapePercent(FaceLandmarkFrame frame, string categoryName)
	{
		if (!frame.BlendshapeScores.TryGetValue(categoryName, out var value))
		{
			return null;
		}
		return Math.Clamp(value, 0.0, 1.0) * 100.0;
	}

	private static double? ScorePercent(double? score)
	{
		return score.HasValue ? Math.Clamp(score.Value, 0.0, 1.0) * 100.0 : null;
	}

	private static bool ShouldUsePairedAverage(FaceLandmarkFrame frame, IReadOnlyList<Point> contour, bool isEye, bool highFidelityLandmarkSource)
	{
		if (contour.Count < 4)
		{
			return false;
		}
		if (isEye && contour.Count == 6)
		{
			return true;
		}
		if (highFidelityLandmarkSource)
		{
			return true;
		}
		if (!isEye && contour.Count == 8 && frame.Source.Contains("LBF", StringComparison.OrdinalIgnoreCase) && !frame.Source.Contains("fused", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return false;
	}

	private void UpdateMediaPipeOpenEyeReference(FaceLandmarkFrame frame, double? averageEyeOpening, double? mediaPipeAverageBlink, bool highFidelityLandmarkSource)
	{
		if (!highFidelityLandmarkSource || !averageEyeOpening.HasValue)
		{
			return;
		}
		double valueOrDefault = averageEyeOpening.GetValueOrDefault();
		if (!mediaPipeAverageBlink.HasValue)
		{
			return;
		}
		double valueOrDefault2 = mediaPipeAverageBlink.GetValueOrDefault();
		if (!(valueOrDefault2 > 35.0) && !(frame.EyeConfidence < 0.45) && !(valueOrDefault < 0.025) && !(valueOrDefault > 0.65))
		{
			double? mediaPipeOpenEyeReferenceRatio = _mediaPipeOpenEyeReferenceRatio;
			double value;
			if (mediaPipeOpenEyeReferenceRatio.HasValue)
			{
				double valueOrDefault3 = mediaPipeOpenEyeReferenceRatio.GetValueOrDefault();
				value = Math.Max(valueOrDefault3 * 0.995, valueOrDefault);
			}
			else
			{
				value = valueOrDefault;
			}
			_mediaPipeOpenEyeReferenceRatio = value;
		}
	}

	private double? StabilizeEyeOpeningWithMediaPipe(double? contourOpening, double? mediaPipeBlinkPercent)
	{
		if (contourOpening.HasValue)
		{
			double valueOrDefault = contourOpening.GetValueOrDefault();
			if (mediaPipeBlinkPercent.HasValue)
			{
				double valueOrDefault2 = mediaPipeBlinkPercent.GetValueOrDefault();
				double? mediaPipeOpenEyeReferenceRatio = _mediaPipeOpenEyeReferenceRatio;
				if (mediaPipeOpenEyeReferenceRatio.HasValue)
				{
					double valueOrDefault3 = mediaPipeOpenEyeReferenceRatio.GetValueOrDefault();
					if (!(valueOrDefault2 < 38.0))
					{
						double x = Math.Clamp((valueOrDefault2 - 38.0) / 54.0, 0.0, 1.0);
						double num = Math.Clamp(1.0 - Math.Pow(x, 0.85) * 0.92, 0.08, 1.0);
						double val = valueOrDefault3 * num;
						return Math.Min(valueOrDefault, val);
					}
				}
			}
		}
		return contourOpening;
	}

	private void UpdateMediaPipeClosedMouthReference(FaceLandmarkFrame frame, double? mouthOpening, double? mediaPipeJawOpenPercent, double? mediaPipeMouthClosePercent, bool highFidelityLandmarkSource)
	{
		if (!highFidelityLandmarkSource || !mouthOpening.HasValue)
		{
			return;
		}
		double valueOrDefault = mouthOpening.GetValueOrDefault();
		if (!mediaPipeJawOpenPercent.HasValue)
		{
			return;
		}
		double valueOrDefault2 = mediaPipeJawOpenPercent.GetValueOrDefault();
		if (!mediaPipeMouthClosePercent.HasValue)
		{
			return;
		}
		double valueOrDefault3 = mediaPipeMouthClosePercent.GetValueOrDefault();
		if (!(valueOrDefault2 > 26.0) && !(valueOrDefault3 < 48.0) && !(frame.MouthConfidence < 0.42) && !(valueOrDefault < 0.004) && !(valueOrDefault > 0.45))
		{
			double? mediaPipeClosedMouthReferenceRatio = _mediaPipeClosedMouthReferenceRatio;
			double value;
			if (mediaPipeClosedMouthReferenceRatio.HasValue)
			{
				double valueOrDefault4 = mediaPipeClosedMouthReferenceRatio.GetValueOrDefault();
				value = Math.Min(valueOrDefault4 * 1.005, valueOrDefault);
			}
			else
			{
				value = valueOrDefault;
			}
			_mediaPipeClosedMouthReferenceRatio = value;
		}
	}

	private double? StabilizeMouthOpeningWithMediaPipe(double? contourOpening, double? mediaPipeJawOpenPercent, double? mediaPipeMouthClosePercent)
	{
		if (contourOpening.HasValue)
		{
			double valueOrDefault = contourOpening.GetValueOrDefault();
			double? mediaPipeClosedMouthReferenceRatio = _mediaPipeClosedMouthReferenceRatio;
			if (mediaPipeClosedMouthReferenceRatio.HasValue)
			{
				double valueOrDefault2 = mediaPipeClosedMouthReferenceRatio.GetValueOrDefault();
				double num = mediaPipeJawOpenPercent ?? double.NaN;
				double num2;
				if (mediaPipeMouthClosePercent.HasValue)
				{
					double valueOrDefault3 = mediaPipeMouthClosePercent.GetValueOrDefault();
					num2 = 100.0 - valueOrDefault3;
				}
				else
				{
					num2 = double.NaN;
				}
				double num3 = num2;
				double num4 = (double.IsNaN(num) ? num3 : (double.IsNaN(num3) ? num : Math.Max(num, num3)));
				if (!double.IsNaN(num4) && num4 >= 58.0)
				{
					double num5 = Math.Max(0.12, valueOrDefault2 * 1.35) * Math.Clamp(num4 / 100.0, 0.4, 1.0);
					double val = Math.Clamp(valueOrDefault2 + num5, 0.0, 0.85);
					return Math.Max(valueOrDefault, val);
				}
				if (mediaPipeJawOpenPercent.HasValue)
				{
					double valueOrDefault4 = mediaPipeJawOpenPercent.GetValueOrDefault();
					if (mediaPipeMouthClosePercent.HasValue)
					{
						double valueOrDefault5 = mediaPipeMouthClosePercent.GetValueOrDefault();
						if (valueOrDefault4 <= 28.0 && valueOrDefault5 >= 60.0)
						{
							return Math.Min(valueOrDefault, Math.Clamp(valueOrDefault2 * 1.35, 0.0, 0.55));
						}
					}
				}
				return contourOpening;
			}
		}
		return contourOpening;
	}

	private static bool IsHighFidelityLandmarkSource(string source)
	{
		if (!source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase) && !source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase) && !source.Contains("dense", StringComparison.OrdinalIgnoreCase))
		{
			return source.Contains("face mesh", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static void ExpandProjectedRange(IReadOnlyList<Point> points, Vector axis, ref double minimum, ref double maximum)
	{
		foreach (Point point in points)
		{
			double val = Dot(point, axis);
			minimum = Math.Min(minimum, val);
			maximum = Math.Max(maximum, val);
		}
	}

	private static double MaximumProjection(IReadOnlyList<Point> points, Vector axis)
	{
		double num = double.NegativeInfinity;
		foreach (Point point in points)
		{
			num = Math.Max(num, Dot(point, axis));
		}
		return num;
	}

	private static double? CalculateCorrection(double? adjusted, double? raw)
	{
		if (adjusted.HasValue)
		{
			double valueOrDefault = adjusted.GetValueOrDefault();
			if (raw.HasValue)
			{
				double valueOrDefault2 = raw.GetValueOrDefault();
				double value = valueOrDefault - valueOrDefault2;
				if (!(Math.Abs(value) < 1E-06))
				{
					return value;
				}
				return null;
			}
		}
		return null;
	}
}
