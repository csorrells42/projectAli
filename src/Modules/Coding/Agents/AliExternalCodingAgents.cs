using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding.Agents;

public sealed record ExternalCodingAgentProviderStatus(
    string Provider,
    bool Ready,
    string Version,
    string Summary,
    string RuntimePath);

public sealed record ExternalCodingAgentStatus(
    string SelectedMode,
    ExternalCodingAgentProviderStatus Aider,
    ExternalCodingAgentProviderStatus OpenHands,
    string Summary);

public sealed record ExternalCodingAgentPassResult(
    string Provider,
    bool Success,
    int ExitCode,
    long DurationMilliseconds,
    string Summary,
    string Output);

public sealed record ExternalCodingAgentRunResult(
    bool Success,
    string Mode,
    string ProjectPath,
    IReadOnlyList<ExternalCodingAgentPassResult> Passes,
    string Summary);

internal interface IExternalCodingAgentProvider
{
    string Name { get; }

    Task<ExternalCodingAgentProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<ExternalCodingAgentPassResult> ExecuteAsync(
        string projectDirectory,
        string objective,
        CancellationToken cancellationToken);
}

internal interface IExternalCodingAgentProcessRunner
{
    Task<BoundedProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null);
}

internal sealed class ExternalCodingAgentProcessRunner : IExternalCodingAgentProcessRunner
{
    public Task<BoundedProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null) =>
        AliBoundedProcessRunner.RunAsync(
            executable,
            workingDirectory,
            arguments,
            Timeout.InfiniteTimeSpan,
            cancellationToken,
            environment);
}

/// <summary>
/// One modular entry point for Ali's external programming engines. The same instance is
/// exposed to the Agent Framework coordinator and MCP catalog through AliCodingModule.
/// English intent is never classified here; the selected persisted mode controls only
/// which already-selected provider executes an explicit programming objective.
/// </summary>
internal sealed class AliExternalCodingAgents
{
    private readonly AliLanguageProjectResolver _resolver;
    private readonly Func<AgentOrchestrationSettings> _settings;
    private readonly IExternalCodingAgentProvider _aider;
    private readonly IExternalCodingAgentProvider _openHands;

    public AliExternalCodingAgents(
        AliWorkstationFileAccess fileAccess,
        Func<AgentOrchestrationSettings> settings,
        Func<OpenAiCompatibleRuntimeOptions> runtimeSettings,
        string? installRoot = null,
        IExternalCodingAgentProcessRunner? processRunner = null,
        IExternalCodingAgentProvider? aider = null,
        IExternalCodingAgentProvider? openHands = null)
    {
        ArgumentNullException.ThrowIfNull(fileAccess);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var runner = processRunner ?? new ExternalCodingAgentProcessRunner();
        var root = installRoot ?? AppContext.BaseDirectory;
        _resolver = new AliLanguageProjectResolver(fileAccess);
        _aider = aider ?? new AiderCodingAgentProvider(root, runtimeSettings, runner);
        _openHands = openHands ?? new OpenHandsCodingAgentProvider(settings, runtimeSettings, runner);
    }

    public async Task<ExternalCodingAgentStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var selected = _settings().Normalize().ProgrammingAgentMode;
        var aider = await _aider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var openHands = await _openHands.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var selectedReady = selected switch
        {
            ProgrammingAgentModes.Aider => aider.Ready,
            ProgrammingAgentModes.OpenHands => openHands.Ready,
            _ => aider.Ready && openHands.Ready
        };
        return new ExternalCodingAgentStatus(
            selected,
            aider,
            openHands,
            selectedReady
                ? $"{selected} programming mode is ready."
                : $"{selected} programming mode is not fully ready; inspect the individual provider status before starting work.");
    }

    public async Task<ExternalCodingAgentRunResult> ExecuteAsync(
        string targetPath,
        string objective,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        var project = _resolver.Resolve(targetPath);
        var mode = _settings().Normalize().ProgrammingAgentMode;
        var passes = new List<ExternalCodingAgentPassResult>();

        if (mode is ProgrammingAgentModes.OpenHands or ProgrammingAgentModes.Hybrid)
        {
            var tractorTask = BuildScopedObjective(
                objective,
                "Implement the complete requested change in the current approved project. Inspect the existing project, modify it directly, build and test it when its native toolchain permits, and leave concrete evidence. Do not access paths outside the current project directory.");
            var tractor = await _openHands.ExecuteAsync(project.ProjectDirectory, tractorTask, cancellationToken).ConfigureAwait(false);
            passes.Add(tractor);
            if (!tractor.Success)
            {
                return Finish(false, mode, targetPath, passes,
                    "OpenHands could not complete the implementation pass. No unsupported success claim was made and Aider was not asked to polish an unverified tractor pass.");
            }
        }

        if (mode is ProgrammingAgentModes.Aider or ProgrammingAgentModes.Hybrid)
        {
            var role = mode == ProgrammingAgentModes.Hybrid
                ? "OpenHands has completed an implementation pass. Review the current working tree against the full objective, find omissions or weak design, improve the implementation directly, and run the most relevant available checks. Preserve correct existing work."
                : "Act as the architect and senior implementer. Inspect the current approved project, make the complete requested change directly, and run the most relevant available checks.";
            var refinement = await _aider.ExecuteAsync(
                project.ProjectDirectory,
                BuildScopedObjective(objective, role + " Do not access paths outside the current project directory."),
                cancellationToken).ConfigureAwait(false);
            passes.Add(refinement);
            if (!refinement.Success)
            {
                return Finish(false, mode, targetPath, passes,
                    "Aider could not complete its architect/refinement pass. Earlier file changes remain available for Ali to inspect; the workflow did not claim completion.");
            }
        }

        return Finish(true, mode, targetPath, passes,
            mode == ProgrammingAgentModes.Hybrid
                ? "OpenHands completed the implementation pass and Aider completed the architect/refinement pass. Ali must now inspect direct build, test, diff, or runtime evidence before claiming the user's task complete."
                : $"{passes[^1].Provider} completed the selected programming pass. Ali must now inspect direct build, test, diff, or runtime evidence before claiming the user's task complete.");
    }

    private static string BuildScopedObjective(string objective, string role) =>
        $"{role}{Environment.NewLine}{Environment.NewLine}User objective:{Environment.NewLine}{objective.Trim()}";

    private static ExternalCodingAgentRunResult Finish(
        bool success,
        string mode,
        string projectPath,
        IReadOnlyList<ExternalCodingAgentPassResult> passes,
        string summary) =>
        new(success, mode, projectPath, passes, summary);
}
