using System;
using System.Diagnostics;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.MediaPipe;

namespace AvatarBuilder.Modules.Vision.Attention;

/// <summary>
/// Converts official MediaPipe face-presence and head-pose output into a
/// debounced attention signal. No iris inference or estimated face box is used.
/// </summary>
public sealed class AttentionModule :
	LatestValueModule<MediaPipeOutput, AttentionOutput>
{
	private readonly AttentionStateDebouncer _debouncer =
		new(TimeSpan.FromMilliseconds(250));
	private int _latestStableAttention;

	public bool LatestStableAttention =>
		Volatile.Read(ref _latestStableAttention) != 0;

	public AttentionModule(
		IModuleOutputSource<MediaPipeOutput> mediaPipe)
		: base(mediaPipe, "Attention MediaPipe pose worker")
	{
	}

	protected override AttentionOutput Process(MediaPipeOutput input)
	{
		var landmarks = input.Tracking.LandmarkFrame;
		bool observed =
			input.Tracking.HasFace
			&& landmarks.HasFace
			&& AttentionPosePolicy.IsAccepted(
				landmarks.HeadYawDegrees,
				landmarks.HeadPitchDegrees,
				landmarks.HeadRollDegrees);
		bool stable = _debouncer.Update(
			observed,
			Stopwatch.GetTimestamp());
		Volatile.Write(
			ref _latestStableAttention,
			stable ? 1 : 0);
		return new AttentionOutput(input, observed, stable);
	}
}
