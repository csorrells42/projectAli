namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynActionDescriptor(
    string Id,
    string DisplayName,
    string Description,
    bool MutatesSource,
    bool RequiresPreview);

public sealed record AliRoslynTargetInspection(
    bool Success,
    string TargetPath,
    RoslynSolutionOverviewResult Solution,
    RoslynAnalysisResult Analysis,
    IReadOnlyList<AliRoslynActionDescriptor> Actions,
    string Summary);

public sealed record AliRoslynActionPreview(
    bool Success,
    string? HandleId,
    string ActionId,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<RoslynTextChange> Changes,
    IReadOnlyList<string> Warnings);

public sealed record AliRoslynActionApplication(
    bool Success,
    string HandleId,
    string ActionId,
    bool Applied,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Warnings);

public sealed record AliRoslynActionVerification(
    bool Success,
    string HandleId,
    string Summary,
    RoslynAnalysisResult Analysis);

internal sealed class AliRoslynActionDeck(
    AliRoslynCodingTools tools,
    AliCodingProjectResolver resolver,
    AliRoslynActionHandleStore handles)
{
    public const string SemanticRenameActionId = "semantic-rename";

    private static IReadOnlyList<AliRoslynActionDescriptor> KnownActions { get; } =
    [
        new(
            SemanticRenameActionId,
            "Semantic rename",
            "Use Roslyn symbol identity to rename a C# declaration and every semantic reference. Preview and staged verification are required before publication.",
            true,
            true)
    ];

    public IReadOnlyList<AliRoslynActionDescriptor> ListActions() => KnownActions;

    public async Task<AliRoslynTargetInspection> InspectTargetAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        var solution = await tools.InspectSolutionAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var target = resolver.ResolveExistingTarget(targetPath);
        if (target.IsSolution)
        {
            return new AliRoslynTargetInspection(
                solution.Success,
                targetPath,
                solution,
                new RoslynAnalysisResult(
                    solution.Success,
                    targetPath,
                    "Project diagnostics are available after selecting one project from this solution.",
                    solution.Projects.Sum(project => project.DocumentCount),
                    [],
                    solution.WorkspaceWarnings),
                KnownActions,
                "Roslyn inspected the solution graph. Select an exact project before previewing a source action.");
        }

        var analysis = await tools.AnalyzeAsync(targetPath, cancellationToken).ConfigureAwait(false);
        return new AliRoslynTargetInspection(
            solution.Success && analysis.Success,
            targetPath,
            solution,
            analysis,
            KnownActions,
            $"Roslyn inspected the target and found {analysis.Diagnostics.Count} compiler diagnostic(s)." );
    }

    public async Task<AliRoslynActionPreview> PreviewRenameAsync(
        string projectPath,
        string documentPath,
        int line,
        int column,
        string newName,
        CancellationToken cancellationToken)
    {
        var preview = await tools.PreviewRenameAsync(
            projectPath,
            documentPath,
            line,
            column,
            newName,
            cancellationToken).ConfigureAwait(false);
        if (!preview.Success)
        {
            return new(false, null, SemanticRenameActionId, preview.Summary, preview.ChangedFiles, preview.Changes, preview.WorkspaceWarnings);
        }

        var target = resolver.ResolveExistingTarget(projectPath);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in preview.ChangedFiles)
        {
            var path = Path.GetFullPath(Path.Combine(target.RootDirectory, relativePath));
            hashes[path] = await AliRoslynActionHandleStore.HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var handle = new AliRoslynActionHandle(
            Guid.NewGuid().ToString("N"),
            SemanticRenameActionId,
            projectPath,
            documentPath,
            line,
            column,
            newName,
            hashes,
            DateTimeOffset.UtcNow);
        await handles.SaveAsync(handle, cancellationToken).ConfigureAwait(false);
        return new(true, handle.Id, handle.ActionId, preview.Summary, preview.ChangedFiles, preview.Changes, preview.WorkspaceWarnings);
    }

    public async Task<AliRoslynActionApplication> ApplyAsync(
        string handleId,
        CancellationToken cancellationToken)
    {
        var handle = await handles.LoadAsync(handleId, cancellationToken).ConfigureAwait(false);
        foreach (var (path, expectedHash) in handle.SourceHashes)
        {
            var currentHash = await AliRoslynActionHandleStore.HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
            {
                return new(
                    false,
                    handle.Id,
                    handle.ActionId,
                    false,
                    $"Roslyn refused the stale action handle because {path} changed after preview.",
                    [],
                    []);
            }
        }

        if (!string.Equals(handle.ActionId, SemanticRenameActionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown Roslyn action: {handle.ActionId}");
        }

        var result = await tools.ApplyRenameAsync(
            handle.TargetPath,
            handle.DocumentPath,
            handle.Line,
            handle.Column,
            handle.RequestedValue,
            cancellationToken).ConfigureAwait(false);
        return new(
            result.Success && result.Applied,
            handle.Id,
            handle.ActionId,
            result.Applied,
            result.Summary,
            result.ChangedFiles,
            result.WorkspaceWarnings);
    }

    public async Task<AliRoslynActionVerification> VerifyAsync(
        string handleId,
        CancellationToken cancellationToken)
    {
        var handle = await handles.LoadAsync(handleId, cancellationToken).ConfigureAwait(false);
        var analysis = await tools.AnalyzeAsync(handle.TargetPath, cancellationToken).ConfigureAwait(false);
        return new(
            analysis.Success,
            handle.Id,
            analysis.Success
                ? "Roslyn reloaded the canonical project after publication and found no compiler errors."
                : "Roslyn reloaded the canonical project and found compiler errors.",
            analysis);
    }
}
