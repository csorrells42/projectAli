using Ali.Core.Voice;

namespace Ali.Infrastructure.Voice;

public sealed record VoiceCalibrationResult(
    string Prompt,
    VoiceDiagnosticSample Sample,
    string Transcript,
    string SpeechToTextProvider,
    bool Accepted,
    string GuardMessage,
    bool SpeechDetected,
    bool TooQuiet,
    bool Clipping,
    DateTimeOffset CreatedAt);

public static class VoiceCalibrationEvaluator
{
    public const string CalibrationPrompt = "Ali, this is a microphone test.";

    public static VoiceCalibrationResult Evaluate(
        VoiceDiagnosticSample sample,
        SpeechTranscript? transcript,
        SpeechTranscriptGuardResult guardResult)
    {
        var levelState = sample.Diagnostics.Level.State;
        return new VoiceCalibrationResult(
            CalibrationPrompt,
            sample,
            transcript?.Text ?? string.Empty,
            transcript?.ProviderName ?? string.Empty,
            guardResult.Accepted,
            guardResult.Message,
            levelState is VoiceInputLevelState.Good or VoiceInputLevelState.Clipping,
            levelState == VoiceInputLevelState.TooQuiet || levelState == VoiceInputLevelState.Silence,
            levelState == VoiceInputLevelState.Clipping,
            DateTimeOffset.UtcNow);
    }
}
