namespace Ali.Modules.Voice;

public static class VoiceInputPreset
{
    public const string Raw = "Raw";
    public const string QuietRoom = "Quiet Room";
    public const string NoisyRoom = "Noisy Room";
    public const string BroadcastMic = "Broadcast Mic / Close Mic";
    public const string HeadsetMic = "Headset Mic";

    public static IReadOnlyList<string> All { get; } =
    [
        Raw,
        QuietRoom,
        NoisyRoom,
        BroadcastMic,
        HeadsetMic
    ];

    public static string Normalize(string? presetName) =>
        All.FirstOrDefault(preset => string.Equals(preset, presetName, StringComparison.OrdinalIgnoreCase)) ?? HeadsetMic;

    public static VoiceProcessorSettings CreateSettings(string? presetName) =>
        Normalize(presetName) switch
        {
            Raw => new VoiceProcessorSettings(
                HighPassEnabled: false,
                NoiseGateEnabled: false,
                NoiseSuppressionEnabled: false,
                EchoReducerEnabled: false,
                CompressorEnabled: false,
                DeEsserEnabled: false,
                DePopperEnabled: false,
                MakeupGainDb: 0,
                LimiterEnabled: true),
            QuietRoom => new VoiceProcessorSettings(
                NoiseGateThresholdDb: -56,
                NoiseSuppressionAmountDb: 3,
                CompressorThresholdDb: -20,
                CompressorRatio: 2.5,
                MakeupGainDb: 6),
            NoisyRoom => new VoiceProcessorSettings(
                HighPassFrequencyHz: 100,
                NoiseGateThresholdDb: -42,
                NoiseSuppressionAmountDb: 10,
                CompressorThresholdDb: -18,
                CompressorRatio: 3.5,
                MakeupGainDb: 7),
            BroadcastMic => new VoiceProcessorSettings(
                HighPassFrequencyHz: 80,
                NoiseGateThresholdDb: -50,
                NoiseSuppressionAmountDb: 5,
                CompressorThresholdDb: -22,
                CompressorRatio: 3,
                DePopperAmountDb: 7,
                MakeupGainDb: 3),
            _ => new VoiceProcessorSettings(
                HighPassFrequencyHz: 100,
                NoiseGateThresholdDb: -50,
                NoiseSuppressionAmountDb: 6,
                CompressorThresholdDb: -18,
                CompressorRatio: 3,
                MakeupGainDb: 9)
        };
}
