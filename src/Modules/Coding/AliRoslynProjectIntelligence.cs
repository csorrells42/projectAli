using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

namespace Ali.Modules.Coding;

public sealed record RoslynDiagnosticItem(
    string Id,
    string Severity,
    string Message,
    string? File,
    int? Line,
    int? Column);

public sealed record RoslynAnalysisResult(
    bool Success,
    string ProjectPath,
    string Summary,
    int DocumentCount,
    IReadOnlyList<RoslynDiagnosticItem> Diagnostics,
    IReadOnlyList<string> WorkspaceWarnings);

public sealed record RoslynFormatResult(
    bool Success,
    string ProjectPath,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> WorkspaceWarnings);

public sealed record RoslynSymbolLocation(string Symbol, string Kind, string? File, int? Line, string Display);

public sealed record RoslynSymbolResult(
    bool Success,
    string ProjectPath,
    string Query,
    string Summary,
    IReadOnlyList<RoslynSymbolLocation> Matches);

public sealed record RoslynCompletionResult(
    bool Success,
    string ProjectPath,
    string DocumentPath,
    int Line,
    int Column,
    string Summary,
    IReadOnlyList<string> Completions);

/// <summary>
/// Roslyn-backed semantic understanding for approved C# projects. It loads real SDK
/// projects with MSBuildWorkspace and exposes analysis, formatting, symbols, and
/// completion without providing arbitrary filesystem or shell access.
/// </summary>
internal sealed class AliRoslynProjectIntelligence(AliCodingProjectResolver resolver)
{
    private const int MaximumDiagnostics = 200;
    private const int MaximumMatches = 100;
    private const int MaximumCompletions = 100;
    private readonly AliRoslynWorkspaceLoader _loader = new(resolver);

    public async Task<RoslynAnalysisResult> AnalyzeAsync(string projectPath, CancellationToken cancellationToken)
    {
        using var session = await _loader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
        return await AnalyzeAsync(session, projectPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoslynAnalysisResult> AnalyzeCompilerOnlyAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        using var session = await _loader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
        return await AnalyzeCompilerOnlyAsync(session, projectPath, cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task<RoslynAnalysisResult> AnalyzeAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        CancellationToken cancellationToken) =>
        AnalyzeCoreAsync(
            session,
            projectPath,
            includeProjectAnalyzers: true,
            cancellationToken);

    internal Task<RoslynAnalysisResult> AnalyzeCompilerOnlyAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        CancellationToken cancellationToken) =>
        AnalyzeCoreAsync(
            session,
            projectPath,
            includeProjectAnalyzers: false,
            cancellationToken);

    private static async Task<RoslynAnalysisResult> AnalyzeCoreAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        bool includeProjectAnalyzers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var analysisSolution = includeProjectAnalyzers
            ? session.Solution
            : RemoveExecutableAnalysisReferences(session.Solution);
        var boundedDiagnostics = new PriorityQueue<RoslynDiagnosticItem, RoslynDiagnosticItem>(
            MaximumDiagnostics,
            WorstDiagnosticFirstComparer.Instance);
        var documentCount = 0;
        var errorCount = 0L;
        var warningCount = 0L;
        foreach (var project in analysisSolution.Projects
                     .Where(project => project.Language == LanguageNames.CSharp)
                     .OrderBy(ProjectIdentity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            documentCount += project.DocumentIds.Count;
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var projectDiagnostics = includeProjectAnalyzers
                ? await GetProjectDiagnosticsAsync(
                        project,
                        compilation,
                        cancellationToken)
                    .ConfigureAwait(false)
                : compilation.GetDiagnostics(cancellationToken);
            foreach (var diagnostic in projectDiagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    errorCount++;
                }
                else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                {
                    warningCount++;
                }
                else
                {
                    continue;
                }

                AddBoundedDiagnostic(boundedDiagnostics, ToDiagnosticItem(diagnostic));
            }
        }

        var diagnostics = boundedDiagnostics.UnorderedItems
            .Select(item => item.Element)
            .Order(DiagnosticOrderComparer.Instance)
            .ToArray();
        var totalDiagnosticCount = errorCount + warningCount;
        var truncationSummary = totalDiagnosticCount > diagnostics.Length
            ? $" The bounded result contains the first {diagnostics.Length} diagnostic(s) in deterministic order."
            : string.Empty;
        return new RoslynAnalysisResult(
            errorCount == 0,
            projectPath,
            $"Roslyn loaded {documentCount} C# document(s) and found {errorCount} error(s) "
            + $"and {warningCount} warning(s), "
            + (includeProjectAnalyzers
                ? "including any diagnostics from the target's loaded analyzers."
                : "using compiler diagnostics only; project analyzers and source generators were excluded.")
            + truncationSummary,
            documentCount,
            diagnostics,
            session.Warnings);
    }

    private static Solution RemoveExecutableAnalysisReferences(Solution solution)
    {
        var compilerOnlySolution = solution.WithAnalyzerReferences(
            ImmutableArray<AnalyzerReference>.Empty);
        foreach (var projectId in compilerOnlySolution.ProjectIds)
        {
            compilerOnlySolution = compilerOnlySolution.WithProjectAnalyzerReferences(
                projectId,
                ImmutableArray<AnalyzerReference>.Empty);
        }

        return compilerOnlySolution;
    }

    private static async Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var analyzers = project.AnalyzerReferences
            .SelectMany(reference => reference.GetAnalyzers(project.Language))
            .Distinct()
            .OrderBy(
                analyzer => analyzer.GetType().AssemblyQualifiedName,
                StringComparer.Ordinal)
            .ToImmutableArray();
        if (analyzers.IsDefaultOrEmpty)
        {
            return compilation.GetDiagnostics(cancellationToken);
        }

        var withAnalyzers = new CompilationWithAnalyzers(
            compilation,
            analyzers,
            project.AnalyzerOptions);
        return await withAnalyzers.GetAllDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddBoundedDiagnostic(
        PriorityQueue<RoslynDiagnosticItem, RoslynDiagnosticItem> diagnostics,
        RoslynDiagnosticItem candidate)
    {
        if (diagnostics.Count < MaximumDiagnostics)
        {
            diagnostics.Enqueue(candidate, candidate);
            return;
        }

        var currentWorst = diagnostics.Peek();
        if (DiagnosticOrderComparer.Instance.Compare(candidate, currentWorst) < 0)
        {
            _ = diagnostics.Dequeue();
            diagnostics.Enqueue(candidate, candidate);
        }
    }

    private static string ProjectIdentity(Project project)
    {
        var path = string.IsNullOrWhiteSpace(project.FilePath)
            ? string.Empty
            : Path.GetFullPath(project.FilePath);
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        return string.Join("|", path, project.Name, project.AssemblyName, project.Language);
    }

    private sealed class DiagnosticOrderComparer : IComparer<RoslynDiagnosticItem>
    {
        internal static DiagnosticOrderComparer Instance { get; } = new();

        public int Compare(RoslynDiagnosticItem? left, RoslynDiagnosticItem? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }

            var comparison = SeverityRank(left.Severity).CompareTo(SeverityRank(right.Severity));
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.File, right.File);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = Nullable.Compare(left.Line, right.Line);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = Nullable.Compare(left.Column, right.Column);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = StringComparer.Ordinal.Compare(left.Id, right.Id);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = StringComparer.Ordinal.Compare(left.Message, right.Message);
            if (comparison != 0)
            {
                return comparison;
            }

            return StringComparer.Ordinal.Compare(left.File, right.File);
        }

        private static int SeverityRank(string severity) =>
            severity == nameof(DiagnosticSeverity.Error) ? 0 : 1;
    }

    private sealed class WorstDiagnosticFirstComparer : IComparer<RoslynDiagnosticItem>
    {
        internal static WorstDiagnosticFirstComparer Instance { get; } = new();

        public int Compare(RoslynDiagnosticItem? left, RoslynDiagnosticItem? right) =>
            DiagnosticOrderComparer.Instance.Compare(right, left);
    }

    public async Task<RoslynFormatResult> FormatAsync(string projectPath, CancellationToken cancellationToken)
    {
        using var session = await _loader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var changed = new List<string>();
        foreach (var document in session.Solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .SelectMany(project => project.Documents)
            .Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(document.FilePath);
            var targetPrefix = Path.TrimEndingDirectorySeparator(session.Target.RootDirectory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AliCodingProjectResolver.RejectReparsePoints(session.Target.MountRoot, fullPath);
            var original = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var formattedDocument = await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
            var formatted = await formattedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (original.ContentEquals(formatted))
            {
                continue;
            }

            var encoding = formatted.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(fullPath, formatted.ToString(), encoding, cancellationToken).ConfigureAwait(false);
            changed.Add(Path.GetRelativePath(session.Target.RootDirectory, fullPath));
        }

        return new RoslynFormatResult(
            true,
            projectPath,
            changed.Count == 0
                ? "Roslyn found no C# formatting changes to apply."
                : $"Roslyn formatted {changed.Count} C# file(s).",
            changed,
            session.Warnings);
    }

    public async Task<RoslynSymbolResult> FindSymbolAsync(
        string projectPath,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        using var session = await _loader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
        return await FindSymbolAsync(session, projectPath, query, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RoslynSymbolResult> FindSymbolAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                session.Solution,
                query.Trim(),
                ignoreCase: true,
                SymbolFilter.TypeAndMember,
                cancellationToken)
            .ConfigureAwait(false);
        var matches = symbols.Take(MaximumMatches).Select(symbol => ToSymbolLocation(symbol, session.Target.RootDirectory)).ToList();
        return new RoslynSymbolResult(
            true,
            projectPath,
            query,
            matches.Count == 0 ? "Roslyn found no matching declarations." : $"Roslyn found {matches.Count} matching declaration(s).",
            matches);
    }

    public async Task<RoslynCompletionResult> GetCompletionsAsync(
        string projectPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        using var session = await _loader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
        return await GetCompletionsAsync(
                session,
                projectPath,
                documentPath,
                line,
                column,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RoslynCompletionResult> GetCompletionsAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var (document, position) = await _loader.ResolvePositionAsync(
            session,
            documentPath,
            line,
            column,
            cancellationToken).ConfigureAwait(false);
        var service = CompletionService.GetService(document)
            ?? throw new InvalidOperationException("Roslyn completion services are unavailable for this document.");
        var completions = await service.GetCompletionsAsync(
                document,
                position,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var items = completions?.ItemsList
            .Select(item => item.DisplayText)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumCompletions)
            .ToList() ?? [];
        return new RoslynCompletionResult(
            true,
            projectPath,
            documentPath,
            line,
            column,
            items.Count == 0 ? "Roslyn found no completions at this location." : $"Roslyn returned {items.Count} completion candidate(s).",
            items);
    }

    private static RoslynDiagnosticItem ToDiagnosticItem(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new RoslynDiagnosticItem(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            diagnostic.Location.IsInSource ? span.Path : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null);
    }

    private static RoslynSymbolLocation ToSymbolLocation(ISymbol symbol, string projectDirectory)
    {
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        var lineSpan = sourceLocation?.GetLineSpan();
        var file = lineSpan?.Path;
        if (!string.IsNullOrWhiteSpace(file) && Path.IsPathFullyQualified(file))
        {
            file = Path.GetRelativePath(projectDirectory, file);
        }

        return new RoslynSymbolLocation(
            symbol.Name,
            symbol.Kind.ToString(),
            file,
            lineSpan?.StartLinePosition.Line + 1,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
    }
}
