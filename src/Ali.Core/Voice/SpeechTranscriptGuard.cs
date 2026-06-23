namespace Ali.Core.Voice;

public sealed record SpeechTranscriptGuardResult(bool Accepted, string Message, string Reason = "");

public static class SpeechTranscriptGuard
{
    public const string RejectionMessage = "I couldn't hear that clearly. Try again or check the microphone. I did not run a command.";
    public const string EmptyReason = "empty transcript";
    public const string TooShortReason = "too short";
    public const string RepeatedTextReason = "repeated text";
    public const string MissingAssistantNameReason = "missing required assistant name";

    public static SpeechTranscriptGuardResult Evaluate(string? transcript, bool requireAssistantName = false)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage, EmptyReason);
        }

        var normalized = transcript.Trim();
        if (normalized.Length < 3)
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage, TooShortReason);
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length >= 4 && words.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage, RepeatedTextReason);
        }

        if (requireAssistantName && !ContainsAssistantName(words))
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage, MissingAssistantNameReason);
        }

        return new SpeechTranscriptGuardResult(true, string.Empty);
    }

    private static bool ContainsAssistantName(IEnumerable<string> words) =>
        words.Any(word => string.Equals(TrimWord(word), "Ali", StringComparison.OrdinalIgnoreCase));

    private static string TrimWord(string word) =>
        new(word.Where(char.IsLetterOrDigit).ToArray());
}
