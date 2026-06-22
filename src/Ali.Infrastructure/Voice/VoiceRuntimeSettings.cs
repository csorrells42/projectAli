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
    string SelectedInputPreset = VoiceInputPreset.HeadsetMic);
