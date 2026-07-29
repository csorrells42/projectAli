using System.Text.Json;
using AngleSharp.Html.Parser;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Web;

internal sealed record AliWebDiagnostic(string File, int Line, string Severity, string Message, string Source);
internal sealed record AliWebAnalysisReport(bool Success, int FilesAnalyzed, IReadOnlyList<AliWebDiagnostic> Diagnostics);

/// <summary>Bounded dependency-free validation for mixed web workspaces.</summary>
internal static class AliWebProjectAnalyzer
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".css", ".scss", ".less", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts", ".json" };
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "dist", "build", "coverage", ".next", ".nuxt", ".svelte-kit", ".angular", ".ali" };

    public static async Task<AliWebAnalysisReport> AnalyzeAsync(string root, CancellationToken cancellationToken)
    {
        var diagnostics = new List<AliWebDiagnostic>();
        var files = 0;
        long bytes = 0;
        foreach (var path in Enumerate(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > 2 * 1024 * 1024 || files >= 5_000 || bytes + info.Length > 64L * 1024 * 1024) break;
            files++;
            bytes += info.Length;
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string source;
            try { source = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(relative, 0, "error", ex.Message, "filesystem"));
                continue;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".json") ValidateJson(relative, source, diagnostics);
            else if (extension is ".html" or ".htm") await ValidateHtmlAsync(relative, source, diagnostics, cancellationToken).ConfigureAwait(false);
            else ValidateDelimiters(relative, source, diagnostics);
        }

        return new AliWebAnalysisReport(true, files, diagnostics);
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if (!Ignored.Contains(info.Name) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
                if (Extensions.Contains(Path.GetExtension(file))) yield return file;
        }
    }

    private static void ValidateJson(string file, string source, List<AliWebDiagnostic> diagnostics)
    {
        try { using var _ = JsonDocument.Parse(source, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { diagnostics.Add(new(file, checked((int)(ex.LineNumber ?? 0) + 1), "error", ex.Message, "json")); }
    }

    private static async Task ValidateHtmlAsync(string file, string source, List<AliWebDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var document = await new HtmlParser().ParseDocumentAsync(source, cancellationToken).ConfigureAwait(false);
            if (document.DocumentElement is null) diagnostics.Add(new(file, 1, "error", "HTML document has no root element.", "anglesharp"));
            var duplicateIds = document.All.Where(element => !string.IsNullOrWhiteSpace(element.Id))
                .GroupBy(element => element.Id, StringComparer.Ordinal).Where(group => group.Count() > 1);
            foreach (var duplicate in duplicateIds)
                diagnostics.Add(new(file, 1, "warning", $"Duplicate HTML id '{duplicate.Key}'.", "anglesharp"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(new(file, 1, "error", ex.Message, "anglesharp"));
        }
    }

    private static void ValidateDelimiters(string file, string source, List<AliWebDiagnostic> diagnostics)
    {
        var stack = new Stack<(char Value, int Line)>();
        var line = 1;
        var quote = '\0';
        var escaped = false;
        var lineComment = false;
        var blockComment = false;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (current == '\n') { line++; lineComment = false; }
            if (lineComment) continue;
            if (blockComment) { if (current == '*' && next == '/') { blockComment = false; index++; } continue; }
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (current == '\\') { escaped = true; continue; }
                if (current == quote) quote = '\0';
                continue;
            }
            if (current == '/' && next == '/') { lineComment = true; index++; continue; }
            if (current == '/' && next == '*') { blockComment = true; index++; continue; }
            if (current is '\'' or '"' or '`') { quote = current; continue; }
            if (current is '(' or '[' or '{') stack.Push((current, line));
            else if (current is ')' or ']' or '}')
            {
                if (stack.Count == 0 || !Matches(stack.Peek().Value, current))
                {
                    diagnostics.Add(new(file, line, "error", $"Unexpected closing delimiter '{current}'.", "bounded-parser"));
                    return;
                }
                stack.Pop();
            }
        }
        if (quote != '\0') diagnostics.Add(new(file, line, "error", "Unterminated string literal.", "bounded-parser"));
        if (blockComment) diagnostics.Add(new(file, line, "error", "Unterminated block comment.", "bounded-parser"));
        foreach (var item in stack) diagnostics.Add(new(file, item.Line, "error", $"Unclosed delimiter '{item.Value}'.", "bounded-parser"));
    }

    private static bool Matches(char open, char close) => (open, close) is ('(', ')') or ('[', ']') or ('{', '}');
}
