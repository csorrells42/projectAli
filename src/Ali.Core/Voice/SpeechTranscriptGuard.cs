namespace Ali.Core.Voice;

public sealed record SpeechTranscriptGuardResult(bool Accepted, string Message, string Reason = "");

public static class SpeechTranscriptGuard
{
    public const string RejectionMessage = "I couldn't hear that clearly. Try again or check the microphone. I did not run a command.";
    public const string EmptyReason = "empty transcript";
    public const string TooShortReason = "too short";
    public const string RepeatedTextReason = "repeated text";
    public const string MissingAssistantNameReason = "missing required assistant name";

    public static string NormalizeAssistantName(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return string.Empty;
        }

        var words = transcript.Split(' ', StringSplitOptions.None);
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = NormalizeAssistantNameWord(words[index]);
        }

        return string.Join(' ', words);
    }

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
        words.Any(word => IsAssistantName(TrimWord(word)));

    private static bool IsAssistantName(string word) =>
        word.Equals("Ali", StringComparison.OrdinalIgnoreCase)
        || word.Equals("Allie", StringComparison.OrdinalIgnoreCase)
        || word.Equals("Ally", StringComparison.OrdinalIgnoreCase)
        || word.Equals("Aly", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAssistantNameWord(string word)
    {
        var leadingLength = word.TakeWhile(character => !char.IsLetterOrDigit(character)).Count();
        var trailingLength = word.Reverse().TakeWhile(character => !char.IsLetterOrDigit(character)).Count();
        var coreLength = word.Length - leadingLength - trailingLength;
        if (coreLength <= 0)
        {
            return word;
        }

        var leading = word[..leadingLength];
        var core = word.Substring(leadingLength, coreLength);
        var trailing = trailingLength == 0 ? string.Empty : word[^trailingLength..];
        return IsAssistantName(core) ? $"{leading}Ali{trailing}" : word;
    }

    private static string TrimWord(string word) =>
        new(word.Where(char.IsLetterOrDigit).ToArray());
}
