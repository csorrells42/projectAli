using System.Text;

namespace Ali.Core.Conversations;

public static class ConversationTitleFactory
{
    private const int MaxTitleLength = 64;

    public static string CreateFromFirstMessage(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Untitled chat";
        }

        return normalized.Length <= MaxTitleLength
            ? normalized
            : $"{normalized[..(MaxTitleLength - 3)].TrimEnd()}...";
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasWhiteSpace = true;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasWhiteSpace)
                {
                    builder.Append(' ');
                    lastWasWhiteSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasWhiteSpace = false;
        }

        return builder.ToString();
    }
}
