using Ali.Modules.Voice;

namespace Ali.Modules.Voice;

public sealed record VoiceDiagnosticSample(
    VoiceAudioInput AudioInput,
    VoiceCaptureDiagnostics Diagnostics,
    int InputDeviceNumber,
    string InputDeviceName,
    InputChannelMode ChannelMode,
    string InputChannelLabel,
    string InputPreset,
    double ExtraGainDb,
    bool NormalizeBeforeStt,
    bool RetainDebugAudio);

