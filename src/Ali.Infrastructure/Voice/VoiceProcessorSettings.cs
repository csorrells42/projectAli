namespace Ali.Infrastructure.Voice;

public sealed record VoiceProcessorSettings(
    bool HighPassEnabled = true,
    double HighPassFrequencyHz = 80,
    bool NoiseGateEnabled = true,
    double NoiseGateThresholdDb = -48,
    bool NoiseSuppressionEnabled = true,
    double NoiseSuppressionAmountDb = 6,
    bool EchoReducerEnabled = false,
    double EchoReducerAmountDb = 4,
    bool CompressorEnabled = true,
    double CompressorThresholdDb = -18,
    double CompressorRatio = 3,
    bool DeEsserEnabled = false,
    double DeEsserAmountDb = 3,
    bool DePopperEnabled = true,
    double DePopperAmountDb = 6,
    double MakeupGainDb = 2,
    bool LimiterEnabled = true,
    double LimiterCeilingDb = -1);
