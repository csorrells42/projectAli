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

public enum ExternalCodingAgentProgressKind
{
    Started,
    Working,
    Completed,
    Warning,
    Error
}

public sealed record ExternalCodingAgentProgress(
    string Provider,
    ExternalCodingAgentProgressKind Kind,
    string Title,
    string Detail);

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
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string, bool>? outputLine = null);
}

internal sealed class ExternalCodingAgentProcessRunner : IExternalCodingAgentProcessRunner
{
    public Task<BoundedProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string, bool>? outputLine = null) =>
        AliBoundedProcessRunner.RunAsync(
            executable,
            workingDirectory,
            arguments,
            Timeout.InfiniteTimeSpan,
            cancellationToken,
            environment,
            outputLine);
}

/// <summary>
/// One modular entry point for Ali's external programming engines. The same instance is
/// exposed to the Agent Framework coordinator and MCP catalog through AliCodingModule.
/// English intent is never classified here; the selected persisted mode controls only
/// which already-selected provider executes an explicit programming objective.
/// </summary>
internal sealed class AliExternalCodingAgents
{
    private readonly AliWorkstationFileAccess _fileAccess;
    private readonly AliLanguageProjectResolver _resolver;
    private readonly Func<AgentOrchestrationSettings> _settings;
    private readonly IExternalCodingAgentProvider _aider;
    private readonly IExternalCodingAgentProvider _openHands;

    public event Action<ExternalCodingAgentProgress>? ProgressReported;

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
        _fileAccess = fileAccess;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var runner = processRunner ?? new ExternalCodingAgentProcessRunner();
        var root = installRoot ?? AppContext.BaseDirectory;
        _resolver = new AliLanguageProjectResolver(fileAccess);
        _aider = aider ?? new AiderCodingAgentProvider(root, runtimeSettings, runner, ReportProgress);
        _openHands = openHands ?? new OpenHandsCodingAgentProvider(settings, runtimeSettings, runner, ReportProgress);
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
        var projectDirectory = await ResolveOrCreateProjectDirectoryAsync(targetPath, cancellationToken)
            .ConfigureAwait(false);
        var mode = _settings().Normalize().ProgrammingAgentMode;
        var passes = new List<ExternalCodingAgentPassResult>();

        if (mode == ProgrammingAgentModes.Off)
        {
            return Finish(
                false,
                mode,
                targetPath,
                passes,
                "Ali is the selected coding executor. No external coding agent was started.");
        }

        if (mode == ProgrammingAgentModes.OpenHands)
        {
            var tractorTask = BuildScopedObjective(
                objective,
                "Perform one bounded collaboration pass for this objective. Use your native file, terminal, build, test, run, inspection and debugging tools as needed within the approved project directory. Inspect the project, implement as much of the requested result as this pass can verify, run authoritative checks, and return exact evidence and remaining diagnostics so Ali can continue through her effective tools. Do not access paths outside the current project directory.");
            var tractor = await _openHands.ExecuteAsync(projectDirectory, tractorTask, cancellationToken).ConfigureAwait(false);
            passes.Add(tractor);
            if (!tractor.Success)
            {
                return Finish(false, mode, targetPath, passes,
                    "OpenHands could not complete its bounded collaboration pass. Its exact diagnostics are available for Ali to inspect and continue through her effective native tools or a later approved collaboration pass.");
            }
        }

        if (mode == ProgrammingAgentModes.Aider)
        {
            var refinement = await _aider.ExecuteAsync(
                projectDirectory,
                BuildScopedObjective(
                    objective,
                    "Perform one bounded collaboration pass for this objective. Act as architect and senior implementer, use Aider's native repository map, file-editing, shell-command, lint and automatic build/test repair capabilities within the approved project directory, and return exact evidence and remaining diagnostics so Ali can continue through her effective tools. Do not access paths outside the current project directory."),
                cancellationToken).ConfigureAwait(false);
            passes.Add(refinement);
            if (!refinement.Success)
            {
                return Finish(false, mode, targetPath, passes,
                    "Aider could not complete its bounded architect/refinement pass. Its exact diagnostics are available for Ali to inspect and continue through her effective native tools or a later approved collaboration pass.");
            }
        }

        return Finish(
            true,
            mode,
            targetPath,
            passes,
            $"{passes[^1].Provider} completed its bounded collaboration pass. Ali remains responsible for the turn and may continue with any effective native inspection, editing, build, test, or runtime tools.");
    }

    private async Task<string> ResolveOrCreateProjectDirectoryAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        var resolved = _fileAccess.ResolvePhysicalDirectoryPath(targetPath);
        if (File.Exists(resolved.PhysicalPath))
        {
            return _resolver.Resolve(targetPath).ProjectDirectory;
        }

        if (!Directory.Exists(resolved.PhysicalPath))
        {
            await _fileAccess.Store.CreateDirectoryAsync(targetPath, cancellationToken).ConfigureAwait(false);
        }

        AliCodingProjectResolver.RejectReparsePoints(resolved.MountRoot, resolved.PhysicalPath);
        return resolved.PhysicalPath;
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

    private void ReportProgress(ExternalCodingAgentProgress progress) =>
        ProgressReported?.Invoke(progress);
}
