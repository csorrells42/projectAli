namespace Ali.Modules.Conversation;

public abstract record MarkdownMessageBlock;

public sealed record MarkdownParagraphBlock(string Text) : MarkdownMessageBlock;

public sealed record MarkdownHeadingBlock(int Level, string Text) : MarkdownMessageBlock;

public sealed record MarkdownListItemBlock(string Marker, string Text) : MarkdownMessageBlock;

public sealed record MarkdownCodeBlock(string Text) : MarkdownMessageBlock;

public sealed record MarkdownTableBlock(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownMessageBlock;

public static class MarkdownMessageParser
{
    public static IReadOnlyList<MarkdownMessageBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Array.Empty<MarkdownMessageBlock>();
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var blocks = new List<MarkdownMessageBlock>();
        for (var index = 0; index < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new List<string>();
                while (index < lines.Length
                       && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index++]);
                }

                if (index < lines.Length)
                {
                    index++;
                }

                blocks.Add(new MarkdownCodeBlock(string.Join(Environment.NewLine, code)));
                continue;
            }

            if (index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))
            {
                var headers = SplitCells(lines[index]);
                index += 2;
                var rows = new List<IReadOnlyList<string>>();
                while (index < lines.Length
                       && !string.IsNullOrWhiteSpace(lines[index])
                       && lines[index].Contains('|'))
                {
                    rows.Add(NormalizeCells(SplitCells(lines[index]), headers.Count));
                    index++;
                }

                blocks.Add(new MarkdownTableBlock(headers, rows));
                continue;
            }

            if (TryReadHeading(lines[index], out var level, out var heading))
            {
                blocks.Add(new MarkdownHeadingBlock(level, heading));
                index++;
                continue;
            }

            if (TryReadListItem(lines[index], out var marker, out var item))
            {
                blocks.Add(new MarkdownListItemBlock(marker, item));
                index++;
                continue;
            }

            var paragraph = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                if (paragraph.Count > 0
                    && (TryReadHeading(lines[index], out _, out _)
                        || TryReadListItem(lines[index], out _, out _)
                        || lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)
                        || (index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))))
                {
                    break;
                }

                paragraph.Add(lines[index].Trim());
                index++;
            }

            blocks.Add(new MarkdownParagraphBlock(string.Join(" ", paragraph)));
        }

        return blocks;
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        var trimmed = line.TrimStart();
        level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
        {
            level++;
        }

        if (level == 0 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            text = string.Empty;
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryReadListItem(string line, out string marker, out string text)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length > 2
            && (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("+ ", StringComparison.Ordinal)))
        {
            marker = "•";
            text = trimmed[2..].Trim();
            return true;
        }

        var digitCount = 0;
        while (digitCount < trimmed.Length && char.IsDigit(trimmed[digitCount]))
        {
            digitCount++;
        }

        if (digitCount > 0
            && digitCount + 1 < trimmed.Length
            && trimmed[digitCount] == '.'
            && trimmed[digitCount + 1] == ' ')
        {
            marker = trimmed[..(digitCount + 1)];
            text = trimmed[(digitCount + 2)..].Trim();
            return true;
        }

        marker = string.Empty;
        text = string.Empty;
        return false;
    }

    private static bool IsTableSeparator(string line)
    {
        var cells = SplitCells(line);
        return cells.Count > 0 && cells.All(cell =>
        {
            var rule = cell.Trim().Trim(':');
            return rule.Length >= 3 && rule.All(character => character == '-');
        });
    }

    private static IReadOnlyList<string> SplitCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var character in trimmed)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static IReadOnlyList<string> NormalizeCells(IReadOnlyList<string> cells, int count)
    {
        var normalized = new string[count];
        for (var index = 0; index < count; index++)
        {
            normalized[index] = index < cells.Count ? cells[index] : string.Empty;
        }

        return normalized;
    }
}
