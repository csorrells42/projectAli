using System.Text;
using AvatarBuilder.Modules.Audio.SpeechToText;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Confidence;

public sealed class InteractionConfidenceModule :
	LatestValueAudioModule<TranscriptionOutput, InteractionConfidenceOutput>
{
	private const string DatabaseFileName = "ali_interactions.sqlite";
	private readonly string _databasePath;
	private InteractionConfidenceStore? _store;

	public string DatabasePath => _databasePath;

	public InteractionConfidenceModule(
		IModuleOutputSource<TranscriptionOutput> aliTranscriptions,
		string dataFolder)
		: base(aliTranscriptions,
			"Interaction confidence SQLite logger",
			ThreadPriority.BelowNormal)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
		_databasePath = Path.Combine(Path.GetFullPath(dataFolder),
			"AvatarSystem", "InteractionConfidence", DatabaseFileName);
	}

	protected override InteractionConfidenceOutput? Process(
		TranscriptionOutput input)
	{
		if (!input.Transcription.Succeeded
			|| !input.Interaction.Decision.AllowSpeechToText)
		{
			return null;
		}
		_store ??= new InteractionConfidenceStore(_databasePath);
		var utterance = input.Interaction.Utterance;
		var decision = input.Interaction.Decision;
		byte[] wave = BuildPcm16Wave(utterance.Samples.Span,
			utterance.SampleRate);
		long rowId = _store.Write(new InteractionConfidenceRecord(
			input.SequenceId, utterance.ProducedAtUtc, input.ProducedAtUtc,
			decision.AttentionSource, input.ExactTextForAli,
			input.Transcription.Provider, input.Transcription.Status,
			decision.PersonIdentityId, decision.ParticipantDisplayName,
			decision.VisualPersonIdentityId,
			decision.VisualIdentityConfidence,
			decision.VoicePersonIdentityId,
			decision.VoiceIdentityConfidence,
			decision.IdentitySignalsAgree, decision.Reason,
			utterance.SampleRate, utterance.Duration, wave));
		return new InteractionConfidenceOutput(
			input.SequenceId,
			rowId,
			_databasePath);
	}

	protected override void DisposeModule() => _store?.Dispose();

	internal static byte[] BuildPcm16Wave(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		using var stream = new MemoryStream(
			44 + checked(samples.Length * sizeof(short)));
		using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
		int dataLength = checked(samples.Length * sizeof(short));
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(36 + dataLength);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
		writer.Write(16);
		writer.Write((short)1);
		writer.Write((short)1);
		writer.Write(sampleRate);
		writer.Write(sampleRate * sizeof(short));
		writer.Write((short)sizeof(short));
		writer.Write((short)16);
		writer.Write(Encoding.ASCII.GetBytes("data"));
		writer.Write(dataLength);
		foreach (float sample in samples)
		{
			writer.Write((short)Math.Round(
				Math.Clamp(sample, -1f, 1f) * short.MaxValue));
		}
		writer.Flush();
		return stream.ToArray();
	}
}
