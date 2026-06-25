using System.Text;

namespace Ali.Infrastructure.Coding;

internal static class SimplePdfWriter
{
    private const int PageWidth = 612;
    private const int PageHeight = 792;
    private const int MarginLeft = 54;
    private const int FirstLineY = 738;
    private const int LineHeight = 14;
    private const int LinesPerPage = 48;
    private const int CharactersPerLine = 86;

    public static byte[] BuildTextPdf(string title, string text)
    {
        var lines = WrapLines(text);
        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        var pages = lines.Chunk(LinesPerPage).Select(page => page.ToArray()).ToList();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var pageObjectIds = new List<int>();
        foreach (var pageLines in pages)
        {
            var pageObjectId = objects.Count + 1;
            var contentObjectId = pageObjectId + 1;
            pageObjectIds.Add(pageObjectId);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObjectId} 0 R >>");
            objects.Add(BuildContentObject(title, pageLines));
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", pageObjectIds.Select(id => $"{id} 0 R"))}] /Count {pageObjectIds.Count} >>";
        return WritePdf(objects);
    }

    private static string BuildContentObject(string title, IReadOnlyList<string> lines)
    {
        var stream = new StringBuilder();
        stream.AppendLine("BT");
        stream.AppendLine("/F1 11 Tf");
        stream.AppendLine($"{MarginLeft} {FirstLineY} Td");
        stream.AppendLine($"{LineHeight} TL");
        if (!string.IsNullOrWhiteSpace(title))
        {
            stream.AppendLine($"({EscapeText(title)}) Tj");
            stream.AppendLine("T*");
            stream.AppendLine("T*");
        }

        foreach (var line in lines)
        {
            stream.AppendLine($"({EscapeText(line)}) Tj");
            stream.AppendLine("T*");
        }

        stream.AppendLine("ET");
        var streamText = stream.ToString();
        var streamLength = Encoding.ASCII.GetByteCount(streamText);
        return $"<< /Length {streamLength} >>{Environment.NewLine}stream{Environment.NewLine}{streamText}endstream";
    }

    private static byte[] WritePdf(IReadOnlyList<string> objects)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            WriteAscii(stream, $"{index + 1} 0 obj\n");
            WriteAscii(stream, objects[index]);
            WriteAscii(stream, "\nendobj\n");
        }

        var xrefOffset = stream.Position;
        WriteAscii(stream, $"xref\n0 {objects.Count + 1}\n");
        WriteAscii(stream, "0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            WriteAscii(stream, $"{offset:0000000000} 00000 n \n");
        }

        WriteAscii(stream, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return stream.ToArray();
    }

    private static List<string> WrapLines(string text)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var words = NormalizePdfText(paragraph)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = new StringBuilder();
            foreach (var word in words)
            {
                if (current.Length == 0)
                {
                    AppendWrappedWord(lines, current, word);
                    continue;
                }

                if (current.Length + 1 + word.Length <= CharactersPerLine)
                {
                    current.Append(' ').Append(word);
                    continue;
                }

                lines.Add(current.ToString());
                current.Clear();
                AppendWrappedWord(lines, current, word);
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines;
    }

    private static void AppendWrappedWord(List<string> lines, StringBuilder current, string word)
    {
        var remaining = word;
        while (remaining.Length > CharactersPerLine)
        {
            lines.Add(remaining[..CharactersPerLine]);
            remaining = remaining[CharactersPerLine..];
        }

        current.Append(remaining);
    }

    private static string NormalizePdfText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '\t' => ' ',
                >= ' ' and <= '~' => character,
                _ => '?'
            });
        }

        return builder.ToString();
    }

    private static string EscapeText(string text) =>
        NormalizePdfText(text)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static void WriteAscii(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
