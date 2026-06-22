namespace Ali.Infrastructure.Voice;

public sealed record VoiceRuntimeSettings(
    int? SelectedInputDeviceNumber = null,
    string? SelectedInputDeviceName = null,
    int? SelectedOutputDeviceNumber = null,
    string? SelectedOutputDeviceName = null,
    int? LastSuccessfulSttDeviceNumber = null,
    string? LastSuccessfulSttDeviceName = null,
    int? LastSuccessfulTtsDeviceNumber = null,
    string? LastSuccessfulTtsDeviceName = null,
    string SelectedInputPreset = VoiceInputPreset.HeadsetMic,
    string SelectedInputChannelMode = nameof(InputChannelMode.HighestEnergy),
    double ExtraInputGainDb = 0,
    bool NormalizeBeforeStt = false,
    bool RetainDebugAudio = false,
    string? WhisperExecutablePath = null,
    string? WhisperModelPath = null,
    string? WhisperArgumentsTemplate = null,
    string? PiperExecutablePath = null,
    string? PiperModelPath = null,
    string? PiperVoiceId = null,
    string? PiperArgumentsTemplate = null);
