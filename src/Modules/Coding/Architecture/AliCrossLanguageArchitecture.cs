using System.Text;
using System.Text.RegularExpressions;
using Ali.Modules.Coding.Indexing;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Architecture;

public sealed record AliArchitectureNode(string Id, string Label, string Kind, AliProgrammingLanguage Language);
public sealed record AliArchitectureEdge(string Source, string Target, string Kind, string Evidence, bool Internal);
public sealed record AliArchitectureHotspot(string Node, int Incoming, int Outgoing);
public sealed record AliCrossLanguageArchitectureReport(
    bool Success,
    string Summary,
    int Files,
    bool Truncated,
    IReadOnlyList<AliArchitectureNode> Nodes,
    IReadOnlyList<AliArchitectureEdge> Edges,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<AliArchitectureHotspot> Hotspots,
    string Mermaid);

/// <summary>Language-neutral architecture graph built from bounded local source evidence.</summary>
internal sealed partial class AliCrossLanguageArchitecture(
    AliLanguageProjectResolver resolver,
    AliSourceIndexService index)
{
    public async Task<AliCrossLanguageArchitectureReport> InspectAsync(string targetPath, CancellationToken cancellationToken)
    {
        var project = resolver.Resolve(targetPath);
        var indexed = await index.BuildAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var fileMap = indexed.Files.ToDictionary(file => Normalize(file.RelativePath), StringComparer.OrdinalIgnoreCase);
        var nodes = indexed.Files.Select(file => new AliArchitectureNode(
            Normalize(file.RelativePath), Path.GetFileName(file.RelativePath), "source", file.Language)).ToList();
        var external = new Dictionary<string, AliArchitectureNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<AliArchitectureEdge>();

        foreach (var file in indexed.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = Normalize(file.RelativePath);
            var physical = Path.Combine(project.ProjectDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try { text = (await File.ReadAllTextAsync(physical, cancellationToken).ConfigureAwait(false)).TrimStart('\uFEFF'); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var reference in ExtractReferences(file.Language, text))
            {
                var target = ResolveInternal(sourceId, reference.Value, fileMap.Keys, file.Language);
                if (target is not null)
                {
                    edges.Add(new(sourceId, target, reference.Kind, reference.Value, true));
                    continue;
                }
                var externalId = "external:" + reference.Value;
                if (!external.ContainsKey(externalId))
                    external[externalId] = new(externalId, reference.Value, "external", AliProgrammingLanguage.Unknown);
                edges.Add(new(sourceId, externalId, reference.Kind, reference.Value, false));
            }
        }

        nodes.AddRange(external.Values);
        var distinctEdges = edges.DistinctBy(edge => (edge.Source, edge.Target, edge.Kind)).ToArray();
        var cycles = FindCycles(distinctEdges.Where(edge => edge.Internal).ToArray());
        var hotspots = nodes.Select(node => new AliArchitectureHotspot(
                node.Id,
                distinctEdges.Count(edge => edge.Target.Equals(node.Id, StringComparison.OrdinalIgnoreCase)),
                distinctEdges.Count(edge => edge.Source.Equals(node.Id, StringComparison.OrdinalIgnoreCase))))
            .Where(item => item.Incoming + item.Outgoing > 0)
            .OrderByDescending(item => item.Incoming + item.Outgoing).ThenBy(item => item.Node, StringComparer.OrdinalIgnoreCase)
            .Take(25).ToArray();
        var mermaid = BuildMermaid(nodes, distinctEdges);
        return new(true,
            $"Mapped {indexed.FileCount} source file(s), {distinctEdges.Length} dependency edge(s), {external.Count} external reference(s), and {cycles.Count} internal cycle(s) across {indexed.Files.Select(file => file.Language).Distinct().Count()} language(s).",
            indexed.FileCount, indexed.Truncated, nodes, distinctEdges, cycles, hotspots, mermaid);
    }

    private static IEnumerable<(string Kind, string Value)> ExtractReferences(AliProgrammingLanguage language, string text)
    {
        if (language == AliProgrammingLanguage.Python)
        {
            foreach (Match match in PythonReferenceRegex().Matches(text))
            {
                var value = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
                if (!string.IsNullOrWhiteSpace(value)) yield return ("python-reference", value);
            }
            yield break;
        }
        var regex = language switch
        {
            AliProgrammingLanguage.CSharp => CSharpReferenceRegex(),
            AliProgrammingLanguage.JavaScript or AliProgrammingLanguage.TypeScript => WebReferenceRegex(),
            AliProgrammingLanguage.Java => JavaReferenceRegex(),
            AliProgrammingLanguage.Cpp or AliProgrammingLanguage.Arduino => CppReferenceRegex(),
            AliProgrammingLanguage.Html => HtmlReferenceRegex(),
            AliProgrammingLanguage.Css => CssReferenceRegex(),
            _ => null
        };
        if (regex is null) yield break;
        foreach (Match match in regex.Matches(text))
        {
            var value = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value)) yield return (language.ToString().ToLowerInvariant() + "-reference", value);
        }
    }

    private static string? ResolveInternal(string source, string reference, IEnumerable<string> files, AliProgrammingLanguage language)
    {
        var known = files as IReadOnlyCollection<string> ?? files.ToArray();
        var sourceDirectory = Normalize(Path.GetDirectoryName(source) ?? string.Empty);
        var candidates = new List<string>();
        if (reference.StartsWith(".", StringComparison.Ordinal) || reference.StartsWith("/", StringComparison.Ordinal))
        {
            var relative = reference.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            candidates.Add(Normalize(Path.GetFullPath(Path.Combine("C:\\ali-root", sourceDirectory, relative))["C:\\ali-root\\".Length..]));
        }
        else if (language == AliProgrammingLanguage.Python)
        {
            candidates.Add(reference.Replace('.', '/'));
        }
        else if (language is AliProgrammingLanguage.Cpp or AliProgrammingLanguage.Arduino)
        {
            candidates.Add(Normalize(Path.Combine(sourceDirectory, reference)));
            candidates.Add(Normalize(reference));
        }
        else if (language == AliProgrammingLanguage.Java)
        {
            candidates.Add(reference.Replace('.', '/'));
        }
        else return null;

        var extensions = new[] { "", ".cs", ".py", ".pyi", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".html", ".css", ".java", ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".ino" };
        foreach (var candidate in candidates)
        foreach (var extension in extensions)
        {
            var exact = Normalize(candidate + extension);
            var match = known.FirstOrDefault(file => file.Equals(exact, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            match = known.FirstOrDefault(file => file.Equals(Normalize(Path.Combine(candidate, "index" + extension)), StringComparison.OrdinalIgnoreCase)
                || file.Equals(Normalize(Path.Combine(candidate, "__init__" + extension)), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindCycles(IReadOnlyList<AliArchitectureEdge> edges)
    {
        var graph = edges.GroupBy(edge => edge.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var cycles = new List<IReadOnlyList<string>>();
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string start, string node, List<string> path, HashSet<string> active)
        {
            if (!active.Add(node))
            {
                if (node.Equals(start, StringComparison.OrdinalIgnoreCase))
                {
                    var cycle = path.Append(node).ToArray();
                    var signature = string.Join("|", cycle.Take(cycle.Length - 1).OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
                    if (signatures.Add(signature)) cycles.Add(cycle);
                }
                return;
            }
            if (path.Count >= 20 || !graph.TryGetValue(node, out var next)) return;
            foreach (var target in next) Visit(start, target, [.. path, node], new(active, StringComparer.OrdinalIgnoreCase));
        }
        foreach (var start in graph.Keys.Take(2_000)) Visit(start, start, [], new(StringComparer.OrdinalIgnoreCase));
        return cycles.Take(100).ToArray();
    }

    private static string BuildMermaid(IReadOnlyList<AliArchitectureNode> nodes, IReadOnlyList<AliArchitectureEdge> edges)
    {
        var builder = new StringBuilder("flowchart LR\n");
        var included = nodes.Where(node => edges.Any(edge => edge.Source == node.Id || edge.Target == node.Id)).Take(150).ToArray();
        var ids = included.Select((node, index) => (node.Id, Safe: "n" + index)).ToDictionary(item => item.Id, item => item.Safe, StringComparer.OrdinalIgnoreCase);
        foreach (var node in included) builder.Append("  ").Append(ids[node.Id]).Append("[\"").Append(Escape(node.Label)).Append("\"]\n");
        foreach (var edge in edges.Where(edge => ids.ContainsKey(edge.Source) && ids.ContainsKey(edge.Target)).Take(300))
            builder.Append("  ").Append(ids[edge.Source]).Append(" -->|\"").Append(Escape(edge.Kind)).Append("\"| ").Append(ids[edge.Target]).Append('\n');
        return builder.ToString().TrimEnd();
    }
    private static string Escape(string value) => value.Replace("\\", "/").Replace("\"", "'");
    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');

    [GeneratedRegex(@"(?:^|\n)\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_$][\w$]*\s*=\s*)?([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)", RegexOptions.Multiline)]
    private static partial Regex CSharpReferenceRegex();
    [GeneratedRegex(@"(?:^|\n)\s*(?:from\s+([.\w]+)\s+import|import\s+([.\w]+))", RegexOptions.Multiline)]
    private static partial Regex PythonReferenceRegex();
    [GeneratedRegex("""(?:from\s*|import\s*\()?['"]([^'"]+)['"]""")]
    private static partial Regex WebReferenceRegex();
    [GeneratedRegex(@"(?:^|\n)\s*import\s+(?:static\s+)?([\w.]+)", RegexOptions.Multiline)]
    private static partial Regex JavaReferenceRegex();
    [GeneratedRegex("#\\s*include\\s*[\"<]([^\">]+)[\">]")]
    private static partial Regex CppReferenceRegex();
    [GeneratedRegex("""(?:src|href)\s*=\s*['"]([^'"]+)['"]""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlReferenceRegex();
    [GeneratedRegex("""@import\s+(?:url\()?['"]?([^'")\s;]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CssReferenceRegex();

}
