using System;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLockStabilityAnalyzer
{
	private const double StabilityWindowSeconds = 12.0;

	private int _sampleCount;

	private DateTime _firstCapturedAtUtc;

	private DateTime _lastCapturedAtUtc;

	private Rect? _previousFaceBounds;

	private double _faceBoundsRatePercent;

	private double _faceContinuityPercent;

	private double _eyeUsableRatePercent;

	private double _mouthUsableRatePercent;

	private double _averageEyeQualityPercent;

	private double _averageMouthQualityPercent;

	private double _averageOverallQualityPercent;

	public void Reset()
	{
		_sampleCount = 0;
		_firstCapturedAtUtc = default;
		_lastCapturedAtUtc = default;
		_previousFaceBounds = null;
		_faceBoundsRatePercent = 0.0;
		_faceContinuityPercent = 0.0;
		_eyeUsableRatePercent = 0.0;
		_mouthUsableRatePercent = 0.0;
		_averageEyeQualityPercent = 0.0;
		_averageMouthQualityPercent = 0.0;
		_averageOverallQualityPercent = 0.0;
	}

	public FaceLockStabilityAnalysis Update(FaceFeatureDetection featureDetection, FaceLandmarkFrame frame, FaceLandmarkMetrics metrics)
	{
		if (!metrics.HasFace && !frame.HasFace && !featureDetection.HasFace)
		{
			Reset();
			return FaceLockStabilityAnalysis.Waiting;
		}
		DateTime capturedAtUtc = (metrics.HasFace ? metrics.CapturedAtUtc : (frame.HasFace ? frame.CapturedAtUtc : DateTime.UtcNow));
		Rect? faceBounds = GetFaceBounds(featureDetection, frame);
		double elapsedSeconds = _sampleCount == 0
			? 0.0
			: Math.Max(0.0, (capturedAtUtc - _lastCapturedAtUtc).TotalSeconds);
		double blend = _sampleCount == 0
			? 1.0
			: 1.0 - Math.Exp(-elapsedSeconds / StabilityWindowSeconds);
		if (blend <= 0.0)
		{
			blend = 1.0 / Math.Min(_sampleCount + 1.0, StabilityWindowSeconds * 30.0);
		}
		if (_sampleCount == 0)
		{
			_firstCapturedAtUtc = capturedAtUtc;
		}
		_sampleCount++;
		_lastCapturedAtUtc = capturedAtUtc;
		double continuity = 50.0;
		if (faceBounds.HasValue)
		{
			if (_previousFaceBounds.HasValue)
			{
				continuity = CalculatePairContinuity(_previousFaceBounds.Value, faceBounds.Value) * 100.0;
			}
			_previousFaceBounds = faceBounds;
		}
		_faceBoundsRatePercent = Blend(_faceBoundsRatePercent, faceBounds.HasValue ? 100.0 : 0.0, blend);
		_faceContinuityPercent = Blend(_faceContinuityPercent, continuity, blend);
		_eyeUsableRatePercent = Blend(_eyeUsableRatePercent, metrics.IsEyeMeasurementUsable ? 100.0 : 0.0, blend);
		_mouthUsableRatePercent = Blend(_mouthUsableRatePercent, metrics.IsMouthMeasurementUsable ? 100.0 : 0.0, blend);
		_averageEyeQualityPercent = Blend(_averageEyeQualityPercent, metrics.EyeMeasurementQualityPercent, blend);
		_averageMouthQualityPercent = Blend(_averageMouthQualityPercent, metrics.MouthMeasurementQualityPercent, blend);
		_averageOverallQualityPercent = Blend(_averageOverallQualityPercent, metrics.OverallMeasurementQualityPercent, blend);
		double eyeReliability = CalculateFeatureReliability(_faceBoundsRatePercent, _faceContinuityPercent, _eyeUsableRatePercent, _averageEyeQualityPercent);
		double mouthReliability = CalculateFeatureReliability(_faceBoundsRatePercent, _faceContinuityPercent, _mouthUsableRatePercent, _averageMouthQualityPercent);
		double compositeReliabilityPercent = Math.Clamp(_faceBoundsRatePercent * 0.18 + _faceContinuityPercent * 0.26 + eyeReliability * 0.34 + mouthReliability * 0.12 + _averageOverallQualityPercent * 0.1, 0.0, 100.0);
		return new FaceLockStabilityAnalysis
		{
			SampleCount = _sampleCount,
			WindowSeconds = Math.Min(StabilityWindowSeconds, Math.Max(0.0, (capturedAtUtc - _firstCapturedAtUtc).TotalSeconds)),
			FaceBoundsRatePercent = _faceBoundsRatePercent,
			FaceContinuityPercent = _faceContinuityPercent,
			EyeUsableRatePercent = _eyeUsableRatePercent,
			MouthUsableRatePercent = _mouthUsableRatePercent,
			AverageEyeQualityPercent = Math.Clamp(_averageEyeQualityPercent, 0.0, 100.0),
			AverageMouthQualityPercent = Math.Clamp(_averageMouthQualityPercent, 0.0, 100.0),
			AverageOverallQualityPercent = Math.Clamp(_averageOverallQualityPercent, 0.0, 100.0),
			EyeReliabilityPercent = eyeReliability,
			MouthReliabilityPercent = mouthReliability,
			CompositeReliabilityPercent = compositeReliabilityPercent
		};
	}

	private static double Blend(double current, double next, double amount)
	{
		return current + (next - current) * amount;
	}

	private static double CalculateFeatureReliability(double faceBoundsRate, double continuityPercent, double usableRate, double qualityPercent)
	{
		return Math.Clamp(faceBoundsRate * 0.2 + continuityPercent * 0.24 + usableRate * 0.34 + qualityPercent * 0.22, 0.0, 100.0);
	}

	private static double CalculatePairContinuity(Rect previous, Rect current)
	{
		Point first = Center(previous);
		Point second = Center(current);
		double num = Distance(first, second);
		double num2 = Diagonal(previous);
		double num3 = Diagonal(current);
		double num4 = Math.Max(0.001, (num2 + num3) / 2.0);
		double num5 = 1.0 - Math.Clamp(num / (num4 * 1.2), 0.0, 1.0);
		double num6 = LogSimilarity(Math.Max(1E-06, current.Width * current.Height), Math.Max(1E-06, previous.Width * previous.Height), 3.5);
		return num5 * 0.72 + num6 * 0.28;
	}

	private static Rect? GetFaceBounds(FaceFeatureDetection featureDetection, FaceLandmarkFrame frame)
	{
		if (featureDetection.HasFace && featureDetection.FaceBox.Width > 0.0 && featureDetection.FaceBox.Height > 0.0)
		{
			return featureDetection.FaceBox;
		}
		return Bounds(frame.FaceContour);
	}

	private static Rect? Bounds(IReadOnlyList<Point> points)
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

	private static Point Center(Rect rect)
	{
		return new Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0);
	}

	private static double Distance(Point first, Point second)
	{
		double num = first.X - second.X;
		double num2 = first.Y - second.Y;
		return Math.Sqrt(num * num + num2 * num2);
	}

	private static double Diagonal(Rect rect)
	{
		return Math.Sqrt(rect.Width * rect.Width + rect.Height * rect.Height);
	}

	private static double LogSimilarity(double value, double target, double toleranceFactor)
	{
		double num = Math.Abs(Math.Log(value / target));
		return 1.0 - Math.Clamp(num / Math.Log(Math.Max(1.01, toleranceFactor)), 0.0, 1.0);
	}

}
