using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Security;

namespace AvatarBuilder.Modules.Audio.SpeechToText;

public sealed class SpeechToTextModule :
	LatestValueAudioModule<AuthorizedInteractionOutput, TranscriptionOutput>,
	ISpeechToTextModule
{
	private readonly ISpeechToTextBackend _backend;

	public string ProviderName => _backend.ProviderName;
	public bool IsConfigured => _backend.IsConfigured;

	public SpeechToTextModule(
		IModuleOutputSource<AuthorizedInteractionOutput> authorized,
		ISpeechToTextBackend? backend = null)
		: base(authorized, "Authorized speech-to-text worker")
	{
		_backend = backend ?? WhisperBackendFactory.Create();
	}

	protected override TranscriptionOutput Process(
		AuthorizedInteractionOutput input)
	{
		SpeechTranscription transcription = _backend.Transcribe(
			input.Utterance.Samples.Span,
			input.Utterance.SampleRate);
		return new TranscriptionOutput(input, transcription);
	}

	protected override void DisposeModule()
	{
		_backend.Dispose();
	}
}
