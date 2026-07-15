namespace Ali.Modules.Memory;

public enum MemoryRequestKind
{
    None,
    Save,
    Forget,
    Ambiguous
}

public sealed record MemoryRequestDecision(
    MemoryRequestKind Kind,
    string? Text,
    MemorySensitivity Sensitivity,
    string Message)
{
    public static MemoryRequestDecision None { get; } =
        new(MemoryRequestKind.None, null, MemorySensitivity.Normal, string.Empty);
}

public static class MemoryRequestParser
{
    private static readonly string[] SavePrefixes =
    [
        "remember that ",
        "remember this,",
        "remember this:",
        "remember this ",
        "remember,",
        "save this ",
        "from now on "
    ];

    private static readonly string[] ForgetPrefixes =
    [
        "forget that ",
        "forget this "
    ];

    private static readonly string[] SensitiveWords =
    [
        "password",
        "passcode",
        "social security",
        "ssn",
        "credit card",
        "bank account",
        "routing number",
        "medical record"
    ];

    public static MemoryRequestDecision Evaluate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return MemoryRequestDecision.None;
        }

        var trimmed = text.Trim();
        foreach (var prefix in SavePrefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var memoryText = trimmed[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(memoryText))
            {
                return new MemoryRequestDecision(
                    MemoryRequestKind.Ambiguous,
                    null,
                    MemorySensitivity.Normal,
                    "Memory request was ambiguous and was not saved.");
            }

            var sensitivity = DetectSensitivity(memoryText);
            return new MemoryRequestDecision(
                MemoryRequestKind.Save,
                memoryText,
                sensitivity,
                sensitivity == MemorySensitivity.PotentiallySensitive
                    ? "Memory looked potentially sensitive and was not saved without confirmation."
                    : "Memory saved locally.");
        }

        foreach (var prefix in ForgetPrefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = trimmed[prefix.Length..].Trim();
            return string.IsNullOrWhiteSpace(target)
                ? new MemoryRequestDecision(
                    MemoryRequestKind.Ambiguous,
                    null,
                    MemorySensitivity.Normal,
                    "Forget request was ambiguous and no memory was removed.")
                : new MemoryRequestDecision(
                    MemoryRequestKind.Forget,
                    target,
                    MemorySensitivity.Normal,
                    "Matching memories removed locally.");
        }

        if (trimmed.StartsWith("remember", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("forget", StringComparison.OrdinalIgnoreCase))
        {
            return new MemoryRequestDecision(
                MemoryRequestKind.Ambiguous,
                null,
                MemorySensitivity.Normal,
                "Memory request was ambiguous and was not saved.");
        }

        return MemoryRequestDecision.None;
    }

    private static MemorySensitivity DetectSensitivity(string text) =>
        SensitiveWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))
            ? MemorySensitivity.PotentiallySensitive
            : MemorySensitivity.Normal;
}
