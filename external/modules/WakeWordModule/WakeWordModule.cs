using AvatarBuilder.Modules.Audio.VoiceActivity;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Audio.WakeWord;

public sealed class WakeWordModule :
	LatestValueAudioModule<UtteranceOutput, WakeWordOutput>
{
	private readonly IWakeWordBackend _backend;

	public string AssistantName => _backend.AssistantName;

	public WakeWordModule(
		IModuleOutputSource<UtteranceOutput> utterances,
		string assistantName,
		IWakeWordBackend? backend = null)
		: base(utterances, "Dynamic wake-word worker")
	{
		_backend = backend
			?? SherpaWakeWordBackend.CreateFromEnvironment(
				assistantName);
		_backend.SetAssistantName(assistantName);
	}

	public void SetAssistantName(string assistantName)
	{
		_backend.SetAssistantName(assistantName);
	}

	protected override WakeWordOutput Process(UtteranceOutput input)
	{
		WakeWordEvidence evidence = _backend.Detect(
			input.Samples.Span,
			input.SampleRate);
		return new WakeWordOutput(input.SequenceId, evidence);
	}

	protected override void DisposeModule()
	{
		_backend.Dispose();
	}
}
