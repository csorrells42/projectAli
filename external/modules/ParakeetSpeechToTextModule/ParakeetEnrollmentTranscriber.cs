using AvatarBuilder.Modules.Audio.SpeakerRecognition;

namespace AvatarBuilder.Modules.Audio.ParakeetSpeechToText;

public sealed class ParakeetEnrollmentTranscriber :
	IEnrollmentTranscriber
{
	private readonly ParakeetModelInfo _model;
	private readonly int _numThreads;
	private ParakeetRecognizer? _recognizer;

	public string ProviderName => ParakeetSpeechToTextModule.Provider;

	public bool IsConfigured => _model.IsReady;

	public ParakeetEnrollmentTranscriber(
		string? modelFolder = null,
		int numThreads = 2)
	{
		_model = ParakeetModelInfo.Load(modelFolder);
		_numThreads = Math.Max(1, numThreads);
	}

	public EnrollmentTranscription Transcribe(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		if (!_model.IsReady)
		{
			return new("", ProviderName, false, _model.Status);
		}
		try
		{
			_recognizer ??= new ParakeetRecognizer(_model, _numThreads);
			var result = _recognizer.Transcribe(samples, sampleRate);
			return new(
				result.Text,
				result.Provider,
				result.Succeeded,
				result.Status);
		}
		catch (Exception exception)
		{
			return new(
				"",
				ProviderName,
				false,
				"Parakeet unavailable: " + exception.Message);
		}
	}

	public void Dispose() => _recognizer?.Dispose();
}
