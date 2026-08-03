using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Xml.Linq;

namespace Ali.Modules.Coding;

public sealed record RoslynProjectOverview(
    string Name,
    string Language,
    string? ProjectPath,
    int DocumentCount,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> TargetFrameworks);

public sealed record RoslynSolutionOverviewResult(
    bool Success,
    string TargetPath,
    string Summary,
    IReadOnlyList<RoslynProjectOverview> Projects,
    IReadOnlyList<string> WorkspaceWarnings);

public sealed record RoslynReferenceLocation(
    string Kind,
    string Symbol,
    string? File,
    int? Line,
    int? Column,
    string Display);

public sealed record RoslynReferenceResult(
    bool Success,
    string TargetPath,
    string DocumentPath,
    string Summary,
    IReadOnlyList<RoslynReferenceLocation> Locations,
    IReadOnlyList<string> WorkspaceWarnings);

/// <summary>
/// Provides solution/project graph inspection and solution-wide semantic reference
/// discovery without mutating source or relying on textual search.
/// </summary>
internal sealed class AliRoslynSolutionIntelligence(AliRoslynWorkspaceLoader loader)
{
    private const int MaximumReferenceLocations = 250;

    public async Task<RoslynSolutionOverviewResult> InspectAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var session = await loader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        return Inspect(session, targetPath);
    }

    internal RoslynSolutionOverviewResult Inspect(
        AliRoslynWorkspaceSession session,
        string targetPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        var projects = session.Solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => new RoslynProjectOverview(
                project.Name,
                project.Language,
                RelativePath(session.Target.RootDirectory, project.FilePath),
                project.DocumentIds.Count,
                project.ProjectReferences
                    .Select(reference => session.Solution.GetProject(reference.ProjectId)?.Name ?? reference.ProjectId.Id.ToString())
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ReadTargetFrameworks(project.FilePath)))
            .ToList();

        return new RoslynSolutionOverviewResult(
            true,
            targetPath,
            $"Roslyn loaded {projects.Count} C# project(s) containing {projects.Sum(project => project.DocumentCount)} document(s).",
            projects,
            session.Warnings);
    }

    public async Task<RoslynReferenceResult> FindReferencesAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        using var session = await loader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        return await FindReferencesAsync(
                session,
                targetPath,
                documentPath,
                line,
                column,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RoslynReferenceResult> FindReferencesAsync(
        AliRoslynWorkspaceSession session,
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var (document, position) = await loader.ResolvePositionAsync(
            session,
            documentPath,
            line,
            column,
            cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return new RoslynReferenceResult(
                false,
                targetPath,
                documentPath,
                "Roslyn found no semantic symbol at the requested source position.",
                [],
                session.Warnings);
        }

        var locations = new List<RoslynReferenceLocation>();
        locations.AddRange(symbol.Locations
            .Where(location => location.IsInSource)
            .Select(location => ToLocation("Definition", symbol, location, session.Target.RootDirectory)));

        var references = await SymbolFinder.FindReferencesAsync(symbol, session.Solution, cancellationToken).ConfigureAwait(false);
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                if (locations.Count >= MaximumReferenceLocations)
                {
                    break;
                }

                locations.Add(ToLocation("Reference", reference.Definition, location.Location, session.Target.RootDirectory));
            }
        }

        return new RoslynReferenceResult(
            true,
            targetPath,
            documentPath,
            $"Roslyn found {locations.Count(location => location.Kind == "Reference")} reference(s) for {symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}.",
            locations,
            session.Warnings);
    }

    private static RoslynReferenceLocation ToLocation(
        string kind,
        ISymbol symbol,
        Location location,
        string rootDirectory)
    {
        var span = location.GetLineSpan();
        return new RoslynReferenceLocation(
            kind,
            symbol.Name,
            RelativePath(rootDirectory, span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
    }

    private static string? RelativePath(string rootDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathFullyQualified(path) ? Path.GetRelativePath(rootDirectory, path) : path;
    }

    private static IReadOnlyList<string> ReadTargetFrameworks(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return [];
        }

        try
        {
            return XDocument.Load(projectPath)
                .Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }
}
