using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ali.Modules.Coding.Architecture;

public sealed record ArchitectureProjectEdge(string From, string To);
public sealed record ArchitectureCallEdge(string Caller, string Callee, string? File, int Line);
public sealed record ArchitectureBoundaryRule(string FromNamespace, string MustNotReferenceNamespace);
public sealed record ArchitectureViolation(string Rule, string FromSymbol, string ToSymbol, string? File, int Line);
public sealed record ArchitectureInspectionResult(bool Success, string Summary, IReadOnlyList<ArchitectureProjectEdge> ProjectEdges, IReadOnlyList<ArchitectureCallEdge> CallEdges, IReadOnlyList<IReadOnlyList<string>> ProjectCycles, IReadOnlyList<string> WorkspaceWarnings);
public sealed record ArchitectureBoundaryResult(bool Success, string Summary, IReadOnlyList<ArchitectureViolation> Violations);

/// <summary>Semantic project, call-graph, cycle, and boundary analysis over a Roslyn solution.</summary>
internal sealed class AliArchitectureEngineering(AliRoslynWorkspaceLoader loader)
{
    private const int MaximumCallEdges = 2_000;

    public async Task<ArchitectureInspectionResult> InspectAsync(string targetPath, CancellationToken cancellationToken)
    {
        using var session = await loader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var projectEdges = session.Solution.Projects.SelectMany(project => project.ProjectReferences.Select(reference =>
            new ArchitectureProjectEdge(project.Name, session.Solution.GetProject(reference.ProjectId)?.Name ?? reference.ProjectId.Id.ToString()))).ToArray();
        var calls = await GetCallEdgesAsync(session, cancellationToken).ConfigureAwait(false);
        var cycles = FindCycles(projectEdges);
        return new ArchitectureInspectionResult(true,
            $"Architecture graph contains {projectEdges.Length} project edge(s), {calls.Count} semantic call edge(s), and {cycles.Count} project cycle(s).",
            projectEdges, calls, cycles, session.Warnings);
    }

    public async Task<ArchitectureBoundaryResult> CheckBoundariesAsync(string targetPath, ArchitectureBoundaryRule[] rules, CancellationToken cancellationToken)
    {
        if (rules.Length == 0) throw new ArgumentException("At least one architecture boundary rule is required.", nameof(rules));
        using var session = await loader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var calls = await GetCallEdgesAsync(session, cancellationToken).ConfigureAwait(false);
        var violations = new List<ArchitectureViolation>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.FromNamespace) || string.IsNullOrWhiteSpace(rule.MustNotReferenceNamespace))
                throw new ArgumentException("Boundary namespaces cannot be blank.", nameof(rules));
            violations.AddRange(calls.Where(edge => edge.Caller.StartsWith(rule.FromNamespace + ".", StringComparison.Ordinal)
                    && edge.Callee.StartsWith(rule.MustNotReferenceNamespace + ".", StringComparison.Ordinal))
                .Select(edge => new ArchitectureViolation($"{rule.FromNamespace} must not reference {rule.MustNotReferenceNamespace}", edge.Caller, edge.Callee, edge.File, edge.Line)));
        }
        return new ArchitectureBoundaryResult(violations.Count == 0,
            violations.Count == 0 ? "All semantic architecture boundary rules passed." : $"Found {violations.Count} architecture boundary violation(s).", violations);
    }

    private static async Task<IReadOnlyList<ArchitectureCallEdge>> GetCallEdgesAsync(AliRoslynWorkspaceSession session, CancellationToken cancellationToken)
    {
        var edges = new List<ArchitectureCallEdge>();
        foreach (var project in session.Solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null) continue;
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var callee = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                var callerNode = invocation.Ancestors().FirstOrDefault(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
                var caller = callerNode is null ? null : model.GetDeclaredSymbol(callerNode, cancellationToken);
                if (callee is null || caller is null) continue;
                var location = invocation.GetLocation().GetLineSpan();
                edges.Add(new ArchitectureCallEdge(
                    caller.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    callee.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    document.FilePath, location.StartLinePosition.Line + 1));
                if (edges.Count >= MaximumCallEdges) return edges;
            }
        }
        return edges.Distinct().ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindCycles(IReadOnlyList<ArchitectureProjectEdge> edges)
    {
        var graph = edges.GroupBy(edge => edge.From).ToDictionary(group => group.Key, group => group.Select(edge => edge.To).ToArray(), StringComparer.OrdinalIgnoreCase);
        var cycles = new List<IReadOnlyList<string>>();
        foreach (var start in graph.Keys)
        {
            Visit(start, start, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        return cycles.DistinctBy(cycle => string.Join("->", cycle), StringComparer.OrdinalIgnoreCase).ToArray();

        void Visit(string start, string node, List<string> path, HashSet<string> active)
        {
            if (!active.Add(node))
            {
                if (node.Equals(start, StringComparison.OrdinalIgnoreCase)) cycles.Add([.. path, node]);
                return;
            }
            path.Add(node);
            if (graph.TryGetValue(node, out var next)) foreach (var item in next) Visit(start, item, [.. path], new HashSet<string>(active, StringComparer.OrdinalIgnoreCase));
        }
    }
}
