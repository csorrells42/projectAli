using System.Text.Json;
using Ali.Modules.Coding.Engineering;

namespace Ali.Modules.Coding.Languages;

/// <summary>Adapts Ali's proven Roslyn/MSBuild implementation to the shared provider contract.</summary>
internal sealed class AliDotNetLanguageProvider : IAliLanguageProvider
{
    private readonly AliRoslynCodingTools _tools;
    private readonly AliDotNetEngineeringLoop _engineering;
    private readonly AliToolchainLocator _locator;

    public AliDotNetLanguageProvider(
        AliRoslynCodingTools tools,
        AliDotNetEngineeringLoop engineering,
        AliToolchainLocator locator)
    {
        _tools = tools;
        _engineering = engineering;
        _locator = locator;
    }

    public string Id => "dotnet-roslyn";
    public string DisplayName => ".NET / Microsoft Roslyn";
    public IReadOnlySet<AliProgrammingLanguage> Languages { get; } = new HashSet<AliProgrammingLanguage> { AliProgrammingLanguage.CSharp };
    public AliLanguageCapability Capabilities =>
        AliLanguageCapability.Inspect | AliLanguageCapability.Index | AliLanguageCapability.Analyze
        | AliLanguageCapability.Format | AliLanguageCapability.Build | AliLanguageCapability.Test
        | AliLanguageCapability.Run | AliLanguageCapability.Debug | AliLanguageCapability.Dependencies
        | AliLanguageCapability.Architecture | AliLanguageCapability.Coverage | AliLanguageCapability.Profile;

    public bool CanHandle(AliResolvedLanguageProject project) => project.Language == AliProgrammingLanguage.CSharp;

    public AliLanguageCapability GetAvailableCapabilities(AliResolvedLanguageProject? project = null)
    {
        var tools = InspectToolchains(project);
        var available = tools.First(item => item.Name == "dotnet").Available
            ? Capabilities
            : AliLanguageCapability.Inspect | AliLanguageCapability.Index | AliLanguageCapability.Architecture;
        if (!tools.First(item => item.Name == "netcoredbg").Available)
        {
            available &= ~AliLanguageCapability.Debug;
        }
        return available;
    }

    public IReadOnlyList<AliLanguageToolchainStatus> InspectToolchains(AliResolvedLanguageProject? project = null)
    {
        var dotnet = _locator.Resolve(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var debugger = Environment.GetEnvironmentVariable("ALI_NETCOREDBG_PATH");
        if (string.IsNullOrWhiteSpace(debugger) || !File.Exists(debugger))
        {
            debugger = Path.Combine(AppContext.BaseDirectory, "dependencies", "debugger", "netcoredbg", "netcoredbg.exe");
        }
        return
        [
            new AliLanguageToolchainStatus("dotnet", dotnet is not null, dotnet, "Build, test, run, publish, and diagnostics"),
            new AliLanguageToolchainStatus("netcoredbg", File.Exists(debugger), File.Exists(debugger) ? debugger : null, "Debug Adapter Protocol")
        ];
    }

    public async Task<AliLanguageOperationResult> AnalyzeAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var result = await _tools.AnalyzeAsync(project.VirtualPath, cancellationToken).ConfigureAwait(false);
        return new AliLanguageOperationResult(result.Success, Id, "analyze", result.Summary, null, 0,
            JsonSerializer.Serialize(result), []);
    }

    public async Task<AliLanguageOperationResult> FormatAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var result = await _tools.FormatAsync(project.VirtualPath, cancellationToken).ConfigureAwait(false);
        return new AliLanguageOperationResult(result.Success, Id, "format", result.Summary, null, 0,
            JsonSerializer.Serialize(result), result.ChangedFiles);
    }

    public async Task<AliLanguageOperationResult> BuildAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var result = await _tools.BuildAsync(project.VirtualPath, configuration, cancellationToken).ConfigureAwait(false);
        return new AliLanguageOperationResult(result.Success, Id, "build", result.Summary, result.ExitCode,
            result.DurationMilliseconds, result.Output, result.ArtifactPath is null ? [] : [result.ArtifactPath]);
    }

    public async Task<AliLanguageOperationResult> TestAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var result = await _engineering.TestAsync(project.VirtualPath, configuration, cancellationToken).ConfigureAwait(false);
        return new AliLanguageOperationResult(result.Success, Id, "test", result.Summary, result.Success ? 0 : 1,
            result.DurationMilliseconds, result.Output, result.ResultsPath is null ? [] : [result.ResultsPath]);
    }

    public async Task<AliLanguageOperationResult> RunAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var result = await _tools.RunAsync(project.VirtualPath, configuration, cancellationToken).ConfigureAwait(false);
        return new AliLanguageOperationResult(
            result.Success,
            Id,
            "run",
            result.ProcessId is null ? result.Summary : $"{result.Summary} Process ID: {result.ProcessId}.",
            result.Success ? 0 : null,
            0,
            result.Summary,
            result.ArtifactPath is null ? [] : [result.ArtifactPath]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
