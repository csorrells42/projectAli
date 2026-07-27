using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Audio.VoiceActivity;

public sealed class UtteranceOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public long SequenceId { get; }
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public int SampleRate { get; }
	public ReadOnlyMemory<float> Samples { get; }
	public TimeSpan Duration =>
		TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

	internal UtteranceOutput(
		long sequenceId,
		long producedAtTimestamp,
		DateTime producedAtUtc,
		int sampleRate,
		float[] samples)
	{
		SequenceId = sequenceId;
		ProducedAtTimestamp = producedAtTimestamp;
		ProducedAtUtc = producedAtUtc;
		SampleRate = sampleRate;
		Samples = samples;
	}

	protected override void DisposeOwnedResources()
	{
	}
}
