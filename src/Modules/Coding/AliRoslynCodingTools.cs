using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coordinator;

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
    private readonly Action? _beforeRunProcessStart;
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TrackedProjectProcess> _runningProjects =
        new(StringComparer.OrdinalIgnoreCase);

    public AliRoslynCodingTools(
        AliCodingProjectResolver resolver,
        AliCodingProjectTracker projectTracker,
        string auditPath,
        Action? beforeRunProcessStart = null)
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
        _beforeRunProcessStart = beforeRunProcessStart;
    }

    internal AliDotNetRunExecutionBinding CaptureRunExecutionBinding(
        string physicalProjectPath,
        string? configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalProjectPath);
        var projectPath = Path.GetFullPath(physicalProjectPath);
        var normalizedConfiguration = NormalizeConfiguration(configuration);
        var artifactPath = FindBuiltArtifact(projectPath, normalizedConfiguration);
        if (artifactPath is null)
        {
            return new AliDotNetRunExecutionBinding(null, null, null);
        }
        var projectRoot = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException(
                "The exact .NET run project has no parent directory.");
        AliCodingProjectResolver.RejectReparsePoints(projectRoot, artifactPath);
        var artifact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            artifactPath,
            "The selected built .NET artifact");
        var host = Path.GetExtension(artifact.PhysicalPath).Equals(
                ".dll",
                StringComparison.OrdinalIgnoreCase)
            ? AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                ResolveExactDotNetHostPath(),
                "The selected .NET runtime host")
            : artifact;
        var launchClosure = AliApplicationLaunchClosure.Capture(artifact);
        return new AliDotNetRunExecutionBinding(artifact, host, launchClosure);
    }

    internal AliDotNetStopExecutionBinding CaptureStopExecutionBinding(
        string physicalProjectPath,
        string? configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalProjectPath);
        var projectPath = Path.GetFullPath(physicalProjectPath);
        var normalizedConfiguration = NormalizeConfiguration(configuration);
        var artifactPath = FindBuiltArtifact(projectPath, normalizedConfiguration);
        AliBoundExecutionFile? artifact = null;
        if (artifactPath is not null)
        {
            var projectRoot = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidDataException(
                    "The exact .NET stop project has no parent directory.");
            AliCodingProjectResolver.RejectReparsePoints(projectRoot, artifactPath);
            artifact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                artifactPath,
                "The selected .NET stop artifact");
        }
        var running = FindRunningTarget(projectPath, artifact?.PhysicalPath);
        return new AliDotNetStopExecutionBinding(
            artifact,
            running is null ? null : CaptureProcessState(running));
    }

    public Task<RoslynAnalysisResult> AnalyzeAsync(string projectPath, CancellationToken cancellationToken)
    {
        // Register lazily so machines without an SDK can still run every non-coding Ali capability.
        // This call must occur before the CLR resolves MSBuildWorkspace implementation types.
        AliMsBuildRuntime.EnsureRegistered();
        return _intelligence.AnalyzeAsync(projectPath, cancellationToken);
    }

    internal Task<RoslynAnalysisResult> AnalyzeCompilerOnlyAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        AliMsBuildRuntime.EnsureRegistered();
        return _intelligence.AnalyzeCompilerOnlyAsync(projectPath, cancellationToken);
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

    internal Task<RoslynAnalysisResult> AnalyzeLoadedAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        CancellationToken cancellationToken) =>
        _intelligence.AnalyzeAsync(session, projectPath, cancellationToken);

    internal Task<RoslynSymbolResult> FindSymbolLoadedAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        string query,
        CancellationToken cancellationToken) =>
        _intelligence.FindSymbolAsync(session, projectPath, query, cancellationToken);

    internal Task<RoslynCompletionResult> GetCompletionsLoadedAsync(
        AliRoslynWorkspaceSession session,
        string projectPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken) =>
        _intelligence.GetCompletionsAsync(
            session,
            projectPath,
            documentPath,
            line,
            column,
            cancellationToken);

    internal RoslynSolutionOverviewResult InspectSolutionLoaded(
        AliRoslynWorkspaceSession session,
        string targetPath) =>
        _solutionIntelligence.Inspect(session, targetPath);

    internal Task<RoslynReferenceResult> FindReferencesLoadedAsync(
        AliRoslynWorkspaceSession session,
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken) =>
        _solutionIntelligence.FindReferencesAsync(
            session,
            targetPath,
            documentPath,
            line,
            column,
            cancellationToken);

    internal Task<RoslynDocumentResult> InspectDocumentLoadedAsync(
        AliRoslynWorkspaceSession session,
        string targetPath,
        string documentPath,
        CancellationToken cancellationToken) =>
        _documentIntelligence.InspectDocumentAsync(
            session,
            targetPath,
            documentPath,
            cancellationToken);

    internal Task<RoslynPositionResult> InspectPositionLoadedAsync(
        AliRoslynWorkspaceSession session,
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken) =>
        _documentIntelligence.InspectPositionAsync(
            session,
            targetPath,
            documentPath,
            line,
            column,
            cancellationToken);

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
        var approved = AliCodingInvocationExecutionContext.Current?.RuntimeBinding.DotNetRun;
        var liveBinding = CaptureRunExecutionBinding(
            project.PhysicalPath,
            normalizedConfiguration);
        if (approved is not null && approved != liveBinding)
        {
            throw new InvalidOperationException(
                "The exact built artifact or runtime host changed after durable authorization.");
        }
        var executionBinding = approved ?? liveBinding;
        var artifact = executionBinding.Artifact?.PhysicalPath;
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

        AliApplicationLaunchLease? launchLease = null;
        AliExecutionFileLease? hostLease = null;
        TrackedProjectProcess? pendingTracked = null;
        try
        {
            var started = Stopwatch.StartNew();
            var boundArtifact = executionBinding.Artifact
                ?? throw new InvalidDataException(
                    "The exact .NET run binding has no application artifact.");
            var boundHost = executionBinding.HostExecutable
                ?? throw new InvalidDataException(
                    "The exact .NET run binding has no host executable.");
            var boundClosure = executionBinding.LaunchClosure
                ?? throw new InvalidDataException(
                    "The exact .NET run binding has no application launch closure.");
            launchLease = AliApplicationLaunchLease.Acquire(
                boundArtifact,
                boundClosure);
            hostLease = AliExecutionFileLease.Acquire(
                boundHost,
                "The exact .NET run host executable");
            var projectDirectoryBinding = AliExecutionDirectoryBinding.Capture(
                project.ProjectDirectory,
                "The exact .NET run project-directory spine");
            using var projectDirectoryLease = projectDirectoryBinding.Acquire(
                "The exact .NET run project-directory spine");
            RequireRunExecutionBindingStable(executionBinding);
            launchLease.RequireStable();
            hostLease.RequireStable();
            projectDirectoryLease.RequireStable();
            _beforeRunProcessStart?.Invoke();
            RequireRunExecutionBindingStable(executionBinding);
            launchLease.RequireStable();
            hostLease.RequireStable();
            projectDirectoryLease.RequireStable();
            var startInfo = CreateLaunchStartInfo(
                artifact,
                boundHost.PhysicalPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the compiled application.");
            var processId = process.Id;
            var trackedRegistered = false;
            try
            {
                RequireStartedRunProcess(
                    process,
                    boundArtifact,
                    launchLease,
                    hostLease);
                pendingTracked = await CaptureStartedProcessAsync(
                        process,
                        executionBinding,
                        launchLease,
                        hostLease,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (pendingTracked is not null)
                {
                    launchLease = null;
                    hostLease = null;
                    RegisterTrackedProcess(project.PhysicalPath, pendingTracked);
                    pendingTracked = null;
                    trackedRegistered = true;
                }
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                throw;
            }
            if (trackedRegistered)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            }
            var exited = process.HasExited;
            var exitCode = exited ? process.ExitCode : (int?)null;
            if (exited)
            {
                RemoveTrackedProcess(project.PhysicalPath, processId);
            }
            if (exited && exitCode != 0)
            {
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
        finally
        {
            pendingTracked?.Dispose();
            hostLease?.Dispose();
            launchLease?.Dispose();
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
        var approved = AliCodingInvocationExecutionContext.Current?.RuntimeBinding.DotNetStop;
        var liveBinding = CaptureStopExecutionBinding(
            project.PhysicalPath,
            normalizedConfiguration);
        if (approved is not null && approved != liveBinding)
        {
            throw new InvalidOperationException(
                "The exact tracked process state changed after durable authorization.");
        }
        var executionBinding = approved ?? liveBinding;
        var artifact = executionBinding.Artifact?.PhysicalPath;
        var runningTarget = executionBinding.Process;
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
            RequireProcessStateStable(process, runningTarget);

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
                    RequireProcessStateStable(process, runningTarget);
                    forced = true;
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            RemoveTrackedProcess(project.PhysicalPath, runningTarget.ProcessId);
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
                artifact,
                runningTarget.ProcessId,
                forced);
        }
        catch (ArgumentException)
        {
            RemoveTrackedProcess(project.PhysicalPath, runningTarget.ProcessId);
            if (approved?.Process is not null)
            {
                var missing = "The exact authorized target process disappeared before it could be stopped; no replacement process was touched.";
                await WriteAuditAsync("stop-project", projectPath, false, null, started.ElapsedMilliseconds, missing, cancellationToken)
                    .ConfigureAwait(false);
                return new DotNetStopProjectResult(
                    false,
                    projectPath,
                    missing,
                    artifact,
                    runningTarget.ProcessId,
                    false);
            }
            var summary = "The previously identified target process had already exited. The project can be rebuilt.";
            await WriteAuditAsync("stop-project", projectPath, true, 0, started.ElapsedMilliseconds, summary, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetStopProjectResult(
                true,
                projectPath,
                summary,
                artifact,
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
                artifact,
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
                return string.IsNullOrWhiteSpace(artifactPath)
                       || Path.GetFullPath(artifactPath).Equals(
                           tracked.ArtifactPath,
                           StringComparison.OrdinalIgnoreCase)
                    ? tracked
                    : null;
            }

            RemoveTrackedProcess(physicalProjectPath, tracked.ProcessId);
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
                        var artifact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                            expected,
                            "The discovered .NET target artifact");
                        var closure = AliApplicationLaunchClosure.Capture(artifact);
                        AliApplicationLaunchLease? launchLease = null;
                        AliExecutionFileLease? hostLease = null;
                        TrackedProjectProcess? discovered = null;
                        try
                        {
                            launchLease = AliApplicationLaunchLease.Acquire(
                                artifact,
                                closure);
                            hostLease = AliExecutionFileLease.Acquire(
                                artifact,
                                "The discovered .NET target executable");
                            hostLease.RequireStartedProcessImage(process);
                            launchLease.RequireStartedPrincipalProcessImage(process);
                            discovered = new TrackedProjectProcess(
                                process.Id,
                                artifact.PhysicalPath,
                                artifact.Identity,
                                process.StartTime.ToUniversalTime().Ticks,
                                artifact.PhysicalPath,
                                artifact.Identity,
                                launchLease,
                                hostLease);
                            launchLease = null;
                            hostLease = null;
                            RegisterTrackedProcess(physicalProjectPath, discovered);
                            return _runningProjects.TryGetValue(
                                       physicalProjectPath,
                                       out var current)
                                   && ReferenceEquals(current, discovered)
                                ? discovered
                                : null;
                        }
                        finally
                        {
                            hostLease?.Dispose();
                            launchLease?.Dispose();
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException
                                               or IOException
                                               or InvalidOperationException
                                               or System.ComponentModel.Win32Exception
                                               or NotSupportedException
                                               or UnauthorizedAccessException)
                {
                    // A recovered process that cannot reacquire the exact host and full output
                    // closure leases is not attributed to this approved project.
                }
            }
        }

        return null;
    }

    private void RegisterTrackedProcess(
        string physicalProjectPath,
        TrackedProjectProcess tracked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalProjectPath);
        ArgumentNullException.ThrowIfNull(tracked);
        try
        {
            while (true)
            {
                if (_runningProjects.TryGetValue(physicalProjectPath, out var prior))
                {
                    if (IsTrackedProcessRunning(prior))
                    {
                        throw new InvalidOperationException(
                            "An exact application process is already running for the selected project.");
                    }
                    RemoveTrackedProcess(physicalProjectPath, prior);
                    continue;
                }
                if (_runningProjects.TryAdd(physicalProjectPath, tracked))
                {
                    break;
                }
            }

            tracked.StartMonitoring(
                exited => RemoveTrackedProcess(physicalProjectPath, exited));
        }
        catch
        {
            RemoveTrackedProcess(physicalProjectPath, tracked);
            tracked.Dispose();
            throw;
        }
    }

    private void RemoveTrackedProcess(string physicalProjectPath, int processId)
    {
        if (_runningProjects.TryGetValue(physicalProjectPath, out var tracked)
            && tracked.ProcessId == processId)
        {
            RemoveTrackedProcess(physicalProjectPath, tracked);
        }
    }

    private void RemoveTrackedProcess(
        string physicalProjectPath,
        TrackedProjectProcess tracked)
    {
        var removed = ((ICollection<KeyValuePair<string, TrackedProjectProcess>>)
                _runningProjects)
            .Remove(new KeyValuePair<string, TrackedProjectProcess>(
                physicalProjectPath,
                tracked));
        if (removed)
        {
            tracked.Dispose();
        }
    }

    private static bool IsTrackedProcessRunning(TrackedProjectProcess tracked)
    {
        try
        {
            tracked.RequireStable();
            var state = CaptureProcessState(tracked);
            return state.ProcessId == tracked.ProcessId
                && state.StartTimeUtcTicks == tracked.StartTimeUtcTicks
                && string.Equals(
                    state.Executable.PhysicalPath,
                    tracked.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    state.Executable.Identity,
                    tracked.ExecutableIdentity,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<TrackedProjectProcess?> CaptureStartedProcessAsync(
        Process process,
        AliDotNetRunExecutionBinding binding,
        AliApplicationLaunchLease launchLease,
        AliExecutionFileLease hostLease,
        CancellationToken cancellationToken)
    {
        var inspectionWindow = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                // A short-lived application still launched through the exact pinned ProcessStartInfo.
                // Revalidate the complete launch closure again after exit before accepting its result.
                RequireRunExecutionBindingStable(binding);
                return null;
            }

            string? executablePath = null;
            try
            {
                process.Refresh();
                executablePath = process.MainModule?.FileName;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
            {
                if (process.HasExited)
                {
                    RequireRunExecutionBindingStable(binding);
                    return null;
                }
            }

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                try
                {
                    return CaptureStartedProcess(
                        process,
                        binding,
                        executablePath,
                        launchLease,
                        hostLease);
                }
                catch (Exception exception) when (
                    (exception is ArgumentException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                    && process.HasExited)
                {
                    RequireRunExecutionBindingStable(binding);
                    launchLease.RequireStable();
                    hostLease.RequireStable();
                    return null;
                }
            }
            if (inspectionWindow.Elapsed >= TimeSpan.FromSeconds(1))
            {
                throw new InvalidOperationException(
                    "The started .NET process executable could not be inspected within the bounded launch window.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }

    private static TrackedProjectProcess CaptureStartedProcess(
        Process process,
        AliDotNetRunExecutionBinding binding,
        string executablePath,
        AliApplicationLaunchLease launchLease,
        AliExecutionFileLease hostLease)
    {
        var artifact = binding.Artifact
            ?? throw new InvalidDataException(
                "A started .NET process has no exact artifact binding.");
        var expectedHost = binding.HostExecutable
            ?? throw new InvalidDataException(
                "A started .NET process has no exact host binding.");
        var executable = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            executablePath,
            "The started .NET process executable");
        if (!string.Equals(
                executable.PhysicalPath,
                expectedHost.PhysicalPath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                executable.Identity,
                expectedHost.Identity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The started process does not match the exact authorized host executable.");
        }
        return new TrackedProjectProcess(
            process.Id,
            artifact.PhysicalPath,
            artifact.Identity,
            process.StartTime.ToUniversalTime().Ticks,
            executable.PhysicalPath,
            executable.Identity,
            launchLease,
            hostLease);
    }

    private static AliBoundProcessState CaptureProcessState(TrackedProjectProcess tracked)
    {
        using var process = Process.GetProcessById(tracked.ProcessId);
        if (process.HasExited)
        {
            throw new InvalidOperationException(
                "The exact tracked process has already exited.");
        }
        var executablePath = process.MainModule?.FileName
            ?? throw new InvalidOperationException(
                "The exact tracked process executable could not be inspected.");
        var executable = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            executablePath,
            "The exact tracked process executable");
        var state = new AliBoundProcessState(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            executable);
        if (state.ProcessId != tracked.ProcessId
            || state.StartTimeUtcTicks != tracked.StartTimeUtcTicks
            || !string.Equals(
                state.Executable.PhysicalPath,
                tracked.ExecutablePath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                state.Executable.Identity,
                tracked.ExecutableIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exact tracked process identity is no longer stable.");
        }
        return state;
    }

    private static void RequireProcessStateStable(
        Process process,
        AliBoundProcessState expected)
        => expected.RequireStable(process);

    private static void RequireRunExecutionBindingStable(
        AliDotNetRunExecutionBinding expected)
    {
        if (expected.Artifact is null
            || expected.HostExecutable is null
            || expected.LaunchClosure is null)
        {
            throw new InvalidOperationException(
                "The exact .NET run binding has no launchable artifact, host, and output closure.");
        }
        var artifact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            expected.Artifact.PhysicalPath,
            "The exact authorized .NET run artifact");
        var host = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            expected.HostExecutable.PhysicalPath,
            "The exact authorized .NET run host");
        if (artifact != expected.Artifact || host != expected.HostExecutable)
        {
            throw new InvalidOperationException(
                "The exact .NET run artifact or host changed immediately before launch.");
        }
        _ = expected.LaunchClosure.RequireStable();
    }

    private static void RequireStartedRunProcess(
        Process process,
        AliBoundExecutionFile artifact,
        AliApplicationLaunchLease launchLease,
        AliExecutionFileLease hostLease)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(launchLease);
        ArgumentNullException.ThrowIfNull(hostLease);
        try
        {
            hostLease.RequireStartedProcessImage(process);
            if (Path.GetExtension(artifact.PhysicalPath).Equals(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                launchLease.RequireStartedPrincipalProcessImage(process);
            }
            else
            {
                launchLease.RequireStable();
            }
        }
        catch (Exception exception) when (
            (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            && process.HasExited)
        {
            // CreateProcess consumed a fixed no-follow pathname while both the host and
            // complete output closure were held no-write/no-delete. A very short-lived
            // child can retire before Windows returns its image path; the still-held
            // identities therefore remain the authoritative fail-closed proof.
            hostLease.RequireStable();
            launchLease.RequireStable();
        }
    }

    internal static bool IsOutputLockFailure(string output) =>
        output.Contains("MSB3021", StringComparison.OrdinalIgnoreCase)
        || output.Contains("MSB3027", StringComparison.OrdinalIgnoreCase);

    private sealed class TrackedProjectProcess : IDisposable
    {
        private readonly Process _monitor;
        private readonly AliApplicationLaunchLease _launchLease;
        private readonly AliExecutionFileLease _hostLease;
        private EventHandler? _exitHandler;
        private int _monitoring;
        private int _disposed;

        internal TrackedProjectProcess(
            int processId,
            string artifactPath,
            string artifactIdentity,
            long startTimeUtcTicks,
            string executablePath,
            string executableIdentity,
            AliApplicationLaunchLease launchLease,
            AliExecutionFileLease hostLease)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifactIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(executableIdentity);
            ProcessId = processId;
            ArtifactPath = Path.GetFullPath(artifactPath);
            ArtifactIdentity = artifactIdentity;
            StartTimeUtcTicks = startTimeUtcTicks;
            ExecutablePath = Path.GetFullPath(executablePath);
            ExecutableIdentity = executableIdentity;
            _launchLease = launchLease
                ?? throw new ArgumentNullException(nameof(launchLease));
            _hostLease = hostLease
                ?? throw new ArgumentNullException(nameof(hostLease));

            var monitor = Process.GetProcessById(processId);
            try
            {
                if (monitor.HasExited
                    || monitor.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks)
                {
                    throw new ArgumentException(
                        "The exact process exited or its PID was reused before tracking began.",
                        nameof(processId));
                }
                _monitor = monitor;
            }
            catch
            {
                monitor.Dispose();
                throw;
            }
        }

        internal int ProcessId { get; }

        internal string ArtifactPath { get; }

        internal string ArtifactIdentity { get; }

        internal long StartTimeUtcTicks { get; }

        internal string ExecutablePath { get; }

        internal string ExecutableIdentity { get; }

        internal void StartMonitoring(Action<TrackedProjectProcess> exited)
        {
            ArgumentNullException.ThrowIfNull(exited);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Interlocked.CompareExchange(ref _monitoring, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "The exact tracked process already has an exit monitor.");
            }

            EventHandler handler = (_, _) => exited(this);
            _exitHandler = handler;
            _monitor.Exited += handler;
            _monitor.EnableRaisingEvents = true;
            try
            {
                if (_monitor.HasExited)
                {
                    exited(this);
                }
            }
            catch (ObjectDisposedException)
            {
                // The exit callback already removed and disposed this exact tracker.
            }
        }

        internal void RequireStable()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            _monitor.Refresh();
            if (_monitor.HasExited
                || _monitor.Id != ProcessId
                || _monitor.StartTime.ToUniversalTime().Ticks != StartTimeUtcTicks)
            {
                throw new InvalidOperationException(
                    "The exact tracked process exited or its PID/start time changed.");
            }
            _hostLease.RequireStartedProcessImage(_monitor);
            _launchLease.RequireStable();
            var artifact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                ArtifactPath,
                "The exact tracked .NET artifact");
            if (!string.Equals(
                    artifact.Identity,
                    ArtifactIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The exact tracked application artifact changed while its process was running.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var handler = Interlocked.Exchange(ref _exitHandler, null);
            if (handler is not null)
            {
                try
                {
                    _monitor.Exited -= handler;
                }
                catch (InvalidOperationException)
                {
                    // The process handle exited while the exact tracker was being removed.
                }
            }
            _monitor.Dispose();
            _hostLease.Dispose();
            _launchLease.Dispose();
        }
    }

    internal static string? FindBuiltArtifact(string projectPath, string configuration)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var outputRoot = Path.Combine(projectDirectory, "bin");
        if (!Directory.Exists(outputRoot))
        {
            return null;
        }

        var assemblyName = ReadAssemblyName(projectPath) ?? Path.GetFileNameWithoutExtension(projectPath);
        var candidates = EnumerateBuildArtifactsNoFollow(outputRoot)
            .Where(path => HasConfigurationSegment(outputRoot, path, configuration))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Select(assemblyName + ".exe") ?? Select(assemblyName + ".dll");

        string? Select(string fileName) => candidates
            .Where(path => Path.GetFileName(path).Equals(
                fileName,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> EnumerateBuildArtifactsNoFollow(string outputRoot)
    {
        const int maximumEntries = 50_000;
        AliGeneratedOutputLayoutFingerprint.CaptureDirectoryLayout(
            outputRoot,
            "The .NET build output tree");
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(outputRoot));
        var entriesSeen = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var children = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                entriesSeen = checked(entriesSeen + 1);
                if (entriesSeen > maximumEntries)
                {
                    throw new InvalidDataException(
                        "The .NET build output exceeds the exact entry-count bound.");
                }
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "The .NET build output contains a reparse point or device entry.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    children.Add(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
        return files;
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

    private static ProcessStartInfo CreateLaunchStartInfo(
        string artifact,
        string hostExecutable)
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
            startInfo.FileName = hostExecutable;
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

    private static string ResolveExactDotNetHostPath()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configured)
            ? AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(configured)
            : AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
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
        if (AliCoreAssistantExecutionContext.IsActive)
        {
            return;
        }

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
