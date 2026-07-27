using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Security;

namespace AvatarBuilder.Modules.Audio.SpeechToText;

public sealed class TranscriptionOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public AuthorizedInteractionOutput Interaction { get; }
	public long SequenceId => Interaction.SequenceId;
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public SpeechTranscription Transcription { get; }
	public string ExactTextForAli => Transcription.Succeeded
		? Transcription.Text
		: "";

	internal TranscriptionOutput(
		AuthorizedInteractionOutput interaction,
		SpeechTranscription transcription)
	{
		interaction.RetainForDownstream();
		Interaction = interaction;
		Transcription = transcription;
		ProducedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		ProducedAtUtc = DateTime.UtcNow;
	}

	protected override void DisposeOwnedResources()
	{
		Interaction.Dispose();
	}
}
