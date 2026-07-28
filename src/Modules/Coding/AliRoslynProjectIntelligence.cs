using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
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
        var diagnostics = new List<RoslynDiagnosticItem>();
        var documentCount = 0;
        foreach (var project in session.Solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            documentCount += project.DocumentIds.Count;
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            diagnostics.AddRange(compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .Select(ToDiagnosticItem));
        }

        diagnostics = diagnostics
            .OrderByDescending(diagnostic => diagnostic.Severity == "Error")
            .ThenBy(diagnostic => diagnostic.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(diagnostic => diagnostic.Line)
            .Take(MaximumDiagnostics)
            .ToList();
        var errors = diagnostics.Count(item => item.Severity == "Error");
        var warnings = diagnostics.Count - errors;
        return new RoslynAnalysisResult(
            errors == 0,
            projectPath,
            $"Roslyn loaded {documentCount} C# document(s) and found {errors} error(s) and {warnings} warning(s).",
            documentCount,
            diagnostics,
            session.Warnings);
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
}
