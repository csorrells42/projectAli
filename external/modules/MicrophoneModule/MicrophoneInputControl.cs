namespace AvatarBuilder.Modules.Audio.Microphone;

public sealed record MicrophoneInputInfo(
	string Id,
	string Name,
	bool IsDefault);

public interface IMicrophoneInputService
{
	IReadOnlyList<MicrophoneInputInfo> GetAvailableInputs();
	string SelectedInputId { get; }
	string InputStatus { get; }
	double GetInputLevel();
	void SelectInput(string inputId);
}
