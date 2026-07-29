using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Toolchains;

namespace Ali.Modules.Coding.Embedded.Arduino;

internal sealed class AliArduinoLanguageProvider(AliArduinoTools tools) : IAliLanguageProvider
{
    public string Id => "arduino-cli";
    public string DisplayName => "Arduino CLI and IDE";
    public IReadOnlySet<AliProgrammingLanguage> Languages { get; } = new HashSet<AliProgrammingLanguage> { AliProgrammingLanguage.Arduino };
    public AliLanguageCapability Capabilities => AliLanguageCapability.Inspect | AliLanguageCapability.Index | AliLanguageCapability.Analyze | AliLanguageCapability.Build | AliLanguageCapability.Dependencies;

    public AliLanguageCapability GetAvailableCapabilities(AliResolvedLanguageProject? project = null) =>
        File.Exists(AliDeveloperToolPaths.ArduinoCli) ? Capabilities : AliLanguageCapability.Inspect | AliLanguageCapability.Index;

    public bool CanHandle(AliResolvedLanguageProject project) => project.Language == AliProgrammingLanguage.Arduino;

    public IReadOnlyList<AliLanguageToolchainStatus> InspectToolchains(AliResolvedLanguageProject? project = null) =>
    [
        new("Arduino CLI", File.Exists(AliDeveloperToolPaths.ArduinoCli), AliDeveloperToolPaths.ArduinoCli, "Board discovery, libraries, cores, compilation, and upload"),
        new("Arduino IDE", File.Exists(AliDeveloperToolPaths.ArduinoIde), AliDeveloperToolPaths.ArduinoIde, "Interactive sketch editing and serial/board workflow")
    ];

    public async Task<AliLanguageOperationResult> AnalyzeAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var fqbn = ReadFqbn(project) ?? "arduino:avr:uno";
        var result = await tools.CompileAsync(project.VirtualPath, fqbn, cancellationToken).ConfigureAwait(false);
        return Convert(result, "analyze", "Arduino compilation diagnostics completed for the configured board profile.");
    }

    public Task<AliLanguageOperationResult> FormatAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken) =>
        Task.FromResult(Unsupported("format", "Arduino formatting is not exposed until a board-independent formatter is provisioned."));

    public async Task<AliLanguageOperationResult> BuildAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var fqbn = string.IsNullOrWhiteSpace(configuration) ? ReadFqbn(project) : configuration;
        if (string.IsNullOrWhiteSpace(fqbn))
            return Unsupported("build", "Supply the board FQBN as configuration or define default_fqbn in sketch.yaml; Ali will not guess hardware.");
        var result = await tools.CompileAsync(project.VirtualPath, fqbn, cancellationToken).ConfigureAwait(false);
        return Convert(result, "build", result.Summary);
    }

    public Task<AliLanguageOperationResult> TestAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken) =>
        Task.FromResult(Unsupported("test", "No board-independent Arduino test runner is configured; compile and hardware upload are separate explicit operations."));

    public Task<AliLanguageOperationResult> RunAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken) =>
        Task.FromResult(Unsupported("run", "Arduino firmware runs on explicitly selected hardware; use arduino_upload with a board FQBN and port."));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string? ReadFqbn(AliResolvedLanguageProject project)
    {
        var path = Path.Combine(project.ProjectDirectory, "sketch.yaml");
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path).Take(500))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("default_fqbn:", StringComparison.OrdinalIgnoreCase))
                return trimmed["default_fqbn:".Length..].Trim().Trim('"', '\'');
        }
        return null;
    }

    private static AliLanguageOperationResult Convert(AliArduinoOperationResult result, string operation, string summary) =>
        new(result.Success, "arduino-cli", operation, summary, result.ExitCode, result.DurationMilliseconds, result.Output, result.Artifacts);

    private static AliLanguageOperationResult Unsupported(string operation, string summary) =>
        new(false, "arduino-cli", operation, summary, null, 0, string.Empty, []);
}
