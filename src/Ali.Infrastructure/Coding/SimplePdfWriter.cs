using System.Text;

namespace Ali.Infrastructure.Coding;

internal static class SimplePdfWriter
{
    private const int PageWidth = 612;
    private const int PageHeight = 792;
    private const int MarginLeft = 54;
    private const int MarginRight = 54;
    private const int ContentWidth = PageWidth - MarginLeft - MarginRight;
    private const int TopY = 724;
    private const int BottomY = 72;
    private const int BodyFontSize = 10;
    private const int BodyLineHeight = 13;
    private const int CharactersPerLine = 92;

    public static byte[] BuildTextPdf(string title, string text) =>
        BuildTextPdf(new SimplePdfDocument(title, null, text, DateTimeOffset.Now, "Ali"));

    public static byte[] BuildTextPdf(SimplePdfDocument document)
    {
        var blocks = BuildBlocks(document.Body);
        if (blocks.Count == 0)
        {
            blocks.Add(PdfBlock.Body(string.Empty));
        }

        var pages = Paginate(blocks);
        if (pages.Count == 0)
        {
            pages.Add([]);
        }

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"
        };

        var pageObjectIds = new List<int>();
        for (var index = 0; index < pages.Count; index++)
        {
            var pageObjectId = objects.Count + 1;
            var contentObjectId = pageObjectId + 1;
            pageObjectIds.Add(pageObjectId);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObjectId} 0 R >>");
            objects.Add(BuildContentObject(document, pages[index], index + 1, pages.Count));
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", pageObjectIds.Select(id => $"{id} 0 R"))}] /Count {pageObjectIds.Count} >>";
        return WritePdf(objects);
    }

    private static List<PdfBlock> BuildBlocks(string text)
    {
        var blocks = new List<PdfBlock>();
        foreach (var rawParagraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var paragraph = NormalizePdfText(rawParagraph).TrimEnd();
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                blocks.Add(PdfBlock.Spacer());
                continue;
            }

            if (IsHeading(paragraph))
            {
                blocks.Add(PdfBlock.Heading(paragraph.Trim('#', ' ')));
                continue;
            }

            if (paragraph.StartsWith("- ", StringComparison.Ordinal)
                || paragraph.StartsWith("* ", StringComparison.Ordinal))
            {
                foreach (var line in WrapText("- " + paragraph[2..].Trim(), CharactersPerLine - 4))
                {
                    blocks.Add(PdfBlock.Body(line));
                }

                continue;
            }

            foreach (var line in WrapText(paragraph, CharactersPerLine))
            {
                blocks.Add(PdfBlock.Body(line));
            }
        }

        TrimExtraSpacers(blocks);
        return blocks;
    }

    private static bool IsHeading(string paragraph) =>
        paragraph.StartsWith("# ", StringComparison.Ordinal)
        || paragraph.StartsWith("## ", StringComparison.Ordinal)
        || paragraph.StartsWith("### ", StringComparison.Ordinal)
        || (paragraph.Length <= 72
            && paragraph.All(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character is ':' or '-' or '/')
            && paragraph.Any(char.IsLetter)
            && paragraph.Count(char.IsLower) < paragraph.Count(char.IsUpper));

    private static void TrimExtraSpacers(List<PdfBlock> blocks)
    {
        while (blocks.Count > 0 && blocks[0].Kind == PdfBlockKind.Spacer)
        {
            blocks.RemoveAt(0);
        }

        while (blocks.Count > 0 && blocks[^1].Kind == PdfBlockKind.Spacer)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }
    }

    private static List<List<PdfBlock>> Paginate(IReadOnlyList<PdfBlock> blocks)
    {
        var pages = new List<List<PdfBlock>>();
        var current = new List<PdfBlock>();
        var y = TopY - 58;
        foreach (var block in blocks)
        {
            var height = block.Height;
            if (current.Count > 0 && y - height < BottomY)
            {
                pages.Add(current);
                current = [];
                y = TopY - 58;
            }

            current.Add(block);
            y -= height;
        }

        if (current.Count > 0)
        {
            pages.Add(current);
        }

        return pages;
    }

    private static string BuildContentObject(SimplePdfDocument document, IReadOnlyList<PdfBlock> blocks, int pageNumber, int pageCount)
    {
        var stream = new StringBuilder();
        stream.AppendLine("BT");
        stream.AppendLine("/F2 18 Tf");
        stream.AppendLine($"{MarginLeft} {TopY} Td");
        stream.AppendLine($"({EscapeText(document.Title)}) Tj");
        stream.AppendLine("ET");

        if (!string.IsNullOrWhiteSpace(document.Subtitle))
        {
            stream.AppendLine("BT");
            stream.AppendLine("/F1 9 Tf");
            stream.AppendLine($"{MarginLeft} {TopY - 20} Td");
            stream.AppendLine($"({EscapeText(document.Subtitle!)}) Tj");
            stream.AppendLine("ET");
        }

        stream.AppendLine($"{MarginLeft} {TopY - 34} m {PageWidth - MarginRight} {TopY - 34} l S");

        var y = TopY - 58;
        foreach (var block in blocks)
        {
            if (block.Kind == PdfBlockKind.Spacer)
            {
                y -= block.Height;
                continue;
            }

            var font = block.Kind == PdfBlockKind.Heading ? "/F2 12 Tf" : $"/F1 {BodyFontSize} Tf";
            stream.AppendLine("BT");
            stream.AppendLine(font);
            stream.AppendLine($"{MarginLeft} {y} Td");
            stream.AppendLine($"({EscapeText(block.Text)}) Tj");
            stream.AppendLine("ET");
            y -= block.Height;
        }

        var footer = $"{document.FooterLabel} - {document.GeneratedAt:yyyy-MM-dd HH:mm} - Page {pageNumber} of {pageCount}";
        stream.AppendLine($"{MarginLeft} 54 m {PageWidth - MarginRight} 54 l S");
        stream.AppendLine("BT");
        stream.AppendLine("/F1 8 Tf");
        stream.AppendLine($"{MarginLeft} 40 Td");
        stream.AppendLine($"({EscapeText(footer)}) Tj");
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

    private static List<string> WrapText(string text, int width)
    {
        var lines = new List<string>();
        var words = NormalizePdfText(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            lines.Add(string.Empty);
            return lines;
        }

        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                AppendWrappedWord(lines, current, word, width);
                continue;
            }

            if (current.Length + 1 + word.Length <= width)
            {
                current.Append(' ').Append(word);
                continue;
            }

            lines.Add(current.ToString());
            current.Clear();
            AppendWrappedWord(lines, current, word, width);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static void AppendWrappedWord(List<string> lines, StringBuilder current, string word, int width)
    {
        var remaining = word;
        while (remaining.Length > width)
        {
            lines.Add(remaining[..width]);
            remaining = remaining[width..];
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
                '\u2013' or '\u2014' or '\u2212' => '-',
                '\u2018' or '\u2019' => '\'',
                '\u201c' or '\u201d' => '"',
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

    private sealed record PdfBlock(PdfBlockKind Kind, string Text, int Height)
    {
        public static PdfBlock Body(string text) => new(PdfBlockKind.Body, text, BodyLineHeight);

        public static PdfBlock Heading(string text) => new(PdfBlockKind.Heading, text, 20);

        public static PdfBlock Spacer() => new(PdfBlockKind.Spacer, string.Empty, 8);
    }

    private enum PdfBlockKind
    {
        Body,
        Heading,
        Spacer
    }
}

internal sealed record SimplePdfDocument(
    string Title,
    string? Subtitle,
    string Body,
    DateTimeOffset GeneratedAt,
    string FooterLabel);
