using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Coding.Changesets;

[Flags]
internal enum AliSourceNativeFileAccess
{
    Read = 1,
    Write = 2,
    Delete = 4
}

internal sealed record AliSourceCapturedFile(
    AliSourceFileIdentity Identity,
    byte[] Bytes);

internal sealed record AliSourceDirectoryEntry(
    string Name,
    FileAttributes Attributes,
    long Length,
    ulong FileIdLow,
    ulong FileIdHigh)
{
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    internal bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
}

internal sealed class AliSourceBoundFile : IDisposable
{
    internal AliSourceBoundFile(
        string relativePath,
        SafeFileHandle handle,
        AliSourceFileIdentity identity)
    {
        RelativePath = relativePath;
        Handle = handle;
        Identity = identity;
    }

    internal string RelativePath { get; private set; }
    internal SafeFileHandle Handle { get; }
    internal AliSourceFileIdentity Identity { get; private set; }

    internal void RebindName(string relativePath, AliSourceFileIdentity identity)
    {
        RelativePath = relativePath;
        Identity = identity;
    }

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// Holds the approved source root and every existing namespace spine without delete sharing.
/// Every leaf is opened relative to its already-held parent, with reparse traversal disabled.
/// Canonical mutations are issued only against exact file handles.
/// </summary>
internal sealed class AliSourceWindowsNamespace : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileTypeDisk = 0x0001;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileAttributeNormal = 0x00000080;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileIdInfoClass = 18;
    private const int FileStandardInfoClass = 1;
    private const int FileRenameInformationClass = 10;
    private const int FileDispositionInfoClass = 4;
    private const int FileIdExtdDirectoryInfoClass = 19;
    private const int FileIdExtdDirectoryRestartInfoClass = 20;
    private const int ErrorNoMoreFiles = 18;
    private const int DirectoryEntryFixedBytes = 88;
    private const int DirectoryQueryBufferBytes = 64 * 1024;

    private readonly string _rootFinalPath;
    private readonly SafeFileHandle _rootHandle;
    private readonly Dictionary<string, BoundDirectory> _directories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _directoryObjects =
        new(StringComparer.Ordinal);
    private bool _disposed;

    private AliSourceWindowsNamespace(
        SafeFileHandle rootHandle,
        AliSourceFileIdentity rootIdentity)
    {
        _rootFinalPath = GetFinalName(
            rootHandle,
            "The approved source root final name is unavailable.");
        _rootHandle = rootHandle;
        RootIdentity = rootIdentity;
        _directories.Add(string.Empty, new BoundDirectory(string.Empty, rootHandle, rootIdentity, OwnsHandle: false));
        _directoryObjects.Add(rootIdentity.ObjectKey, string.Empty);
    }

    internal AliSourceFileIdentity RootIdentity { get; }
    internal string RootFinalPath => _rootFinalPath;

    internal ImmutableArray<AliSourceDirectoryEntry> EnumerateDirectory(
        string relativeDirectoryPath)
    {
        ThrowIfDisposed();
        var directory = string.IsNullOrEmpty(relativeDirectoryPath)
            ? _directories[string.Empty]
            : EnsureDirectory(relativeDirectoryPath);
        return EnumerateDirectoryHandle(directory.Handle);
    }

    internal void HoldEnumeratedDirectory(
        string relativeDirectoryPath,
        AliSourceDirectoryEntry expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.IsDirectory || expected.IsReparsePoint)
        {
            throw new InvalidDataException("An enumerated source directory is not an ordinary directory.");
        }
        var directory = EnsureDirectory(relativeDirectoryPath);
        RequireEnumeratedObject(directory.Identity, expected, "A source directory changed after enumeration.");
    }

    internal AliSourceBoundFile OpenEnumeratedFileForRead(
        string relativePath,
        AliSourceDirectoryEntry expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.IsDirectory || expected.IsReparsePoint || expected.Length < 0)
        {
            throw new InvalidDataException("An enumerated source file is not an ordinary file.");
        }
        var file = TryOpenExisting(
            relativePath,
            AliSourceNativeFileAccess.Read,
            expectedIdentity: null,
            requireExactFinalName: false)
            ?? throw new FileNotFoundException("An enumerated source file disappeared.", relativePath);
        try
        {
            RequireEnumeratedObject(file.Identity, expected, "A source file changed after enumeration.");
            if (RandomAccess.GetLength(file.Handle) != expected.Length)
            {
                throw new InvalidDataException("A source file length changed after enumeration.");
            }
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal void CreateDirectory(string relativeDirectoryPath)
    {
        ThrowIfDisposed();
        var normalized = AliSourceChangeSetStore.NormalizeRelativePath(relativeDirectoryPath);
        if (_directories.ContainsKey(normalized))
        {
            throw new IOException("A staged directory already exists.");
        }
        var parent = ParentOf(normalized);
        var handle = OpenRelativeDirectory(parent.Handle, LeafOf(normalized), createNew: true);
        try
        {
            var identity = CaptureIdentity(
                handle,
                requireDirectory: true,
                "A newly-created staged directory is invalid.");
            RegisterDirectory(normalized, handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal async Task<long> CopyExactAsync(
        AliSourceBoundFile source,
        AliSourceBoundFile destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        var length = RandomAccess.GetLength(source.Handle);
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidOperationException("A staged verification tree exceeds its byte bound.");
        }

        RandomAccess.SetLength(destination.Handle, 0);
        var buffer = new byte[128 * 1024];
        try
        {
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = checked((int)Math.Min(buffer.Length, length - offset));
                var read = await RandomAccess.ReadAsync(
                        source.Handle,
                        buffer.AsMemory(0, requested),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("A source file changed while it was copied.");
                }
                await RandomAccess.WriteAsync(
                        destination.Handle,
                        buffer.AsMemory(0, read),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                offset += read;
            }
            if (RandomAccess.GetLength(source.Handle) != length)
            {
                throw new InvalidDataException("A source file length changed while it was copied.");
            }
            RandomAccess.SetLength(destination.Handle, length);
            RandomAccess.FlushToDisk(destination.Handle);
            destination.RebindName(
                destination.RelativePath,
                CaptureIdentity(
                    destination.Handle,
                    requireDirectory: false,
                    "A copied staged file is invalid."));
            return length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal ImmutableArray<AliSourceNamespaceSpineIdentity> CapturedSpines =>
        _directories.Values
            .Where(directory => directory.RelativePath.Length > 0)
            .OrderBy(directory => directory.RelativePath.Count(character => character == '/'))
            .ThenBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(directory => new AliSourceNamespaceSpineIdentity(
                directory.RelativePath,
                directory.Identity))
            .ToImmutableArray();

    internal static AliSourceWindowsNamespace Capture(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var rootHandle = OpenAbsoluteDirectory(canonicalRoot);
        try
        {
            var identity = CaptureIdentity(rootHandle, requireDirectory: true, "The approved source root is invalid.");
            return new AliSourceWindowsNamespace(rootHandle, identity);
        }
        catch
        {
            rootHandle.Dispose();
            throw;
        }
    }

    internal static AliSourceWindowsNamespace OpenAuthenticated(AliSourceChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var result = Capture(changeSet.CanonicalSourceRoot);
        try
        {
            if (!changeSet.SourceRootIdentity.MatchesExactNamespace(result.RootIdentity))
            {
                throw new InvalidDataException(
                    "The authenticated source root FILE_ID_INFO binding changed.");
            }

            foreach (var expected in changeSet.NamespaceSpines)
            {
                var actual = result.EnsureDirectory(expected.RelativeDirectoryPath);
                if (!expected.Identity.MatchesExactNamespace(actual.Identity))
                {
                    throw new InvalidDataException(
                        $"The authenticated source namespace spine changed: {expected.RelativeDirectoryPath}");
                }
            }

            if (result._directories.Count != changeSet.NamespaceSpines.Length + 1)
            {
                throw new InvalidDataException("The authenticated source namespace spine is incomplete.");
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal void EnsureParentOf(string relativePath)
    {
        _ = ParentOf(relativePath);
    }

    internal async Task<AliSourceCapturedFile?> CaptureOptionalFileAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var file = TryOpenExisting(
            relativePath,
            AliSourceNativeFileAccess.Read,
            expectedIdentity: null,
            requireExactFinalName: false);
        if (file is null)
        {
            return null;
        }
        RequireBoundAt(file, relativePath);
        if (file.Identity.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "A source changeset leaf with a hard-link alias is not accepted.");
        }
        var bytes = await ReadBoundedAsync(file.Handle, cancellationToken).ConfigureAwait(false);
        return new AliSourceCapturedFile(file.Identity, bytes);
    }

    internal AliSourceBoundFile? TryOpenExisting(
        string relativePath,
        AliSourceNativeFileAccess access,
        AliSourceFileIdentity? expectedIdentity,
        bool requireExactFinalName = true)
    {
        ThrowIfDisposed();
        var normalized = AliSourceChangeSetStore.NormalizeRelativePath(relativePath);
        var parent = ParentOf(normalized);
        var handle = OpenRelativeFile(
            parent.Handle,
            LeafOf(normalized),
            access,
            createNew: false,
            allowMissing: true);
        if (handle is null)
        {
            return null;
        }
        try
        {
            var identity = CaptureIdentity(handle, requireDirectory: false, "A source leaf is invalid.");
            if (identity.NumberOfLinks != 1)
            {
                throw new InvalidDataException(
                    "A source publication leaf with a hard-link alias is not accepted.");
            }
            if (expectedIdentity is not null
                && !(requireExactFinalName
                    ? expectedIdentity.MatchesExactNamespace(identity)
                    : expectedIdentity.SameObject(identity)))
            {
                throw new InvalidDataException(
                    "A source leaf FILE_ID_INFO binding is missing, rebound, or ambiguous.");
            }
            return new AliSourceBoundFile(normalized, handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal AliSourceBoundFile CreateNew(string relativePath)
    {
        ThrowIfDisposed();
        var normalized = AliSourceChangeSetStore.NormalizeRelativePath(relativePath);
        var parent = ParentOf(normalized);
        var handle = OpenRelativeFile(
            parent.Handle,
            LeafOf(normalized),
            AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Write | AliSourceNativeFileAccess.Delete,
            createNew: true,
            allowMissing: false)
            ?? throw new IOException("A new source leaf could not be created.");
        try
        {
            var identity = CaptureIdentity(handle, requireDirectory: false, "A new source leaf is invalid.");
            if (identity.NumberOfLinks != 1)
            {
                throw new InvalidDataException("A newly-created source leaf has an ambiguous link topology.");
            }
            return new AliSourceBoundFile(normalized, handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal async Task<AliSourceFileImage> CaptureImageAsync(
        string relativePath,
        AliSourceFileIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        using var file = TryOpenExisting(
            relativePath,
            AliSourceNativeFileAccess.Read,
            expectedIdentity,
            requireExactFinalName: true);
        if (file is null)
        {
            return AliSourceFileImage.Absent;
        }
        var bytes = await ReadBoundedAsync(file.Handle, cancellationToken).ConfigureAwait(false);
        try
        {
            return AliSourceFileImage.FromBytes(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal async Task<AliSourceFileImage> CaptureImageAsync(
        AliSourceBoundFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var bytes = await ReadBoundedAsync(file.Handle, cancellationToken).ConfigureAwait(false);
        try
        {
            return AliSourceFileImage.FromBytes(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal bool IsAbsent(string relativePath)
    {
        using var file = TryOpenExisting(
            relativePath,
            AliSourceNativeFileAccess.Read,
            expectedIdentity: null,
            requireExactFinalName: false);
        return file is null;
    }

    internal void RequireBoundAt(AliSourceBoundFile file, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(file);
        var normalized = AliSourceChangeSetStore.NormalizeRelativePath(relativePath);
        var parent = ParentOf(normalized);
        var expected = Path.GetFullPath(Path.Combine(
            GetFinalName(parent.Handle, "A source parent final name is unavailable."),
            LeafOf(normalized)));
        var actual = GetFinalName(file.Handle, "A source leaf final name is unavailable.");
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An exact source object is bound under an unexpected namespace name.");
        }
    }

    internal bool IsBoundAt(AliSourceBoundFile file, string relativePath)
    {
        try
        {
            RequireBoundAt(file, relativePath);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal async Task WriteExactAsync(
        AliSourceBoundFile file,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (content.Length > AliSourceChangeSetStore.MaximumFileBytes)
        {
            throw new InvalidDataException("A source publication payload is unbounded.");
        }
        RandomAccess.SetLength(file.Handle, 0);
        if (!content.IsEmpty)
        {
            await RandomAccess.WriteAsync(file.Handle, content, 0, cancellationToken).ConfigureAwait(false);
        }
        RandomAccess.SetLength(file.Handle, content.Length);
        RandomAccess.FlushToDisk(file.Handle);
        file.RebindName(
            file.RelativePath,
            CaptureIdentity(file.Handle, requireDirectory: false, "A written source leaf is invalid."));
    }

    internal AliSourceFileIdentity Rename(
        AliSourceBoundFile file,
        string destinationRelativePath)
    {
        ArgumentNullException.ThrowIfNull(file);
        var destination = AliSourceChangeSetStore.NormalizeRelativePath(destinationRelativePath);
        var parent = ParentOf(destination);
        if (file.Identity.VolumeSerialNumber != parent.Identity.VolumeSerialNumber)
        {
            throw new InvalidOperationException("A source rename must remain on its authenticated volume.");
        }

        RenameByHandle(file.Handle, parent.Handle, LeafOf(destination), replaceExisting: false);
        var identity = CaptureIdentity(file.Handle, requireDirectory: false, "A renamed source leaf is invalid.");
        if (!file.Identity.SameObject(identity))
        {
            throw new InvalidDataException("A source rename changed physical object identity.");
        }
        file.RebindName(destination, identity);
        return identity;
    }

    internal void Delete(AliSourceBoundFile file) => SetDeleteDisposition(file, delete: true);

    internal void CancelDelete(AliSourceBoundFile file) => SetDeleteDisposition(file, delete: false);

    internal bool IsDeletePending(AliSourceBoundFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!GetFileInformationByHandleEx(
                file.Handle,
                FileStandardInfoClass,
                out FileStandardInformation information,
                (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            ThrowIo("The exact source deletion state could not be read.", Marshal.GetLastWin32Error());
        }
        return information.DeletePending != 0 && information.Directory == 0;
    }

    private static void SetDeleteDisposition(AliSourceBoundFile file, bool delete)
    {
        ArgumentNullException.ThrowIfNull(file);
        Span<byte> disposition = stackalloc byte[1];
        disposition[0] = delete ? (byte)1 : (byte)0;
        unsafe
        {
            fixed (byte* pointer = disposition)
            {
                if (!SetFileInformationByHandle(
                        file.Handle,
                        FileDispositionInfoClass,
                        (IntPtr)pointer,
                        1))
                {
                    ThrowIo("The exact source leaf could not be deleted.", Marshal.GetLastWin32Error());
                }
            }
        }
    }

    internal static bool SamePhysicalObject(
        AliSourceFileIdentity left,
        AliSourceFileIdentity right) => left.SameObject(right);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var directory in _directories.Values
                     .Where(directory => directory.OwnsHandle)
                     .Reverse())
        {
            directory.Handle.Dispose();
        }
        _rootHandle.Dispose();
        _directories.Clear();
        _directoryObjects.Clear();
    }

    private BoundDirectory ParentOf(string relativePath)
    {
        var normalized = AliSourceChangeSetStore.NormalizeRelativePath(relativePath);
        var segments = normalized.Split('/');
        var current = _directories[string.Empty];
        var currentRelative = string.Empty;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            currentRelative = currentRelative.Length == 0
                ? segments[index]
                : currentRelative + "/" + segments[index];
            if (_directories.TryGetValue(currentRelative, out var existing))
            {
                current = existing;
                continue;
            }

            var handle = OpenRelativeDirectory(current.Handle, segments[index]);
            try
            {
                RequireExactChildName(
                    current.Handle,
                    segments[index],
                    handle,
                    "A source namespace spine entered through a non-canonical alias.");
                var identity = CaptureIdentity(
                    handle,
                    requireDirectory: true,
                    "A source namespace spine is invalid.");
                if (_directoryObjects.TryGetValue(identity.ObjectKey, out var alias)
                    && !string.Equals(alias, currentRelative, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Two source namespace paths resolve to the same physical directory.");
                }
                current = RegisterDirectory(currentRelative, handle, identity);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        return current;
    }

    private BoundDirectory EnsureDirectory(string relativeDirectoryPath)
    {
        var normalized = AliSourceChangeSetStore.NormalizeRelativePath(relativeDirectoryPath);
        _ = ParentOf(normalized + "/.ali-spine-probe");
        return _directories[normalized];
    }

    private BoundDirectory RegisterDirectory(
        string relativeDirectoryPath,
        SafeFileHandle handle,
        AliSourceFileIdentity identity)
    {
        if (_directoryObjects.TryGetValue(identity.ObjectKey, out var alias)
            && !string.Equals(alias, relativeDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Two source namespace paths resolve to the same physical directory.");
        }
        var directory = new BoundDirectory(
            relativeDirectoryPath,
            handle,
            identity,
            OwnsHandle: true);
        _directories.Add(relativeDirectoryPath, directory);
        _directoryObjects.Add(identity.ObjectKey, relativeDirectoryPath);
        return directory;
    }

    private static string LeafOf(string normalizedRelativePath)
    {
        var separator = normalizedRelativePath.LastIndexOf('/');
        return separator < 0 ? normalizedRelativePath : normalizedRelativePath[(separator + 1)..];
    }

    private static void RequireExactChildName(
        SafeFileHandle parent,
        string leaf,
        SafeFileHandle child,
        string invalidMessage)
    {
        var expected = Path.GetFullPath(Path.Combine(
            GetFinalName(parent, invalidMessage),
            leaf));
        var actual = GetFinalName(child, invalidMessage);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(invalidMessage);
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectory(string path)
    {
        var handle = CreateFileW(
            ToExtendedLengthPath(path),
            FileListDirectory | FileReadAttributes | Synchronize,
            FileShare.ReadWrite,
            IntPtr.Zero,
            3,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            ThrowIo("The approved source root could not be opened without following reparse points.", Marshal.GetLastWin32Error(), handle);
        }
        ValidateHandleKind(handle, requireDirectory: true, "The approved source root is invalid.");
        return handle;
    }

    private static SafeFileHandle OpenRelativeDirectory(
        SafeFileHandle parent,
        string leaf,
        bool createNew = false) =>
        OpenRelative(
            parent,
            leaf,
            FileListDirectory | FileReadAttributes | Synchronize,
            FileShare.ReadWrite,
            createNew ? FileCreate : FileOpen,
            FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert,
            allowMissing: false)
        ?? throw new DirectoryNotFoundException("A source namespace spine is missing.");

    private static SafeFileHandle? OpenRelativeFile(
        SafeFileHandle parent,
        string leaf,
        AliSourceNativeFileAccess access,
        bool createNew,
        bool allowMissing)
    {
        var desired = FileReadAttributes | Synchronize;
        if ((access & AliSourceNativeFileAccess.Read) != 0)
        {
            desired |= GenericRead;
        }
        if ((access & AliSourceNativeFileAccess.Write) != 0)
        {
            desired |= GenericWrite;
        }
        if ((access & AliSourceNativeFileAccess.Delete) != 0)
        {
            desired |= DeleteAccess;
        }
        var handle = OpenRelative(
            parent,
            leaf,
            desired,
            FileShare.Read,
            createNew ? FileCreate : FileOpen,
            FileNonDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert,
            allowMissing);
        if (handle is not null)
        {
            ValidateHandleKind(handle, requireDirectory: false, "A source leaf is invalid.");
        }
        return handle;
    }

    private static SafeFileHandle? OpenRelative(
        SafeFileHandle parent,
        string leaf,
        uint desiredAccess,
        FileShare share,
        uint disposition,
        uint options,
        bool allowMissing)
    {
        if (string.IsNullOrWhiteSpace(leaf)
            || leaf.Contains('/')
            || leaf.Contains('\\')
            || leaf.Contains(':'))
        {
            throw new InvalidDataException("A native source leaf name is invalid.");
        }

        var buffer = Marshal.StringToHGlobalUni(leaf);
        var unicodePointer = IntPtr.Zero;
        var addedRef = false;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(leaf.Length * sizeof(char))),
                MaximumLength = checked((ushort)((leaf.Length + 1) * sizeof(char))),
                Buffer = buffer
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, fDeleteOld: false);
            parent.DangerousAddRef(ref addedRef);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjectCaseInsensitive
            };
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                share,
                disposition,
                options,
                IntPtr.Zero,
                0);
            if (status >= 0 && !handle.IsInvalid)
            {
                return handle;
            }

            handle?.Dispose();
            var error = unchecked((int)RtlNtStatusToDosError(status));
            if (allowMissing && error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }
            ThrowIo("A source namespace leaf could not be opened relative to its held parent.", error);
            return null;
        }
        finally
        {
            if (addedRef)
            {
                parent.DangerousRelease();
            }
            if (unicodePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodePointer);
            }
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < 0 || length > AliSourceChangeSetStore.MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"A source file cannot exceed {AliSourceChangeSetStore.MaximumFileBytes} bytes.");
        }
        var bytes = new byte[checked((int)length)];
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await RandomAccess.ReadAsync(
                        handle,
                        bytes.AsMemory(offset),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("A source file changed while it was read.");
                }
                offset += read;
            }
            if (RandomAccess.GetLength(handle) != length)
            {
                throw new InvalidDataException("A source file changed while its exact image was captured.");
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static ImmutableArray<AliSourceDirectoryEntry> EnumerateDirectoryHandle(
        SafeFileHandle directory)
    {
        var entries = ImmutableArray.CreateBuilder<AliSourceDirectoryEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = Marshal.AllocHGlobal(DirectoryQueryBufferBytes);
        try
        {
            var restart = true;
            while (true)
            {
                if (!GetFileInformationByHandleEx(
                        directory,
                        restart
                            ? FileIdExtdDirectoryRestartInfoClass
                            : FileIdExtdDirectoryInfoClass,
                        buffer,
                        DirectoryQueryBufferBytes))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles)
                    {
                        break;
                    }
                    ThrowIo("A source directory could not be enumerated through its exact handle.", error);
                }
                restart = false;

                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > DirectoryQueryBufferBytes - DirectoryEntryFixedBytes)
                    {
                        throw new InvalidDataException("A native source directory entry is malformed.");
                    }
                    var entry = buffer + offset;
                    var nextOffset = Marshal.ReadInt32(entry, 0);
                    var length = Marshal.ReadInt64(entry, 40);
                    var attributes = (FileAttributes)unchecked((uint)Marshal.ReadInt32(entry, 56));
                    var fileNameBytes = unchecked((uint)Marshal.ReadInt32(entry, 60));
                    var fileIdLow = unchecked((ulong)Marshal.ReadInt64(entry, 72));
                    var fileIdHigh = unchecked((ulong)Marshal.ReadInt64(entry, 80));
                    if ((fileNameBytes & 1) != 0
                        || fileNameBytes > (uint)(
                            DirectoryQueryBufferBytes - offset - DirectoryEntryFixedBytes))
                    {
                        throw new InvalidDataException("A native source directory name is malformed.");
                    }
                    var fileName = Marshal.PtrToStringUni(
                        entry + DirectoryEntryFixedBytes,
                        checked((int)fileNameBytes / sizeof(char)))
                        ?? throw new InvalidDataException("A native source directory name is missing.");
                    if (fileName is not ("." or ".."))
                    {
                        var normalizedName = AliSourceChangeSetStore.NormalizeRelativePath(fileName);
                        if (!string.Equals(normalizedName, fileName, StringComparison.Ordinal)
                            || !names.Add(fileName))
                        {
                            throw new InvalidDataException(
                                "A source directory contains an ambiguous native namespace name.");
                        }
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if (!isDirectory && length < 0)
                        {
                            throw new InvalidDataException("A native source file length is invalid.");
                        }
                        entries.Add(new AliSourceDirectoryEntry(
                            fileName,
                            attributes,
                            isDirectory ? 0 : length,
                            fileIdLow,
                            fileIdHigh));
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }
                    if (nextOffset < DirectoryEntryFixedBytes
                        || nextOffset > DirectoryQueryBufferBytes - offset)
                    {
                        throw new InvalidDataException("A native source directory chain is malformed.");
                    }
                    offset = checked(offset + nextOffset);
                }
            }
            return entries
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void RequireEnumeratedObject(
        AliSourceFileIdentity actual,
        AliSourceDirectoryEntry expected,
        string message)
    {
        if (actual.VolumeSerialNumber != RootIdentity.VolumeSerialNumber
            || actual.FileIdLow != expected.FileIdLow
            || actual.FileIdHigh != expected.FileIdHigh)
        {
            throw new InvalidDataException(message);
        }
    }

    private static AliSourceFileIdentity CaptureIdentity(
        SafeFileHandle handle,
        bool requireDirectory,
        string invalidMessage)
    {
        ValidateHandleKind(handle, requireDirectory, invalidMessage);
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out FileIdInformation fileId,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            ThrowIo(invalidMessage, Marshal.GetLastWin32Error());
        }
        if (!GetFileInformationByHandle(handle, out var basic))
        {
            ThrowIo(invalidMessage, Marshal.GetLastWin32Error());
        }
        var finalName = GetFinalName(handle, invalidMessage);
        var finalBytes = Encoding.UTF8.GetBytes(finalName.ToUpperInvariant());
        try
        {
            return new AliSourceFileIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileId.Low,
                fileId.FileId.High,
                basic.NumberOfLinks,
                AliSourceChangeSetStore.Hash(finalBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(finalBytes);
        }
    }

    private static string GetFinalName(SafeFileHandle handle, string invalidMessage)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, builder, (uint)capacity, 0);
            if (length == 0)
            {
                ThrowIo(invalidMessage, Marshal.GetLastWin32Error());
            }
            if (length < capacity)
            {
                var value = builder.ToString();
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    value = @"\\" + value[8..];
                }
                else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                {
                    value = value[4..];
                }
                return Path.GetFullPath(value);
            }
            capacity = checked((int)length + 1);
        }
        throw new InvalidDataException(invalidMessage);
    }

    private static void RenameByHandle(
        SafeFileHandle file,
        SafeFileHandle destinationParent,
        string destinationLeaf,
        bool replaceExisting)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(destinationLeaf);
        var fileNameOffset = Marshal.OffsetOf<FileRenameInformationHeader>(
                nameof(FileRenameInformationHeader.FileNameLength))
            .ToInt32() + sizeof(uint);
        var totalBytes = checked(
            Marshal.SizeOf<FileRenameInformationHeader>() + fileNameBytes.Length);
        var buffer = Marshal.AllocHGlobal(totalBytes);
        var addedRef = false;
        try
        {
            Marshal.Copy(new byte[totalBytes], 0, buffer, totalBytes);
            destinationParent.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(
                new FileRenameInformationHeader
                {
                    ReplaceIfExists = replaceExisting ? 1u : 0u,
                    RootDirectory = destinationParent.DangerousGetHandle(),
                    FileNameLength = checked((uint)fileNameBytes.Length)
                },
                buffer,
                fDeleteOld: false);
            Marshal.Copy(
                fileNameBytes,
                0,
                IntPtr.Add(buffer, fileNameOffset),
                fileNameBytes.Length);
            var status = NtSetInformationFile(
                    file,
                    out _,
                    buffer,
                    checked((uint)totalBytes),
                    FileRenameInformationClass);
            if (status < 0)
            {
                ThrowIo(
                    "The exact source leaf could not be renamed.",
                    checked((int)RtlNtStatusToDosError(status)));
            }
        }
        finally
        {
            if (addedRef)
            {
                destinationParent.DangerousRelease();
            }
            CryptographicOperations.ZeroMemory(fileNameBytes);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateHandleKind(
        SafeFileHandle handle,
        bool requireDirectory,
        string invalidMessage)
    {
        var attributes = File.GetAttributes(handle);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (GetFileType(handle) != FileTypeDisk
            || isDirectory != requireDirectory
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(invalidMessage);
        }
    }

    private static string ToExtendedLengthPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    private static void ThrowIo(string message, int error, SafeFileHandle? handle = null)
    {
        handle?.Dispose();
        throw new IOException(message, new Win32Exception(error));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record BoundDirectory(
        string RelativePath,
        SafeFileHandle Handle,
        AliSourceFileIdentity Identity,
        bool OwnsHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader
    {
        public uint ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
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

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        FileShare shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileStandardInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle fileHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
