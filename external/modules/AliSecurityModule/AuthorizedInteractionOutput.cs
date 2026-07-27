using System;
using AvatarBuilder.Modules.Audio.VoiceActivity;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Security;

public sealed class AuthorizedInteractionOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public UtteranceOutput Utterance { get; }
	public long SequenceId => Utterance.SequenceId;
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public SecurityDecision Decision { get; }

	internal AuthorizedInteractionOutput(
		UtteranceOutput utterance,
		SecurityDecision decision)
	{
		utterance.RetainForDownstream();
		Utterance = utterance;
		Decision = decision;
		ProducedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		ProducedAtUtc = DateTime.UtcNow;
	}

	protected override void DisposeOwnedResources()
	{
		Utterance.Dispose();
	}
}
