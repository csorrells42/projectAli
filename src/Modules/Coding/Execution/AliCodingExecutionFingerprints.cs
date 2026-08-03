using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.WorkstationFiles;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Coding.Execution;

internal sealed record AliBoundExecutionFile(
    string PhysicalPath,
    string Identity);

internal sealed record AliDotNetRunExecutionBinding(
    AliBoundExecutionFile? Artifact,
    AliBoundExecutionFile? HostExecutable,
    AliApplicationLaunchClosure? LaunchClosure)
{
    internal void AddTo(IDictionary<string, string> values)
    {
        Add(values, "dotnet.run.artifact", Artifact);
        Add(values, "dotnet.run.host", HostExecutable);
        if (LaunchClosure is null)
        {
            values["dotnet.run.output.path"] = "<absent>";
            values["dotnet.run.output.identity"] = "absent";
        }
        else
        {
            LaunchClosure.AddTo(values, "dotnet.run.output");
        }
    }

    private static void Add(
        IDictionary<string, string> values,
        string prefix,
        AliBoundExecutionFile? file)
    {
        values[prefix + ".path"] = file?.PhysicalPath ?? "<absent>";
        values[prefix + ".identity"] = file?.Identity ?? "absent";
    }
}

internal sealed record AliBoundProcessState(
    int ProcessId,
    long StartTimeUtcTicks,
    AliBoundExecutionFile Executable)
{
    internal void AddTo(IDictionary<string, string> values)
    {
        values["dotnet.stop.process.id"] = ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        values["dotnet.stop.process.startUtcTicks"] = StartTimeUtcTicks.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        values["dotnet.stop.process.executable.path"] = Executable.PhysicalPath;
        values["dotnet.stop.process.executable.identity"] = Executable.Identity;
    }

    internal void RequireStable(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited
            || process.Id != ProcessId
            || process.StartTime.ToUniversalTime().Ticks != StartTimeUtcTicks)
        {
            throw new InvalidOperationException(
                "The exact authorized process ID/start-time state is no longer stable.");
        }
        var executablePath = process.MainModule?.FileName
            ?? throw new InvalidOperationException(
                "The exact authorized process executable could not be inspected.");
        var executable = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            executablePath,
            "The exact authorized process executable");
        if (!string.Equals(
                executable.PhysicalPath,
                Executable.PhysicalPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !string.Equals(
                executable.Identity,
                Executable.Identity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exact authorized process executable state is no longer stable.");
        }
    }
}

internal sealed record AliDotNetStopExecutionBinding(
    AliBoundExecutionFile? Artifact,
    AliBoundProcessState? Process)
{
    internal void AddTo(IDictionary<string, string> values)
    {
        values["dotnet.stop.artifact.path"] = Artifact?.PhysicalPath ?? "<absent>";
        values["dotnet.stop.artifact.identity"] = Artifact?.Identity ?? "absent";
        values["dotnet.stop.process.present"] = (Process is not null).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        Process?.AddTo(values);
    }
}

internal sealed record AliCodingRuntimeBinding(
    AliBoundExecutionFile? DotNetHost,
    AliDotNetRunExecutionBinding? DotNetRun,
    AliDotNetStopExecutionBinding? DotNetStop)
{
    internal static AliCodingRuntimeBinding None { get; } = new(null, null, null);

    internal void AddTo(IDictionary<string, string> values)
    {
        values["dotnet.host.path"] = DotNetHost?.PhysicalPath ?? "<absent>";
        values["dotnet.host.identity"] = DotNetHost?.Identity ?? "absent";
        DotNetRun?.AddTo(values);
        DotNetStop?.AddTo(values);
    }
}

/// <summary>
/// Captures and revalidates the exact dotnet host executable. This deliberately has no PATH
/// lookup during revalidation, so coding and DevOps bindings can share the same pinned-host
/// primitive without rediscovering a different executable immediately before launch.
/// </summary>
internal static class AliExactDotNetHost
{
    internal static AliBoundExecutionFile CaptureCurrent()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var path = !string.IsNullOrWhiteSpace(configured)
            ? AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(configured)
            : AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return Capture(path);
    }

    internal static AliBoundExecutionFile Capture(string path) =>
        AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(path),
            "The selected .NET host executable");

    internal static string Revalidate(AliBoundExecutionFile expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            expected.PhysicalPath,
            "The exact authorized .NET host executable");
        if (!string.Equals(
                current.PhysicalPath,
                expected.PhysicalPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !string.Equals(current.Identity, expected.Identity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exact authorized .NET host executable changed before launch.");
        }
        return expected.PhysicalPath;
    }
}

/// <summary>
/// Carries the exact binding that was revalidated by the durable adapter into the executor call.
/// It is structural authorization state, not a routing or interpretation mechanism.
/// </summary>
internal static class AliCodingInvocationExecutionContext
{
    private static readonly AsyncLocal<Frame?> CurrentFrame = new();

    internal static AliCodingInvocationBinding? Current => CurrentFrame.Value?.Binding;

    internal static IDisposable Enter(AliCodingInvocationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var prior = CurrentFrame.Value;
        CurrentFrame.Value = new Frame(binding, prior);
        return new Scope(CurrentFrame.Value);
    }

    internal static string ValidateProcessLaunch(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var binding = Current;
        if (binding is null)
        {
            return executable;
        }

        RevalidateAllAssets(binding);
        var resolvedExecutable = IsDotNetHostRequest(executable)
                                 && binding.RuntimeBinding.DotNetHost is { } pinnedHost
            ? AliExactDotNetHost.Revalidate(pinnedHost)
            : AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(executable);
        RequireBoundFile(binding, resolvedExecutable, "process executable");
        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument)
                || argument[0] == '@'
                || !Path.IsPathFullyQualified(argument)
                || !File.Exists(argument))
            {
                continue;
            }
            var normalized = AliCodingExecutionAssetFingerprint.NormalizePath(argument);
            if (IsOrdinaryBoundSource(binding.TargetRoot, normalized))
            {
                continue;
            }
            RequireBoundFile(binding, normalized, "executed script or tool asset");
        }
        return resolvedExecutable;
    }

    internal static AliCodingExecutedAssetLeaseGroup AcquireExecutedAssetLeases(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var binding = Current;
        if (binding is null)
        {
            return AliCodingExecutedAssetLeaseGroup.Empty();
        }

        RevalidateAllAssets(binding);
        var files = new List<AliBoundExecutionFile>();
        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument)
                || argument[0] == '@'
                || !Path.IsPathFullyQualified(argument)
                || !File.Exists(argument))
            {
                continue;
            }
            var normalized = AliCodingExecutionAssetFingerprint.NormalizePath(argument);
            if (IsOrdinaryBoundSource(binding.TargetRoot, normalized))
            {
                continue;
            }
            files.Add(RequireBoundFile(
                binding,
                normalized,
                "executed script or tool asset"));
        }
        var leases = AliCodingExecutedAssetLeaseGroup.Acquire(files);
        try
        {
            RevalidateAllAssets(binding);
            leases.RequireStable();
            return leases;
        }
        catch
        {
            leases.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the absolute dotnet host authorized for the active coding invocation. Direct
    /// unit-level callers without a durable invocation still receive a freshly captured exact
    /// absolute host, never a bare command that would be resolved again by Process.Start.
    /// </summary>
    internal static AliBoundExecutionFile ResolveDotNetHostBindingForExecution()
    {
        if (AliExactProcessExecutionContext.Current is { } shared)
        {
            var sharedExpected = shared.DotNetHost
                ?? throw new InvalidOperationException(
                    "The exact process execution scope did not bind a .NET host executable.");
            _ = shared.RequireStableDotNetHost();
            return sharedExpected;
        }
        var binding = Current;
        if (binding is null)
        {
            return AliExactDotNetHost.CaptureCurrent();
        }
        var expected = binding.RuntimeBinding.DotNetHost
            ?? throw new InvalidOperationException(
                "The authorized coding invocation did not bind an exact .NET host executable.");
        RevalidateAllAssets(binding);
        _ = AliExactDotNetHost.Revalidate(expected);
        return expected;
    }

    internal static string ResolveDotNetHostForExecution() =>
        ResolveDotNetHostBindingForExecution().PhysicalPath;

    private static bool IsDotNetHostRequest(string executable)
    {
        var fileName = Path.GetFileName(executable);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void RevalidateAllAssets(AliCodingInvocationBinding binding)
    {
        foreach (var asset in binding.ExecutionAssets)
        {
            var exists = File.Exists(asset.Key) || Directory.Exists(asset.Key);
            if (string.Equals(asset.Value, "absent", StringComparison.Ordinal))
            {
                if (exists)
                {
                    throw new InvalidOperationException(
                        "An exact coding execution asset appeared after authorization.");
                }
                continue;
            }
            if (!exists)
            {
                throw new InvalidOperationException(
                    "An exact coding execution asset disappeared after authorization.");
            }
            var current = AliCodingExecutionAssetFingerprint.CaptureRequiredAsset(
                asset.Key,
                "An exact coding execution asset");
            if (!string.Equals(current, asset.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "An exact coding execution asset changed after authorization.");
            }
        }
    }

    private static AliBoundExecutionFile RequireBoundFile(
        AliCodingInvocationBinding binding,
        string path,
        string description)
    {
        var normalized = AliCodingExecutionAssetFingerprint.NormalizePath(path);
        var current = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            normalized,
            "The exact coding " + description);
        if (binding.ExecutionAssets.TryGetValue(normalized, out var expected)
            && string.Equals(current.Identity, expected, StringComparison.Ordinal))
        {
            return current;
        }
        foreach (var asset in binding.ExecutionAssets)
        {
            if (!asset.Value.StartsWith("directory:", StringComparison.Ordinal)
                || !IsWithin(asset.Key, normalized))
            {
                continue;
            }
            return current;
        }
        throw new InvalidOperationException(
            "The selected coding " + description + " is not part of the exact authorized execution assets.");
    }

    private static bool IsOrdinaryBoundSource(string targetRoot, string path)
    {
        if (!IsWithin(targetRoot, path))
        {
            return false;
        }
        var relative = Path.GetRelativePath(targetRoot, path);
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".ali", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("release", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = AliCodingExecutionAssetFingerprint.NormalizePath(root);
        var normalizedPath = AliCodingExecutionAssetFingerprint.NormalizePath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedRoot, normalizedPath, comparison)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    private sealed record Frame(AliCodingInvocationBinding Binding, Frame? Prior);

    private sealed class Scope(Frame frame) : IDisposable
    {
        private Frame? _frame = frame;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _frame, null);
            if (current is null)
            {
                return;
            }
            if (!ReferenceEquals(CurrentFrame.Value, current))
            {
                throw new InvalidOperationException(
                    "The exact coding invocation execution scope was disposed out of order.");
            }
            CurrentFrame.Value = current.Prior;
        }
    }
}

internal sealed class AliCodingExecutedAssetLeaseGroup : IDisposable
{
    private readonly List<AliExecutionFileLease> _leases;
    private bool _disposed;

    private AliCodingExecutedAssetLeaseGroup(List<AliExecutionFileLease> leases) =>
        _leases = leases;

    internal static AliCodingExecutedAssetLeaseGroup Empty() => new([]);

    internal static AliCodingExecutedAssetLeaseGroup Acquire(
        IEnumerable<AliBoundExecutionFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var unique = new Dictionary<string, AliBoundExecutionFile>(comparer);
        foreach (var file in files)
        {
            if (unique.TryGetValue(file.PhysicalPath, out var prior)
                && !string.Equals(prior.Identity, file.Identity, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "One executed coding asset has conflicting exact identities.");
            }
            unique[file.PhysicalPath] = file;
        }

        var leases = new List<AliExecutionFileLease>();
        try
        {
            foreach (var file in unique.Values.OrderBy(
                         item => item.PhysicalPath,
                         comparer))
            {
                leases.Add(AliExecutionFileLease.Acquire(
                    file,
                    "An exact executed coding script or tool asset"));
            }
            return new AliCodingExecutedAssetLeaseGroup(leases);
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

/// <summary>
/// Produces bounded, content-addressed identities for exact executables and local tool assets.
/// Directory traversal rejects links/devices and is captured twice so a moving tool tree fails
/// closed instead of being accepted under an unstable digest.
/// </summary>
internal static class AliCodingExecutionAssetFingerprint
{
    private const int MaximumFiles = 50_000;
    private const long MaximumFileBytes = 1024L * 1024 * 1024;
    private const long MaximumAggregateBytes = 2L * 1024 * 1024 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    internal static AliBoundExecutionFile CaptureRequiredFile(
        string path,
        string description) =>
        CaptureRequiredFile(path, description, allowedHardLinkRoot: null);

    /// <summary>
    /// Captures an executable while allowing Windows-serviced hard links only for a binary selected
    /// from an OS system directory. Every alias must still remain inside the pinned Windows install.
    /// Ordinary project files and third-party tool assets continue to require one link.
    /// </summary>
    internal static AliBoundExecutionFile CaptureRequiredExecutable(
        string path,
        string description) =>
        CaptureRequiredExecutable(path, description, allowedHardLinkRoot: null);

    internal static AliBoundExecutionFile CaptureRequiredExecutable(
        string path,
        string description,
        string? allowedHardLinkRoot) =>
        CaptureRequiredFile(
            path,
            description,
            ResolveAllowedWindowsExecutableHardLinkRoot(path, allowedHardLinkRoot));

    internal static string? ResolveAllowedWindowsExecutableHardLinkRoot(string path) =>
        ResolveAllowedWindowsExecutableHardLinkRoot(path, allowedHardLinkRoot: null);

    internal static string? ResolveAllowedWindowsExecutableHardLinkRoot(
        string path,
        string? allowedHardLinkRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var fullPath = NormalizePath(path);
        if (allowedHardLinkRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(allowedHardLinkRoot);
            var pinnedRoot = NormalizePath(allowedHardLinkRoot);
            ValidateRegularDirectoryNoFollow(
                pinnedRoot,
                "The pinned executable provider root is not a regular local directory.");
            if (!IsWithin(pinnedRoot, fullPath))
            {
                throw new InvalidDataException(
                    "The selected executable leaves its pinned provider root.");
            }
            return pinnedRoot;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
        {
            return null;
        }

        foreach (var specialFolder in new[]
                 {
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.SystemX86
                 })
        {
            var systemDirectory = Environment.GetFolderPath(specialFolder);
            if (!string.IsNullOrWhiteSpace(systemDirectory)
                && IsWithin(NormalizePath(systemDirectory), fullPath))
            {
                return NormalizePath(windows);
            }
        }
        return null;
    }

    private static AliBoundExecutionFile CaptureRequiredFile(
        string path,
        string description,
        string? allowedHardLinkRoot)
    {
        var fullPath = NormalizePath(path);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(description + " is a directory, not a regular file.");
        }
        RejectSpecial(attributes, description);
        return new AliBoundExecutionFile(
            fullPath,
            "file:sha256:" + HashRegularFile(
                fullPath,
                description,
                allowedHardLinkRoot));
    }

    internal static string CaptureRequiredAsset(string path, string description)
    {
        var fullPath = NormalizePath(path);
        var attributes = File.GetAttributes(fullPath);
        RejectSpecial(attributes, description);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            return "file:sha256:" + HashRegularFile(fullPath, description);
        }

        var first = HashDirectory(fullPath, description);
        var second = HashDirectory(fullPath, description);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new IOException(description + " changed while its exact identity was captured.");
        }
        return "directory:sha256:" + second;
    }

    internal static string ResolveRequiredExecutable(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (Path.IsPathFullyQualified(executable))
        {
            var fullPath = Path.GetFullPath(executable);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    "The exact coding executable does not exist.",
                    fullPath);
            }
            return fullPath;
        }

        foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries
                         | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(segment, executable));
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or IOException
                                               or NotSupportedException)
            {
                continue;
            }
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "The exact coding executable could not be resolved to a stable local file.",
            executable);
    }

    internal static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static bool IsWithin(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRegularDirectoryNoFollow(
        string directory,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException(description);
        var current = root;
        RequireRegularDirectory(current, description);
        var relative = Path.GetRelativePath(root, fullPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return;
        }
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            RequireRegularDirectory(current, description);
        }
    }

    private static string HashDirectory(string root, string description)
    {
        ValidateRegularDirectoryNoFollow(
            root,
            description + " is not a regular local directory.");
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(root);
        var fileCount = 0;
        long aggregateBytes = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            ValidateRegularDirectoryNoFollow(
                directory,
                description + " is not a regular local directory.");
            var entries = Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var children = new List<string>();
            foreach (var entry in entries)
            {
                var attributes = File.GetAttributes(entry);
                RejectSpecial(attributes, description);
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                Append(aggregate, relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Append(aggregate, "directory");
                    children.Add(entry);
                    continue;
                }

                fileCount = checked(fileCount + 1);
                if (fileCount > MaximumFiles)
                {
                    throw new InvalidDataException(
                        description + " exceeds the exact file-count bound.");
                }
                Append(aggregate, "file");
                Append(
                    aggregate,
                    HashRegularFile(
                        entry,
                        description,
                        ref aggregateBytes));
            }

            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }

        Append(aggregate, fileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(aggregate, aggregateBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Finish(aggregate);
    }

    private static string HashRegularFile(
        string path,
        string description,
        string? allowedHardLinkRoot = null)
    {
        long aggregateBytes = 0;
        return HashRegularFile(
            path,
            description,
            ref aggregateBytes,
            allowedHardLinkRoot);
    }

    private static string HashRegularFile(
        string path,
        string description,
        ref long aggregateBytes,
        string? allowedHardLinkRoot = null)
    {
        AliExecutionDirectoryLease.RequireFixedLocalVolume(path, description);
        using var stream = OpenRegularFileNoFollow(
            Path.GetFullPath(path),
            description + " is not a regular local file.");
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            path,
            description + " does not have a stable file identity.",
            allowedHardLinkRoot);
        var fullFileIdentity = AliExecutionFileLease.CaptureFullFileIdentity(
            stream.SafeFileHandle,
            description + " does not have a stable full file identity.");
        var length = stream.Length;
        if (length < 0 || length > MaximumFileBytes)
        {
            throw new InvalidDataException(description + " exceeds the exact file-size bound.");
        }
        aggregateBytes = checked(aggregateBytes + length);
        if (aggregateBytes > MaximumAggregateBytes)
        {
            throw new InvalidDataException(description + " exceeds the aggregate byte bound.");
        }
        var hash = SHA256.HashData(stream);
        try
        {
            if (stream.Position != length || stream.Length != length)
            {
                throw new IOException(description + " changed while it was hashed.");
            }
            var repeatedIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                path,
                description + " does not have a stable file identity.",
                allowedHardLinkRoot);
            var repeatedFullIdentity = AliExecutionFileLease.CaptureFullFileIdentity(
                stream.SafeFileHandle,
                description + " does not have a stable full file identity.");
            if (!string.Equals(
                    fileIdentity.CanonicalIdentity,
                    repeatedIdentity.CanonicalIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    fullFileIdentity,
                    repeatedFullIdentity,
                    StringComparison.Ordinal))
            {
                throw new IOException(description + " changed file identity while it was hashed.");
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                    "\0",
                    fileIdentity.CanonicalIdentity,
                    fullFileIdentity,
                    Convert.ToHexString(hash).ToLowerInvariant()))))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static void RejectSpecial(FileAttributes attributes, string description)
    {
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(description + " contains a reparse point or device entry.");
        }
    }

    internal static FileStream OpenRegularFileNoFollow(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        ValidateRegularDirectoryNoFollow(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(description),
            description);
        RejectSpecial(File.GetAttributes(fullPath), description);

        if (!OperatingSystem.IsWindows())
        {
            var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                RejectSpecial(File.GetAttributes(stream.SafeFileHandle), description);
                ValidateRegularDirectoryNoFollow(
                    Path.GetDirectoryName(fullPath)!,
                    description);
                RejectSpecial(File.GetAttributes(fullPath), description);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        var handle = CreateFileW(
            ToExtendedLengthWin32Path(fullPath),
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagOverlapped | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(description, new Win32Exception(error));
        }
        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException(description);
            }
            RejectSpecial(attributes, description);
            ValidateRegularDirectoryNoFollow(
                Path.GetDirectoryName(fullPath)!,
                description);
            RejectSpecial(File.GetAttributes(fullPath), description);
            return new FileStream(handle, FileAccess.Read, 4096, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RequireRegularDirectory(string directory, string description)
    {
        var attributes = File.GetAttributes(directory);
        RejectSpecial(attributes, description);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(description);
        }
    }

    private static string ToExtendedLengthWin32Path(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Finish(IncrementalHash hash)
    {
        var bytes = hash.GetHashAndReset();
        try
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>
/// Fingerprints only the bounded layout and metadata of generated .ali output. It intentionally
/// does not treat potentially large generated bytes as semantic source input, while every entry
/// is still inspected and any link/device below the output root is rejected.
/// </summary>
internal static class AliGeneratedOutputLayoutFingerprint
{
    private const int MaximumEntries = 50_000;
    private const int MaximumDepth = 64;

    internal static string Capture(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
        {
            return "project-root-absent";
        }
        AliCodingExecutionAssetFingerprint.ValidateRegularDirectoryNoFollow(
            root,
            "The coding project root is not a regular local directory.");
        return CaptureDirectoryLayout(
            Path.Combine(root, ".ali"),
            "The generated .ali output layout");
    }

    internal static string CaptureDirectoryLayout(string directory, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var fullPath = Path.GetFullPath(directory);
        var first = CaptureOnce(fullPath, description);
        var second = CaptureOnce(fullPath, description);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new IOException(description + " changed while it was captured.");
        }
        return second;
    }

    private static string CaptureOnce(string outputRoot, string description)
    {
        if (!Directory.Exists(outputRoot) && !File.Exists(outputRoot))
        {
            return "absent";
        }

        var rootAttributes = File.GetAttributes(outputRoot);
        RejectSpecial(rootAttributes);
        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(description + " is not a directory.");
        }
        AliCodingExecutionAssetFingerprint.ValidateRegularDirectoryNoFollow(
            outputRoot,
            description + " is not a regular local directory.");

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((outputRoot, 0));
        var entriesSeen = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            AliCodingExecutionAssetFingerprint.ValidateRegularDirectoryNoFollow(
                directory,
                description + " is not a regular local directory.");
            if (depth > MaximumDepth)
            {
                throw new InvalidDataException(
                    "The generated .ali output layout exceeds the directory-depth bound.");
            }
            var entries = Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var children = new List<string>();
            foreach (var entry in entries)
            {
                entriesSeen = checked(entriesSeen + 1);
                if (entriesSeen > MaximumEntries)
                {
                    throw new InvalidDataException(
                        "The generated .ali output layout exceeds the entry-count bound.");
                }
                var attributes = File.GetAttributes(entry);
                RejectSpecial(attributes);
                var relative = Path.GetRelativePath(outputRoot, entry).Replace('\\', '/');
                Append(aggregate, relative);
                Append(
                    aggregate,
                    ((attributes & FileAttributes.Directory) != 0) ? "directory" : "file");
                Append(
                    aggregate,
                    File.GetLastWriteTimeUtc(entry).Ticks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    children.Add(entry);
                }
                else
                {
                    Append(
                        aggregate,
                        new FileInfo(entry).Length.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push((children[index], depth + 1));
            }
        }
        Append(aggregate, entriesSeen.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var bytes = aggregate.GetHashAndReset();
        try
        {
            return "layout:sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void RejectSpecial(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                "The generated .ali output layout contains a reparse point or device entry.");
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
