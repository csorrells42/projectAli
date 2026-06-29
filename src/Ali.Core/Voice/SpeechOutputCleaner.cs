using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ali.Core.Voice;

public static partial class SpeechOutputCleaner
{
    private const int MaxSpokenCharacters = 900;

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text.ReplaceLineEndings("\n");
        cleaned = ThinkingBlockRegex().Replace(cleaned, " ");
        cleaned = UnclosedThinkingRegex().Replace(cleaned, " ");
        cleaned = CodeBlockRegex().Replace(cleaned, " Code block omitted. ");
        cleaned = StackTraceLineRegex().Replace(cleaned, " ");
        cleaned = SourceAppendixRegex().Replace(cleaned, " ");
        cleaned = UrlRegex().Replace(cleaned, " ");
        cleaned = MetadataLineRegex().Replace(cleaned, " ");
        cleaned = CitationRegex().Replace(cleaned, string.Empty);
        cleaned = InlineCodeRegex().Replace(cleaned, "$1");
        cleaned = RemoveEmoticons(cleaned);
        cleaned = MarkdownMarkerRegex().Replace(cleaned, string.Empty);
        cleaned = BulletPrefixRegex().Replace(cleaned, string.Empty);
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        cleaned = SpaceBeforePunctuationRegex().Replace(cleaned, "$1");

        if (cleaned.Length <= MaxSpokenCharacters)
        {
            return cleaned;
        }

        var trimmed = cleaned[..MaxSpokenCharacters].Trim();
        var lastSentence = Math.Max(
            trimmed.LastIndexOf('.'),
            Math.Max(
                trimmed.LastIndexOf('!'),
                trimmed.LastIndexOf('?')));

        return lastSentence > 120
            ? trimmed[..(lastSentence + 1)].Trim()
            : $"{trimmed.TrimEnd('.', ',', ';', ':')}...";
    }

    private static string RemoveEmoticons(string text)
    {
        foreach (var emoticon in CommonAsciiEmoticons)
        {
            text = text.Replace(emoticon, " ", StringComparison.Ordinal);
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    private static readonly string[] CommonAsciiEmoticons =
    [
        ":-)",
        ":)",
        ";-)",
        ";)",
        ":-D",
        ":D",
        ":-(",
        ":(",
        ":-P",
        ":P",
        ";-P",
        ";P",
        ":-/",
        ":/",
        @":-\",
        @":\",
        ":-|",
        ":|",
        "<3",
        "xD",
        "XD"
    ];

    [GeneratedRegex(@"<think\b[^>]*>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ThinkingBlockRegex();

    [GeneratedRegex(@"<think\b[^>]*>.*\z", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UnclosedThinkingRegex();

    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"^\s+at\s+[\w\.`]+\([^\n]*\)\s*$", RegexOptions.Multiline)]
    private static partial Regex StackTraceLineRegex();

    [GeneratedRegex(@"(?:^|\n)\s*Sources checked:\s*(?:\n\s*\[\d+\][^\n]*)*(?=\n{2,}|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceAppendixRegex();

    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^\s*(?:Source|Sources|Confidence|Stable knowledge|Live source used|No live source was required|Runtime|Endpoint|Model|Status)[^\n]*(?:\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex MetadataLineRegex();

    [GeneratedRegex(@"\[(?:\d+|source|citation)[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex CitationRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"[*_#>]")]
    private static partial Regex MarkdownMarkerRegex();

    [GeneratedRegex(@"^\s*[-+]\s+", RegexOptions.Multiline)]
    private static partial Regex BulletPrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuationRegex();
}
