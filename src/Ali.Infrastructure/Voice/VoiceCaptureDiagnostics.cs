namespace Ali.Infrastructure.Voice;

public sealed record VoiceCaptureDiagnostics(
    string FilePath,
    double DurationSeconds,
    int SampleRate,
    int Channels,
    int RmsPcm,
    int PeakPcm,
    VoiceInputLevelSnapshot Level)
{
    public bool IsSilent => Level.IsSilent;

    public bool IsTooQuiet => Level.IsTooQuiet;

    public bool IsClipping => Level.IsClipping;

    public string Summary =>
        $"{DurationSeconds:N2}s | {SampleRate} Hz | {Channels} ch | peak {PeakPcm} | RMS {RmsPcm} | {Level.Summary}";
}
