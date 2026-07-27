using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Audio.WakeWord;

public sealed class WakeWordOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public long SequenceId { get; }
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public WakeWordEvidence Evidence { get; }

	internal WakeWordOutput(
		long sequenceId,
		WakeWordEvidence evidence)
	{
		SequenceId = sequenceId;
		Evidence = evidence;
		ProducedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		ProducedAtUtc = DateTime.UtcNow;
	}

	protected override void DisposeOwnedResources()
	{
	}
}
