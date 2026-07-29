using Ali.Modules.Coding.Indexing;

namespace Ali.Modules.Coding.Languages;

public sealed record AliCodingProjectInspection(
    bool Success,
    string Summary,
    string TargetPath,
    string Manifest,
    AliProgrammingLanguage Language,
    string? Provider,
    AliLanguageCapability Capabilities,
    IReadOnlyList<AliLanguageToolchainStatus> Toolchains);

internal sealed class AliMultiLanguageCodingTools
{
    private readonly AliLanguageProjectResolver _resolver;
    private readonly AliLanguageProviderRegistry _registry;
    private readonly AliSourceIndexService _index;

    public AliMultiLanguageCodingTools(
        AliLanguageProjectResolver resolver,
        AliLanguageProviderRegistry registry,
        AliSourceIndexService index)
    {
        _resolver = resolver;
        _registry = registry;
        _index = index;
    }

    public AliLanguageCapabilityReport GetCapabilities()
    {
        var providers = _registry.Providers
            .Select(provider => new AliLanguageProviderStatus(
                provider.Id,
                provider.DisplayName,
                provider.Languages.OrderBy(language => language).ToArray(),
                provider.Capabilities,
                provider.GetAvailableCapabilities(),
                provider.InspectToolchains()))
            .ToArray();
        return new AliLanguageCapabilityReport(
            true,
            $"Ali has {providers.Length} registered coding provider(s). Capabilities reflect the live registry, not model memory.",
            [
                "Approved project resolver",
                "Bounded no-shell process runner",
                "Language Server Protocol client",
                "Debug Adapter Protocol client",
                "Bounded structural source index",
                "MCP-compatible high-level coding tools"
            ],
            providers);
    }

    public AliCodingProjectInspection InspectProject(string targetPath)
    {
        var project = _resolver.Resolve(targetPath);
        var provider = _registry.Providers.FirstOrDefault(item => item.CanHandle(project));
        return new AliCodingProjectInspection(
            true,
            provider is null
                ? $"Detected a {project.Language} target, but no provider is registered yet."
                : $"Detected a {project.Language} target handled by {provider.DisplayName}.",
            project.VirtualPath,
            project.ManifestName,
            project.Language,
            provider?.Id,
            provider?.GetAvailableCapabilities(project) ?? (AliLanguageCapability.Inspect | AliLanguageCapability.Index),
            provider?.InspectToolchains(project) ?? []);
    }

    public Task<AliSourceIndexResult> IndexProjectAsync(string targetPath, CancellationToken cancellationToken) =>
        _index.BuildAsync(targetPath, cancellationToken);

    public Task<AliSymbolSearchResult> SearchSymbolsAsync(
        string targetPath,
        string query,
        int maximumResults,
        CancellationToken cancellationToken) =>
        _index.SearchAsync(targetPath, query, maximumResults, cancellationToken);

    public Task<AliLanguageOperationResult> AnalyzeAsync(string targetPath, CancellationToken cancellationToken) =>
        WithProviderAsync(targetPath, (provider, project) => provider.AnalyzeAsync(project, cancellationToken));

    public Task<AliLanguageOperationResult> FormatAsync(string targetPath, CancellationToken cancellationToken) =>
        WithProviderAsync(targetPath, (provider, project) => provider.FormatAsync(project, cancellationToken));

    public Task<AliLanguageOperationResult> BuildAsync(string targetPath, string? configuration, CancellationToken cancellationToken) =>
        WithProviderAsync(targetPath, (provider, project) => provider.BuildAsync(project, configuration, cancellationToken));

    public Task<AliLanguageOperationResult> TestAsync(string targetPath, string? configuration, CancellationToken cancellationToken) =>
        WithProviderAsync(targetPath, (provider, project) => provider.TestAsync(project, configuration, cancellationToken));

    public Task<AliLanguageOperationResult> RunAsync(string targetPath, string? configuration, CancellationToken cancellationToken) =>
        WithProviderAsync(targetPath, (provider, project) => provider.RunAsync(project, configuration, cancellationToken));

    private Task<AliLanguageOperationResult> WithProviderAsync(
        string targetPath,
        Func<IAliLanguageProvider, AliResolvedLanguageProject, Task<AliLanguageOperationResult>> operation)
    {
        var project = _resolver.Resolve(targetPath);
        return operation(_registry.Resolve(project), project);
    }
}
