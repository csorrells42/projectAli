namespace Ali.Core.Voice;

public sealed record SpeechTranscriptGuardResult(bool Accepted, string Message);

public static class SpeechTranscriptGuard
{
    public const string RejectionMessage = "I couldn't hear that clearly. Try again or check the microphone. I did not run a command.";

    public static SpeechTranscriptGuardResult Evaluate(string? transcript, bool requireAssistantName = false)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage);
        }

        var normalized = transcript.Trim();
        if (normalized.Length < 3)
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage);
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length >= 4 && words.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage);
        }

        if (requireAssistantName && !ContainsAssistantName(words))
        {
            return new SpeechTranscriptGuardResult(false, RejectionMessage);
        }

        return new SpeechTranscriptGuardResult(true, string.Empty);
    }

    private static bool ContainsAssistantName(IEnumerable<string> words) =>
        words.Any(word => string.Equals(TrimWord(word), "Ali", StringComparison.OrdinalIgnoreCase));

    private static string TrimWord(string word) =>
        new(word.Where(char.IsLetterOrDigit).ToArray());
}
