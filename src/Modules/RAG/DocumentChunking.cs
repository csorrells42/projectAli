using TreeSitter;

namespace Ali.Modules.RAG;

public sealed record DocumentChunk(
    string Text,
    int StartLine,
    int EndLine,
    string Symbol,
    string Parser);

public interface IDocumentChunker
{
    IReadOnlyList<DocumentChunk> Chunk(string filePath, string text, int targetCharacters, int overlapCharacters);
}

public sealed class StructuredDocumentChunker : IDocumentChunker
{
    private readonly PlainTextDocumentChunker _plainText = new();

    public IReadOnlyList<DocumentChunk> Chunk(string filePath, string text, int targetCharacters, int overlapCharacters)
    {
        if (string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var structured = ChunkCSharp(text, targetCharacters);
                if (structured.Count > 0)
                {
                    return structured;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                // A missing native grammar must never prevent ordinary document retrieval.
            }
        }

        return _plainText.Chunk(filePath, text, targetCharacters, overlapCharacters);
    }

    private static IReadOnlyList<DocumentChunk> ChunkCSharp(string text, int targetCharacters)
    {
        using var language = new Language("tree-sitter-c-sharp", "tree_sitter_c_sharp");
        using var parser = new Parser(language);
        using var tree = parser.Parse(text) ?? throw new InvalidOperationException("Tree-sitter could not parse the C# document.");
        var maximum = Math.Max(800, targetCharacters * 3);
        var chunks = new List<DocumentChunk>();
        CollectDeclarations(tree.RootNode, text, maximum, chunks);
        return chunks;
    }

    private static void CollectDeclarations(Node node, string source, int maximum, List<DocumentChunk> chunks)
    {
        foreach (var child in node.NamedChildren)
        {
            if (!IsDeclaration(child.Type))
            {
                CollectDeclarations(child, source, maximum, chunks);
                continue;
            }

            var length = checked((int)(child.EndIndex - child.StartIndex));
            var nested = child.NamedChildren.Where(item => IsDeclaration(item.Type)).ToArray();
            if (length > maximum && nested.Length > 0)
            {
                foreach (var item in nested)
                {
                    AddChunk(item, source, chunks);
                }
            }
            else
            {
                AddChunk(child, source, chunks);
            }
        }
    }

    private static void AddChunk(Node node, string source, List<DocumentChunk> chunks)
    {
        var start = checked((int)node.StartIndex);
        var end = checked((int)node.EndIndex);
        if (start < 0 || end <= start || end > source.Length)
        {
            return;
        }

        var text = source[start..end].Trim();
        if (text.Length == 0)
        {
            return;
        }

        var symbol = node.NamedChildren
            .FirstOrDefault(child => child.Type is "identifier" or "name")?.Text
            ?? node.Type.Replace('_', ' ');
        chunks.Add(new DocumentChunk(
            text,
            checked((int)node.StartPosition.Row + 1),
            checked((int)node.EndPosition.Row + 1),
            symbol,
            "tree-sitter-c-sharp"));
    }

    private static bool IsDeclaration(string type) => type is
        "namespace_declaration" or "file_scoped_namespace_declaration" or
        "class_declaration" or "interface_declaration" or "struct_declaration" or
        "record_declaration" or "enum_declaration" or "method_declaration" or
        "constructor_declaration" or "property_declaration" or "field_declaration" or
        "event_declaration" or "delegate_declaration";
}

public sealed class PlainTextDocumentChunker : IDocumentChunker
{
    public IReadOnlyList<DocumentChunk> Chunk(string filePath, string text, int targetCharacters, int overlapCharacters)
    {
        var normalized = text.Replace("\0", string.Empty).ReplaceLineEndings("\n").Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        var size = Math.Max(400, targetCharacters);
        var overlap = Math.Clamp(overlapCharacters, 0, size / 2);
        var result = new List<DocumentChunk>();
        var start = 0;
        while (start < normalized.Length)
        {
            var length = Math.Min(size, normalized.Length - start);
            var chunk = normalized.Substring(start, length).Trim();
            if (chunk.Length > 0)
            {
                result.Add(new DocumentChunk(
                    chunk,
                    CountLines(normalized, start) + 1,
                    CountLines(normalized, start + length) + 1,
                    Path.GetFileName(filePath),
                    "plain-text"));
            }

            if (start + length >= normalized.Length)
            {
                break;
            }

            start += Math.Max(1, length - overlap);
        }

        return result;
    }

    private static int CountLines(string value, int end)
    {
        var count = 0;
        foreach (var character in value.AsSpan(0, Math.Min(end, value.Length)))
        {
            if (character == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
