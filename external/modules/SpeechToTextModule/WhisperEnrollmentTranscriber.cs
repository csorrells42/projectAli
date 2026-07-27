using AvatarBuilder.Modules.Audio.SpeakerRecognition;

namespace AvatarBuilder.Modules.Audio.SpeechToText;

public sealed class WhisperEnrollmentTranscriber :
	IEnrollmentTranscriber
{
	private readonly Lazy<ISpeechToTextBackend> _backend = new(
		() => WhisperBackendFactory.Create(TimeSpan.FromSeconds(15)),
		LazyThreadSafetyMode.ExecutionAndPublication);

	public string ProviderName => "Local Whisper CLI";

	public bool IsConfigured => _backend.Value.IsConfigured;

	public EnrollmentTranscription Transcribe(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		SpeechTranscription result = _backend.Value.Transcribe(
			samples,
			sampleRate);
		return new(
			result.Text,
			result.Provider,
			result.Succeeded,
			result.Status);
	}

	public void Dispose()
	{
		if (_backend.IsValueCreated)
		{
			_backend.Value.Dispose();
		}
	}
}
