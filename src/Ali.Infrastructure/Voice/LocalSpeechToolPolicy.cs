using System.Text.RegularExpressions;

namespace Ali.Infrastructure.Voice;

public static partial class LocalSpeechToolPolicy
{
    public static void EnsureLocalOnly(string purpose, params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && CloudUrlRegex().IsMatch(value))
            {
                throw new InvalidOperationException(
                    $"{purpose} must be local-only. Cloud or hosted speech endpoints are not allowed.");
            }
        }
    }

    public static bool ContainsCloudReference(params string?[] values) =>
        values.Any(value => !string.IsNullOrWhiteSpace(value) && CloudUrlRegex().IsMatch(value));

    [GeneratedRegex(@"https?://", RegexOptions.IgnoreCase)]
    private static partial Regex CloudUrlRegex();
}
