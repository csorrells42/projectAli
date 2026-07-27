using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Audio.VoiceActivity;

namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public sealed class SpeakerRecognitionOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public UtteranceOutput Utterance { get; }
	public long SequenceId => Utterance.SequenceId;
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public SpeakerRecognitionEvidence Evidence { get; }

	internal SpeakerRecognitionOutput(
		UtteranceOutput utterance,
		SpeakerRecognitionEvidence evidence)
	{
		utterance.RetainForDownstream();
		Utterance = utterance;
		Evidence = evidence;
		ProducedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		ProducedAtUtc = DateTime.UtcNow;
	}

	protected override void DisposeOwnedResources()
	{
		Utterance.Dispose();
	}
}
