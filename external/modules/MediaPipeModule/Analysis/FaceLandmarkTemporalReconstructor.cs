using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkTemporalReconstructor
{
	private const double DefaultEyeSymmetryScale = 1.0;

	private double? _lastLeftEyeOpeningRatio;

	private double? _lastRightEyeOpeningRatio;

	private double? _lastMouthOpeningRatio;

	private double _leftToRightEyeOpeningScale = 1.0;

	private Rect? _lastLeftEyeBounds;

	private Rect? _lastRightEyeBounds;

	private Rect? _lastMouthBounds;

	private Rect? _lastFaceBounds;

	private DateTime? _lastCapturedAtUtc;

	public void Reset()
	{
		_lastLeftEyeOpeningRatio = null;
		_lastRightEyeOpeningRatio = null;
		_lastMouthOpeningRatio = null;
		_leftToRightEyeOpeningScale = 1.0;
		_lastLeftEyeBounds = null;
		_lastRightEyeBounds = null;
		_lastMouthBounds = null;
		_lastFaceBounds = null;
		_lastCapturedAtUtc = null;
	}

	public FaceLandmarkFrame Update(FaceLandmarkFrame frame)
	{
		if (!frame.HasFace)
		{
			Reset();
			return frame;
		}
		double elapsedSeconds = CalculateElapsedSeconds(frame.CapturedAtUtc);
		Rect? rect = TryGetBounds(frame.LeftEyeContour);
		Rect? rect2 = TryGetBounds(frame.RightEyeContour);
		Rect? rect3 = TryGetBounds((frame.InnerLipContour.Count >= 4) ? frame.InnerLipContour : frame.OuterLipContour);
		Rect? rect4 = EstimateFaceBounds(frame);
		Rect? previous = MapPreviousBoundsToCurrentFace(_lastLeftEyeBounds, _lastFaceBounds, rect4);
		Rect? previous2 = MapPreviousBoundsToCurrentFace(_lastRightEyeBounds, _lastFaceBounds, rect4);
		Rect? rect5 = MapPreviousBoundsToCurrentFace(_lastMouthBounds, _lastFaceBounds, rect4);
		bool preferPairedAverage = ShouldUsePairedAverage(frame.Source, frame.LeftEyeContour, isEye: true);
		bool preferPairedAverage2 = ShouldUsePairedAverage(frame.Source, frame.RightEyeContour, isEye: true);
		IReadOnlyList<Point> contour = ((frame.InnerLipContour.Count >= 4) ? frame.InnerLipContour : frame.OuterLipContour);
		bool preferPairedAverage3 = ShouldUsePairedAverage(frame.Source, contour, isEye: false);
		double? num = ContourOpeningEstimator.CalculateOpeningRatio(frame.LeftEyeContour, preferPairedAverage);
		double? num2 = ContourOpeningEstimator.CalculateOpeningRatio(frame.RightEyeContour, preferPairedAverage2);
		double? measured = ContourOpeningEstimator.CalculateOpeningRatio(contour, preferPairedAverage3);
		double? num3 = BlendshapePercent(frame, "eyeBlinkLeft");
		double? num4 = BlendshapePercent(frame, "eyeBlinkRight");
		double? num5 = Average(num3, num4);
		double? mediaPipeJawOpenPercent = ScorePercent(frame.MediaPipeJawOpenScore) ?? BlendshapePercent(frame, "jawOpen");
		double? mediaPipeMouthClosePercent = ScorePercent(frame.MediaPipeMouthCloseScore) ?? BlendshapePercent(frame, "mouthClose");
		double eyeConfidence = frame.EyeConfidence;
		double mouthConfidence = frame.MouthConfidence;
		bool reconstructed = false;
		bool featureReconstructed = false;
		bool featureReconstructed2 = false;
		bool featureReconstructed3 = false;
		bool artifactSuppressed = false;
		bool guardLowFidelityOpening = !IsHighFidelityLandmarkSource(frame.Source) || frame.BlendshapeScores.Count == 0;
		bool flag = IsLikelyEyeContourShapeArtifact(rect, previous, rect2, frame);
		bool flag2 = IsLikelyEyeContourShapeArtifact(rect2, previous2, rect, frame);
		if (flag)
		{
			num = null;
			reconstructed = true;
			featureReconstructed = true;
			artifactSuppressed = true;
		}
		if (flag2)
		{
			num2 = null;
			reconstructed = true;
			featureReconstructed2 = true;
			artifactSuppressed = true;
		}
		UpdateEyeSymmetryScale(num, num2, eyeConfidence);
		double? num6 = ReconstructEyeRatio(num, num2, _lastLeftEyeOpeningRatio, _lastRightEyeOpeningRatio, _leftToRightEyeOpeningScale, elapsedSeconds, eyeConfidence, num3 ?? num5, guardLowFidelityOpening, isLeftEye: true, ref reconstructed, ref featureReconstructed, ref artifactSuppressed);
		double? num7 = ReconstructEyeRatio(num2, num, _lastRightEyeOpeningRatio, _lastLeftEyeOpeningRatio, _leftToRightEyeOpeningScale, elapsedSeconds, eyeConfidence, num4 ?? num5, guardLowFidelityOpening, isLeftEye: false, ref reconstructed, ref featureReconstructed2, ref artifactSuppressed);
		double? num8 = ReconstructMouthRatio(measured, _lastMouthOpeningRatio, elapsedSeconds, mouthConfidence, mediaPipeJawOpenPercent, mediaPipeMouthClosePercent, ref reconstructed, ref featureReconstructed3);
		Rect? rect6 = ChooseEyeReconstructionBounds(frame, rect, previous, rect2, flag, isLeftEye: true);
		Rect? rect7 = ChooseEyeReconstructionBounds(frame, rect2, previous2, rect, flag2, isLeftEye: false);
		IReadOnlyList<Point> leftEyeContour = BuildReconstructedContour(frame.LeftEyeContour, rect6, num6, preferPairedAverage, ref reconstructed, ref featureReconstructed);
		IReadOnlyList<Point> rightEyeContour = BuildReconstructedContour(frame.RightEyeContour, rect7, num7, preferPairedAverage2, ref reconstructed, ref featureReconstructed2);
		IReadOnlyList<Point> innerLipContour = BuildReconstructedContour(frame.InnerLipContour, rect3 ?? rect5, num8, preferPairedAverage3, ref reconstructed, ref featureReconstructed3);
		Remember(frame.CapturedAtUtc, num6, num7, num8, rect6, rect7, rect3 ?? rect5, rect4);
		if (!reconstructed)
		{
			return frame;
		}
		return new FaceLandmarkFrame
		{
			HasFace = true,
			Source = (string.IsNullOrWhiteSpace(frame.Source) ? "temporal reconstruction" : (frame.Source + "; temporal reconstruction")),
			CapturedAtUtc = frame.CapturedAtUtc,
			TrackingConfidence = frame.TrackingConfidence,
			EyeConfidence = Math.Max(frame.EyeConfidence, (num6.HasValue || num7.HasValue) ? 0.32 : frame.EyeConfidence),
			MouthConfidence = Math.Max(frame.MouthConfidence, num8.HasValue ? 0.24 : frame.MouthConfidence),
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
			LeftEyeReconstructed = (frame.LeftEyeReconstructed || featureReconstructed),
			RightEyeReconstructed = (frame.RightEyeReconstructed || featureReconstructed2),
			MouthReconstructed = (frame.MouthReconstructed || featureReconstructed3),
			EyeArtifactSuppressed = (frame.EyeArtifactSuppressed || artifactSuppressed),
			HeadYawDegrees = frame.HeadYawDegrees,
			HeadPitchDegrees = frame.HeadPitchDegrees,
			HeadRollDegrees = frame.HeadRollDegrees,
			BlendshapeScores = frame.BlendshapeScores,
			MediaPipeEyeBlinkLeftScore = frame.MediaPipeEyeBlinkLeftScore,
			MediaPipeEyeBlinkRightScore = frame.MediaPipeEyeBlinkRightScore,
			MediaPipeJawOpenScore = frame.MediaPipeJawOpenScore,
			MediaPipeMouthCloseScore = frame.MediaPipeMouthCloseScore,
			DenseMeshTopology = frame.DenseMeshTopology,
			DenseMeshPoints = frame.DenseMeshPoints,
			FacialTransformationMatrix = frame.FacialTransformationMatrix,
			FaceContour = frame.FaceContour,
			LeftEyeContour = leftEyeContour,
			RightEyeContour = rightEyeContour,
			LeftBrowContour = frame.LeftBrowContour,
			RightBrowContour = frame.RightBrowContour,
			OuterLipContour = frame.OuterLipContour,
			InnerLipContour = innerLipContour,
			JawContour = frame.JawContour
		};
	}

	private double CalculateElapsedSeconds(DateTime capturedAtUtc)
	{
		DateTime? lastCapturedAtUtc = _lastCapturedAtUtc;
		if (lastCapturedAtUtc.HasValue)
		{
			DateTime valueOrDefault = lastCapturedAtUtc.GetValueOrDefault();
			return Math.Clamp((capturedAtUtc - valueOrDefault).TotalSeconds, 0.1, 3.0);
		}
		return 0.5;
	}

	private void UpdateEyeSymmetryScale(double? leftEye, double? rightEye, double eyeConfidence)
	{
		if (eyeConfidence < 0.45 || !leftEye.HasValue)
		{
			return;
		}
		double valueOrDefault = leftEye.GetValueOrDefault();
		if (rightEye.HasValue)
		{
			double valueOrDefault2 = rightEye.GetValueOrDefault();
			if (!(valueOrDefault2 <= 0.001))
			{
				double num = Math.Clamp(valueOrDefault / valueOrDefault2, 0.55, 1.45);
				_leftToRightEyeOpeningScale += (num - _leftToRightEyeOpeningScale) * 0.08;
			}
		}
	}

	private static double? ReconstructEyeRatio(double? measured, double? pairedMeasured, double? previous, double? pairedPrevious, double leftToRightScale, double elapsedSeconds, double confidence, double? mediaPipeBlinkPercent, bool guardLowFidelityOpening, bool isLeftEye, ref bool reconstructed, ref bool featureReconstructed, ref bool artifactSuppressed)
	{
		double? num = EstimateFromPair(pairedMeasured, leftToRightScale, isLeftEye);
		bool hasValue = measured.HasValue;
		bool flag = false;
		double? num2 = measured;
		if (!num2.HasValue && num.HasValue)
		{
			double valueOrDefault = num.GetValueOrDefault();
			num2 = valueOrDefault;
			reconstructed = true;
			featureReconstructed = true;
		}
		else if (!num2.HasValue && previous.HasValue)
		{
			double valueOrDefault2 = previous.GetValueOrDefault();
			num2 = valueOrDefault2;
			reconstructed = true;
			featureReconstructed = true;
		}
		if (num2.HasValue)
		{
			double valueOrDefault3 = num2.GetValueOrDefault();
			if (num.HasValue)
			{
				double valueOrDefault4 = num.GetValueOrDefault();
				if (previous.HasValue)
				{
					double valueOrDefault5 = previous.GetValueOrDefault();
					if (confidence < 0.58 && IsLikelyEyeArtifact(valueOrDefault3, valueOrDefault4, valueOrDefault5))
					{
						num2 = valueOrDefault4 * 0.7 + valueOrDefault5 * 0.3;
						reconstructed = true;
						featureReconstructed = true;
						artifactSuppressed = true;
					}
				}
			}
		}
		if (num2.HasValue)
		{
			double valueOrDefault6 = num2.GetValueOrDefault();
			if (previous.HasValue)
			{
				double valueOrDefault7 = previous.GetValueOrDefault();
				if (mediaPipeBlinkPercent.HasValue)
				{
					double valueOrDefault8 = mediaPipeBlinkPercent.GetValueOrDefault();
					if (valueOrDefault8 >= 58.0 && ((!hasValue | featureReconstructed) || confidence < 0.45))
					{
						double num3 = Math.Clamp(1.0 - valueOrDefault8 / 100.0, 0.08, 1.0);
						double num4 = Math.Min(valueOrDefault6, valueOrDefault7 * num3);
						if (num4 < valueOrDefault6 - 0.0001)
						{
							num2 = num4;
							reconstructed = true;
							featureReconstructed = true;
							flag = true;
						}
					}
				}
			}
		}
		if ((((flag || guardLowFidelityOpening) | featureReconstructed) || !hasValue || confidence < 0.72) && num2.HasValue)
		{
			double valueOrDefault9 = num2.GetValueOrDefault();
			if (previous.HasValue)
			{
				double valueOrDefault10 = previous.GetValueOrDefault();
				double closingRatePerSecond = (flag ? 1.2 : 0.24);
				double openingRatePerSecond = ((guardLowFidelityOpening && valueOrDefault9 > valueOrDefault10 && (featureReconstructed || !hasValue || confidence < 0.72 || !mediaPipeBlinkPercent.HasValue)) ? 0.025 : 0.18);
				double num5 = LimitRatioChange(valueOrDefault9, valueOrDefault10, elapsedSeconds, closingRatePerSecond, openingRatePerSecond);
				if (Math.Abs(num5 - valueOrDefault9) > 0.0001)
				{
					num2 = num5;
					reconstructed = true;
					featureReconstructed = true;
				}
			}
		}
		if (num2.HasValue)
		{
			double valueOrDefault11 = num2.GetValueOrDefault();
			return Math.Clamp(valueOrDefault11, 0.015, 0.85);
		}
		if (pairedPrevious.HasValue)
		{
			double valueOrDefault12 = pairedPrevious.GetValueOrDefault();
			reconstructed = true;
			featureReconstructed = true;
			return EstimateFromPair(valueOrDefault12, leftToRightScale, isLeftEye);
		}
		return null;
	}

	private static double? ReconstructMouthRatio(double? measured, double? previous, double elapsedSeconds, double confidence, double? mediaPipeJawOpenPercent, double? mediaPipeMouthClosePercent, ref bool reconstructed, ref bool featureReconstructed)
	{
		bool hasValue = measured.HasValue;
		bool flag = false;
		bool flag2 = false;
		double? num = measured;
		if (!num.HasValue && previous.HasValue)
		{
			double valueOrDefault = previous.GetValueOrDefault();
			num = valueOrDefault;
			reconstructed = true;
			featureReconstructed = true;
		}
		if (num.HasValue)
		{
			double valueOrDefault2 = num.GetValueOrDefault();
			if (previous.HasValue)
			{
				double valueOrDefault3 = previous.GetValueOrDefault();
				if ((!hasValue | featureReconstructed) || confidence < 0.4)
				{
					double num2 = mediaPipeJawOpenPercent ?? double.NaN;
					double num3 = num2;
					if (num2 >= 35.0 && mediaPipeMouthClosePercent.HasValue)
					{
						double valueOrDefault4 = mediaPipeMouthClosePercent.GetValueOrDefault();
						if (valueOrDefault4 <= 35.0)
						{
							num3 = Math.Max(num2, 100.0 - valueOrDefault4);
						}
					}
					if (!double.IsNaN(num3) && num3 >= 58.0)
					{
						double num4 = Math.Max(0.12, valueOrDefault3 * 1.35) * Math.Clamp(num3 / 100.0, 0.4, 1.0);
						double num5 = Math.Max(valueOrDefault2, valueOrDefault3 + num4);
						if (num5 > valueOrDefault2 + 0.0001)
						{
							num = num5;
							reconstructed = true;
							featureReconstructed = true;
							flag = true;
						}
					}
					if (mediaPipeJawOpenPercent.HasValue)
					{
						double valueOrDefault5 = mediaPipeJawOpenPercent.GetValueOrDefault();
						if (mediaPipeMouthClosePercent.HasValue)
						{
							double valueOrDefault6 = mediaPipeMouthClosePercent.GetValueOrDefault();
							if (valueOrDefault5 <= 28.0 && valueOrDefault6 >= 60.0)
							{
								double num6 = Math.Min(valueOrDefault2, Math.Clamp(valueOrDefault3 * 0.9, 0.01, 0.55));
								if (num6 < valueOrDefault2 - 0.0001)
								{
									num = num6;
									reconstructed = true;
									featureReconstructed = true;
									flag2 = true;
								}
							}
						}
					}
				}
			}
		}
		if (num.HasValue)
		{
			double valueOrDefault7 = num.GetValueOrDefault();
			if (previous.HasValue)
			{
				double valueOrDefault8 = previous.GetValueOrDefault();
				if (confidence < 0.4)
				{
					double closingRatePerSecond = (flag2 ? 0.65 : 0.22);
					double openingRatePerSecond = (flag ? 0.85 : 0.28);
					double num7 = LimitRatioChange(valueOrDefault7, valueOrDefault8, elapsedSeconds, closingRatePerSecond, openingRatePerSecond);
					if (Math.Abs(num7 - valueOrDefault7) > 0.0001)
					{
						num = num7;
						reconstructed = true;
						featureReconstructed = true;
					}
				}
			}
		}
		if (num.HasValue)
		{
			double valueOrDefault9 = num.GetValueOrDefault();
			return Math.Clamp(valueOrDefault9, 0.01, 1.2);
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
			return valueOrDefault;
		}
		return second;
	}

	private static double? EstimateFromPair(double? pairedRatio, double leftToRightScale, bool isLeftEye)
	{
		if (pairedRatio.HasValue)
		{
			double valueOrDefault = pairedRatio.GetValueOrDefault();
			return isLeftEye ? (valueOrDefault * leftToRightScale) : (valueOrDefault / Math.Max(0.001, leftToRightScale));
		}
		return null;
	}

	private static bool IsLikelyEyeArtifact(double measured, double pairEstimate, double previous)
	{
		double num = Math.Abs(measured - pairEstimate);
		double num2 = Math.Abs(measured - previous);
		bool flag = Math.Abs(pairEstimate - previous) < 0.08;
		return num > 0.1 && num2 > 0.1 && flag;
	}

	private static bool IsLikelyEyeContourShapeArtifact(Rect? current, Rect? previous, Rect? paired, FaceLandmarkFrame frame)
	{
		if (current.HasValue)
		{
			Rect valueOrDefault = current.GetValueOrDefault();
			bool flag = frame.EyeImageQualityAvailable && (frame.EyeGlarePercent >= 8.0 || frame.EyeContrastPercent < 30.0 || frame.EyeSharpnessPercent < 22.0);
			if (frame.EyeConfidence >= 0.58 && !flag)
			{
				return false;
			}
			double num = ((valueOrDefault.Width <= 0.0001) ? 2.0 : (valueOrDefault.Height / valueOrDefault.Width));
			bool num2 = valueOrDefault.Width > 0.24 || valueOrDefault.Height > 0.18 || num > 0.9;
			bool flag2 = previous.HasValue && IsEyeBoundsSizeOutlier(reference: previous.GetValueOrDefault(), current: valueOrDefault);
			int num3;
			if (previous.HasValue)
			{
				Rect valueOrDefault2 = previous.GetValueOrDefault();
				num3 = ((Distance(Center(valueOrDefault), Center(valueOrDefault2)) > Math.Max(0.055, valueOrDefault2.Width * 0.85)) ? 1 : 0);
			}
			else
			{
				num3 = 0;
			}
			bool flag3 = (byte)num3 != 0;
			bool flag4 = paired.HasValue && IsEyeBoundsSizeOutlier(reference: paired.GetValueOrDefault(), current: valueOrDefault);
			if (!(num2 || flag2 || flag4))
			{
				return flag3 && !paired.HasValue && flag;
			}
			return true;
		}
		return false;
	}

	private static bool IsEyeBoundsSizeOutlier(Rect current, Rect reference)
	{
		if (reference.Width <= 0.0001 || reference.Height <= 0.0001)
		{
			return false;
		}
		double num = current.Width / reference.Width;
		double num2 = current.Height / reference.Height;
		if ((!(num > 1.75) && !(num < 0.48)) || 1 == 0)
		{
			return num2 > 2.65;
		}
		return true;
	}

	private static bool IsHighFidelityLandmarkSource(string source)
	{
		if (!source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase) && !source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase) && !source.Contains("dense", StringComparison.OrdinalIgnoreCase))
		{
			return source.Contains("face mesh", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static Rect? ChooseEyeReconstructionBounds(FaceLandmarkFrame frame, Rect? current, Rect? previous, Rect? paired, bool shapeArtifact, bool isLeftEye)
	{
		if (shapeArtifact)
		{
			return previous ?? EstimateEyeBoundsFromPairedEye(frame, paired, isLeftEye);
		}
		return current ?? previous ?? EstimateEyeBoundsFromPairedEye(frame, paired, isLeftEye);
	}

	private static Rect? EstimateEyeBoundsFromPairedEye(FaceLandmarkFrame frame, Rect? paired, bool isLeftEye)
	{
		if (paired.HasValue)
		{
			Rect valueOrDefault = paired.GetValueOrDefault();
			Point? point = Center(frame.FaceContour) ?? Center(frame.JawContour) ?? Center(frame.OuterLipContour);
			if (point.HasValue)
			{
				Point valueOrDefault2 = point.GetValueOrDefault();
				Point point2 = Center(valueOrDefault);
				Point point3 = new Point(valueOrDefault2.X - (point2.X - valueOrDefault2.X), valueOrDefault2.Y - (point2.Y - valueOrDefault2.Y));
				double num = valueOrDefault.Width / 2.0;
				double num2 = valueOrDefault.Height / 2.0;
				double num3 = Math.Clamp(point3.X - num, 0.0, 1.0);
				double num4 = Math.Clamp(point3.Y - num2, 0.0, 1.0);
				double num5 = Math.Min(valueOrDefault.Width, 1.0 - num3);
				double num6 = Math.Min(valueOrDefault.Height, 1.0 - num4);
				if (num5 <= 0.0001 || num6 <= 0.0001)
				{
					return null;
				}
				return new Rect(num3, num4, num5, num6);
			}
			return null;
		}
		return null;
	}

	private static double LimitRatioChange(double current, double previous, double elapsedSeconds, double closingRatePerSecond, double openingRatePerSecond)
	{
		double num = current - previous;
		double num2 = ((num >= 0.0) ? openingRatePerSecond : closingRatePerSecond) * elapsedSeconds;
		return previous + Math.Clamp(num, 0.0 - num2, num2);
	}

	private static IReadOnlyList<Point> BuildReconstructedContour(IReadOnlyList<Point> original, Rect? bounds, double? ratio, bool preferPairedAverage, ref bool reconstructed, ref bool featureReconstructed)
	{
		if (ratio.HasValue)
		{
			double valueOrDefault = ratio.GetValueOrDefault();
			if (bounds.HasValue)
			{
				Rect valueOrDefault2 = bounds.GetValueOrDefault();
				if (!(valueOrDefault2.Width <= 0.0))
				{
					double? num = ((original.Count >= 4) ? ContourOpeningEstimator.CalculateOpeningRatio(original, preferPairedAverage) : CalculateOpeningRatio(valueOrDefault2));
					Rect? rect = TryGetBounds(original);
					int num2;
					if (rect.HasValue)
					{
						Rect valueOrDefault3 = rect.GetValueOrDefault();
						num2 = (AreBoundsClose(valueOrDefault3, valueOrDefault2) ? 1 : 0);
					}
					else
					{
						num2 = 0;
					}
					bool flag = (byte)num2 != 0;
					if (original.Count >= 4 && flag && num.HasValue)
					{
						double valueOrDefault4 = num.GetValueOrDefault();
						if (Math.Abs(valueOrDefault4 - valueOrDefault) < 0.006)
						{
							return original;
						}
					}
					reconstructed = true;
					featureReconstructed = true;
					double centerX = valueOrDefault2.Left + valueOrDefault2.Width / 2.0;
					double centerY = valueOrDefault2.Top + valueOrDefault2.Height / 2.0;
					double halfWidth = valueOrDefault2.Width / 2.0;
					double halfHeight = Math.Max(0.0025, valueOrDefault2.Width * valueOrDefault / 2.0);
					return CreateOvalContour(centerX, centerY, halfWidth, halfHeight);
				}
			}
		}
		return original;
	}

	private static bool ShouldUsePairedAverage(string source, IReadOnlyList<Point> contour, bool isEye)
	{
		if (contour.Count < 4)
		{
			return false;
		}
		if (isEye && contour.Count == 6)
		{
			return true;
		}
		if (source.Contains("dense", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (!isEye && contour.Count == 8 && source.Contains("LBF", StringComparison.OrdinalIgnoreCase) && !source.Contains("fused", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return false;
	}

	private void Remember(DateTime capturedAtUtc, double? leftEye, double? rightEye, double? mouth, Rect? leftBounds, Rect? rightBounds, Rect? mouthBounds, Rect? faceBounds)
	{
		_lastCapturedAtUtc = capturedAtUtc;
		_lastLeftEyeOpeningRatio = leftEye ?? _lastLeftEyeOpeningRatio;
		_lastRightEyeOpeningRatio = rightEye ?? _lastRightEyeOpeningRatio;
		_lastMouthOpeningRatio = mouth ?? _lastMouthOpeningRatio;
		_lastLeftEyeBounds = leftBounds ?? _lastLeftEyeBounds;
		_lastRightEyeBounds = rightBounds ?? _lastRightEyeBounds;
		_lastMouthBounds = mouthBounds ?? _lastMouthBounds;
		_lastFaceBounds = faceBounds ?? _lastFaceBounds;
	}

	private static Rect? EstimateFaceBounds(FaceLandmarkFrame frame)
	{
		return TryGetBounds(frame.FaceContour) ?? TryGetBounds(frame.JawContour) ?? UnionBounds(TryGetBounds(frame.LeftEyeContour), TryGetBounds(frame.RightEyeContour), TryGetBounds(frame.OuterLipContour), TryGetBounds(frame.InnerLipContour));
	}

	private static Rect? MapPreviousBoundsToCurrentFace(Rect? previousFeatureBounds, Rect? previousFaceBounds, Rect? currentFaceBounds)
	{
		if (previousFeatureBounds.HasValue)
		{
			Rect valueOrDefault = previousFeatureBounds.GetValueOrDefault();
			if (previousFaceBounds.HasValue)
			{
				Rect valueOrDefault2 = previousFaceBounds.GetValueOrDefault();
				if (currentFaceBounds.HasValue)
				{
					Rect valueOrDefault3 = currentFaceBounds.GetValueOrDefault();
					if (!(valueOrDefault2.Width <= 0.0001) && !(valueOrDefault2.Height <= 0.0001) && !(valueOrDefault3.Width <= 0.0001) && !(valueOrDefault3.Height <= 0.0001))
					{
						double num = (valueOrDefault.Left - valueOrDefault2.Left) / valueOrDefault2.Width;
						double num2 = (valueOrDefault.Top - valueOrDefault2.Top) / valueOrDefault2.Height;
						double num3 = valueOrDefault.Width / valueOrDefault2.Width;
						double num4 = valueOrDefault.Height / valueOrDefault2.Height;
						double left = valueOrDefault3.Left + num * valueOrDefault3.Width;
						double top = valueOrDefault3.Top + num2 * valueOrDefault3.Height;
						double width = num3 * valueOrDefault3.Width;
						double height = num4 * valueOrDefault3.Height;
						return ClampRect(left, top, width, height);
					}
				}
			}
		}
		return previousFeatureBounds;
	}

	private static Rect? UnionBounds(params Rect?[] bounds)
	{
		Rect? result = null;
		for (int i = 0; i < bounds.Length; i++)
		{
			Rect? rect = bounds[i];
			if (rect.HasValue)
			{
				Rect valueOrDefault = rect.GetValueOrDefault();
				Rect value;
				if (result.HasValue)
				{
					Rect valueOrDefault2 = result.GetValueOrDefault();
					value = Rect.Union(valueOrDefault2, valueOrDefault);
				}
				else
				{
					value = valueOrDefault;
				}
				result = value;
			}
		}
		return result;
	}

	private static Rect? ClampRect(double left, double top, double width, double height)
	{
		double num = Math.Clamp(left, 0.0, 1.0);
		double num2 = Math.Clamp(top, 0.0, 1.0);
		double num3 = Math.Clamp(left + width, num, 1.0);
		double num4 = Math.Clamp(top + height, num2, 1.0);
		double num5 = num3 - num;
		double num6 = num4 - num2;
		if (!(num5 <= 0.0001) && !(num6 <= 0.0001))
		{
			return new Rect(num, num2, num5, num6);
		}
		return null;
	}

	private static double? CalculateOpeningRatio(Rect? bounds)
	{
		if (bounds.HasValue)
		{
			Rect valueOrDefault = bounds.GetValueOrDefault();
			return CalculateOpeningRatio(valueOrDefault);
		}
		return null;
	}

	private static double? CalculateOpeningRatio(Rect rect)
	{
		if (rect.Width <= 0.0001 || rect.Height <= 0.0001)
		{
			return null;
		}
		return Math.Clamp(rect.Height / rect.Width, 0.0, 2.0);
	}

	private static Rect? TryGetBounds(IReadOnlyList<Point> points)
	{
		if (points.Count < 4)
		{
			return null;
		}
		double num = points.Min((Point point) => point.X);
		double num2 = points.Max((Point point) => point.X);
		double num3 = points.Min((Point point) => point.Y);
		double num4 = points.Max((Point point) => point.Y);
		if (num2 <= num || num4 <= num3)
		{
			return null;
		}
		return new Rect(num, num3, num2 - num, num4 - num3);
	}

	private static bool AreBoundsClose(Rect first, Rect second)
	{
		double num = Distance(Center(first), Center(second));
		double num2 = Math.Abs(first.Width - second.Width);
		double num3 = Math.Abs(first.Height - second.Height);
		if (num < Math.Max(0.006, second.Width * 0.08) && num2 < Math.Max(0.006, second.Width * 0.1))
		{
			return num3 < Math.Max(0.006, second.Height * 0.16);
		}
		return false;
	}

	private static Point Center(Rect rect)
	{
		return new Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0);
	}

	private static Point? Center(IReadOnlyList<Point> points)
	{
		if (points.Count == 0)
		{
			return null;
		}
		return new Point(points.Average((Point point) => point.X), points.Average((Point point) => point.Y));
	}

	private static double Distance(Point first, Point second)
	{
		double num = first.X - second.X;
		double num2 = first.Y - second.Y;
		return Math.Sqrt(num * num + num2 * num2);
	}

	private static IReadOnlyList<Point> CreateOvalContour(double centerX, double centerY, double halfWidth, double halfHeight)
	{
		return
		[
			new Point(centerX - halfWidth, centerY),
			new Point(centerX - halfWidth * 0.72, centerY - halfHeight * 0.7),
			new Point(centerX, centerY - halfHeight),
			new Point(centerX + halfWidth * 0.72, centerY - halfHeight * 0.7),
			new Point(centerX + halfWidth, centerY),
			new Point(centerX + halfWidth * 0.72, centerY + halfHeight * 0.7),
			new Point(centerX, centerY + halfHeight),
			new Point(centerX - halfWidth * 0.72, centerY + halfHeight * 0.7)
		];
	}
}
