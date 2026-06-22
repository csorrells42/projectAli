using Ali.Core.Voice;

namespace Ali.Infrastructure.Voice;

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

