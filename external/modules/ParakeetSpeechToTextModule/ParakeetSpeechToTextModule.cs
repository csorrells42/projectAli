using AvatarBuilder.Modules.Audio.SpeechToText;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Security;

namespace AvatarBuilder.Modules.Audio.ParakeetSpeechToText;

/// <summary>
/// Drop-in local Parakeet producer. It consumes only authorized utterances,
/// owns one latest-value worker, and publishes the shared transcription output.
/// </summary>
public sealed class ParakeetSpeechToTextModule :
	LatestValueAudioModule<AuthorizedInteractionOutput, TranscriptionOutput>,
	ISpeechToTextModule
{
	internal const string Provider = "Local Parakeet TDT v2 int8";

	private readonly ParakeetModelInfo _model;
	private readonly int _numThreads;
	private ParakeetRecognizer? _recognizer;
	private string _configurationStatus;

	public string ProviderName => Provider;

	public bool IsConfigured => _model.IsReady
		&& !ConfigurationStatus.StartsWith(
			"Parakeet unavailable:",
			StringComparison.Ordinal);

	public string ConfigurationStatus =>
		System.Threading.Volatile.Read(ref _configurationStatus);

	public ParakeetSpeechToTextModule(
		IModuleOutputSource<AuthorizedInteractionOutput> authorized,
		string? modelFolder = null,
		int numThreads = 2)
		: base(authorized, "Authorized Parakeet speech-to-text worker")
	{
		_model = ParakeetModelInfo.Load(modelFolder);
		_numThreads = Math.Max(1, numThreads);
		_configurationStatus = _model.Status;
	}

	protected override TranscriptionOutput Process(
		AuthorizedInteractionOutput input)
	{
		ParakeetRecognizer? recognizer;
		try
		{
			recognizer = EnsureRecognizer();
		}
		catch (Exception exception)
		{
			string status = "Parakeet unavailable: " + exception.Message;
			System.Threading.Volatile.Write(
				ref _configurationStatus,
				status);
			recognizer = null;
		}
		SpeechTranscription transcription = recognizer is null
			? new(
				"",
				ProviderName,
				false,
				ConfigurationStatus)
			: recognizer.Transcribe(
				input.Utterance.Samples.Span,
				input.Utterance.SampleRate);
		return new TranscriptionOutput(input, transcription);
	}

	protected override void DisposeModule() => _recognizer?.Dispose();

	private ParakeetRecognizer? EnsureRecognizer()
	{
		if (!_model.IsReady)
		{
			return null;
		}
		return _recognizer ??= new ParakeetRecognizer(_model, _numThreads);
	}
}
