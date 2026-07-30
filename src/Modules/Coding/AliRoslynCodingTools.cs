using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Ali.Modules.Coding;

public sealed record DotNetBuildResult(
    bool Success,
    string ProjectPath,
    string Configuration,
    int? ExitCode,
    string Summary,
    string Output,
    string? ArtifactPath,
    long DurationMilliseconds,
    string? DiagnosticLogPath = null,
    int WarningCount = 0,
    int ErrorCount = 0,
    string? FailureKind = null,
    int? BlockingProcessId = null);

public sealed record DotNetRunResult(
    bool Success,
    string ProjectPath,
    string Summary,
    string? ArtifactPath,
    int? ProcessId);

public sealed record DotNetStopProjectResult(
    bool Success,
    string ProjectPath,
    string Summary,
    string? ArtifactPath,
    int? ProcessId,
    bool Forced);

/// <summary>
/// Coordinates Roslyn project intelligence, Microsoft's in-process MSBuild API, and
/// bounded launch operations for projects inside approved workstation mounts.
/// </summary>
internal sealed class AliRoslynCodingTools
{
    // Tool results are fed back into the local model. A raw MSBuild transcript can
    // be tens of thousands of characters and crowd the next agent decision out of
    // a 16K context window, so return only actionable diagnostics to the model.
    // The complete transcript is retained separately for human troubleshooting.
    private const int MaximumOutputCharacters = 4_000;
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AliCodingProjectResolver _resolver;
    private readonly AliCodingProjectTracker _projectTracker;
    private readonly AliRoslynProjectIntelligence _intelligence;
    private readonly AliRoslynSolutionIntelligence _solutionIntelligence;
    private readonly AliRoslynDocumentIntelligence _documentIntelligence;
    private readonly AliRoslynRefactoringService _refactoring;
    private readonly string _auditPath;
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TrackedProjectProcess> _runningProjects =
        new(StringComparer.OrdinalIgnoreCase);

    public AliRoslynCodingTools(
        AliCodingProjectResolver resolver,
        AliCodingProjectTracker projectTracker,
        string auditPath)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _projectTracker = projectTracker ?? throw new ArgumentNullException(nameof(projectTracker));
        _intelligence = new AliRoslynProjectIntelligence(resolver);
        var workspaceLoader = new AliRoslynWorkspaceLoader(resolver);
        _solutionIntelligence = new AliRoslynSolutionIntelligence(workspaceLoader);
        _documentIntelligence = new AliRoslynDocumentIntelligence(workspaceLoader);
        _refactoring = new AliRoslynRefactoringService(workspaceLoader);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditPath);
        _auditPath = Path.GetFullPath(auditPath);
    }

    public Task<RoslynAnalysisResult> AnalyzeAsync(string projectPath, CancellationToken cancellationToken)
    {
        // Register lazily so machines without an SDK can still run every non-coding Ali capability.
        // This call must occur before the CLR resolves MSBuildWorkspace implementation types.
        AliMsBuildRuntime.EnsureRegistered();
        return _intelligence.AnalyzeAsync(projectPath, cancellationToken);
    }

    public async Task<RoslynFormatResult> FormatAsync(string projectPath, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            AliMsBuildRuntime.EnsureRegistered();
            var result = await _intelligence.FormatAsync(projectPath, cancellationToken).ConfigureAwait(false);
            started.Stop();
            await WriteAuditAsync(
                    "format",
                    projectPath,
                    result.Success,
                    null,
                    started.ElapsedMilliseconds,
                    result.Summary,
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            started.Stop();
            await WriteAuditAsync("format", projectPath, false, null, started.ElapsedMilliseconds, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return new RoslynFormatResult(false, projectPath, $"Roslyn could not format the project: {ex.Message}", [], []);
        }
    }

    public Task<RoslynSymbolResult> FindSymbolAsync(
        string projectPath,
        string query,
        CancellationToken cancellationToken) =>
        PrepareRoslyn().FindSymbolAsync(projectPath, query, cancellationToken);

    public Task<RoslynCompletionResult> GetCompletionsAsync(
        string projectPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken) =>
        PrepareRoslyn().GetCompletionsAsync(projectPath, documentPath, line, column, cancellationToken);

    public Task<RoslynSolutionOverviewResult> InspectSolutionAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        AliMsBuildRuntime.EnsureRegistered();
        return _solutionIntelligence.InspectAsync(targetPath, cancellationToken);
    }

    public Task<RoslynReferenceResult> FindReferencesAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        AliMsBuildRuntime.EnsureRegistered();
        return _solutionIntelligence.FindReferencesAsync(targetPath, documentPath, line, column, cancellationToken);
    }

    public Task<RoslynDocumentResult> InspectDocumentAsync(
        string targetPath,
        string documentPath,
        CancellationToken cancellationToken)
    {
        AliMsBuildRuntime.EnsureRegistered();
        return _documentIntelligence.InspectDocumentAsync(targetPath, documentPath, cancellationToken);
    }

    public Task<RoslynPositionResult> InspectPositionAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        AliMsBuildRuntime.EnsureRegistered();
        return _documentIntelligence.InspectPositionAsync(targetPath, documentPath, line, column, cancellationToken);
    }

    public Task<RoslynRenameResult> PreviewRenameAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        string newName,
        CancellationToken cancellationToken)
    {
        AliMsBuildRuntime.EnsureRegistered();
        return _refactoring.PreviewRenameAsync(targetPath, documentPath, line, column, newName, cancellationToken);
    }

    public async Task<RoslynRenameResult> ApplyRenameAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        string newName,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        AliMsBuildRuntime.EnsureRegistered();
        var result = await _refactoring.ApplyRenameAsync(
            targetPath,
            documentPath,
            line,
            column,
            newName,
            cancellationToken).ConfigureAwait(false);
        started.Stop();
        await WriteAuditAsync(
            "rename",
            targetPath,
            result.Success,
            null,
            started.ElapsedMilliseconds,
            result.Summary,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<DotNetBuildResult> BuildAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var target = _resolver.ResolveExistingTarget(projectPath);
        var normalizedConfiguration = NormalizeConfiguration(configuration);
        if (!target.IsSolution)
        {
            var implementation = _projectTracker.CheckImplementationChanges(target.PhysicalPath);
            if (!implementation.HasImplementationChanges)
            {
                await WriteAuditAsync("build", projectPath, false, null, 0, implementation.Detail, cancellationToken)
                    .ConfigureAwait(false);
                return new DotNetBuildResult(false, projectPath, normalizedConfiguration, null, implementation.Detail,
                    "No requested implementation changes were detected after project scaffolding.", null, 0);
            }
        }

        var started = Stopwatch.StartNew();
        if (!target.IsSolution)
        {
            var existingArtifact = FindBuiltArtifact(target.PhysicalPath, normalizedConfiguration);
            var runningTarget = FindRunningTarget(target.PhysicalPath, existingArtifact);
            if (runningTarget is not null)
            {
                var detail = $"Build not started: process {runningTarget.ProcessId} is running the target artifact '{runningTarget.ArtifactPath}' and can lock the build output. Call dotnet_stop_project after user approval, then build again.";
                await WriteAuditAsync("build", projectPath, false, null, 0, detail, cancellationToken)
                    .ConfigureAwait(false);
                return new DotNetBuildResult(
                    false,
                    projectPath,
                    normalizedConfiguration,
                    null,
                    "The project target is still running, so MSBuild was not started.",
                    detail,
                    runningTarget.ArtifactPath,
                    0,
                    WarningCount: 0,
                    ErrorCount: 0,
                    FailureKind: "RunningTarget",
                    BlockingProcessId: runningTarget.ProcessId);
            }
        }

        MsBuildExecutionResult execution;
        try
        {
            AliMsBuildRuntime.EnsureRegistered();
            execution = await AliMsBuildProjectExecutor.BuildAsync(
                    target.PhysicalPath,
                    normalizedConfiguration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            started.Stop();
            await WriteAuditAsync("build", projectPath, false, null, started.ElapsedMilliseconds, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetBuildResult(
                false,
                projectPath,
                normalizedConfiguration,
                null,
                "The Roslyn/MSBuild build could not be started.",
                CompactOutput(ex.Message),
                null,
                started.ElapsedMilliseconds);
        }

        started.Stop();
        var diagnosticLogPath = await WriteBuildLogAsync(
                target.PhysicalPath,
                normalizedConfiguration,
                execution,
                cancellationToken)
            .ConfigureAwait(false);
        var artifact = execution.Success && !target.IsSolution
            ? FindBuiltArtifact(target.PhysicalPath, normalizedConfiguration)
            : null;
        var diagnosticCounts = CountBuildDiagnostics(execution.Output);
        var outputLocked = IsOutputLockFailure(execution.Output);
        var blockingTarget = outputLocked && !target.IsSolution
            ? FindRunningTarget(
                target.PhysicalPath,
                FindBuiltArtifact(target.PhysicalPath, normalizedConfiguration))
            : null;
        await WriteAuditAsync(
                "build",
                projectPath,
                execution.Success,
                execution.ExitCode,
                started.ElapsedMilliseconds,
                execution.Success ? "Roslyn/MSBuild build completed." : "Roslyn/MSBuild returned project errors.",
                cancellationToken)
            .ConfigureAwait(false);
        return new DotNetBuildResult(
            execution.Success,
            projectPath,
            normalizedConfiguration,
            execution.ExitCode,
            execution.Success
                ? target.IsSolution
                    ? $"Roslyn/MSBuild successfully restored and compiled the approved solution with {diagnosticCounts.WarningCount} warning(s)."
                    : artifact is null
                    ? $"Roslyn/MSBuild succeeded with {diagnosticCounts.WarningCount} warning(s), but no launchable artifact was found under the project's bin folder."
                    : $"Roslyn/MSBuild succeeded with {diagnosticCounts.WarningCount} warning(s) and produced a launchable artifact."
                : outputLocked
                    ? "Roslyn/MSBuild could not replace the running target artifact. The build tool is available; close the identified target application with dotnet_stop_project after approval, then build again."
                    : $"Roslyn/MSBuild failed with {diagnosticCounts.ErrorCount} error(s) and {diagnosticCounts.WarningCount} warning(s). Review the diagnostics, correct the files, and build again.",
            BuildModelOutput(execution.Success, execution.ToolsetPath, execution.Output),
            artifact ?? blockingTarget?.ArtifactPath,
            started.ElapsedMilliseconds,
            diagnosticLogPath,
            diagnosticCounts.WarningCount,
            diagnosticCounts.ErrorCount,
            outputLocked ? "OutputLocked" : null,
            blockingTarget?.ProcessId);
    }

    public async Task<DotNetRunResult> RunAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _resolver.ResolveExistingProject(projectPath);
        var implementation = _projectTracker.CheckImplementationChanges(project.PhysicalPath);
        if (!implementation.HasImplementationChanges)
        {
            await WriteAuditAsync("run", projectPath, false, null, 0, implementation.Detail, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(false, projectPath, implementation.Detail, null, null);
        }

        var normalizedConfiguration = NormalizeConfiguration(configuration);
        var artifact = FindBuiltArtifact(project.PhysicalPath, normalizedConfiguration);
        if (artifact is null)
        {
            await WriteAuditAsync("run", projectPath, false, null, 0, "No built artifact was found.", cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(
                false,
                projectPath,
                "No built artifact was found. Build the project successfully before trying to run it.",
                null,
                null);
        }

        AliCodingProjectResolver.RejectReparsePoints(project.MountRoot, artifact);
        var runtimeFailure = ValidateRequiredRuntime(artifact);
        if (runtimeFailure is not null)
        {
            await WriteAuditAsync("run", projectPath, false, null, 0, runtimeFailure, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(false, projectPath, runtimeFailure, artifact, null);
        }

        try
        {
            var started = Stopwatch.StartNew();
            var startInfo = CreateLaunchStartInfo(artifact);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the compiled application.");
            var processId = process.Id;
            _runningProjects[project.PhysicalPath] = new TrackedProjectProcess(
                processId,
                artifact,
                process.StartTime.ToUniversalTime().Ticks);
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            var exited = process.HasExited;
            var exitCode = exited ? process.ExitCode : (int?)null;
            if (exited && exitCode != 0)
            {
                _runningProjects.TryRemove(project.PhysicalPath, out _);
                var failure = $"The compiled application exited during startup with code {exitCode}.";
                await WriteAuditAsync("run", projectPath, false, exitCode, started.ElapsedMilliseconds, failure, cancellationToken)
                    .ConfigureAwait(false);
                return new DotNetRunResult(false, projectPath, failure, artifact, processId);
            }

            var summary = exited
                ? "The compiled application ran and exited successfully during the startup check."
                : "The compiled application was launched and remained running through the startup check.";
            await WriteAuditAsync("run", projectPath, true, exitCode, started.ElapsedMilliseconds, $"{summary} Process {processId}.", cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(true, projectPath, summary, artifact, processId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            await WriteAuditAsync("run", projectPath, false, null, 0, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(
                false,
                projectPath,
                $"The compiled application could not be launched: {ex.Message}",
                artifact,
                null);
        }
    }

    public async Task<DotNetStopProjectResult> StopProjectAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _resolver.ResolveExistingProject(projectPath);
        var normalizedConfiguration = NormalizeConfiguration(configuration);
        var artifact = FindBuiltArtifact(project.PhysicalPath, normalizedConfiguration);
        var runningTarget = FindRunningTarget(project.PhysicalPath, artifact);
        if (runningTarget is null)
        {
            var summary = "No running target application was found for the approved project; it is already safe to rebuild.";
            await WriteAuditAsync("stop-project", projectPath, true, null, 0, summary, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetStopProjectResult(true, projectPath, summary, artifact, null, false);
        }

        var started = Stopwatch.StartNew();
        var forced = false;
        try
        {
            using var process = Process.GetProcessById(runningTarget.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != runningTarget.StartTimeUtcTicks)
            {
                _runningProjects.TryRemove(project.PhysicalPath, out _);
                var reusedSummary = "The previously identified target process had already exited. Its old process ID now belongs to a different process, which was left untouched. The project can be rebuilt.";
                await WriteAuditAsync("stop-project", projectPath, true, 0, started.ElapsedMilliseconds, reusedSummary, cancellationToken)
                    .ConfigureAwait(false);
                return new DotNetStopProjectResult(
                    true,
                    projectPath,
                    reusedSummary,
                    runningTarget.ArtifactPath,
                    runningTarget.ProcessId,
                    false);
            }

            if (!process.HasExited)
            {
                var closeRequested = process.CloseMainWindow();
                if (closeRequested)
                {
                    using var gracefulClose = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gracefulClose.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(gracefulClose.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The approved termination may fall back to ending the process tree.
                    }
                }

                if (!process.HasExited)
                {
                    forced = true;
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _runningProjects.TryRemove(project.PhysicalPath, out _);
            started.Stop();
            var summary = forced
                ? $"Stopped running target process {runningTarget.ProcessId} after it did not close gracefully. The project can be rebuilt."
                : $"Closed running target process {runningTarget.ProcessId}. The project can be rebuilt.";
            await WriteAuditAsync("stop-project", projectPath, true, 0, started.ElapsedMilliseconds, summary, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetStopProjectResult(
                true,
                projectPath,
                summary,
                runningTarget.ArtifactPath,
                runningTarget.ProcessId,
                forced);
        }
        catch (ArgumentException)
        {
            _runningProjects.TryRemove(project.PhysicalPath, out _);
            var summary = "The previously identified target process had already exited. The project can be rebuilt.";
            await WriteAuditAsync("stop-project", projectPath, true, 0, started.ElapsedMilliseconds, summary, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetStopProjectResult(
                true,
                projectPath,
                summary,
                runningTarget.ArtifactPath,
                runningTarget.ProcessId,
                false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            started.Stop();
            var summary = $"The running target process could not be stopped: {ex.Message}";
            await WriteAuditAsync("stop-project", projectPath, false, null, started.ElapsedMilliseconds, summary, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetStopProjectResult(
                false,
                projectPath,
                summary,
                runningTarget.ArtifactPath,
                runningTarget.ProcessId,
                forced);
        }
    }

    private static string NormalizeConfiguration(string? configuration) =>
        string.IsNullOrWhiteSpace(configuration)
            ? "Debug"
            : configuration.Trim() switch
            {
                var value when value.Equals("Debug", StringComparison.OrdinalIgnoreCase) => "Debug",
                var value when value.Equals("Release", StringComparison.OrdinalIgnoreCase) => "Release",
                _ => throw new ArgumentException("Configuration must be Debug or Release.", nameof(configuration))
            };

    private TrackedProjectProcess? FindRunningTarget(string physicalProjectPath, string? artifactPath)
    {
        if (_runningProjects.TryGetValue(physicalProjectPath, out var tracked))
        {
            if (IsTrackedProcessRunning(tracked))
            {
                return tracked;
            }

            _runningProjects.TryRemove(physicalProjectPath, out _);
        }

        if (string.IsNullOrWhiteSpace(artifactPath)
            || !Path.GetExtension(artifactPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var expected = Path.GetFullPath(artifactPath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable)
                        && Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase)
                        && !process.HasExited)
                    {
                        var discovered = new TrackedProjectProcess(
                            process.Id,
                            expected,
                            process.StartTime.ToUniversalTime().Ticks);
                        _runningProjects[physicalProjectPath] = discovered;
                        return discovered;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // A process that cannot be inspected is not attributed to this approved project.
                }
            }
        }

        return null;
    }

    private static bool IsTrackedProcessRunning(TrackedProjectProcess tracked)
    {
        try
        {
            using var process = Process.GetProcessById(tracked.ProcessId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime().Ticks == tracked.StartTimeUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsOutputLockFailure(string output) =>
        output.Contains("MSB3021", StringComparison.OrdinalIgnoreCase)
        || output.Contains("MSB3027", StringComparison.OrdinalIgnoreCase);

    private sealed record TrackedProjectProcess(
        int ProcessId,
        string ArtifactPath,
        long StartTimeUtcTicks);

    internal static string? FindBuiltArtifact(string projectPath, string configuration)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var outputRoot = Path.Combine(projectDirectory, "bin");
        if (!Directory.Exists(outputRoot))
        {
            return null;
        }

        var assemblyName = ReadAssemblyName(projectPath) ?? Path.GetFileNameWithoutExtension(projectPath);
        var executable = Directory.EnumerateFiles(outputRoot, assemblyName + ".exe", SearchOption.AllDirectories)
            .Where(path => HasConfigurationSegment(outputRoot, path, configuration))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return executable ?? Directory.EnumerateFiles(outputRoot, assemblyName + ".dll", SearchOption.AllDirectories)
            .Where(path => HasConfigurationSegment(outputRoot, path, configuration))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool HasConfigurationSegment(string outputRoot, string path, string configuration) =>
        Path.GetRelativePath(outputRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(configuration, StringComparison.OrdinalIgnoreCase));

    private static string? ReadAssemblyName(string projectPath)
    {
        try
        {
            return XDocument.Load(projectPath)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?
                .Value
                .Trim();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static ProcessStartInfo CreateLaunchStartInfo(string artifact)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(artifact)!,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        if (Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = ResolveDotNetHost();
            startInfo.ArgumentList.Add(artifact);
        }
        else
        {
            startInfo.FileName = artifact;
        }

        return startInfo;
    }

    private static string? ValidateRequiredRuntime(string artifact)
    {
        var runtimeConfigPath = Path.ChangeExtension(artifact, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
            {
                return null;
            }

            var requirements = new List<(string Name, string Version)>();
            if (runtimeOptions.TryGetProperty("framework", out var framework))
            {
                AddRuntimeRequirement(framework, requirements);
            }
            if (runtimeOptions.TryGetProperty("frameworks", out var frameworks)
                && frameworks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in frameworks.EnumerateArray())
                {
                    AddRuntimeRequirement(item, requirements);
                }
            }

            var dotnetRoots = ResolveDotNetRoots();
            foreach (var requirement in requirements.Distinct())
            {
                if (!TryParseRuntimeVersion(requirement.Version, out var requiredVersion))
                {
                    continue;
                }
                var required = requiredVersion!;

                var installed = dotnetRoots
                    .Select(root => Path.Combine(root, "shared", requirement.Name))
                    .Where(Directory.Exists)
                    .SelectMany(Directory.EnumerateDirectories)
                        .Select(Path.GetFileName)
                        .Where(version => !string.IsNullOrWhiteSpace(version))
                        .Select(version => new
                        {
                            Label = version!,
                            Parsed = TryParseRuntimeVersion(version!, out var parsed) ? parsed : null
                        })
                        .Where(item => item.Parsed is not null)
                        .DistinctBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                if (installed.Any(item =>
                        item.Parsed is { } installedVersion
                        && installedVersion.Major == required.Major
                        && installedVersion >= required))
                {
                    continue;
                }

                var available = installed.Length == 0
                    ? "none"
                    : string.Join(", ", installed.Select(item => item.Label));
                return $"The compiled application requires {requirement.Name} {requirement.Version}, but compatible installed versions are: {available}. Rebuild for an installed target framework before running; the process was not started.";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return $"Ali could not validate the application's runtime requirements before launch: {ex.Message}";
        }
    }

    private static void AddRuntimeRequirement(
        JsonElement framework,
        ICollection<(string Name, string Version)> requirements)
    {
        if (framework.ValueKind != JsonValueKind.Object
            || !framework.TryGetProperty("name", out var nameElement)
            || !framework.TryGetProperty("version", out var versionElement))
        {
            return;
        }

        var name = nameElement.GetString();
        var version = versionElement.GetString();
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
        {
            requirements.Add((name, version));
        }
    }

    private static IReadOnlyList<string> ResolveDotNetRoots()
    {
        var runtimeDirectory = new DirectoryInfo(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
            runtimeDirectory.Parent?.Parent?.Parent?.FullName
        };
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Where(path => Directory.Exists(Path.Combine(path, "shared")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseRuntimeVersion(string value, out Version? version)
    {
        var stablePrefix = value.Split('-', 2)[0];
        return Version.TryParse(stablePrefix, out version);
    }

    private static string ResolveDotNetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : "dotnet";
    }

    private AliRoslynProjectIntelligence PrepareRoslyn()
    {
        AliMsBuildRuntime.EnsureRegistered();
        return _intelligence;
    }

    private async Task WriteAuditAsync(
        string operation,
        string projectPath,
        bool success,
        int? exitCode,
        long durationMilliseconds,
        string detail,
        CancellationToken cancellationToken)
    {
        var entry = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            engine = "Roslyn/MSBuild",
            operation,
            projectPath,
            success,
            exitCode,
            durationMilliseconds,
            detail
        }, AuditJsonOptions);
        await _auditLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);
            await File.AppendAllTextAsync(_auditPath, entry + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _auditLock.Release();
        }
    }

    internal static string BuildModelOutput(bool success, string toolsetPath, string output)
    {
        var normalized = output.ReplaceLineEndings(Environment.NewLine).Trim();
        var diagnosticCounts = CountBuildDiagnostics(normalized);
        var diagnosticLines = normalized
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                || line.Contains(": warning ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error CS", StringComparison.OrdinalIgnoreCase)
                || line.Contains("warning CS", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error MSB", StringComparison.OrdinalIgnoreCase)
                || line.Contains("warning MSB", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
                || line.EndsWith("Warning(s)", StringComparison.OrdinalIgnoreCase)
                || line.EndsWith("Error(s)", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var summary = new List<string>
        {
            success
                ? $"Build succeeded with {diagnosticCounts.WarningCount} warning(s)."
                : $"Build failed with {diagnosticCounts.ErrorCount} error(s) and {diagnosticCounts.WarningCount} warning(s).",
            $"MSBuild toolset: {toolsetPath}"
        };
        summary.AddRange(diagnosticLines);
        if (diagnosticLines.Count == 0 && !success && normalized.Length > 0)
        {
            summary.Add("MSBuild output tail:");
            summary.Add(normalized.Length <= 2_500 ? normalized : normalized[^2_500..]);
        }
        else if (diagnosticLines.Count == 0)
        {
            summary.Add(success
                ? "No compiler warnings or errors were reported."
                : "No structured compiler diagnostic was captured; inspect the retained full build log.");
        }
        summary.Add("The complete MSBuild transcript was retained in Ali's local coding audit folder.");
        return CompactOutput(string.Join(Environment.NewLine, summary));
    }

    private static (int WarningCount, int ErrorCount) CountBuildDiagnostics(string output)
    {
        var lines = output.ReplaceLineEndings(Environment.NewLine)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var warningCount = lines.Count(line =>
            line.Contains(": warning ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("warning CS", StringComparison.OrdinalIgnoreCase)
            || line.Contains("warning MSB", StringComparison.OrdinalIgnoreCase));
        var errorCount = lines.Count(line =>
            line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error CS", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error MSB", StringComparison.OrdinalIgnoreCase));
        return (warningCount, errorCount);
    }

    private async Task<string?> WriteBuildLogAsync(
        string targetPath,
        string configuration,
        MsBuildExecutionResult execution,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(_auditPath)!, "BuildLogs");
            Directory.CreateDirectory(directory);
            var projectName = Path.GetFileNameWithoutExtension(targetPath);
            var safeName = string.Concat(projectName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var path = Path.Combine(
                directory,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeName}-{configuration}.log");
            var transcript = string.Join(
                Environment.NewLine,
                $"Target: {targetPath}",
                $"Configuration: {configuration}",
                $"MSBuild toolset: {execution.ToolsetPath}",
                $"Exit code: {execution.ExitCode}",
                string.Empty,
                execution.Output);
            await File.WriteAllTextAsync(path, transcript, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CompactOutput(string output)
    {
        var normalized = output.ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= MaximumOutputCharacters
            ? normalized
            : "... earlier Roslyn/MSBuild output omitted ..." + Environment.NewLine + normalized[^MaximumOutputCharacters..];
    }
}
