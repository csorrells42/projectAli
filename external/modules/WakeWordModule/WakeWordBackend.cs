namespace AvatarBuilder.Modules.Audio.WakeWord;

public sealed record WakeWordEvidence(
	bool Detected,
	string AssistantName,
	double Confidence,
	string Status);

public interface IWakeWordBackend : IDisposable
{
	string AssistantName { get; }
	void SetAssistantName(string assistantName);
	WakeWordEvidence Detect(
		ReadOnlySpan<float> samples,
		int sampleRate);
}

public sealed class UnconfiguredWakeWordBackend : IWakeWordBackend
{
	private string _assistantName;

	public string AssistantName => _assistantName;

	public UnconfiguredWakeWordBackend(string assistantName)
	{
		_assistantName = Normalize(assistantName);
	}

	public void SetAssistantName(string assistantName)
	{
		_assistantName = Normalize(assistantName);
	}

	public WakeWordEvidence Detect(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		return new WakeWordEvidence(
			false,
			_assistantName,
			0,
			"Wake-word model not configured");
	}

	public void Dispose()
	{
	}

	private static string Normalize(string value) =>
		string.IsNullOrWhiteSpace(value) ? "Ali" : value.Trim();
}
