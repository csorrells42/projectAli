using System.Diagnostics;
using System.Text.Json;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coordinator;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding;

public sealed record DotNetCreateProjectResult(
    bool Success,
    string ProjectPath,
    string Template,
    int? ExitCode,
    string Summary,
    string Output,
    long DurationMilliseconds);

/// <summary>
/// Creates a new C# project from a small allowlist of SDK templates. This is intentionally
/// separate from the build/launch tools and never accepts arbitrary commands or arguments.
/// </summary>
internal sealed class AliDotNetProjectScaffolder
{
    private const int MaximumOutputCharacters = 12_000;
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim ScaffoldLock = new(1, 1);
    private readonly AliWorkstationFileAccess _fileAccess;
    private readonly AliCodingProjectTracker _projectTracker;
    private readonly string _auditPath;
    private readonly SemaphoreSlim _auditLock = new(1, 1);

    public AliDotNetProjectScaffolder(
        AliWorkstationFileAccess fileAccess,
        AliCodingProjectTracker projectTracker,
        string auditPath)
    {
        _fileAccess = fileAccess ?? throw new ArgumentNullException(nameof(fileAccess));
        _projectTracker = projectTracker ?? throw new ArgumentNullException(nameof(projectTracker));
        ArgumentException.ThrowIfNullOrWhiteSpace(auditPath);
        _auditPath = Path.GetFullPath(auditPath);
    }

    public async Task<DotNetCreateProjectResult> CreateAsync(
        string projectPath,
        string template,
        CancellationToken cancellationToken)
    {
        projectPath = NormalizeNativeProjectPath(projectPath);
        var normalizedTemplate = NormalizeTemplate(template);
        var project = ResolveNewProject(projectPath);
        var started = Stopwatch.StartNew();
        ProcessExecutionResult execution;
        try
        {
            await ScaffoldLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                execution = await ExecuteCreateAsync(project, normalizedTemplate, cancellationToken).ConfigureAwait(false);
                if (execution.ExitCode == 0 && File.Exists(project.PhysicalPath))
                {
                    var git = await ExecuteGitInitializeAsync(project, cancellationToken).ConfigureAwait(false);
                    execution = new ProcessExecutionResult(
                        git.ExitCode,
                        execution.Output + Environment.NewLine + git.Output);
                }
            }
            finally
            {
                ScaffoldLock.Release();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            started.Stop();
            await WriteAuditAsync(projectPath, normalizedTemplate, false, null, started.ElapsedMilliseconds, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return new DotNetCreateProjectResult(
                false,
                projectPath,
                normalizedTemplate,
                null,
                "The .NET project could not be created.",
                CompactOutput(ex.Message),
                started.ElapsedMilliseconds);
        }

        started.Stop();
        var success = execution.ExitCode == 0
            && File.Exists(project.PhysicalPath)
            && Directory.Exists(Path.Combine(project.ProjectDirectory, ".git"));
        if (success)
        {
            _projectTracker.RecordScaffold(project.PhysicalPath);
            if (AliCoreAssistantExecutionContext.IsActive)
            {
                AliCoreAssistantExecutionContext.BindActiveProject(projectPath);
            }
        }
        await WriteAuditAsync(
                projectPath,
                normalizedTemplate,
                success,
                execution.ExitCode,
                started.ElapsedMilliseconds,
                success ? "Project scaffold and Git repository created." : "Project creation returned an error.",
                cancellationToken)
            .ConfigureAwait(false);
        return new DotNetCreateProjectResult(
            success,
            projectPath,
            normalizedTemplate,
            execution.ExitCode,
            success
                ? "The project scaffold and its Git repository were created. No commit was made. Write the requested application files next, then build it."
                : "Project creation failed. Review the SDK output before continuing.",
            CompactOutput(execution.Output),
            started.ElapsedMilliseconds);
    }

    private static string NormalizeNativeProjectPath(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var normalized = projectPath.Trim().Trim('"', '\'', '`');
        if (Path.IsPathFullyQualified(normalized)
            || normalized.Contains('/')
            || normalized.Contains('\\'))
        {
            return normalized.Replace('\\', '/');
        }

        var projectName = normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(normalized)
            : normalized;
        return $"Workspace/{projectName}/{projectName}.csproj";
    }

    private NewProject ResolveNewProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var resolved = _fileAccess.ResolvePhysicalFilePath(projectPath);
        if (!Path.GetExtension(resolved.PhysicalPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The project creation tool requires an approved .csproj path.", nameof(projectPath));
        }

        if (File.Exists(resolved.PhysicalPath))
        {
            throw new IOException("The requested .csproj already exists. Project creation never overwrites an existing project.");
        }

        var projectDirectory = Path.GetDirectoryName(resolved.PhysicalPath)
            ?? throw new ArgumentException("The project path must include a folder.", nameof(projectPath));
        if (Directory.Exists(projectDirectory) && Directory.EnumerateFileSystemEntries(projectDirectory).Any())
        {
            throw new IOException("The destination project folder is not empty. Choose a new empty folder.");
        }

        RejectReparsePoints(resolved.MountRoot, projectDirectory);
        var projectName = Path.GetFileNameWithoutExtension(resolved.PhysicalPath);
        if (string.IsNullOrWhiteSpace(projectName)
            || projectName.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException("The project name may contain only letters, digits, periods, underscores, and hyphens.", nameof(projectPath));
        }

        return new NewProject(resolved.PhysicalPath, projectDirectory, projectName);
    }

    private static async Task<ProcessExecutionResult> ExecuteCreateAsync(
        NewProject project,
        string template,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AliCodingInvocationExecutionContext.ResolveDotNetHostForExecution(),
            WorkingDirectory = FindExistingParent(project.ProjectDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add(template);
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(project.ProjectName);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(project.ProjectDirectory);
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add("net10.0");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the .NET SDK.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<ProcessExecutionResult> ExecuteGitInitializeAsync(
        NewProject project,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = project.ProjectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("init");
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start Git.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

    private static void RejectReparsePoints(string mountRoot, string projectDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot));
        var current = new DirectoryInfo(projectDirectory);
        while (current is not null && !current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(".NET projects reached through a reparse point cannot be created by Ali.");
            }

            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException("The new .NET project escaped its approved workstation mount.");
        }
    }

    private static string FindExistingParent(string path)
    {
        var current = new DirectoryInfo(path);
        while (!current.Exists)
        {
            current = current.Parent
                ?? throw new InvalidOperationException("No existing approved parent folder was found.");
        }

        return current.FullName;
    }

    private static string NormalizeTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        return template.Trim().ToLowerInvariant() switch
        {
            "wpf" => "wpf",
            "console" => "console",
            _ => throw new ArgumentException("Template must be wpf or console.", nameof(template))
        };
    }

    private async Task WriteAuditAsync(
        string projectPath,
        string template,
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
            operation = "create",
            projectPath,
            template,
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
            : "... earlier project creation output omitted ..." + Environment.NewLine + normalized[^MaximumOutputCharacters..];
    }

    private sealed record NewProject(string PhysicalPath, string ProjectDirectory, string ProjectName);

    private sealed record ProcessExecutionResult(int ExitCode, string Output);
}
