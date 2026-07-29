namespace Ali.Modules.Coding.Languages;

public enum AliProgrammingLanguage
{
    Unknown,
    CSharp,
    Python,
    JavaScript,
    TypeScript,
    Html,
    Css,
    Java,
    Cpp,
    Arduino
}

[Flags]
public enum AliLanguageCapability
{
    None = 0,
    Inspect = 1 << 0,
    Index = 1 << 1,
    Analyze = 1 << 2,
    Format = 1 << 3,
    Build = 1 << 4,
    Test = 1 << 5,
    Run = 1 << 6,
    Debug = 1 << 7,
    Dependencies = 1 << 8,
    Architecture = 1 << 9,
    Coverage = 1 << 10,
    Profile = 1 << 11
}

public sealed record AliLanguageToolchainStatus(
    string Name,
    bool Available,
    string? Executable,
    string Purpose);

public sealed record AliLanguageProviderStatus(
    string Id,
    string DisplayName,
    IReadOnlyList<AliProgrammingLanguage> Languages,
    AliLanguageCapability Capabilities,
    AliLanguageCapability AvailableCapabilities,
    IReadOnlyList<AliLanguageToolchainStatus> Toolchains);

public sealed record AliLanguageCapabilityReport(
    bool Success,
    string Summary,
    IReadOnlyList<string> SharedInfrastructure,
    IReadOnlyList<AliLanguageProviderStatus> Providers);

public sealed record AliLanguageOperationResult(
    bool Success,
    string Provider,
    string Operation,
    string Summary,
    int? ExitCode,
    long DurationMilliseconds,
    string Output,
    IReadOnlyList<string> Artifacts);

internal interface IAliLanguageProvider : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlySet<AliProgrammingLanguage> Languages { get; }
    AliLanguageCapability Capabilities { get; }
    AliLanguageCapability GetAvailableCapabilities(AliResolvedLanguageProject? project = null);
    bool CanHandle(AliResolvedLanguageProject project);
    IReadOnlyList<AliLanguageToolchainStatus> InspectToolchains(AliResolvedLanguageProject? project = null);
    Task<AliLanguageOperationResult> AnalyzeAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken);
    Task<AliLanguageOperationResult> FormatAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken);
    Task<AliLanguageOperationResult> BuildAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken);
    Task<AliLanguageOperationResult> TestAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken);
    Task<AliLanguageOperationResult> RunAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken);
}

internal sealed class AliLanguageProviderRegistry
{
    private readonly List<IAliLanguageProvider> _providers = [];

    public IReadOnlyList<IAliLanguageProvider> Providers => _providers;

    public void Register(IAliLanguageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.Any(item => item.Id.Equals(provider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A coding provider named '{provider.Id}' is already registered.");
        }
        _providers.Add(provider);
    }

    public IAliLanguageProvider Resolve(AliResolvedLanguageProject project) =>
        _providers.FirstOrDefault(provider => provider.CanHandle(project))
        ?? throw new NotSupportedException(
            $"No registered coding provider handles {project.Language} project '{project.VirtualPath}'.");
}
