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
    long DurationMilliseconds);

public sealed record DotNetRunResult(
    bool Success,
    string ProjectPath,
    string Summary,
    string? ArtifactPath,
    int? ProcessId);

/// <summary>
/// Coordinates Roslyn project intelligence, Microsoft's in-process MSBuild API, and
/// bounded launch operations for projects inside approved workstation mounts.
/// </summary>
internal sealed class AliRoslynCodingTools
{
    private const int MaximumOutputCharacters = 24_000;
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AliCodingProjectResolver _resolver;
    private readonly AliCodingProjectTracker _projectTracker;
    private readonly AliRoslynProjectIntelligence _intelligence;
    private readonly AliRoslynSolutionIntelligence _solutionIntelligence;
    private readonly AliRoslynDocumentIntelligence _documentIntelligence;
    private readonly AliRoslynRefactoringService _refactoring;
    private readonly string _auditPath;
    private readonly SemaphoreSlim _auditLock = new(1, 1);

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
        var artifact = execution.Success && !target.IsSolution
            ? FindBuiltArtifact(target.PhysicalPath, normalizedConfiguration)
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
                    ? "Roslyn/MSBuild successfully restored and compiled the approved solution."
                    : artifact is null
                    ? "Roslyn/MSBuild succeeded, but no launchable artifact was found under the project's bin folder."
                    : "Roslyn/MSBuild succeeded and produced a launchable artifact."
                : "Roslyn/MSBuild failed. Review the diagnostics, correct the files, and build again.",
            CompactOutput($"MSBuild toolset: {execution.ToolsetPath}{Environment.NewLine}{execution.Output}"),
            artifact,
            started.ElapsedMilliseconds);
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
        try
        {
            var startInfo = CreateLaunchStartInfo(artifact);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the compiled application.");
            var processId = process.Id;
            process.Dispose();
            await WriteAuditAsync("run", projectPath, true, 0, 0, $"Started process {processId}.", cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(true, projectPath, "The compiled application was launched.", artifact, processId);
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

    private static string NormalizeConfiguration(string? configuration) =>
        string.IsNullOrWhiteSpace(configuration)
            ? "Debug"
            : configuration.Trim() switch
            {
                var value when value.Equals("Debug", StringComparison.OrdinalIgnoreCase) => "Debug",
                var value when value.Equals("Release", StringComparison.OrdinalIgnoreCase) => "Release",
                _ => throw new ArgumentException("Configuration must be Debug or Release.", nameof(configuration))
            };

    internal static string? FindBuiltArtifact(string projectPath, string configuration)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var outputRoot = Path.Combine(projectDirectory, "bin", configuration);
        if (!Directory.Exists(outputRoot))
        {
            return null;
        }

        var assemblyName = ReadAssemblyName(projectPath) ?? Path.GetFileNameWithoutExtension(projectPath);
        var executable = Directory.EnumerateFiles(outputRoot, assemblyName + ".exe", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return executable ?? Directory.EnumerateFiles(outputRoot, assemblyName + ".dll", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

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

    private static string CompactOutput(string output)
    {
        var normalized = output.ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= MaximumOutputCharacters
            ? normalized
            : "... earlier Roslyn/MSBuild output omitted ..." + Environment.NewLine + normalized[^MaximumOutputCharacters..];
    }
}
