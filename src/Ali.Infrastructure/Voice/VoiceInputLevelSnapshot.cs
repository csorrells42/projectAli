namespace Ali.Infrastructure.Voice;

public sealed record VoiceInputLevelSnapshot(
    int DeviceNumber,
    string DeviceName,
    int SampleRate,
    int Channels,
    double Rms,
    double Peak,
    double LevelPercent,
    VoiceInputLevelState State,
    DateTimeOffset CapturedAt)
{
    public bool IsSilent => State == VoiceInputLevelState.Silence;

    public bool IsTooQuiet => State == VoiceInputLevelState.TooQuiet;

    public bool IsClipping => State == VoiceInputLevelState.Clipping;

    public string Summary => State switch
    {
        VoiceInputLevelState.Silence => "No speech signal detected.",
        VoiceInputLevelState.TooQuiet => "Input is too quiet.",
        VoiceInputLevelState.Clipping => "Input is clipping.",
        _ => "Input level looks usable."
    };
}
