using System.Collections.Concurrent;
using Ali.Modules.Coding.RoslynActions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Ali.Modules.Coding;

internal sealed class AliRoslynWorkspaceSession : IDisposable
{
    private AliRoslynSolutionFingerprintSnapshot? _semanticFingerprint;
    private readonly ConcurrentQueue<string> _workspaceWarnings;

    public AliRoslynWorkspaceSession(
        MSBuildWorkspace workspace,
        Solution solution,
        AliResolvedCodingTarget target,
        ConcurrentQueue<string> workspaceWarnings)
    {
        Workspace = workspace;
        Solution = solution;
        Target = target;
        _workspaceWarnings = workspaceWarnings
            ?? throw new ArgumentNullException(nameof(workspaceWarnings));
    }

    public MSBuildWorkspace Workspace { get; }
    public Solution Solution { get; }
    public AliResolvedCodingTarget Target { get; }
    public IReadOnlyList<string> Warnings => _workspaceWarnings.ToArray();
    public AliRoslynSolutionFingerprintSnapshot SemanticFingerprint =>
        Volatile.Read(ref _semanticFingerprint)
        ?? throw new InvalidOperationException(
            "The loaded Roslyn workspace has not received its authorized semantic fingerprint.");

    internal void BindSemanticFingerprint(
        AliRoslynSolutionFingerprintSnapshot semanticFingerprint)
    {
        ArgumentNullException.ThrowIfNull(semanticFingerprint);
        if (Interlocked.CompareExchange(
                ref _semanticFingerprint,
                semanticFingerprint,
                comparand: null) is not null)
        {
            throw new InvalidOperationException(
                "The loaded Roslyn workspace semantic fingerprint is already bound.");
        }
    }

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Owns Roslyn/MSBuildWorkspace lifetime and loads either one SDK project or an
/// entire solution while retaining the approved mount boundary.
/// </summary>
internal sealed class AliRoslynWorkspaceLoader(AliCodingProjectResolver resolver)
{
    public AliResolvedCodingTarget ResolveTarget(string targetPath) =>
        resolver.ResolveExistingTarget(targetPath);

    public async Task<AliRoslynWorkspaceSession> LoadAsync(
        string targetPath,
        CancellationToken cancellationToken) =>
        await LoadAsync(ResolveTarget(targetPath), cancellationToken).ConfigureAwait(false);

    public async Task<AliRoslynWorkspaceSession> LoadAsync(
        AliResolvedCodingTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
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
            var solution = target.IsSolution
                ? await workspace.OpenSolutionAsync(target.PhysicalPath, progress: null, cancellationToken).ConfigureAwait(false)
                : (await workspace.OpenProjectAsync(target.PhysicalPath, progress: null, cancellationToken).ConfigureAwait(false)).Solution;
            return new AliRoslynWorkspaceSession(workspace, solution, target, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public async Task<(Document Document, int Position)> ResolvePositionAsync(
        AliRoslynWorkspaceSession session,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        if (line < 1 || column < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "Line and column are one-based and must be positive.");
        }

        var physicalDocument = resolver.ResolveDocument(session.Target, documentPath);
        var matches = session.Solution.Projects
            .SelectMany(project => project.Documents)
            .Where(candidate => candidate.FilePath?.Equals(
                physicalDocument,
                StringComparison.OrdinalIgnoreCase) == true)
            .Take(2)
            .ToArray();
        var document = matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                matches.Length == 0
                    ? "Roslyn did not load the requested document as part of this target."
                    : "The requested physical document is shared by more than one Roslyn project; an exact project identity is required.");
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

        return (document, textLine.Start + zeroBasedColumn);
    }

    internal static async Task RequireExactLoadAsync(
        AliRoslynWorkspaceSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = session.Warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (warnings.Length != 0)
        {
            throw new InvalidOperationException(
                "Roslyn refused an exact semantic operation because MSBuildWorkspace reported: "
                + string.Join(" | ", warnings));
        }

        var projects = session.Solution.Projects.ToArray();
        if (projects.Length == 0)
        {
            throw new InvalidOperationException(
                "Roslyn loaded no projects for the exact semantic target.");
        }

        var projectIds = projects.Select(project => project.Id).ToHashSet();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(project.FilePath)
                || project.ProjectReferences.Any(reference => !projectIds.Contains(reference.ProjectId))
                || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException(
                    $"Roslyn incompletely loaded exact project '{project.Name}'.");
            }
        }

        warnings = session.Warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (warnings.Length != 0)
        {
            throw new InvalidOperationException(
                "Roslyn refused an exact semantic operation because MSBuildWorkspace reported: "
                + string.Join(" | ", warnings));
        }
    }
}
