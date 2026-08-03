using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.Evidence;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Coding.Execution;

/// <summary>
/// Exact no-follow identity for every existing directory component from a volume root to one
/// selected execution root. This is structural state only; it cannot select an operation.
/// </summary>
internal sealed record AliExecutionDirectoryBinding(
    IReadOnlyList<AliExecutionDirectoryEntry> Entries,
    string Identity)
{
    internal string TargetPath => Entries.Count == 0
        ? throw new InvalidDataException("The exact execution directory spine is empty.")
        : Entries[^1].PhysicalPath;

    internal static AliExecutionDirectoryBinding Capture(string path, string description) =>
        AliExecutionDirectoryLease.Capture(path, description);

    internal static AliExecutionDirectoryBinding CaptureExistingAncestor(
        string path,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        while (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current)
                ?? throw new DirectoryNotFoundException(
                    description + " has no existing authenticated parent.");
        }
        return Capture(current, description);
    }

    internal AliExecutionDirectoryLease Acquire(string description) =>
        AliExecutionDirectoryLease.Acquire(this, description);

    internal void AddTo(IDictionary<string, string> values, string prefix)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        values[prefix + ".identity"] = Identity;
        values[prefix + ".count"] = Entries.Count.ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < Entries.Count; index++)
        {
            values[$"{prefix}.{index}.path"] = Entries[index].PhysicalPath;
            values[$"{prefix}.{index}.identity"] = Entries[index].Identity;
        }
    }
}

internal sealed record AliExecutionDirectoryEntry(string PhysicalPath, string Identity);

/// <summary>
/// Holds every directory in an authenticated spine open without delete sharing. Existing child
/// files remain writable, while replacing or renaming the selected root chain fails closed.
/// </summary>
internal sealed class AliExecutionDirectoryLease : IDisposable
{
    private readonly AliExecutionDirectoryBinding _expected;
    private readonly string _description;
    private readonly List<SafeFileHandle> _handles;
    private bool _disposed;

    private AliExecutionDirectoryLease(
        AliExecutionDirectoryBinding expected,
        string description,
        List<SafeFileHandle> handles)
    {
        _expected = expected;
        _description = description;
        _handles = handles;
    }

    internal static AliExecutionDirectoryBinding Capture(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var paths = BuildSpine(path, description);
        if (!OperatingSystem.IsWindows())
        {
            var portable = paths
                .Select(current => CapturePortableEntry(current, description))
                .ToArray();
            return CreateBinding(portable);
        }

        var handles = new List<SafeFileHandle>(paths.Count);
        try
        {
            var entries = new List<AliExecutionDirectoryEntry>(paths.Count);
            foreach (var current in paths)
            {
                var handle = OpenDirectory(current, description);
                handles.Add(handle);
                entries.Add(new AliExecutionDirectoryEntry(current, CaptureIdentity(handle, description)));
            }
            var binding = CreateBinding(entries);
            RequirePathsMatch(binding, handles, description);
            return binding;
        }
        finally
        {
            DisposeReverse(handles);
        }
    }

    internal static AliExecutionDirectoryLease Acquire(
        AliExecutionDirectoryBinding expected,
        string description)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ValidateBindingShape(expected, description);
        if (!OperatingSystem.IsWindows())
        {
            var current = Capture(expected.TargetPath, description);
            RequireBinding(expected, current, description);
            return new AliExecutionDirectoryLease(expected, description, []);
        }

        var handles = new List<SafeFileHandle>(expected.Entries.Count);
        try
        {
            foreach (var entry in expected.Entries)
            {
                var handle = OpenDirectory(entry.PhysicalPath, description);
                handles.Add(handle);
                RequireIdentity(handle, entry.Identity, description);
            }
            var lease = new AliExecutionDirectoryLease(expected, description, handles);
            lease.RequireStable();
            return lease;
        }
        catch
        {
            DisposeReverse(handles);
            throw;
        }
    }

    internal void RequireStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            RequireBinding(
                _expected,
                Capture(_expected.TargetPath, _description),
                _description);
            return;
        }
        if (_handles.Count != _expected.Entries.Count)
        {
            throw new IOException(_description + " no longer has its complete held directory spine.");
        }
        for (var index = 0; index < _handles.Count; index++)
        {
            RequireIdentity(
                _handles[index],
                _expected.Entries[index].Identity,
                _description);
        }
        RequirePathsMatch(_expected, _handles, _description);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DisposeReverse(_handles);
    }

    private static void RequirePathsMatch(
        AliExecutionDirectoryBinding expected,
        IReadOnlyList<SafeFileHandle> heldHandles,
        string description)
    {
        for (var index = 0; index < expected.Entries.Count; index++)
        {
            RequireIdentity(heldHandles[index], expected.Entries[index].Identity, description);
            using var current = OpenDirectory(expected.Entries[index].PhysicalPath, description);
            RequireIdentity(current, expected.Entries[index].Identity, description);
        }
    }

    private static AliExecutionDirectoryBinding CreateBinding(
        IReadOnlyList<AliExecutionDirectoryEntry> entries)
    {
        var material = string.Join(
            "\0",
            entries.SelectMany(entry => new[] { Normalize(entry.PhysicalPath), entry.Identity }));
        return new AliExecutionDirectoryBinding(
            Array.AsReadOnly(entries.ToArray()),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
                .ToLowerInvariant());
    }

    private static void RequireBinding(
        AliExecutionDirectoryBinding expected,
        AliExecutionDirectoryBinding current,
        string description)
    {
        if (!string.Equals(expected.Identity, current.Identity, StringComparison.Ordinal))
        {
            throw new IOException(description + " changed exact directory identity before execution.");
        }
    }

    private static void ValidateBindingShape(
        AliExecutionDirectoryBinding binding,
        string description)
    {
        if (binding.Entries.Count == 0)
        {
            throw new InvalidDataException(description + " has an empty directory spine.");
        }
        var expectedPaths = BuildSpine(binding.TargetPath, description);
        if (expectedPaths.Count != binding.Entries.Count)
        {
            throw new InvalidDataException(description + " is not a complete directory spine.");
        }
        for (var index = 0; index < expectedPaths.Count; index++)
        {
            if (!string.Equals(
                    Normalize(expectedPaths[index]),
                    Normalize(binding.Entries[index].PhysicalPath),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new InvalidDataException(description + " is not one contiguous directory spine.");
            }
        }
        var rebuilt = CreateBinding(binding.Entries);
        RequireBinding(binding, rebuilt, description);
    }

    private static IReadOnlyList<string> BuildSpine(string path, string description)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RequireFixedLocalVolume(target, description);
        var root = Path.GetPathRoot(target)
            ?? throw new InvalidDataException(description + " has no filesystem root.");
        var result = new List<string> { Path.GetFullPath(root) };
        var relative = Path.GetRelativePath(root, target);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return result;
        }
        var current = root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
            {
                throw new InvalidDataException(description + " contains a non-canonical directory component.");
            }
            current = Path.GetFullPath(Path.Combine(current, component));
            result.Add(current);
        }
        return result;
    }

    internal static void RequireFixedLocalVolume(string path, string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new InvalidDataException(description + " has no filesystem root.");
        if (GetDriveTypeW(root) != DriveFixed)
        {
            throw new InvalidDataException(
                description + " is not on a supported fixed local Windows volume.");
        }
    }

    private static AliExecutionDirectoryEntry CapturePortableEntry(
        string path,
        string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(description + " is not a regular local directory.");
        }
        var info = new DirectoryInfo(path);
        var material = string.Join(
            "\0",
            Normalize(path),
            info.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        return new AliExecutionDirectoryEntry(
            Path.GetFullPath(path),
            "directory:portable:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
                .ToLowerInvariant());
    }

    private static SafeFileHandle OpenDirectory(string path, string description)
    {
        var handle = CreateFileW(
            ToExtendedLengthPath(path),
            FileReadAttributes,
            FileShare.ReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(description, new Win32Exception(error));
        }
        try
        {
            ValidateDirectory(handle, description);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string CaptureIdentity(SafeFileHandle handle, string description)
    {
        ValidateDirectory(handle, description);
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation information,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            throw new IOException(description, new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return "directory:"
               + information.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture)
               + ":"
               + information.FileId.High.ToString("x16", CultureInfo.InvariantCulture)
               + information.FileId.Low.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static void RequireIdentity(
        SafeFileHandle handle,
        string expectedIdentity,
        string description)
    {
        if (!string.Equals(
                CaptureIdentity(handle, description),
                expectedIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(description + " changed exact directory identity before execution.");
        }
    }

    private static void ValidateDirectory(SafeFileHandle handle, string description)
    {
        if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk)
        {
            throw new InvalidDataException(description + " is not a local disk directory.");
        }
        var attributes = File.GetAttributes(handle);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(description + " is not a regular no-follow directory.");
        }
    }

    private static string Normalize(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string ToExtendedLengthPath(string path)
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

    private static void DisposeReverse(IReadOnlyList<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 1;
    private const uint DriveFixed = 3;
    private const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetDriveTypeW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string rootPathName);
}

internal sealed class AliExecutionDirectoryLeaseGroup : IDisposable
{
    private readonly List<AliExecutionDirectoryLease> _leases;
    private bool _disposed;

    private AliExecutionDirectoryLeaseGroup(List<AliExecutionDirectoryLease> leases) =>
        _leases = leases;

    internal static AliExecutionDirectoryLeaseGroup Acquire(
        IEnumerable<string> paths,
        string description)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var leases = new List<AliExecutionDirectoryLease>();
        try
        {
            foreach (var path in paths
                         .Select(Path.GetFullPath)
                         .Distinct(comparer)
                         .Order(comparer))
            {
                var binding = AliExecutionDirectoryBinding.Capture(path, description);
                leases.Add(binding.Acquire(description));
            }
            return new AliExecutionDirectoryLeaseGroup(leases);
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
/// Holds a validated executable or application artifact open without write/delete sharing and
/// revalidates both the held object and the pathname immediately around process creation. .NET's
/// Process.Start/CreateProcess surface still consumes a pathname rather than this file handle;
/// the sharing lease closes replacement, and post-start image inspection proves what Windows ran.
/// </summary>
internal sealed class AliExecutionFileLease : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;
    private readonly string _description;
    private readonly string? _allowedHardLinkRoot;
    private readonly string _heldIdentity;
    private readonly Action _validateExpectedIdentity;
    private bool _disposed;

    private AliExecutionFileLease(
        FileStream stream,
        string path,
        string description,
        string? allowedHardLinkRoot,
        string heldIdentity,
        Action validateExpectedIdentity)
    {
        _stream = stream;
        _path = path;
        _description = description;
        _allowedHardLinkRoot = allowedHardLinkRoot;
        _heldIdentity = heldIdentity;
        _validateExpectedIdentity = validateExpectedIdentity;
    }

    internal static AliExecutionFileLease Acquire(
        AliBoundExecutionFile expected,
        string description)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return Acquire(
            expected.PhysicalPath,
            description,
            allowedHardLinkRoot: null,
            () =>
            {
                var current = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                    expected.PhysicalPath,
                    description);
                if (current != expected)
                {
                    throw new InvalidOperationException(
                        description + " changed after exact authorization.");
                }
            });
    }

    internal static AliExecutionFileLease Acquire(
        string path,
        string description,
        string? allowedHardLinkRoot,
        Action validateExpectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(validateExpectedIdentity);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Exact live execution file leases require Windows FILE_ID_INFO identity support.");
        }
        var fullPath = Path.GetFullPath(path);
        AliExecutionDirectoryLease.RequireFixedLocalVolume(fullPath, description);
        var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            description);
        try
        {
            _ = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                fullPath,
                description,
                allowedHardLinkRoot);
            var heldIdentity = CaptureFullFileIdentity(stream.SafeFileHandle, description);
            var lease = new AliExecutionFileLease(
                stream,
                fullPath,
                description,
                allowedHardLinkRoot,
                heldIdentity,
                validateExpectedIdentity);
            lease.RequireStable();
            return lease;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal void RequireStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            _stream,
            _path,
            _description,
            _allowedHardLinkRoot);
        var held = CaptureFullFileIdentity(_stream.SafeFileHandle, _description);
        if (!string.Equals(held, _heldIdentity, StringComparison.Ordinal))
        {
            throw new IOException(_description + " changed held file identity before execution.");
        }
        _validateExpectedIdentity();
        using var current = WindowsOrchestrationFileBoundary.OpenRegularFile(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            _description);
        _ = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            current,
            _path,
            _description,
            _allowedHardLinkRoot);
        var pathIdentity = CaptureFullFileIdentity(current.SafeFileHandle, _description);
        if (!string.Equals(pathIdentity, _heldIdentity, StringComparison.Ordinal))
        {
            throw new IOException(_description + " pathname no longer resolves to the held file.");
        }
    }

    internal void RequireStartedProcessImage(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var imagePath = OperatingSystem.IsWindows()
            ? QueryImagePath(process)
            : process.MainModule?.FileName
              ?? throw new InvalidOperationException("The started process image could not be inspected.");
        if (!string.Equals(
                Path.GetFullPath(imagePath),
                Path.GetFullPath(_path),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The started process image does not match the exact held executable.");
        }
        RequireStable();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream.Dispose();
    }

    private static string QueryImagePath(Process process)
    {
        var capacity = 32_768;
        var value = new StringBuilder(capacity);
        if (!QueryFullProcessImageNameW(process.Handle, 0, value, ref capacity))
        {
            throw new InvalidOperationException(
                "The started process image could not be inspected.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return value.ToString();
    }

    internal static string CaptureFullFileIdentity(
        SafeFileHandle handle,
        string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Exact execution fingerprints require Windows FILE_ID_INFO identity support.");
        }
        if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk)
        {
            throw new InvalidDataException(description + " is not a local disk file.");
        }
        var attributes = File.GetAttributes(handle);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(description + " is not a regular no-follow file.");
        }
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation information,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            throw new IOException(description, new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return "file:"
               + information.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture)
               + ":"
               + information.FileId.High.ToString("x16", CultureInfo.InvariantCulture)
               + information.FileId.Low.ToString("x16", CultureInfo.InvariantCulture);
    }

    private const uint FileTypeDisk = 1;
    private const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref int size);
}

/// <summary>
/// Holds the principal artifact and every existing file in its authenticated output closure for
/// the application verification lifetime. This prevents late sidecar replacement after approval.
/// </summary>
internal sealed class AliApplicationLaunchLease : IDisposable
{
    private readonly AliBoundExecutionFile _principal;
    private readonly AliApplicationLaunchClosure _closure;
    private readonly AliExecutionDirectoryLease _directory;
    private readonly AliExecutionFileLease _principalLease;
    private readonly List<AliExecutionFileLease> _files;
    private bool _disposed;

    private AliApplicationLaunchLease(
        AliBoundExecutionFile principal,
        AliApplicationLaunchClosure closure,
        AliExecutionDirectoryLease directory,
        AliExecutionFileLease principalLease,
        List<AliExecutionFileLease> files)
    {
        _principal = principal;
        _closure = closure;
        _directory = directory;
        _principalLease = principalLease;
        _files = files;
    }

    internal static AliApplicationLaunchLease Acquire(
        AliBoundExecutionFile principal,
        AliApplicationLaunchClosure closure)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(closure);
        var directory = closure.DirectoryBinding.Acquire(
            "The exact application launch output directory spine");
        AliExecutionFileLease? principalLease = null;
        var files = new List<AliExecutionFileLease>();
        try
        {
            _ = closure.RequireStable();
            principalLease = AliExecutionFileLease.Acquire(
                principal,
                "The exact application launch principal artifact");
            foreach (var path in Directory.EnumerateFiles(
                         closure.OutputDirectoryPath,
                         "*",
                         SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(principal.PhysicalPath),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    continue;
                }
                var expected = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                    path,
                    "An application launch output file");
                files.Add(AliExecutionFileLease.Acquire(
                    expected,
                    "An exact application launch output file"));
            }
            var lease = new AliApplicationLaunchLease(
                principal,
                closure,
                directory,
                principalLease
                    ?? throw new InvalidOperationException(
                        "The exact application principal lease was not acquired."),
                files);
            lease.RequireStable();
            return lease;
        }
        catch
        {
            for (var index = files.Count - 1; index >= 0; index--)
            {
                files[index].Dispose();
            }
            principalLease?.Dispose();
            directory.Dispose();
            throw;
        }
    }

    internal void RequireStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _directory.RequireStable();
        _principalLease.RequireStable();
        foreach (var file in _files)
        {
            file.RequireStable();
        }
        var principal = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            _principal.PhysicalPath,
            "The exact application launch principal artifact");
        if (principal != _principal)
        {
            throw new InvalidOperationException(
                "The application launch principal artifact changed after exact authorization.");
        }
        _ = _closure.RequireStable();
    }

    internal void RequireStartedPrincipalProcessImage(Process process) =>
        _principalLease.RequireStartedProcessImage(process);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (var index = _files.Count - 1; index >= 0; index--)
        {
            _files[index].Dispose();
        }
        _principalLease.Dispose();
        _directory.Dispose();
    }
}
