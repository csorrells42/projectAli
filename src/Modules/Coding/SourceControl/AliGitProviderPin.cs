using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.SourceControl;

/// <summary>
/// Immutable provider selection shared by Git preparation, revalidation, and execution. The
/// executable is resolved once; later PATH changes cannot select a different provider.
/// </summary>
internal sealed class AliGitProviderPin
{
    private static readonly string[] PinnedEnvironmentNames =
    [
        "HOME",
        "USERPROFILE",
        "XDG_CONFIG_HOME",
        "PROGRAMDATA",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "SystemRoot",
        "WINDIR",
        "TEMP",
        "TMP"
    ];

    private static readonly string[] RemovedGitEnvironmentNames =
    [
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_ALLOW_PROTOCOL",
        "GIT_ASKPASS",
        "GIT_ATTR_NOSYSTEM",
        "GIT_AUTHOR_DATE",
        "GIT_AUTHOR_EMAIL",
        "GIT_AUTHOR_NAME",
        "GIT_CEILING_DIRECTORIES",
        "GIT_COMMON_DIR",
        "GIT_CONFIG",
        "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_NOSYSTEM",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_SYSTEM",
        "GIT_COMMITTER_DATE",
        "GIT_COMMITTER_EMAIL",
        "GIT_COMMITTER_NAME",
        "GIT_DIR",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
        "GIT_EXTERNAL_DIFF",
        "GIT_GRAFT_FILE",
        "GIT_ICASE_PATHSPECS",
        "GIT_INDEX_FILE",
        "GIT_NAMESPACE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_PROXY_COMMAND",
        "GIT_PROTOCOL",
        "GIT_PROTOCOL_FROM_USER",
        "GIT_REPLACE_REF_BASE",
        "GIT_SHALLOW_FILE",
        "GIT_SSH",
        "GIT_SSH_COMMAND",
        "GIT_SSL_NO_VERIFY",
        "GIT_TEMPLATE_DIR",
        "GIT_WORK_TREE",
        "SSH_ASKPASS"
    ];

    private readonly IReadOnlyDictionary<string, string?> _pinnedEnvironment;

    internal AliGitProviderPin(
        string executablePath,
        string execPath,
        string installationRoot,
        IReadOnlyList<string> toolDirectories,
        IReadOnlyDictionary<string, string?> pinnedEnvironment)
    {
        ExecutablePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(executablePath)
                ? throw new ArgumentException("A pinned Git executable is required.", nameof(executablePath))
                : executablePath);
        ExecPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(execPath)
                ? throw new ArgumentException("A pinned Git exec path is required.", nameof(execPath))
                : execPath));
        InstallationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(installationRoot)
                ? throw new ArgumentException(
                    "A pinned Git installation root is required.",
                    nameof(installationRoot))
                : installationRoot));
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            InstallationRoot,
            "The pinned Git installation root is not a regular local directory.");
        ArgumentNullException.ThrowIfNull(toolDirectories);
        ToolDirectories = Array.AsReadOnly(toolDirectories
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(PathComparer)
            .ToArray());
        if (ToolDirectories.Count == 0)
        {
            throw new ArgumentException("At least one fixed Git tool directory is required.", nameof(toolDirectories));
        }
        _pinnedEnvironment = new Dictionary<string, string?>(
            pinnedEnvironment ?? throw new ArgumentNullException(nameof(pinnedEnvironment)),
            StringComparer.OrdinalIgnoreCase);
        ExecutionPath = string.Join(Path.PathSeparator, ToolDirectories);
        InitialIdentity = CaptureIdentity();
    }

    internal string ExecutablePath { get; }

    internal string ExecPath { get; }

    internal string InstallationRoot { get; }

    internal IReadOnlyList<string> ToolDirectories { get; }

    internal string ExecutionPath { get; }

    internal string InitialIdentity { get; }

    internal string CaptureIdentity()
    {
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            ExecPath,
            "The pinned Git exec path is no longer a regular local directory.");
        var executableIdentity = AliGitBoundedFileIdentity.Capture(
            ExecutablePath,
            512L * 1024 * 1024,
            "The pinned Git provider executable is not a regular local file.",
            InstallationRoot);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["policy"] = AliGitExecutionPolicy.PolicyIdentity,
            ["executable"] = executableIdentity,
            ["execPath"] = NormalizePath(ExecPath),
            ["installationRoot"] = NormalizePath(InstallationRoot),
            ["executionPath"] = string.Join("\0", ToolDirectories.Select(NormalizePath))
        };
        foreach (var name in PinnedEnvironmentNames)
        {
            values["environment:" + name] = _pinnedEnvironment.TryGetValue(name, out var value)
                ? value ?? "<absent>"
                : "<absent>";
        }
        return WorkIdentityCanonicalizer.MapDigest("ali-git-provider-pin-v2", values);
    }

    internal void RequireStableIdentity()
    {
        var current = CaptureIdentity();
        var expectedBytes = Encoding.UTF8.GetBytes(InitialIdentity);
        var currentBytes = Encoding.UTF8.GetBytes(current);
        try
        {
            if (expectedBytes.Length != currentBytes.Length
                || !CryptographicOperations.FixedTimeEquals(expectedBytes, currentBytes))
            {
                throw new InvalidOperationException(
                    "The pinned Git provider changed before execution.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(currentBytes);
        }
    }

    internal ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            workingDirectory,
            "The Git working directory is not a regular local directory.");
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var name in RemovedGitEnvironmentNames)
        {
            startInfo.Environment.Remove(name);
        }
        foreach (var name in startInfo.Environment.Keys
                     .Where(name => name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
                                    || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)
                                    || name.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(name);
        }
        foreach (var name in PinnedEnvironmentNames)
        {
            if (_pinnedEnvironment.TryGetValue(name, out var value) && value is not null)
            {
                startInfo.Environment[name] = value;
            }
            else
            {
                startInfo.Environment.Remove(name);
            }
        }

        startInfo.Environment["PATH"] = ExecutionPath;
        startInfo.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        startInfo.Environment["GIT_EXEC_PATH"] = ExecPath;
        startInfo.Environment["GIT_CONFIG_COUNT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["PAGER"] = "cat";
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        return startInfo;
    }

    internal string CaptureProviderCommand(string commandName) =>
        CaptureProviderCommandBinding(commandName).Identity;

    internal AliGitExecutionFileBinding CaptureProviderCommandBinding(string commandName)
    {
        if (!AliGitProviderIdentity.CommandNamePattern().IsMatch(commandName))
        {
            throw new InvalidDataException("A Git helper command name is not fixed and portable.");
        }
        var fileNames = OperatingSystem.IsWindows()
            ? new[] { commandName + ".exe", commandName }
            : new[] { commandName };
        foreach (var directory in ToolDirectories)
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
                if (!File.Exists(candidate))
                {
                    continue;
                }
                return AliGitExecutionFileBinding.Capture(
                    candidate,
                    512L * 1024 * 1024,
                    "A Git helper executable is not a regular local file.",
                    InstallationRoot);
            }
        }
        throw new FileNotFoundException(
            "A required fixed Git helper is unavailable in the pinned provider tool directories.",
            commandName);
    }

    internal static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PinnedEnvironmentNames)
        {
            values[name] = Environment.GetEnvironmentVariable(name);
        }
        return values;
    }

    internal string? GetPinnedEnvironment(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _pinnedEnvironment.TryGetValue(name, out var value) ? value : null;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}

/// <summary>
/// Exact file input consumed by Git after authorization. The binding can be leased no-write and
/// no-delete across the complete provider process lifetime.
/// </summary>
internal sealed record AliGitExecutionFileBinding(
    string PhysicalPath,
    string Identity,
    long MaximumBytes,
    string Description,
    string? AllowedHardLinkRoot)
{
    internal static AliGitExecutionFileBinding Capture(
        string path,
        long maximumBytes,
        string description,
        string? allowedHardLinkRoot = null)
    {
        var fullPath = Path.GetFullPath(path);
        return new AliGitExecutionFileBinding(
            fullPath,
            AliGitBoundedFileIdentity.Capture(
                fullPath,
                maximumBytes,
                description,
                allowedHardLinkRoot),
            maximumBytes,
            description,
            allowedHardLinkRoot);
    }

    internal AliExecutionFileLease Acquire() =>
        AliExecutionFileLease.Acquire(
            PhysicalPath,
            Description,
            AllowedHardLinkRoot,
            () =>
            {
                var current = AliGitBoundedFileIdentity.Capture(
                    PhysicalPath,
                    MaximumBytes,
                    Description,
                    AllowedHardLinkRoot);
                if (!string.Equals(current, Identity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        Description + " changed after exact authorization.");
                }
            });
}

internal sealed class AliGitExecutionFileLeaseGroup : IDisposable
{
    private readonly List<AliExecutionFileLease> _leases;
    private bool _disposed;

    private AliGitExecutionFileLeaseGroup(List<AliExecutionFileLease> leases) =>
        _leases = leases;

    internal static AliGitExecutionFileLeaseGroup Acquire(
        IReadOnlyList<AliGitExecutionFileBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var unique = new Dictionary<string, AliGitExecutionFileBinding>(comparer);
        foreach (var binding in bindings)
        {
            if (unique.TryGetValue(binding.PhysicalPath, out var prior)
                && !string.Equals(prior.Identity, binding.Identity, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "One exact Git execution input has conflicting authorized identities.");
            }
            unique[binding.PhysicalPath] = binding;
        }

        var leases = new List<AliExecutionFileLease>();
        try
        {
            foreach (var binding in unique.Values.OrderBy(
                         item => item.PhysicalPath,
                         comparer))
            {
                leases.Add(binding.Acquire());
            }
            return new AliGitExecutionFileLeaseGroup(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                leases[index].Dispose();
            }
            throw;
        }
    }

    internal void RequireStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var lease in _leases)
        {
            lease.RequireStable();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (var index = _leases.Count - 1; index >= 0; index--)
        {
            _leases[index].Dispose();
        }
    }
}

internal static partial class AliGitProviderIdentity
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex CommandNamePattern();

    internal static AliGitProviderPin Pin()
    {
        var anchor = ResolveExecutableAnchorPath();
        var layout = ResolveStaticProviderLayout(anchor);
        var toolDirectories = ResolveToolDirectories(
            layout.ExecutablePath,
            layout.ExecPath,
            layout.InstallationRoot);
        return new AliGitProviderPin(
            layout.ExecutablePath,
            layout.ExecPath,
            layout.InstallationRoot,
            toolDirectories,
            AliGitProviderPin.CaptureEnvironment());
    }

    private static string ResolveExecutableAnchorPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(segment.Trim('"'), fileName));
                if (!File.Exists(candidate))
                {
                    continue;
                }
                return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
            {
                // Invalid or inaccessible PATH entries are not executable identities.
            }
        }
        throw new FileNotFoundException(
            "The fixed Git provider executable is unavailable on PATH.",
            fileName);
    }

    private static StaticProviderLayout ResolveStaticProviderLayout(string anchor)
    {
        var anchorDirectory = Path.GetDirectoryName(anchor)
            ?? throw new InvalidDataException("The Git provider path has no directory.");
        var candidates = new List<(string InstallationRoot, string ExecPath)>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in Ancestors(anchorDirectory, maximum: 4))
            {
                candidates.Add((root, Path.Combine(root, "mingw64", "libexec", "git-core")));
                candidates.Add((root, Path.Combine(root, "libexec", "git-core")));
            }
        }
        else
        {
            foreach (var root in Ancestors(anchorDirectory, maximum: 3))
            {
                candidates.Add((root, Path.Combine(root, "lib", "git-core")));
                candidates.Add((root, Path.Combine(root, "libexec", "git-core")));
            }
        }

        var providerName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var candidate in candidates
                     .Distinct())
        {
            var installationRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate.InstallationRoot));
            var execPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate.ExecPath));
            var executable = Path.GetFullPath(Path.Combine(execPath, providerName));
            try
            {
                WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                    installationRoot,
                    "The fixed Git installation root is not a regular local directory.");
                WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                    execPath,
                    "The fixed Git exec path is not a regular local directory.");
                _ = AliGitBoundedFileIdentity.Capture(
                    executable,
                    512L * 1024 * 1024,
                    "The canonical Git provider executable is not a regular local file.",
                    installationRoot);
                return new StaticProviderLayout(executable, execPath, installationRoot);
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
            {
                // Only a completely static, readable Git installation layout is eligible.
            }
        }
        throw new FileNotFoundException(
            "A canonical Git provider executable and static exec path could not be proven without execution.",
            anchor);
    }

    private static IReadOnlyList<string> ResolveToolDirectories(
        string executable,
        string execPath,
        string installationRoot)
    {
        var candidates = new List<string> { execPath };
        var executableDirectory = Path.GetDirectoryName(executable)
                                  ?? throw new InvalidDataException(
                                      "The Git provider executable path has no directory.");
        candidates.Add(executableDirectory);
        candidates.Add(Path.Combine(installationRoot, "mingw64", "bin"));
        candidates.Add(Path.Combine(installationRoot, "usr", "bin"));
        candidates.Add(Path.Combine(installationRoot, "bin"));
        candidates.Add(Path.Combine(installationRoot, "cmd"));
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Environment.SystemDirectory);
            var windows = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(windows))
            {
                candidates.Add(windows);
            }
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!Directory.Exists(path) || result.Contains(path, comparer))
            {
                continue;
            }
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                path,
                "A fixed Git provider tool directory is not a regular local directory.");
            result.Add(path);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static IEnumerable<string> Ancestors(string start, int maximum)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
        for (var count = 0; count < maximum; count++)
        {
            yield return current;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(
                    Path.TrimEndingDirectorySeparator(parent),
                    current,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                yield break;
            }
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        }
    }

    private sealed record StaticProviderLayout(
        string ExecutablePath,
        string ExecPath,
        string InstallationRoot);
}

internal static class AliGitExecutionPolicy
{
    internal const string PolicyIdentity = "ali-git-fixed-provider-policy-v2";

    private static readonly string[] CommonArguments =
    [
        "--no-pager",
        "--no-optional-locks",
        "--no-replace-objects",
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.fsmonitor=false",
        "-c", "core.excludesFile=/dev/null",
        "-c", "core.attributesFile=/dev/null",
        "-c", "core.askPass=",
        "-c", "credential.interactive=never",
        "-c", "protocol.ext.allow=never",
        "-c", "push.recurseSubmodules=no",
        "-c", "commit.gpgSign=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false"
    ];

    internal static IReadOnlyList<string> Bind(
        AliGitInvocationKind kind,
        IReadOnlyList<string> operationArguments)
    {
        ArgumentNullException.ThrowIfNull(operationArguments);
        var result = new List<string>(CommonArguments.Length + operationArguments.Count + 2);
        result.AddRange(CommonArguments);
        if (kind != AliGitInvocationKind.Push)
        {
            result.Add("-c");
            result.Add("credential.helper=");
        }
        result.AddRange(operationArguments);
        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> CommandLineValues(
        AliGitInvocationKind kind)
    {
        var values = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["core.hookspath"] = ["/dev/null"],
            ["core.fsmonitor"] = ["false"],
            ["core.excludesfile"] = ["/dev/null"],
            ["core.attributesfile"] = ["/dev/null"],
            ["core.askpass"] = [string.Empty],
            ["credential.interactive"] = ["never"],
            ["protocol.ext.allow"] = ["never"],
            ["push.recursesubmodules"] = ["no"],
            ["commit.gpgsign"] = ["false"],
            ["gc.auto"] = ["0"],
            ["maintenance.auto"] = ["false"]
        };
        if (kind != AliGitInvocationKind.Push)
        {
            values["credential.helper"] = [string.Empty];
        }
        return values;
    }
}

internal static class AliGitFixedProcess
{
    private const int MaximumReportedOutputBytes = 30_000;

    internal static async Task<BoundedProcessResult> RunAsync(
        AliGitProviderPin provider,
        string workingDirectory,
        AliGitInvocationKind kind,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action? beforeProcessStart = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var started = Stopwatch.StartNew();
        var startInfo = provider.CreateStartInfo(workingDirectory);
        foreach (var argument in AliGitExecutionPolicy.Bind(kind, arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        using var executableLease = AliExecutionFileLease.Acquire(
            provider.ExecutablePath,
            "The exact pinned Git provider executable",
            provider.InstallationRoot,
            provider.RequireStableIdentity);
        using var providerDirectories = AliExecutionDirectoryLeaseGroup.Acquire(
            provider.ToolDirectories
                .Append(provider.ExecPath)
                .Append(provider.InstallationRoot),
            "The exact pinned Git provider directory spine");
        var workingDirectoryBinding = AliExecutionDirectoryBinding.Capture(
            workingDirectory,
            "The Git working directory spine");
        using var workingDirectoryLease = workingDirectoryBinding.Acquire(
            "The exact Git working directory spine");
        var processStarted = false;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            provider.RequireStableIdentity();
            executableLease.RequireStable();
            providerDirectories.RequireStable();
            workingDirectoryLease.RequireStable();
            beforeProcessStart?.Invoke();
            executableLease.RequireStable();
            providerDirectories.RequireStable();
            workingDirectoryLease.RequireStable();
            provider.RequireStableIdentity();
            process.Start();
            processStarted = true;
            executableLease.RequireStartedProcessImage(process);
            var stdout = ReadTailAsync(
                process.StandardOutput.BaseStream,
                MaximumReportedOutputBytes,
                linkedSource.Token);
            var stderr = ReadTailAsync(
                process.StandardError.BaseStream,
                MaximumReportedOutputBytes,
                linkedSource.Token);
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            started.Stop();
            var output = Compact(
                Encoding.UTF8.GetString(stdout.Result)
                + Environment.NewLine
                + Encoding.UTF8.GetString(stderr.Result));
            return new BoundedProcessResult(
                process.ExitCode == 0,
                process.ExitCode,
                output,
                started.ElapsedMilliseconds,
                TimedOut: false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            Kill(process, processStarted);
            started.Stop();
            return new BoundedProcessResult(
                false,
                -1,
                string.Empty,
                started.ElapsedMilliseconds,
                TimedOut: true);
        }
        catch
        {
            Kill(process, processStarted);
            throw;
        }
    }

    private static async Task<byte[]> ReadTailAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var ring = new byte[maximumBytes];
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            for (var index = 0; index < read; index++)
            {
                ring[(int)(total % maximumBytes)] = buffer[index];
                total++;
            }
        }
        CryptographicOperations.ZeroMemory(buffer);
        var length = (int)Math.Min(total, maximumBytes);
        if (total <= maximumBytes)
        {
            return ring[..length];
        }
        var result = new byte[length];
        var start = (int)(total % maximumBytes);
        var first = maximumBytes - start;
        Buffer.BlockCopy(ring, start, result, 0, first);
        Buffer.BlockCopy(ring, 0, result, first, start);
        CryptographicOperations.ZeroMemory(ring);
        return result;
    }

    private static void Kill(Process process, bool processStarted)
    {
        if (!processStarted)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5_000);
            }
        }
        catch
        {
            // Preserve the original timeout, cancellation, or process failure.
        }
    }

    private static string Compact(string value)
    {
        var normalized = value.ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= MaximumReportedOutputBytes
            ? normalized
            : normalized[^MaximumReportedOutputBytes..];
    }
}

internal static class AliGitBoundedFileIdentity
{
    internal static string Capture(
        string path,
        long maximumBytes,
        string invalidMessage,
        string? allowedHardLinkRoot = null)
    {
        AliExecutionDirectoryLease.RequireFixedLocalVolume(path, invalidMessage);
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            invalidMessage);
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            path,
            invalidMessage,
            allowedHardLinkRoot);
        var fullFileIdentity = AliExecutionFileLease.CaptureFullFileIdentity(
            stream.SafeFileHandle,
            invalidMessage);
        var length = stream.Length;
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException(invalidMessage);
        }
        var digest = SHA256.HashData(stream);
        try
        {
            if (stream.Position != length || stream.Length != length)
            {
                throw new InvalidDataException("A Git input changed while it was captured.");
            }
            var repeatedIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                path,
                invalidMessage,
                allowedHardLinkRoot);
            var repeatedFullIdentity = AliExecutionFileLease.CaptureFullFileIdentity(
                stream.SafeFileHandle,
                invalidMessage);
            if (!string.Equals(
                    fileIdentity.CanonicalIdentity,
                    repeatedIdentity.CanonicalIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    fullFileIdentity,
                    repeatedFullIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("A Git input changed file identity while it was captured.");
            }
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (OperatingSystem.IsWindows())
            {
                normalized = normalized.ToUpperInvariant();
            }
            return HashText(string.Join(
                "\0",
                normalized,
                fileIdentity.CanonicalIdentity,
                fullFileIdentity,
                length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToHexString(digest).ToLowerInvariant()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
