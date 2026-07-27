using System;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Contracts;

/// <summary>
/// Immutable, normalized region hint for a vision module that can optionally
/// steer its next detection. It carries no producer-specific types and never
/// requires a consumer to acknowledge or wait for it.
/// </summary>
public sealed class VisionTargetHintOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public long SequenceId { get; }
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public bool IsActive { get; }
	public string UserId { get; }
	public double Left { get; }
	public double Top { get; }
	public double Right { get; }
	public double Bottom { get; }
	public double Confidence { get; }
	public long ExpiresAtTimestamp { get; }

	public VisionTargetHintOutput(
		long sequenceId,
		bool isActive,
		string userId,
		double left,
		double top,
		double right,
		double bottom,
		double confidence,
		long expiresAtTimestamp)
	{
		SequenceId = sequenceId;
		ProducedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		ProducedAtUtc = DateTime.UtcNow;
		IsActive = isActive;
		UserId = userId ?? "";
		Left = Math.Clamp(left, 0d, 1d);
		Top = Math.Clamp(top, 0d, 1d);
		Right = Math.Clamp(right, 0d, 1d);
		Bottom = Math.Clamp(bottom, 0d, 1d);
		Confidence = Math.Clamp(confidence, 0d, 1d);
		ExpiresAtTimestamp = expiresAtTimestamp;
	}

	public bool HasValidRegion => IsActive
		&& Right > Left
		&& Bottom > Top
		&& ExpiresAtTimestamp > ProducedAtTimestamp;

	protected override void DisposeOwnedResources()
	{
	}
}
