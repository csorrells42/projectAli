using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;

namespace Ali.Modules.Coding.RoslynQueries;

/// <summary>
/// Broker-authorized production entry points for Roslyn semantic queries. The underlying
/// Roslyn tools stay independently reusable; only these production-facing entry points
/// require and consume the exact one-use execution grant.
/// </summary>
internal sealed class AliRoslynQueryFacade(
    AliRoslynCodingTools tools,
    AliCodingProjectResolver resolver,
    AliRoslynWorkspaceLoader workspaceLoader,
    AliRoslynQueryTargetStateAdapter targetStates)
{
    private readonly AliRoslynCodingTools _tools =
        tools ?? throw new ArgumentNullException(nameof(tools));
    private readonly AliCodingProjectResolver _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly AliRoslynWorkspaceLoader _workspaceLoader =
        workspaceLoader ?? throw new ArgumentNullException(nameof(workspaceLoader));
    private readonly AliRoslynQueryTargetStateAdapter _targetStates =
        targetStates ?? throw new ArgumentNullException(nameof(targetStates));

    public async Task<RoslynAnalysisResult> AnalyzeAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        using var session = await AuthorizeAndLoadAsync(
                AliCapabilityCatalog.RoslynAnalyzeProjectName,
                projectPath,
                documentPath: null,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _tools.AnalyzeLoadedAsync(session, projectPath, cancellationToken)
            .ConfigureAwait(false);
        await RequireBoundSemanticFingerprintAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<RoslynSymbolResult> FindSymbolAsync(
        string projectPath,
        string query,
        CancellationToken cancellationToken)
    {
        using var session = await AuthorizeAndLoadAsync(
                AliCapabilityCatalog.RoslynFindSymbolName,
                projectPath,
                documentPath: null,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _tools.FindSymbolLoadedAsync(
                session,
                projectPath,
                query,
                cancellationToken)
            .ConfigureAwait(false);
        await RequireBoundSemanticFingerprintAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<RoslynCompletionResult> GetCompletionsAsync(
        string projectPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        using var session = await AuthorizeAndLoadAsync(
                AliCapabilityCatalog.RoslynGetCompletionsName,
                projectPath,
                documentPath,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _tools.GetCompletionsLoadedAsync(
            session,
            projectPath,
            documentPath,
            line,
            column,
            cancellationToken).ConfigureAwait(false);
        await RequireBoundSemanticFingerprintAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<RoslynSolutionOverviewResult> InspectSolutionAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var session = await AuthorizeAndLoadAsync(
                AliCapabilityCatalog.RoslynInspectSolutionName,
                targetPath,
                documentPath: null,
                cancellationToken)
            .ConfigureAwait(false);
        var result = _tools.InspectSolutionLoaded(session, targetPath);
        await RequireBoundSemanticFingerprintAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<RoslynDocumentResult> InspectDocumentAsync(
        string targetPath,
        string documentPath,
        CancellationToken cancellationToken)
    {
        using var session = await AuthorizeAndLoadAsync(
                AliCapabilityCatalog.RoslynInspectDocumentName,
                targetPath,
                documentPath,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _tools.InspectDocumentLoadedAsync(
                session,
                targetPath,
                documentPath,
                cancellationToken)
            .ConfigureAwait(false);
        await RequireBoundSemanticFingerprintAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<RoslynPositionResult> InspectPositionAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        using var session = await AuthorizeAndLoadAsync(
                AliCapabilityCatalog.RoslynInspectPositionName,
                targetPath,
                documentPath,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _tools.InspectPositionLoadedAsync(
            session,
            targetPath,
            documentPath,
            line,
            column,
            cancellationToken).ConfigureAwait(false);
        await RequireBoundSemanticFingerprintAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<RoslynReferenceResult> FindReferencesAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        using var session = await AuthorizeAndLoadAsync(
                AliCapabilityCatalog.RoslynFindReferencesName,
                targetPath,
                documentPath,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _tools.FindReferencesLoadedAsync(
            session,
            targetPath,
            documentPath,
            line,
            column,
            cancellationToken).ConfigureAwait(false);
        await RequireBoundSemanticFingerprintAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<AliRoslynWorkspaceSession> AuthorizeAndLoadAsync(
        string toolName,
        string targetPath,
        string? documentPath,
        CancellationToken cancellationToken)
    {
        var capabilityId = AliRoslynQueryExecutionAdapter.CapabilityIdFor(toolName);
        var reconcilerId = AliRoslynQueryExecutionAdapter.ReconcilerIdFor(toolName);
        if (!AliExecutionGrantContext.TryConsumeCurrent(
                toolName,
                capabilityId,
                reconcilerId,
                out var grant)
            || grant is null)
        {
            throw new InvalidOperationException(
                "Roslyn semantic queries require an exact durable execution-broker grant.");
        }

        var target = _resolver.ResolveExistingTarget(targetPath);
        var expectedRootBinding =
            AliRoslynActionExecutionAdapter.RootBinding(target.RootDirectory);
        if (!string.Equals(
                grant.RootBinding,
                expectedRootBinding,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Roslyn semantic-query grant does not authorize this source root.");
        }

        var documentPhysicalPath = documentPath is null
            ? null
            : _resolver.ResolveDocument(target, documentPath);
        _targetStates.RequireStaticGrantVersion(
            grant,
            target,
            documentPhysicalPath);
        AliRoslynWorkspaceSession? session = null;
        try
        {
            session = await _workspaceLoader.LoadAsync(target, cancellationToken)
                .ConfigureAwait(false);
            await _targetStates.BindLoadedAsync(
                    session,
                    target,
                    documentPhysicalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            _targetStates.RequireStaticGrantVersion(
                grant,
                target,
                documentPhysicalPath);
            return session;
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    private Task RequireBoundSemanticFingerprintAsync(
        AliRoslynWorkspaceSession session,
        CancellationToken cancellationToken) =>
        _targetStates.RequireBoundSemanticFingerprintAsync(
            session,
            cancellationToken);
}
