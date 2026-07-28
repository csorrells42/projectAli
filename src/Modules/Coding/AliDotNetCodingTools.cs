using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Ali.Modules.WorkstationFiles;

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
/// Provides fixed, bounded .NET build and launch operations for projects inside Ali's
/// approved workstation mounts. It deliberately does not expose a general shell.
/// Agent Framework approval is applied by the coordinator before either method runs.
/// </summary>
internal sealed class AliDotNetCodingTools
{
    private const int BuildTimeoutSeconds = 180;
    private const int MaximumOutputCharacters = 16_000;
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim DotNetBuildLock = new(1, 1);
    private readonly AliWorkstationFileAccess _fileAccess;
    private readonly string _auditPath;
    private readonly SemaphoreSlim _auditLock = new(1, 1);

    public AliDotNetCodingTools(AliWorkstationFileAccess fileAccess, string auditPath)
    {
        _fileAccess = fileAccess ?? throw new ArgumentNullException(nameof(fileAccess));
        ArgumentException.ThrowIfNullOrWhiteSpace(auditPath);
        _auditPath = Path.GetFullPath(auditPath);
    }

    public async Task<DotNetBuildResult> BuildAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var project = ResolveProject(projectPath);
        var normalizedConfiguration = NormalizeConfiguration(configuration);
        var started = Stopwatch.StartNew();
        ProcessExecutionResult execution;
        try
        {
            await DotNetBuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                execution = await ExecuteBuildAsync(project, normalizedConfiguration, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DotNetBuildLock.Release();
            }
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
                "The .NET build could not be started.",
                CompactOutput(ex.Message),
                null,
                started.ElapsedMilliseconds);
        }

        started.Stop();
        var artifact = execution.ExitCode == 0
            ? FindBuiltArtifact(project.PhysicalPath, normalizedConfiguration)
            : null;
        var success = execution.ExitCode == 0;
        await WriteAuditAsync(
                "build",
                projectPath,
                success,
                execution.ExitCode,
                started.ElapsedMilliseconds,
                success ? "Build completed." : "Build returned compiler or project errors.",
                cancellationToken)
            .ConfigureAwait(false);
        return new DotNetBuildResult(
            success,
            projectPath,
            normalizedConfiguration,
            execution.ExitCode,
            success
                ? artifact is null
                    ? "Build succeeded, but no launchable artifact was found under the project's bin folder."
                    : "Build succeeded and produced a launchable artifact."
                : "Build failed. Review the compiler output, correct the files, and build again.",
            CompactOutput(execution.Output),
            artifact,
            started.ElapsedMilliseconds);
    }

    public async Task<DotNetRunResult> RunAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = ResolveProject(projectPath);
        var normalizedConfiguration = NormalizeConfiguration(configuration);
        var artifact = FindBuiltArtifact(project.PhysicalPath, normalizedConfiguration);
        if (artifact is null)
        {
            await WriteAuditAsync(
                    "run",
                    projectPath,
                    false,
                    null,
                    0,
                    "No built artifact was found.",
                    cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(
                false,
                projectPath,
                "No built artifact was found. Build the project successfully before trying to run it.",
                null,
                null);
        }

        RejectReparsePoints(Path.GetDirectoryName(project.PhysicalPath)!, artifact);

        try
        {
            var startInfo = CreateLaunchStartInfo(artifact);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the compiled application.");
            var processId = process.Id;
            process.Dispose();
            await WriteAuditAsync(
                    "run",
                    projectPath,
                    true,
                    0,
                    0,
                    $"Started process {processId}.",
                    cancellationToken)
                .ConfigureAwait(false);
            return new DotNetRunResult(
                true,
                projectPath,
                "The compiled application was launched.",
                artifact,
                processId);
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

    private async Task<ProcessExecutionResult> ExecuteBuildAsync(
        ResolvedProject project,
        string configuration,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = Path.GetDirectoryName(project.PhysicalPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(project.PhysicalPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the .NET SDK.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(BuildTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new ProcessExecutionResult(
                -1,
                $"Build stopped after the {BuildTimeoutSeconds}-second safety timeout.\n"
                + await standardOutput.ConfigureAwait(false)
                + await standardError.ConfigureAwait(false));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ProcessExecutionResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false));
    }

    private ResolvedProject ResolveProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var resolved = _fileAccess.ResolvePhysicalFilePath(projectPath);
        if (!Path.GetExtension(resolved.PhysicalPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The .NET build tool accepts only an approved .csproj path.", nameof(projectPath));
        }

        if (!File.Exists(resolved.PhysicalPath))
        {
            throw new FileNotFoundException("The requested .csproj file does not exist.", resolved.PhysicalPath);
        }

        RejectReparsePoints(resolved.MountRoot, resolved.PhysicalPath);
        return new ResolvedProject(projectPath, resolved.PhysicalPath);
    }

    private static void RejectReparsePoints(string mountRoot, string physicalPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot));
        var current = File.Exists(physicalPath)
            ? new FileInfo(physicalPath).Directory
            : new DirectoryInfo(Path.GetDirectoryName(physicalPath)!);
        while (current is not null
               && !current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(".NET projects reached through a reparse point are not executable by Ali.");
            }

            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException("The .NET project escaped its approved workstation mount.");
        }

        if ((File.GetAttributes(physicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("A reparse-point .csproj cannot be executed by Ali.");
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

    private static string ResolveDotNetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? configured
            : "dotnet";
    }

    private static string? FindBuiltArtifact(string projectPath, string configuration)
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
        if (executable is not null)
        {
            return executable;
        }

        return Directory.EnumerateFiles(outputRoot, assemblyName + ".dll", SearchOption.AllDirectories)
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
            await File.AppendAllTextAsync(_auditPath, entry + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
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
            : "... earlier build output omitted ..." + Environment.NewLine + normalized[^MaximumOutputCharacters..];
    }

    private sealed record ResolvedProject(string VirtualPath, string PhysicalPath);

    private sealed record ProcessExecutionResult(int ExitCode, string Output);
}
