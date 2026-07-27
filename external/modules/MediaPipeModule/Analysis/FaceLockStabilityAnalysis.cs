namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLockStabilityAnalysis
{
	public static FaceLockStabilityAnalysis Waiting { get; } = new FaceLockStabilityAnalysis();

	public int SampleCount { get; init; }

	public double WindowSeconds { get; init; }

	public double FaceBoundsRatePercent { get; init; }

	public double FaceContinuityPercent { get; init; }

	public double EyeUsableRatePercent { get; init; }

	public double MouthUsableRatePercent { get; init; }

	public double AverageEyeQualityPercent { get; init; }

	public double AverageMouthQualityPercent { get; init; }

	public double AverageOverallQualityPercent { get; init; }

	public double EyeReliabilityPercent { get; init; }

	public double MouthReliabilityPercent { get; init; }

	public double CompositeReliabilityPercent { get; init; }

	public bool HasUsableLock
	{
		get
		{
			if (SampleCount >= 3)
			{
				return CompositeReliabilityPercent >= 55.0;
			}
			return false;
		}
	}

	public string Label
	{
		get
		{
			if (SampleCount < 3)
			{
				return "warming";
			}
			if (CompositeReliabilityPercent >= 78.0)
			{
				return "strong";
			}
			if (CompositeReliabilityPercent >= 55.0)
			{
				return "usable";
			}
			return "limited";
		}
	}

	public string Status
	{
		get
		{
			if (SampleCount <= 0)
			{
				return "face reliability waiting";
			}
			if (SampleCount >= 3)
			{
				return $"face reliability {Label} {CompositeReliabilityPercent:0}% over {WindowSeconds:0}s; continuity {FaceContinuityPercent:0}%; eye {EyeReliabilityPercent:0}%; mouth {MouthReliabilityPercent:0}%";
			}
			return $"face reliability warming {SampleCount} sample(s)";
		}
	}
}
