using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;

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

    public async Task<RoslynAnalysisResult> AnalyzeAsync(string projectPath, CancellationToken cancellationToken)
    {
        var resolved = resolver.ResolveExistingProject(projectPath);
        var loaded = await LoadProjectAsync(resolved, cancellationToken).ConfigureAwait(false);
        using (loaded.Workspace)
        {
            var compilation = await loaded.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                return new RoslynAnalysisResult(
                    false,
                    projectPath,
                    "Roslyn could not create a compilation for this project.",
                    loaded.Project.DocumentIds.Count,
                    [],
                    loaded.Warnings);
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .OrderByDescending(diagnostic => diagnostic.Severity)
                .ThenBy(diagnostic => diagnostic.Location.GetLineSpan().Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line)
                .Take(MaximumDiagnostics)
                .Select(ToDiagnosticItem)
                .ToList();
            var errors = diagnostics.Count(item => item.Severity == "Error");
            var warnings = diagnostics.Count - errors;
            return new RoslynAnalysisResult(
                errors == 0,
                projectPath,
                $"Roslyn loaded {loaded.Project.DocumentIds.Count} C# document(s) and found {errors} error(s) and {warnings} warning(s).",
                loaded.Project.DocumentIds.Count,
                diagnostics,
                loaded.Warnings);
        }
    }

    public async Task<RoslynFormatResult> FormatAsync(string projectPath, CancellationToken cancellationToken)
    {
        var resolved = resolver.ResolveExistingProject(projectPath);
        var loaded = await LoadProjectAsync(resolved, cancellationToken).ConfigureAwait(false);
        using (loaded.Workspace)
        {
            var changed = new List<string>();
            foreach (var document in loaded.Project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(document.FilePath);
                var projectPrefix = Path.TrimEndingDirectorySeparator(resolved.ProjectDirectory) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AliCodingProjectResolver.RejectReparsePoints(resolved.MountRoot, fullPath);
                var original = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var formattedDocument = await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
                var formatted = await formattedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (original.ContentEquals(formatted))
                {
                    continue;
                }

                var encoding = formatted.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                await File.WriteAllTextAsync(fullPath, formatted.ToString(), encoding, cancellationToken).ConfigureAwait(false);
                changed.Add(Path.GetRelativePath(resolved.ProjectDirectory, fullPath));
            }

            return new RoslynFormatResult(
                true,
                projectPath,
                changed.Count == 0
                    ? "Roslyn found no C# formatting changes to apply."
                    : $"Roslyn formatted {changed.Count} C# file(s).",
                changed,
                loaded.Warnings);
        }
    }

    public async Task<RoslynSymbolResult> FindSymbolAsync(
        string projectPath,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var resolved = resolver.ResolveExistingProject(projectPath);
        var loaded = await LoadProjectAsync(resolved, cancellationToken).ConfigureAwait(false);
        using (loaded.Workspace)
        {
            var symbols = await SymbolFinder.FindDeclarationsAsync(
                    loaded.Project,
                    query.Trim(),
                    ignoreCase: true,
                    SymbolFilter.TypeAndMember,
                    cancellationToken)
                .ConfigureAwait(false);
            var matches = symbols.Take(MaximumMatches).Select(symbol => ToSymbolLocation(symbol, resolved.ProjectDirectory)).ToList();
            return new RoslynSymbolResult(
                true,
                projectPath,
                query,
                matches.Count == 0 ? "Roslyn found no matching declarations." : $"Roslyn found {matches.Count} matching declaration(s).",
                matches);
        }
    }

    public async Task<RoslynCompletionResult> GetCompletionsAsync(
        string projectPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        if (line < 1 || column < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "Line and column are one-based and must be positive.");
        }

        var resolved = resolver.ResolveExistingProject(projectPath);
        var physicalDocument = resolver.ResolveDocument(resolved, documentPath);
        var loaded = await LoadProjectAsync(resolved, cancellationToken).ConfigureAwait(false);
        using (loaded.Workspace)
        {
            var document = loaded.Project.Documents.FirstOrDefault(candidate =>
                candidate.FilePath?.Equals(physicalDocument, StringComparison.OrdinalIgnoreCase) == true)
                ?? throw new InvalidOperationException("Roslyn did not load the requested document as part of this project.");
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (line > text.Lines.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(line), "The requested line is beyond the end of the document.");
            }

            var textLine = text.Lines[line - 1];
            var zeroBasedColumn = column - 1;
            if (zeroBasedColumn > textLine.Span.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(column), "The requested column is beyond the end of the line.");
            }

            var service = CompletionService.GetService(document)
                ?? throw new InvalidOperationException("Roslyn completion services are unavailable for this document.");
            var completions = await service.GetCompletionsAsync(
                    document,
                    textLine.Start + zeroBasedColumn,
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
    }

    private static async Task<LoadedRoslynProject> LoadProjectAsync(
        AliResolvedCodingProject resolved,
        CancellationToken cancellationToken)
    {
        AliMsBuildRuntime.EnsureRegistered();
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["Configuration"] = "Debug",
            ["RestoreIgnoreFailedSources"] = "true"
        });
        var warnings = new ConcurrentQueue<string>();
        workspace.RegisterWorkspaceFailedHandler(args => warnings.Enqueue(args.Diagnostic.Message));
        try
        {
            var project = await workspace.OpenProjectAsync(
                    resolved.PhysicalPath,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return new LoadedRoslynProject(workspace, project, warnings.ToArray());
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static RoslynDiagnosticItem ToDiagnosticItem(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new RoslynDiagnosticItem(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
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

    private sealed record LoadedRoslynProject(MSBuildWorkspace Workspace, Project Project, IReadOnlyList<string> Warnings);
}
