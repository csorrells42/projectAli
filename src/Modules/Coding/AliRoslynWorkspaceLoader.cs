using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Ali.Modules.Coding;

internal sealed class AliRoslynWorkspaceSession : IDisposable
{
    public AliRoslynWorkspaceSession(
        MSBuildWorkspace workspace,
        Solution solution,
        AliResolvedCodingTarget target,
        IReadOnlyList<string> warnings)
    {
        Workspace = workspace;
        Solution = solution;
        Target = target;
        Warnings = warnings;
    }

    public MSBuildWorkspace Workspace { get; }
    public Solution Solution { get; }
    public AliResolvedCodingTarget Target { get; }
    public IReadOnlyList<string> Warnings { get; }

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Owns Roslyn/MSBuildWorkspace lifetime and loads either one SDK project or an
/// entire solution while retaining the approved mount boundary.
/// </summary>
internal sealed class AliRoslynWorkspaceLoader(AliCodingProjectResolver resolver)
{
    public async Task<AliRoslynWorkspaceSession> LoadAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        var target = resolver.ResolveExistingTarget(targetPath);
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
            return new AliRoslynWorkspaceSession(workspace, solution, target, warnings.ToArray());
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
        var document = session.Solution.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath?.Equals(physicalDocument, StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new InvalidOperationException("Roslyn did not load the requested document as part of this target.");
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
}
