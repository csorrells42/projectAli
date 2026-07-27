using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Audio.Microphone;

public sealed class MicrophoneOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public long SequenceId { get; }
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public int SampleRate { get; }
	public int Channels { get; }
	public ReadOnlyMemory<float> Samples { get; }

	internal MicrophoneOutput(
		long sequenceId,
		long producedAtTimestamp,
		DateTime producedAtUtc,
		int sampleRate,
		int channels,
		float[] samples)
	{
		SequenceId = sequenceId;
		ProducedAtTimestamp = producedAtTimestamp;
		ProducedAtUtc = producedAtUtc;
		SampleRate = sampleRate;
		Channels = channels;
		Samples = samples;
	}

	protected override void DisposeOwnedResources()
	{
	}
}
