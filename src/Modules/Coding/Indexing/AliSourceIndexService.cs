using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Indexing;

public sealed record AliIndexedSourceFile(
    string RelativePath,
    AliProgrammingLanguage Language,
    long Bytes,
    string Sha256);

public sealed record AliIndexedSymbol(
    string Name,
    string Kind,
    string RelativePath,
    int Line,
    AliProgrammingLanguage Language);

public sealed record AliSourceIndexResult(
    bool Success,
    string Summary,
    string ProjectPath,
    int FileCount,
    long IndexedBytes,
    bool Truncated,
    IReadOnlyList<AliIndexedSourceFile> Files,
    IReadOnlyList<AliIndexedSymbol> Symbols);

public sealed record AliSymbolSearchResult(
    bool Success,
    string Summary,
    string Query,
    IReadOnlyList<AliIndexedSymbol> Matches);

/// <summary>
/// Bounded, language-neutral local index. Semantic providers can enrich this index,
/// while it remains a deterministic fallback for broken and partially written code.
/// </summary>
internal sealed partial class AliSourceIndexService
{
    private const int MaximumFiles = 5_000;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", ".venv", "venv",
        "__pycache__", "target", "build", "dist", "out", "coverage", ".gradle", ".mypy_cache",
        ".pytest_cache", ".ruff_cache"
    };

    private readonly AliLanguageProjectResolver _resolver;
    private readonly ConcurrentDictionary<string, AliSourceIndexResult> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public AliSourceIndexService(AliLanguageProjectResolver resolver) => _resolver = resolver;

    public async Task<AliSourceIndexResult> BuildAsync(string targetPath, CancellationToken cancellationToken)
    {
        var project = _resolver.Resolve(targetPath);
        var result = await Task.Run(
            () => Build(project, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        _indexes[Path.GetFullPath(project.ProjectDirectory)] = result;
        return result;
    }

    public Task<AliSymbolSearchResult> SearchAsync(
        string targetPath,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maximumResults is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults), "maximumResults must be between 1 and 200.");
        }

        var project = _resolver.Resolve(targetPath);
        var key = Path.GetFullPath(project.ProjectDirectory);
        return SearchCoreAsync(project, key, query.Trim(), maximumResults, cancellationToken);
    }

    private async Task<AliSymbolSearchResult> SearchCoreAsync(
        AliResolvedLanguageProject project,
        string key,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (!_indexes.TryGetValue(key, out var index))
        {
            index = await Task.Run(() => Build(project, cancellationToken), cancellationToken).ConfigureAwait(false);
            _indexes[key] = index;
        }

        var matches = index.Symbols
            .Select(symbol => (Symbol: symbol, Score: Score(symbol.Name, query)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Symbol.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(maximumResults)
            .Select(item => item.Symbol)
            .ToArray();
        return new AliSymbolSearchResult(
            true,
            $"Found {matches.Length} indexed symbol match(es) for '{query}'.",
            query,
            matches);
    }

    private static AliSourceIndexResult Build(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var files = new List<AliIndexedSourceFile>();
        var symbols = new List<AliIndexedSymbol>();
        long totalBytes = 0;
        var truncated = false;

        foreach (var path in EnumerateSourceFiles(project.ProjectDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count >= MaximumFiles || totalBytes >= MaximumBytes)
            {
                truncated = true;
                break;
            }

            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes || totalBytes + info.Length > MaximumBytes)
            {
                if (info.Exists && info.Length > MaximumFileBytes)
                {
                    truncated = true;
                }
                continue;
            }

            var language = AliLanguageProjectResolver.DetectLanguage(path);
            if (language == AliProgrammingLanguage.Unknown)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (bytes.AsSpan().IndexOf((byte)0) >= 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(project.ProjectDirectory, path).Replace('\\', '/');
            files.Add(new AliIndexedSourceFile(
                relative,
                language,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            totalBytes += bytes.LongLength;

            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            ExtractSymbols(text, relative, language, symbols);
        }

        return new AliSourceIndexResult(
            true,
            $"Indexed {files.Count} source file(s), {symbols.Count} structural symbol(s), and {totalBytes} byte(s).",
            project.VirtualPath,
            files.Count,
            totalBytes,
            truncated,
            files,
            symbols);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var info = new DirectoryInfo(child);
                if (IgnoredDirectories.Contains(info.Name)
                    || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                pending.Push(child);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (AliLanguageProjectResolver.DetectLanguage(file) != AliProgrammingLanguage.Unknown)
                {
                    yield return file;
                }
            }
        }
    }

    private static void ExtractSymbols(
        string text,
        string relativePath,
        AliProgrammingLanguage language,
        ICollection<AliIndexedSymbol> output)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            foreach (var (kind, regex) in Patterns(language))
            {
                var match = regex.Match(lines[index]);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    output.Add(new AliIndexedSymbol(name, kind, relativePath, index + 1, language));
                }
                break;
            }
        }
    }

    private static IEnumerable<(string Kind, Regex Pattern)> Patterns(AliProgrammingLanguage language) => language switch
    {
        AliProgrammingLanguage.Python =>
        [
            ("class", PythonClassRegex()),
            ("function", PythonFunctionRegex())
        ],
        AliProgrammingLanguage.JavaScript or AliProgrammingLanguage.TypeScript =>
        [
            ("type", WebTypeRegex()),
            ("function", WebFunctionRegex()),
            ("function", WebAssignedFunctionRegex())
        ],
        AliProgrammingLanguage.Java =>
        [
            ("type", JavaTypeRegex()),
            ("method", JavaMethodRegex())
        ],
        AliProgrammingLanguage.Cpp =>
        [
            ("type", CppTypeRegex()),
            ("function", CppFunctionRegex())
        ],
        AliProgrammingLanguage.CSharp =>
        [
            ("type", CSharpTypeRegex()),
            ("member", CSharpMemberRegex())
        ],
        AliProgrammingLanguage.Html => [("element", HtmlIdRegex())],
        AliProgrammingLanguage.Css => [("selector", CssSelectorRegex())],
        _ => []
    };

    private static int Score(string value, string query)
    {
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;
        if (value.Contains(query, StringComparison.OrdinalIgnoreCase)) return 50;
        return IsSubsequence(query, value) ? 20 : 0;
    }

    private static bool IsSubsequence(string query, string value)
    {
        var offset = 0;
        foreach (var character in value)
        {
            if (offset < query.Length && char.ToUpperInvariant(character) == char.ToUpperInvariant(query[offset]))
            {
                offset++;
            }
        }
        return offset == query.Length;
    }

    [GeneratedRegex(@"^\s*class\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex PythonClassRegex();
    [GeneratedRegex(@"^\s*(?:async\s+)?def\s+(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex PythonFunctionRegex();
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?(?:class|interface|type|enum)\s+(?<name>[$A-Za-z_][$\w]*)", RegexOptions.CultureInvariant)]
    private static partial Regex WebTypeRegex();
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(?<name>[$A-Za-z_][$\w]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex WebFunctionRegex();
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(?<name>[$A-Za-z_][$\w]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[$A-Za-z_][$\w]*)\s*=>", RegexOptions.CultureInvariant)]
    private static partial Regex WebAssignedFunctionRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|protected|private|abstract|final|static|sealed|non-sealed)\s+)*(?:class|interface|record|enum)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex JavaTypeRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|protected|private|static|final|abstract|synchronized|native)\s+)+[\w<>,.?\[\]]+\s+(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex JavaMethodRegex();
    [GeneratedRegex(@"^\s*(?:class|struct|enum(?:\s+class)?|union|concept)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex CppTypeRegex();
    [GeneratedRegex(@"^\s*(?:[\w:<>,~*&]+\s+)+(?<name>[A-Za-z_]\w*(?:::\w+)*)\s*\([^;]*\)\s*(?:const\s*)?(?:\{|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CppFunctionRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|protected|private|internal|static|abstract|sealed|partial|readonly|ref|file)\s+)*(?:class|struct|interface|record|enum|delegate)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpTypeRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|protected|private|internal|static|virtual|abstract|override|sealed|async|partial|extern|unsafe|new)\s+)+[\w<>,.?\[\]]+\s+(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpMemberRegex();
    [GeneratedRegex(@"\bid\s*=\s*[""'](?<name>[A-Za-z_][\w-]*)[""']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlIdRegex();
    [GeneratedRegex(@"^\s*[.#](?<name>[A-Za-z_][\w-]*)\s*(?:[,>{:]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CssSelectorRegex();
}
