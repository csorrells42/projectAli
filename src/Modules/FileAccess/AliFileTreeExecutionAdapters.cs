using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.WorkstationFiles;

internal sealed record AliFileTreeItemSnapshot(string Kind, string Digest)
{
    internal static AliFileTreeItemSnapshot Absent { get; } = new("absent", "absent");

    internal bool Exists => !string.Equals(Kind, "absent", StringComparison.Ordinal);
}

internal enum AliFileTreeOperation
{
    Copy,
    Move,
    CreateDirectory,
    Delete
}

internal enum AliFileTreePreparationCheckpoint
{
    ExactTargetCaptured
}

internal enum AliFileTreeExecutionCheckpoint
{
    DirectoryStagingRootCreated,
    DirectoryStagingChildCreated,
    DirectoryChainPublished,
    CopyChunkWritten,
    CopyStagingPopulated,
    CopyExecutionBindingPersisted,
    CopySourceDirectoryChildOpened,
    CopyDestinationDirectoryChildCreated,
    DirectoryCreateExecutionBindingPersisted,
    DeleteExecutionBindingPersisted,
    CopyBeforeHandleRename,
    MoveBeforeHandleRename,
    DeleteBeforeHandleRename,
    DirectoryCreateBeforeHandleRename,
    RecoveryBeforeHandleRollback,
    CopyAfterHandleRename,
    MoveAfterHandleRename,
    DeleteAfterHandleRename,
    DirectoryCreateAfterHandleRename
}

internal sealed class AliFileTreeSimulatedInterruptionException(
    AliFileTreeExecutionCheckpoint checkpoint) : Exception(
        $"Simulated interruption at exact file-tree checkpoint '{checkpoint}'.")
{
    internal AliFileTreeExecutionCheckpoint Checkpoint { get; } = checkpoint;
}

internal sealed record AliFileTreeDirectoryBinding(
    string VirtualPath,
    string PhysicalPath,
    AliFileTreeItemSnapshot Before,
    AliFileTreeItemSnapshot After);

internal sealed record AliFileTreeNamespaceBinding(
    string RelativePath,
    string PhysicalPath,
    string Identity);

internal sealed record AliFileTreeCapturedTarget(
    AliFileTreeOperation Operation,
    string ToolName,
    string? SourceVirtualPath,
    string? DestinationVirtualPath,
    string? SourcePhysicalPath,
    string? DestinationPhysicalPath,
    AliResolvedWorkstationPath? SourceResolution,
    AliResolvedWorkstationPath? DestinationResolution,
    AliFileTreeItemSnapshot SourceBefore,
    AliFileTreeItemSnapshot DestinationBefore,
    TargetStateSnapshot TargetState,
    IReadOnlyList<AliFileTreeDirectoryBinding> DirectoryChain);

internal sealed record AliFileTreeDomainPlan(
    string DomainId,
    AliFileTreeOperation Operation,
    string ToolName,
    string? SourceVirtualPath,
    string? DestinationVirtualPath,
    string? SourcePhysicalPath,
    string? DestinationPhysicalPath,
    string? TrashPhysicalPath,
    string? StagingPhysicalPath,
    AliFileTreeItemSnapshot SourceBefore,
    AliFileTreeItemSnapshot DestinationBefore,
    AliFileTreeItemSnapshot TrashBefore,
    AliFileTreeItemSnapshot StagingBefore,
    AliFileTreeItemSnapshot SourceAfter,
    AliFileTreeItemSnapshot DestinationAfter,
    AliFileTreeItemSnapshot TrashAfter,
    AliFileTreeItemSnapshot StagingAfter,
    IReadOnlyList<AliFileTreeDirectoryBinding> DirectoryChain,
    string? SourceObjectIdentity,
    string? DestinationObjectIdentity,
    string? SourceAnchorPhysicalPath,
    string? SourceAnchorIdentity,
    string? SourceParentPhysicalPath,
    string? SourceParentIdentity,
    string PublicationAnchorPhysicalPath,
    string PublicationAnchorIdentity,
    string PublicationParentPhysicalPath,
    string? PublicationParentIdentity,
    IReadOnlyList<AliFileTreeNamespaceBinding> SourceNamespaceSpine,
    IReadOnlyList<AliFileTreeNamespaceBinding> PublicationNamespaceSpine);

internal sealed record AliFileTreeExecutionBinding(
    int FormatVersion,
    string DomainId,
    string DomainDigest,
    string AuthorizationDigest,
    string PublicationParentPhysicalPath,
    string PublicationParentIdentity,
    IReadOnlyList<AliFileTreeNamespaceBinding> PublicationNamespaceSpine,
    string? StagingPhysicalPath,
    string? StagingObjectIdentity);

/// <summary>
/// FileAccess-local Windows namespace boundary for the irreversible file-tree commit. It binds
/// paths to full FILE_ID_INFO identities, holds every directory in the namespace spine without
/// delete sharing, and renames only through the already-open destination-parent handle.
/// </summary>
internal static class AliFileTreeWindowsBoundary
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExistingDisposition = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 1;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfo = 18;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidHandle = 6;

    internal static string CaptureExistingIdentity(string path, string kind)
    {
        using var handle = OpenExisting(path, kind, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete);
        return CaptureIdentity(handle, kind);
    }

    internal static void ValidateNativeLeaf(string leaf) => RequireLeaf(leaf);

    internal static bool PathMatchesIdentity(string path, string kind, string expectedIdentity)
    {
        try
        {
            return string.Equals(
                CaptureExistingIdentity(path, kind),
                expectedIdentity,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<AliFileTreeNamespaceBinding> CaptureDirectorySpine(
        string anchorPath,
        string parentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        var anchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchorPath));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        if (!IsWithinOrEqual(parent, anchor))
        {
            throw new InvalidDataException(
                "The exact file-tree namespace parent escaped its authenticated anchor.");
        }

        var handles = new List<SafeFileHandle>();
        var bindings = new List<AliFileTreeNamespaceBinding>();
        try
        {
            var current = anchor;
            var anchorHandle = OpenExistingDirectoryForSpine(current);
            handles.Add(anchorHandle);
            bindings.Add(new AliFileTreeNamespaceBinding(
                ".",
                current,
                CaptureIdentity(anchorHandle, "directory")));
            var relative = Path.GetRelativePath(anchor, parent);
            if (!string.Equals(relative, ".", StringComparison.Ordinal))
            {
                foreach (var component in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    RequireLeaf(component);
                    current = Path.Combine(current, component);
                    var handle = OpenDirectoryRelative(
                        handles[^1],
                        component,
                        writableDirectory: false);
                    handles.Add(handle);
                    bindings.Add(new AliFileTreeNamespaceBinding(
                        Path.GetRelativePath(anchor, current).Replace('\\', '/'),
                        current,
                        CaptureIdentity(handle, "directory")));
                }
            }
            return bindings;
        }
        finally
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
        }
    }

    internal static AliFileTreeDirectorySpine OpenDirectorySpine(
        IReadOnlyList<AliFileTreeNamespaceBinding> bindings,
        bool writableParent = false)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0
            || !string.Equals(bindings[0].RelativePath, ".", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact file-tree namespace spine has no authenticated anchor.");
        }
        var handles = new List<SafeFileHandle>();
        try
        {
            var anchor = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(bindings[0].PhysicalPath));
            var anchorHandle = OpenExistingDirectoryForSpine(
                anchor,
                writableParent && bindings.Count == 1);
            handles.Add(anchorHandle);
            RequireIdentity(anchorHandle, "directory", bindings[0].Identity);
            var current = anchor;
            for (var index = 1; index < bindings.Count; index++)
            {
                var binding = bindings[index];
                var leaf = Path.GetFileName(binding.PhysicalPath);
                RequireLeaf(leaf);
                var expectedPath = Path.Combine(current, leaf);
                if (!string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(binding.PhysicalPath)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        binding.RelativePath.Replace('\\', '/'),
                        Path.GetRelativePath(anchor, expectedPath).Replace('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The exact file-tree namespace spine is not one contiguous relative chain.");
                }
                var handle = OpenDirectoryRelative(
                    handles[^1],
                    leaf,
                    writableParent && index == bindings.Count - 1);
                handles.Add(handle);
                RequireIdentity(handle, "directory", binding.Identity);
                current = expectedPath;
            }
            return new AliFileTreeDirectorySpine(
                handles,
                current,
                bindings[^1].Identity,
                bindings.ToList());
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
            throw;
        }
    }

    internal static void ExtendDirectorySpine(
        AliFileTreeDirectorySpine spine,
        string finalParentPath)
    {
        ArgumentNullException.ThrowIfNull(spine);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalParentPath);
        var finalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalParentPath));
        if (!IsWithinOrEqual(finalParent, spine.ParentPath)
            || string.Equals(spine.ParentPath, finalParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The exact file-tree publication parent is not beneath its authenticated anchor.");
        }
        var current = spine.ParentPath;
        var relative = Path.GetRelativePath(current, finalParent);
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            RequireLeaf(component);
            var next = Path.Combine(current, component);
            var handle = CreateDirectoryRelative(spine.ParentHandle, component, next);
            spine.Append(handle, next, CaptureIdentity(handle, "directory"));
            current = next;
        }
    }

    internal static AliFileTreeBoundObject OpenBoundChild(
        SafeFileHandle parent,
        string leaf,
        string kind,
        string? expectedIdentity = null)
    {
        RequireKind(kind);
        var handle = OpenRelative(parent, leaf, kind);
        try
        {
            var identity = CaptureIdentity(handle, kind);
            if (expectedIdentity is not null
                && !string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
            {
                throw new IOException(
                    "The exact relative filesystem child identity changed after authorization.");
            }
            return new AliFileTreeBoundObject(handle, kind, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static AliFileTreeExactTreeLease AcquireExactTreeLease(
        AliFileTreeBoundObject root,
        string currentPath,
        AliFileTreeItemSnapshot expected,
        bool rootHasDeleteAccess)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentNullException.ThrowIfNull(expected);
        var rootPath = Path.GetFullPath(currentPath);
        if (!PathMatchesIdentity(rootPath, root.Kind, root.Identity))
        {
            throw new IOException(
                "The exact tree root path no longer names its held filesystem object.");
        }

        var handles = new List<SafeFileHandle>();
        try
        {
            var rootLease = OpenExisting(
                rootPath,
                root.Kind,
                string.Equals(root.Kind, "directory", StringComparison.Ordinal)
                    ? FileListDirectory | FileReadAttributes
                    : GenericRead,
                FileShare.Read | (rootHasDeleteAccess ? FileShare.Delete : 0));
            handles.Add(rootLease);
            RequireIdentity(rootLease, root.Kind, root.Identity);

            if (string.Equals(root.Kind, "directory", StringComparison.Ordinal))
            {
                var pending = new Stack<(SafeFileHandle Handle, string Path)>();
                pending.Push((rootLease, rootPath));
                var entries = 0;
                long bytes = 0;
                while (pending.Count > 0)
                {
                    var directory = pending.Pop();
                    var children = Directory.EnumerateFileSystemEntries(directory.Path)
                        .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                        .ToArray();
                    foreach (var childPath in children)
                    {
                        if (++entries > AliFileTreeSnapshotter.MaximumEntries)
                        {
                            throw new IOException(
                                $"An exact file-tree target cannot exceed {AliFileTreeSnapshotter.MaximumEntries} entries.");
                        }
                        var attributes = File.GetAttributes(childPath);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        {
                            throw new InvalidDataException(
                                "The exact tree closure contains a reparse point or device entry.");
                        }
                        var kind = (attributes & FileAttributes.Directory) != 0
                            ? "directory"
                            : "file";
                        var child = OpenOrCreateRelative(
                            directory.Handle,
                            Path.GetFileName(childPath),
                            kind,
                            FileOpen,
                            renameable: false,
                            writableDirectory: false,
                            shareOverride: FileShare.Read);
                        handles.Add(child);
                        if (string.Equals(kind, "directory", StringComparison.Ordinal))
                        {
                            pending.Push((child, childPath));
                            continue;
                        }
                        bytes = checked(bytes + RandomAccess.GetLength(child));
                        if (bytes > AliFileTreeSnapshotter.MaximumBytes)
                        {
                            throw new IOException(
                                $"An exact file-tree target cannot exceed {AliFileTreeSnapshotter.MaximumBytes} bytes.");
                        }
                    }
                }
            }

            var lease = new AliFileTreeExactTreeLease(
                handles,
                root.Kind,
                root.Identity);
            lease.RequireStable(rootPath, expected);
            return lease;
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
            throw;
        }
    }

    internal static AliFileTreeBoundObject OpenBoundRenameChild(
        SafeFileHandle parent,
        string leaf,
        string kind,
        string expectedIdentity)
    {
        RequireKind(kind);
        var handle = OpenOrCreateRelative(
            parent,
            leaf,
            kind,
            FileOpen,
            renameable: true,
            writableDirectory: false);
        try
        {
            RequireIdentity(handle, kind, expectedIdentity);
            return new AliFileTreeBoundObject(handle, kind, expectedIdentity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static AliFileTreeBoundObject OpenBoundChildForDelete(
        SafeFileHandle parent,
        string leaf,
        string expectedIdentity)
    {
        var handle = OpenOrCreateRelative(
            parent,
            leaf,
            "directory",
            FileOpen,
            renameable: true,
            writableDirectory: false);
        try
        {
            RequireIdentity(handle, "directory", expectedIdentity);
            return new AliFileTreeBoundObject(handle, "directory", expectedIdentity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static AliFileTreeBoundObject OpenBoundChildForDelete(
        AliFileTreeBoundObject parent,
        string leaf)
    {
        RequireDirectoryParent(parent);
        var handle = OpenOrCreateRelative(
            parent.Handle,
            leaf,
            "directory",
            FileOpen,
            renameable: true,
            writableDirectory: false);
        try
        {
            return new AliFileTreeBoundObject(
                handle,
                "directory",
                CaptureIdentity(handle, "directory"));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void DeleteBoundEmptyDirectory(AliFileTreeBoundObject directory)
    {
        RequireDirectoryParent(directory);
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(
                    directory.Handle,
                    FileDispositionInfo,
                    buffer,
                    1))
            {
                ThrowIo(
                    "The exact owned staging directory could not be removed through its held handle.",
                    Marshal.GetLastWin32Error());
            }
            directory.Dispose();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string CaptureChildIdentity(
        IReadOnlyList<AliFileTreeNamespaceBinding> parentSpine,
        string childPath,
        string kind)
    {
        using var parent = OpenDirectorySpine(parentSpine);
        var fullPath = Path.GetFullPath(childPath);
        RequireChildPath(parent.ParentPath, fullPath);
        using var child = OpenBoundChild(
            parent.ParentHandle,
            Path.GetFileName(fullPath),
            kind);
        return child.Identity;
    }

    internal static AliFileTreeBoundObject CreateBoundRegularFile(
        AliFileTreeDirectorySpine parent,
        string path)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var fullPath = Path.GetFullPath(path);
        RequireChildPath(parent.ParentPath, fullPath);
        var handle = CreateRelative(
            parent.ParentHandle,
            Path.GetFileName(fullPath),
            "file",
            renameable: true);
        try
        {
            var identity = CaptureIdentity(handle, "file");
            return new AliFileTreeBoundObject(handle, "file", identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static AliFileTreeBoundObject CreateBoundChildRegularFile(
        AliFileTreeBoundObject parent,
        string leaf)
    {
        RequireDirectoryParent(parent);
        var handle = CreateRelative(parent.Handle, leaf, "file", renameable: false);
        try
        {
            return new AliFileTreeBoundObject(
                handle,
                "file",
                CaptureIdentity(handle, "file"));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static AliFileTreeBoundObject CreateBoundDirectory(
        AliFileTreeDirectorySpine parent,
        string path)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var fullPath = Path.GetFullPath(path);
        RequireChildPath(parent.ParentPath, fullPath);
        var handle = CreateDirectoryRelative(parent.ParentHandle, Path.GetFileName(fullPath), fullPath);
        try
        {
            var identity = CaptureIdentity(handle, "directory");
            return new AliFileTreeBoundObject(handle, "directory", identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static AliFileTreeBoundObject CreateBoundChildDirectory(
        AliFileTreeBoundObject parent,
        string leaf)
    {
        RequireDirectoryParent(parent);
        var handle = CreateRelative(parent.Handle, leaf, "directory", renameable: false);
        try
        {
            return new AliFileTreeBoundObject(
                handle,
                "directory",
                CaptureIdentity(handle, "directory"));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void RenameNoReplace(
        AliFileTreeBoundObject source,
        AliFileTreeDirectorySpine destinationParent,
        string destinationLeaf)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationParent);
        RequireLeaf(destinationLeaf);
        if (!string.Equals(
                VolumeIdentity(source.Identity),
                VolumeIdentity(destinationParent.ParentIdentity),
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The exact file-tree handle rename cannot cross filesystem volumes.");
        }

        var fileName = Encoding.Unicode.GetBytes(destinationLeaf);
        var fileNameOffset = Marshal.OffsetOf<FileRenameInformationHeader>(
                nameof(FileRenameInformationHeader.FileNameLength))
            .ToInt32() + sizeof(uint);
        var size = Math.Max(
            Marshal.SizeOf<FileRenameInformationHeader>(),
            checked(fileNameOffset + fileName.Length));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            var header = new FileRenameInformationHeader
            {
                Flags = 0,
                RootDirectory = destinationParent.ParentHandle.DangerousGetHandle(),
                FileNameLength = checked((uint)fileName.Length)
            };
            Marshal.StructureToPtr(header, buffer, fDeleteOld: false);
            Marshal.Copy(fileName, 0, IntPtr.Add(buffer, fileNameOffset), fileName.Length);
            if (!SetFileInformationByHandle(
                    source.Handle,
                    FileRenameInfo,
                    buffer,
                    checked((uint)size)))
            {
                ThrowIo(
                    "The exact file-tree object could not be renamed through its held parent handle.",
                    Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileName);
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static AliFileTreeItemSnapshot CaptureBoundSnapshot(
        AliFileTreeBoundObject item,
        string currentPath)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!PathMatchesIdentity(currentPath, item.Kind, item.Identity))
        {
            throw new IOException(
                "The exact file-tree path no longer names its held filesystem object.");
        }
        if (string.Equals(item.Kind, "directory", StringComparison.Ordinal))
        {
            var snapshot = AliFileTreeSnapshotter.CaptureStable(currentPath);
            if (!PathMatchesIdentity(currentPath, item.Kind, item.Identity))
            {
                throw new IOException(
                    "The exact file-tree directory identity changed during recapture.");
            }
            return snapshot;
        }

        var first = CaptureBoundRegularFile(item.Handle);
        var second = CaptureBoundRegularFile(item.Handle);
        if (first != second)
        {
            throw new IOException(
                "The exact held file changed during authenticated recapture.");
        }
        if (!PathMatchesIdentity(currentPath, item.Kind, item.Identity))
        {
            throw new IOException(
                "The exact file-tree file identity changed during recapture.");
        }
        return second;
    }

    internal static void CopyBoundRegularFile(
        AliFileTreeBoundObject source,
        AliFileTreeBoundObject destination,
        int bufferSize,
        Action chunkWritten,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(chunkWritten);
        if (!string.Equals(source.Kind, "file", StringComparison.Ordinal)
            || !string.Equals(destination.Kind, "file", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A bound file copy requires two regular files.");
        }
        RandomAccess.SetLength(destination.Handle, 0);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(source.Handle, buffer.AsSpan(0, bufferSize), offset);
                if (read == 0)
                {
                    break;
                }
                cancellationToken.ThrowIfCancellationRequested();
                RandomAccess.Write(destination.Handle, buffer.AsSpan(0, read), offset);
                offset = checked(offset + read);
                chunkWritten();
                cancellationToken.ThrowIfCancellationRequested();
            }
            RandomAccess.FlushToDisk(destination.Handle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static AliFileTreeItemSnapshot CaptureBoundRegularFile(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                var read = RandomAccess.Read(
                    handle,
                    buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)),
                    offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The exact held file ended before its authenticated length.");
                }
                hash.AppendData(buffer, 0, read);
                offset = checked(offset + read);
            }
            if (RandomAccess.GetLength(handle) != length)
            {
                throw new IOException("The exact held file length changed during recapture.");
            }
            return new AliFileTreeItemSnapshot(
                "file",
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static SafeFileHandle OpenExistingDirectoryForSpine(
        string path,
        bool writableDirectory = false) => OpenExisting(
        path,
        "directory",
        FileListDirectory | FileReadAttributes
        | (writableDirectory ? FileAddFile | FileAddSubdirectory : 0),
        FileShare.ReadWrite);

    private static SafeFileHandle OpenExisting(
        string path,
        string kind,
        uint desiredAccess,
        FileShare share)
    {
        RequireKind(kind);
        var handle = CreateFileW(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(Path.GetFullPath(path)),
            desiredAccess,
            share,
            IntPtr.Zero,
            OpenExistingDisposition,
            FileFlagOpenReparsePoint
            | (string.Equals(kind, "directory", StringComparison.Ordinal)
                ? FileFlagBackupSemantics
                : 0),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == ErrorFileNotFound)
            {
                throw new FileNotFoundException(
                    "The exact bound filesystem object does not exist.",
                    path);
            }
            if (error == ErrorPathNotFound)
            {
                throw new DirectoryNotFoundException(
                    "The exact bound filesystem object's parent does not exist.");
            }
            ThrowIo("The exact filesystem object could not be opened without following links.", error);
        }
        ValidateHandle(handle, kind);
        return handle;
    }

    private static SafeFileHandle CreateDirectoryRelative(
        SafeFileHandle parent,
        string leaf,
        string intendedPath)
    {
        var handle = CreateRelative(parent, leaf, "directory", renameable: true);
        if (!PathMatchesIdentity(intendedPath, "directory", CaptureIdentity(handle, "directory")))
        {
            handle.Dispose();
            throw new IOException(
                "The atomically created directory is not bound at its authenticated path.");
        }
        return handle;
    }

    private static SafeFileHandle OpenDirectoryRelative(
        SafeFileHandle parent,
        string leaf,
        bool writableDirectory) => OpenRelative(
        parent,
        leaf,
        "directory",
        writableDirectory);

    private static SafeFileHandle OpenRelative(
        SafeFileHandle parent,
        string leaf,
        string kind,
        bool writableDirectory = false) => OpenOrCreateRelative(
        parent,
        leaf,
        kind,
        FileOpen,
        renameable: false,
        writableDirectory);

    private static SafeFileHandle CreateRelative(
        SafeFileHandle parent,
        string leaf,
        string kind,
        bool renameable) => OpenOrCreateRelative(
        parent,
        leaf,
        kind,
        FileCreate,
        renameable,
        writableDirectory: string.Equals(kind, "directory", StringComparison.Ordinal));

    private static SafeFileHandle OpenOrCreateRelative(
        SafeFileHandle parent,
        string leaf,
        string kind,
        uint createDisposition,
        bool renameable,
        bool writableDirectory,
        FileShare? shareOverride = null)
    {
        RequireKind(kind);
        RequireLeaf(leaf);
        var nameBytes = Encoding.Unicode.GetBytes(leaf);
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new PathTooLongException(
                "The exact file-tree directory leaf exceeds the Windows native name limit.");
        }
        var nameBuffer = Marshal.StringToHGlobalUni(leaf);
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)nameBytes.Length),
                MaximumLength = checked((ushort)nameBytes.Length),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjectCaseInsensitive,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var isDirectory = string.Equals(kind, "directory", StringComparison.Ordinal);
            var desiredAccess = FileReadAttributes | Synchronize
                                | (isDirectory
                                    ? FileListDirectory
                                    : GenericRead)
                                | (isDirectory && writableDirectory
                                    ? FileAddFile | FileAddSubdirectory
                                    : 0)
                                | (createDisposition == FileCreate && !isDirectory
                                    ? GenericWrite
                                    : 0)
                                | (renameable ? DeleteAccess : 0);
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                createDisposition == FileCreate ? FileAttributeNormal : 0,
                (uint)(shareOverride
                    ?? (isDirectory ? FileShare.Read | FileShare.Write : FileShare.Read)),
                createDisposition,
                (isDirectory ? FileDirectoryFile : FileNonDirectoryFile)
                | FileOpenReparsePoint
                | FileSynchronousIoNonAlert,
                IntPtr.Zero,
                0);
            if (status < 0 || handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                ThrowIo(
                    createDisposition == FileCreate
                        ? "The exact child could not be created atomically beneath its held parent."
                        : "The exact child could not be opened relative to its held parent.",
                    status < 0
                        ? checked((int)RtlNtStatusToDosError(status))
                        : ErrorInvalidHandle);
            }
            ValidateHandle(handle!, kind);
            return handle!;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nameBytes);
            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static string CaptureIdentity(SafeFileHandle handle, string kind)
    {
        ValidateHandle(handle, kind);
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation information,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            ThrowIo(
                "The exact filesystem identity could not be captured.",
                Marshal.GetLastWin32Error());
        }
        return kind + ":"
               + information.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture)
               + ":"
               + information.FileId.High.ToString("x16", CultureInfo.InvariantCulture)
               + information.FileId.Low.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static void RequireIdentity(
        SafeFileHandle handle,
        string kind,
        string expectedIdentity)
    {
        if (!string.Equals(
                CaptureIdentity(handle, kind),
                expectedIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The exact filesystem object identity changed after authorization.");
        }
    }

    private static void ValidateHandle(SafeFileHandle handle, string kind)
    {
        if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk)
        {
            throw new InvalidDataException("The exact filesystem object is not a local disk object.");
        }
        var attributes = File.GetAttributes(handle);
        var expectedDirectory = string.Equals(kind, "directory", StringComparison.Ordinal);
        if (((attributes & FileAttributes.Directory) != 0) != expectedDirectory
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The exact filesystem object kind changed or contains a reparse point.");
        }
    }

    private static string VolumeIdentity(string identity)
    {
        var parts = identity.Split(':');
        if (parts.Length != 3 || parts[1].Length != 16)
        {
            throw new InvalidDataException("The exact filesystem identity has an invalid format.");
        }
        return parts[1];
    }

    private static void RequireKind(string kind)
    {
        if (!string.Equals(kind, "file", StringComparison.Ordinal)
            && !string.Equals(kind, "directory", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The exact filesystem object has an invalid kind.");
        }
    }

    private static void RequireLeaf(string leaf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaf);
        if (leaf is "." or ".."
            || leaf.Length > 255
            || leaf.EndsWith(' ')
            || leaf.EndsWith('.')
            || leaf.Any(character => character < 32)
            || leaf.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0
            || !string.Equals(Path.GetFileName(leaf), leaf, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact handle-rooted rename or create requires one ordinary leaf name.");
        }
        var deviceStem = leaf.Split('.')[0].ToUpperInvariant();
        if (deviceStem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || IsReservedIndexedDevice(deviceStem, "COM")
            || IsReservedIndexedDevice(deviceStem, "LPT"))
        {
            throw new InvalidDataException(
                "The exact handle-rooted rename or create rejects Windows device aliases.");
        }
    }

    private static bool IsReservedIndexedDevice(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 1)
        {
            return false;
        }
        return value[^1] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static void RequireDirectoryParent(AliFileTreeBoundObject parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!string.Equals(parent.Kind, "directory", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact relative file-tree operation requires a held directory parent.");
        }
    }

    private static void RequireChildPath(string parent, string child)
    {
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(child))
                    ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The exact created filesystem object is not an immediate child of its held parent.");
        }
        RequireLeaf(Path.GetFileName(child));
    }

    private static bool IsWithinOrEqual(string candidatePath, string directoryPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        return string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(
                   directory + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIo(string message, int error, SafeFileHandle? handle = null)
    {
        handle?.Dispose();
        throw new IOException(message, new Win32Exception(error));
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader
    {
        internal uint Flags;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}

internal sealed class AliFileTreeDirectorySpine(
    List<SafeFileHandle> handles,
    string parentPath,
    string parentIdentity,
    List<AliFileTreeNamespaceBinding> bindings) : IDisposable
{
    private readonly List<SafeFileHandle> _handles = handles;
    private readonly List<AliFileTreeNamespaceBinding> _bindings = bindings;

    internal SafeFileHandle ParentHandle => _handles[^1];

    internal string ParentPath { get; private set; } = parentPath;

    internal string ParentIdentity { get; private set; } = parentIdentity;

    internal IReadOnlyList<AliFileTreeNamespaceBinding> Bindings => _bindings;

    internal void Append(SafeFileHandle handle, string path, string identity)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handles.Add(handle);
        ParentPath = path;
        ParentIdentity = identity;
        _bindings.Add(new AliFileTreeNamespaceBinding(
            Path.GetRelativePath(_bindings[0].PhysicalPath, path).Replace('\\', '/'),
            path,
            identity));
    }

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--)
        {
            _handles[index].Dispose();
        }
    }
}

internal sealed class AliFileTreeBoundObject(
    SafeFileHandle handle,
    string kind,
    string identity) : IDisposable
{
    internal SafeFileHandle Handle { get; } = handle;

    internal string Kind { get; } = kind;

    internal string Identity { get; } = identity;

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// Keeps every no-follow descendant handle open across the final authenticated snapshot,
/// handle-relative publication, and immediate postimage/rollback decision. Directory handles
/// deny write/delete sharing and file handles deny write/delete sharing, closing the last
/// nested-content interposition window without changing the bounded snapshot contract.
/// </summary>
internal sealed class AliFileTreeExactTreeLease(
    List<SafeFileHandle> handles,
    string rootKind,
    string rootIdentity) : IDisposable
{
    private readonly List<SafeFileHandle> _handles = handles;
    private bool _disposed;

    internal void RequireStable(
        string currentRootPath,
        AliFileTreeItemSnapshot expected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRootPath);
        ArgumentNullException.ThrowIfNull(expected);
        if (!AliFileTreeWindowsBoundary.PathMatchesIdentity(
                currentRootPath,
                rootKind,
                rootIdentity))
        {
            throw new IOException(
                "The exact tree closure root path no longer names its held object.");
        }
        if (AliFileTreeSnapshotter.CaptureStable(currentRootPath) != expected)
        {
            throw new IOException(
                "The complete held file-tree closure does not match its authenticated state.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (var index = _handles.Count - 1; index >= 0; index--)
        {
            _handles[index].Dispose();
        }
    }
}

internal sealed record AliFileTreeProtectedExecutionBindingEnvelope(
    int FormatVersion,
    string DomainId,
    string DomainDigest,
    string AuthorizationDigest,
    string PublicationParentPhysicalPath,
    string PublicationParentIdentity,
    IReadOnlyList<AliFileTreeNamespaceBinding> PublicationNamespaceSpine,
    string? StagingPhysicalPath,
    string? StagingObjectIdentity,
    string ProtectedPayload);

internal sealed class AliFileTreeExecutionBindingStore
{
    private const int FormatVersion = 1;
    private const int MaximumBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _profileBinding;

    internal AliFileTreeExecutionBindingStore(string root, string profileBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBinding);
        _root = Path.GetFullPath(root);
        _profileBinding = profileBinding;
    }

    internal void WriteOnce(AliFileTreeExecutionBinding binding)
    {
        Validate(binding);
        var plaintext = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            JsonSerializer.SerializeToElement(binding, JsonOptions));
        byte[]? protectedBytes = null;
        byte[]? envelopeBytes = null;
        try
        {
            protectedBytes = Protect(binding, plaintext);
            var envelope = new AliFileTreeProtectedExecutionBindingEnvelope(
                binding.FormatVersion,
                binding.DomainId,
                binding.DomainDigest,
                binding.AuthorizationDigest,
                binding.PublicationParentPhysicalPath,
                binding.PublicationParentIdentity,
                binding.PublicationNamespaceSpine,
                binding.StagingPhysicalPath,
                binding.StagingObjectIdentity,
                Convert.ToBase64String(protectedBytes));
            envelopeBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
                JsonSerializer.SerializeToElement(envelope, JsonOptions));
            if (envelopeBytes.Length > MaximumBytes)
            {
                throw new IOException(
                    "The protected file-tree execution binding is too large.");
            }

            WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                _root,
                "The file-tree execution-binding root is not a regular local directory.");
            using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                PathFor(binding.DomainId),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                writeThrough: true,
                "The file-tree execution binding is not a regular local file.");
            stream.Write(envelopeBytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
            if (envelopeBytes is not null)
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }
        }
    }

    internal AliFileTreeExecutionBinding? TryRead(
        string domainId,
        string expectedDomainDigest,
        string expectedAuthorizationDigest)
    {
        AliDurableInvocationValidation.RequireId(domainId, nameof(domainId));
        TurnStateIntegrity.RequireDigest(expectedDomainDigest, nameof(expectedDomainDigest));
        TurnStateIntegrity.RequireDigest(
            expectedAuthorizationDigest,
            nameof(expectedAuthorizationDigest));
        var path = PathFor(domainId);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] envelopeBytes;
        using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   writeThrough: false,
                   "The file-tree execution binding is not a regular local file."))
        {
            if (stream.Length < 1 || stream.Length > MaximumBytes)
            {
                throw new InvalidDataException(
                    "The file-tree execution binding has an invalid size.");
            }
            envelopeBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(envelopeBytes);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<AliFileTreeProtectedExecutionBindingEnvelope>(
                    envelopeBytes,
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "The protected file-tree execution binding is empty.");
            var bindingEnvelope = new AliFileTreeExecutionBinding(
                envelope.FormatVersion,
                envelope.DomainId,
                envelope.DomainDigest,
                envelope.AuthorizationDigest,
                envelope.PublicationParentPhysicalPath,
                envelope.PublicationParentIdentity,
                envelope.PublicationNamespaceSpine,
                envelope.StagingPhysicalPath,
                envelope.StagingObjectIdentity);
            Validate(bindingEnvelope);
            if (!string.Equals(envelope.DomainId, domainId, StringComparison.Ordinal)
                || !string.Equals(
                    envelope.DomainDigest,
                    expectedDomainDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    envelope.AuthorizationDigest,
                    expectedAuthorizationDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The protected file-tree execution binding does not match its started invocation.");
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(envelope.ProtectedPayload);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "The protected file-tree execution binding payload is malformed.",
                    exception);
            }
            try
            {
                var plaintext = Unprotect(bindingEnvelope, protectedBytes);
                try
                {
                    var binding = JsonSerializer.Deserialize<AliFileTreeExecutionBinding>(
                            plaintext,
                            JsonOptions)
                        ?? throw new InvalidDataException(
                            "The protected file-tree execution binding payload is empty.");
                    Validate(binding);
                    if (binding.FormatVersion != bindingEnvelope.FormatVersion
                        || !string.Equals(binding.DomainId, bindingEnvelope.DomainId, StringComparison.Ordinal)
                        || !string.Equals(binding.DomainDigest, bindingEnvelope.DomainDigest, StringComparison.Ordinal)
                        || !string.Equals(binding.AuthorizationDigest, bindingEnvelope.AuthorizationDigest, StringComparison.Ordinal)
                        || !string.Equals(binding.PublicationParentPhysicalPath, bindingEnvelope.PublicationParentPhysicalPath, StringComparison.Ordinal)
                        || !string.Equals(binding.PublicationParentIdentity, bindingEnvelope.PublicationParentIdentity, StringComparison.Ordinal)
                        || !binding.PublicationNamespaceSpine.SequenceEqual(
                            bindingEnvelope.PublicationNamespaceSpine)
                        || !string.Equals(binding.StagingPhysicalPath, bindingEnvelope.StagingPhysicalPath, StringComparison.Ordinal)
                        || !string.Equals(binding.StagingObjectIdentity, bindingEnvelope.StagingObjectIdentity, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The protected file-tree execution binding envelope does not match its payload.");
                    }
                    return binding;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    private static void Validate(AliFileTreeExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException(
                "The protected file-tree execution binding format is unsupported.");
        }
        AliDurableInvocationValidation.RequireId(binding.DomainId, nameof(binding.DomainId));
        TurnStateIntegrity.RequireDigest(binding.DomainDigest, nameof(binding.DomainDigest));
        TurnStateIntegrity.RequireDigest(
            binding.AuthorizationDigest,
            nameof(binding.AuthorizationDigest));
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.PublicationParentPhysicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.PublicationParentIdentity);
        if (binding.PublicationNamespaceSpine is null
            || binding.PublicationNamespaceSpine.Count == 0
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                    binding.PublicationNamespaceSpine[^1].PhysicalPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                    binding.PublicationParentPhysicalPath)),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                binding.PublicationNamespaceSpine[^1].Identity,
                binding.PublicationParentIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected file-tree execution binding has an invalid publication spine.");
        }
        if ((binding.StagingPhysicalPath is null) != (binding.StagingObjectIdentity is null))
        {
            throw new InvalidDataException(
                "The protected file-tree execution binding must bind both staging path and identity.");
        }
    }

    private byte[] Protect(AliFileTreeExecutionBinding binding, byte[] plaintext)
    {
        var entropy = Entropy(binding);
        try
        {
            return ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] Unprotect(AliFileTreeExecutionBinding binding, byte[] protectedBytes)
    {
        var entropy = Entropy(binding);
        try
        {
            return ProtectedData.Unprotect(
                protectedBytes,
                entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The file-tree execution binding failed its current-user integrity check.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] Entropy(AliFileTreeExecutionBinding binding) => SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join(
            "\0",
            "Ali.FileTree.ExecutionBinding",
            _profileBinding,
            binding.DomainId,
            binding.DomainDigest,
            binding.AuthorizationDigest,
            Path.GetFullPath(binding.PublicationParentPhysicalPath),
            binding.PublicationParentIdentity,
            NamespaceSpineDigest(binding.PublicationNamespaceSpine),
            binding.StagingPhysicalPath is null
                ? string.Empty
                : Path.GetFullPath(binding.StagingPhysicalPath),
            binding.StagingObjectIdentity ?? string.Empty)));

    private static string NamespaceSpineDigest(
        IReadOnlyList<AliFileTreeNamespaceBinding> bindings)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            JsonSerializer.SerializeToElement(bindings, JsonOptions));
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string PathFor(string domainId) => Path.Combine(
        _root,
        domainId + ".file-tree-execution-binding.protected");
}

/// <summary>
/// Captures a bounded, no-follow digest for one exact file or directory tree. The second pass
/// prevents a moving tree from being accepted as a stable target-version observation.
/// </summary>
internal static class AliFileTreeSnapshotter
{
    internal const int MaximumEntries = 100_000;
    internal const long MaximumBytes = 8L * 1024 * 1024 * 1024;

    internal static AliFileTreeItemSnapshot EmptyDirectory { get; } = new(
        "directory",
        Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("ali-file-tree-v1\0directory\0")))
            .ToLowerInvariant());

    internal static void AppendCanonicalDirectoryPrefix(IncrementalHash hash) =>
        AppendText(hash, "ali-file-tree-v1\0directory\0");

    internal static void AppendCanonicalDirectoryEntry(
        IncrementalHash hash,
        string relativePath) =>
        AppendText(hash, "d\0" + relativePath.Replace('\\', '/') + "\0");

    internal static AliFileTreeItemSnapshot CaptureStable(string path)
    {
        var first = CaptureOnce(path);
        var second = CaptureOnce(path);
        if (first != second)
        {
            throw new IOException("The exact file-tree target changed while it was captured.");
        }
        return second;
    }

    private static AliFileTreeItemSnapshot CaptureOnce(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        ValidateExistingParentBoundary(fullPath);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return AliFileTreeItemSnapshot.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return AliFileTreeItemSnapshot.Absent;
        }

        RequireRegularAttributes(attributes);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            return new AliFileTreeItemSnapshot("file", HashRegularFile(fullPath));
        }

        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            fullPath,
            "The exact file-tree target contains a reparse point or non-regular directory.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, "ali-file-tree-v1\0directory\0");
        var pending = new Stack<(string PhysicalPath, string RelativePath)>();
        pending.Push((fullPath, string.Empty));
        var entries = 0;
        long bytes = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var children = Directory.EnumerateFileSystemEntries(current.PhysicalPath)
                .OrderBy(item => Path.GetFileName(item), StringComparer.Ordinal)
                .ToArray();
            foreach (var child in children)
            {
                if (++entries > MaximumEntries)
                {
                    throw new IOException(
                        $"An exact file-tree target cannot exceed {MaximumEntries} entries.");
                }
                var childAttributes = File.GetAttributes(child);
                RequireRegularAttributes(childAttributes);
                var relative = Path.GetRelativePath(fullPath, child).Replace('\\', '/');
                if ((childAttributes & FileAttributes.Directory) != 0)
                {
                    WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                        child,
                        "The exact file-tree target contains a reparse point or non-regular directory.");
                    AppendText(hash, "d\0" + relative + "\0");
                    pending.Push((child, relative));
                    continue;
                }

                var file = HashRegularFileWithLength(child);
                bytes = checked(bytes + file.Length);
                if (bytes > MaximumBytes)
                {
                    throw new IOException(
                        $"An exact file-tree target cannot exceed {MaximumBytes} bytes.");
                }
                AppendText(hash, "f\0" + relative + "\0" + file.Length + "\0" + file.Digest + "\0");
            }
        }

        return new AliFileTreeItemSnapshot(
            "directory",
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static (long Length, string Digest) HashRegularFileWithLength(string path)
    {
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The exact file-tree target contains a reparse point or non-regular file.");
        var length = stream.Length;
        var digest = SHA256.HashData(stream);
        try
        {
            if (stream.Length != length)
            {
                throw new IOException(
                    "The exact file-tree target changed while a file was hashed.");
            }
            return (length, Convert.ToHexString(digest).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string HashRegularFile(string path) => HashRegularFileWithLength(path).Digest;

    private static void ValidateExistingParentBoundary(string path)
    {
        var current = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The exact file-tree target has no parent directory.");
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new InvalidDataException(
                    "A non-directory entry blocks the exact file-tree target path.");
            }
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException(
                    "The exact file-tree target has no existing filesystem root.");
        }
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            current,
            "The exact file-tree target parent contains a reparse point or non-regular directory.");
    }

    private static void RequireRegularAttributes(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                "The exact file-tree target contains a reparse point or device entry.");
        }
    }

    private static void AppendText(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal sealed class AliFileTreeTargetStateAdapter(
    AliWorkstationFileStore store) : IActionTargetStateAdapter
{
    private readonly AliWorkstationFileStore _store = store
        ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        AliCapabilityCatalog.FileDeleteName,
        AliCapabilityCatalog.FileMoveName,
        AliCapabilityCatalog.FileCopyName,
        AliCapabilityCatalog.FileCreateDirectoryName
    ];

    public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
        => CaptureExact(toolName, arguments).TargetState;

    internal AliFileTreeCapturedTarget CaptureExact(
        string toolName,
        JsonElement arguments)
    {
        return toolName switch
        {
            AliCapabilityCatalog.FileDeleteName => CaptureDelete(arguments),
            AliCapabilityCatalog.FileMoveName => CaptureTransfer(
                AliFileTreeOperation.Move,
                toolName,
                arguments),
            AliCapabilityCatalog.FileCopyName => CaptureTransfer(
                AliFileTreeOperation.Copy,
                toolName,
                arguments),
            AliCapabilityCatalog.FileCreateDirectoryName => CaptureCreateDirectory(arguments),
            _ => throw new InvalidDataException(
                "The file-tree target-state adapter does not own this exact tool name.")
        };
    }

    private AliFileTreeCapturedTarget CaptureDelete(JsonElement arguments)
    {
        AliExactFileTreeArguments.RequireObject(arguments, ["fileName"]);
        var sourcePath = AliExactFileTreeArguments.RequireString(arguments, "fileName");
        var source = _store.ResolveExistingItemPath(sourcePath);
        var sourceBefore = AliFileTreeSnapshotter.CaptureStable(source.PhysicalPath);
        var versions = VersionMap(
            ("source:" + Normalize(sourcePath), sourceBefore));
        return new AliFileTreeCapturedTarget(
            AliFileTreeOperation.Delete,
            AliCapabilityCatalog.FileDeleteName,
            sourcePath,
            null,
            source.PhysicalPath,
            null,
            source,
            null,
            sourceBefore,
            AliFileTreeItemSnapshot.Absent,
            Snapshot(versions),
            []);
    }

    private AliFileTreeCapturedTarget CaptureCreateDirectory(JsonElement arguments)
    {
        AliExactFileTreeArguments.RequireObject(arguments, ["path"]);
        var path = AliExactFileTreeArguments.RequireString(arguments, "path");
        var destination = _store.ResolvePhysicalDirectoryPath(path);
        if (string.IsNullOrWhiteSpace(destination.RelativePath))
        {
            throw new InvalidDataException(
                "An exact directory creation target must be beneath a workstation mount.");
        }
        var destinationBefore = AliFileTreeSnapshotter.CaptureStable(destination.PhysicalPath);
        var directoryChain = CaptureMissingDirectoryChain(destination);
        var versions = VersionMap(
            ("destination:" + Normalize(path), destinationBefore));
        foreach (var binding in directoryChain)
        {
            versions.Add(
                "directory-chain:" + Normalize(binding.VirtualPath),
                Version(binding.Before));
        }
        return new AliFileTreeCapturedTarget(
            AliFileTreeOperation.CreateDirectory,
            AliCapabilityCatalog.FileCreateDirectoryName,
            null,
            path,
            null,
            destination.PhysicalPath,
            null,
            destination,
            AliFileTreeItemSnapshot.Absent,
            destinationBefore,
            Snapshot(versions),
            directoryChain);
    }

    private AliFileTreeCapturedTarget CaptureTransfer(
        AliFileTreeOperation operation,
        string toolName,
        JsonElement arguments)
    {
        var paths = AliExactFileTreeArguments.RequireTwoPaths(arguments);
        var source = _store.ResolveExistingItemPath(paths.SourcePath);
        var destination = _store.ResolveItemDestinationPath(
            paths.DestinationPath,
            source.MountName);
        var sourceBefore = AliFileTreeSnapshotter.CaptureStable(source.PhysicalPath);
        RequireExistingDestinationParent(destination.PhysicalPath);
        if (string.Equals(sourceBefore.Kind, "directory", StringComparison.Ordinal)
            && IsWithinOrEqual(destination.PhysicalPath, source.PhysicalPath))
        {
            throw new InvalidDataException(
                "A directory cannot be copied or moved to itself or one of its descendants.");
        }
        var destinationBefore = AliFileTreeSnapshotter.CaptureStable(destination.PhysicalPath);
        var versions = VersionMap(
            ("source:" + Normalize(paths.SourcePath), sourceBefore),
            ("destination:" + Normalize(paths.DestinationPath), destinationBefore));
        return new AliFileTreeCapturedTarget(
            operation,
            toolName,
            paths.SourcePath,
            paths.DestinationPath,
            source.PhysicalPath,
            destination.PhysicalPath,
            source,
            destination,
            sourceBefore,
            destinationBefore,
            Snapshot(versions),
            []);
    }

    private static void RequireExistingDestinationParent(string destinationPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
            ?? throw new InvalidDataException(
                "The exact file-tree destination has no parent directory.");
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            parent,
            "The exact file-tree destination parent must already exist as a regular local directory.");
    }

    private static bool IsWithinOrEqual(string candidatePath, string directoryPath)
    {
        var candidate = NormalizePhysicalPath(candidatePath);
        var directory = NormalizePhysicalPath(directoryPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(candidate, directory, comparison)
               || candidate.StartsWith(
                   Path.EndsInDirectorySeparator(directory)
                       ? directory
                       : directory + Path.DirectorySeparatorChar,
                   comparison);
    }

    private static string NormalizePhysicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IReadOnlyList<AliFileTreeDirectoryBinding> CaptureMissingDirectoryChain(
        AliResolvedWorkstationPath destination)
    {
        var mountRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destination.MountRoot));
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            mountRoot,
            "The exact directory target mount is not a regular local directory.");

        var relativeSegments = destination.RelativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var missing = new List<(string VirtualPath, string PhysicalPath)>();
        for (var length = relativeSegments.Length; length > 0; length--)
        {
            var relative = string.Join('/', relativeSegments.Take(length));
            var physical = Path.GetFullPath(Path.Combine(
                mountRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (WindowsOrchestrationFileBoundary.RegularDirectoryExists(
                    physical,
                    "The exact directory target contains a reparse point or non-regular directory."))
            {
                break;
            }
            if (AliFileTreeSnapshotter.CaptureStable(physical).Exists)
            {
                throw new InvalidDataException(
                    "The exact directory target contains a non-directory filesystem entry.");
            }
            missing.Add(($"{destination.MountName}/{relative}", physical));
        }
        missing.Reverse();

        var bindings = new List<AliFileTreeDirectoryBinding>(missing.Count);
        for (var index = 0; index < missing.Count; index++)
        {
            bindings.Add(new AliFileTreeDirectoryBinding(
                missing[index].VirtualPath,
                missing[index].PhysicalPath,
                AliFileTreeItemSnapshot.Absent,
                ExpectedDirectoryChainSnapshot(missing, index)));
        }
        return bindings;
    }

    private static AliFileTreeItemSnapshot ExpectedDirectoryChainSnapshot(
        IReadOnlyList<(string VirtualPath, string PhysicalPath)> chain,
        int index)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AliFileTreeSnapshotter.AppendCanonicalDirectoryPrefix(hash);
        var relative = string.Empty;
        for (var child = index + 1; child < chain.Count; child++)
        {
            var name = Path.GetFileName(chain[child].PhysicalPath);
            relative = relative.Length == 0 ? name : relative + "/" + name;
            AliFileTreeSnapshotter.AppendCanonicalDirectoryEntry(hash, relative);
        }
        return new AliFileTreeItemSnapshot(
            "directory",
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static Dictionary<string, string> VersionMap(
        params (string Key, AliFileTreeItemSnapshot Snapshot)[] entries) =>
        entries.ToDictionary(
            entry => entry.Key,
            entry => Version(entry.Snapshot),
            StringComparer.Ordinal);

    private static TargetStateSnapshot Snapshot(
        IReadOnlyDictionary<string, string> versions) =>
        new(
            versions,
            versions,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static string Version(AliFileTreeItemSnapshot snapshot) =>
        snapshot.Exists ? snapshot.Kind + ":sha256:" + snapshot.Digest : "absent";

    private static string Normalize(string path) => path.Trim().Replace('\\', '/');
}

internal static class AliExactFileTreeArguments
{
    internal static (string SourcePath, string DestinationPath) RequireTwoPaths(
        JsonElement arguments)
    {
        RequireObject(arguments, ["sourcePath", "destinationPath"]);
        return (
            RequireString(arguments, "sourcePath"),
            RequireString(arguments, "destinationPath"));
    }

    internal static string RequireString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' file-tree argument is required.");
        }
        return value.GetString()!.Trim();
    }

    internal static void RequireObject(
        JsonElement arguments,
        IReadOnlyCollection<string> required)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The exact file-tree arguments must be an object.");
        }
        var expected = required.ToHashSet(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !present.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The exact file-tree arguments contain unsupported or duplicate property '{property.Name}'.");
            }
        }
        if (!expected.SetEquals(present))
        {
            throw new InvalidDataException(
                "The exact file-tree arguments are missing a required property.");
        }
    }
}

internal sealed class AliFileTreeDomainPlanStore
{
    private const int MaximumBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;

    internal AliFileTreeDomainPlanStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    internal async Task<string> WriteAsync(
        AliFileTreeDomainPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var bytes = CanonicalBytes(plan);
        try
        {
            if (bytes.Length > MaximumBytes)
            {
                throw new IOException("The exact file-tree domain plan is too large.");
            }
            WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                _root,
                "The exact file-tree plan root is not a regular local directory.");
            var destination = PathFor(plan.DomainId);
            if (File.Exists(destination))
            {
                throw new InvalidOperationException(
                    "The exact file-tree domain-plan identity already exists.");
            }
            var temporary = Path.Combine(_root, "." + plan.DomainId + ".tmp");
            await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             writeThrough: true,
                             "The exact file-tree plan temporary is not a regular local file."))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporary,
                destination,
                replaceExisting: false,
                "The exact file-tree plan could not be committed safely.");
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal async Task<AliFileTreeDomainPlan> ReadAsync(
        string domainId,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        AliDurableInvocationValidation.RequireId(domainId, nameof(domainId));
        TurnStateIntegrity.RequireDigest(expectedDigest, nameof(expectedDigest));
        await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            PathFor(domainId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The exact file-tree plan is not a regular local file.");
        if (stream.Length < 1 || stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("The exact file-tree plan has an invalid size.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    TurnStateIntegrity.Digest(bytes),
                    expectedDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The exact file-tree plan digest does not match its protected invocation.");
            }
            var plan = JsonSerializer.Deserialize<AliFileTreeDomainPlan>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The exact file-tree plan is empty.");
            if (!string.Equals(plan.DomainId, domainId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The exact file-tree plan identity does not match its file.");
            }
            return plan;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] CanonicalBytes(AliFileTreeDomainPlan plan) =>
        CanonicalEvidenceJson.SerializeToUtf8Bytes(JsonSerializer.SerializeToElement(plan));

    private string PathFor(string domainId) => Path.Combine(_root, domainId + ".file-tree-plan.json");
}

/// <summary>
/// Owns the exact durable plans and effect boundary for the four ordinary workstation tree
/// mutations. Each entry point starts only its one concrete adapter tuple.
/// </summary>
internal sealed class AliFileTreeMutationCoordinator
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly AliWorkstationFileStore _store;
    private readonly AliFileTreeTargetStateAdapter _targetStates;
    private readonly AliDurableInvocationStore _invocations;
    private readonly AliFileTreeDomainPlanStore _domainPlans;
    private readonly AliFileTreeExecutionBindingStore _executionBindings;
    private readonly EvidenceLedger _evidence;
    private readonly Action<AliFileTreePreparationCheckpoint>? _preparationFaultHook;
    private readonly Action<AliFileTreeExecutionCheckpoint>? _executionFaultHook;

    internal AliFileTreeMutationCoordinator(
        AliWorkstationFileStore store,
        string durableOrchestrationRoot,
        string assistantProfileBinding,
        EvidenceLedger? evidence = null,
        Action<AliFileTreePreparationCheckpoint>? preparationFaultHook = null,
        Action<AliFileTreeExecutionCheckpoint>? executionFaultHook = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(durableOrchestrationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        var root = Path.Combine(
            Path.GetFullPath(durableOrchestrationRoot),
            "FileTreeInvocations");
        _targetStates = new AliFileTreeTargetStateAdapter(store);
        _invocations = new AliDurableInvocationStore(
            Path.Combine(root, "Kernel"),
            assistantProfileBinding);
        _domainPlans = new AliFileTreeDomainPlanStore(Path.Combine(root, "Domain"));
        _executionBindings = new AliFileTreeExecutionBindingStore(
            Path.Combine(root, "ExecutionBindings"),
            assistantProfileBinding);
        _evidence = evidence ?? new EvidenceLedger(
            Path.GetFullPath(durableOrchestrationRoot),
            assistantProfileBinding);
        _preparationFaultHook = preparationFaultHook;
        _executionFaultHook = executionFaultHook;
        TargetStateAdapters = [_targetStates];
        ExecutionEffectAdapters =
        [
            new AliFileDeleteExecutionAdapter(this),
            new AliFileMoveExecutionAdapter(this),
            new AliFileCopyExecutionAdapter(this),
            new AliFileCreateDirectoryExecutionAdapter(this)
        ];
    }

    internal IReadOnlyList<IActionTargetStateAdapter> TargetStateAdapters { get; }

    internal IReadOnlyList<IAliExecutionEffectAdapter> ExecutionEffectAdapters { get; }

    internal ValueTask<AliExecutionPreparation> PrepareDeleteAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliFileTreeOperation.Delete,
            AliCapabilityCatalog.FileDeleteName,
            cancellationToken);

    internal ValueTask<AliExecutionPreparation> PrepareMoveAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliFileTreeOperation.Move,
            AliCapabilityCatalog.FileMoveName,
            cancellationToken);

    internal ValueTask<AliExecutionPreparation> PrepareCopyAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliFileTreeOperation.Copy,
            AliCapabilityCatalog.FileCopyName,
            cancellationToken);

    internal ValueTask<AliExecutionPreparation> PrepareCreateDirectoryAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            request,
            AliFileTreeOperation.CreateDirectory,
            AliCapabilityCatalog.FileCreateDirectoryName,
            cancellationToken);

    private async ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        AliFileTreeOperation operation,
        string exactToolName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var exactIdentity = Identity(exactToolName);
        if (!exactIdentity.Matches(
                request.ToolName,
                request.CapabilityId,
                request.ReconcilerId))
        {
            throw new AliExecutionPreparationException(
                "The exact file-tree adapter received a mismatched execution identity.");
        }

        var captured = _targetStates.CaptureExact(exactToolName, request.Arguments);
        if (captured.Operation != operation
            || !string.Equals(captured.ToolName, exactToolName, StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The exact file-tree captured target does not match its adapter operation.");
        }
        var targetDigest = WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            captured.TargetState.TargetVersions);
        if (!string.Equals(targetDigest, request.TargetVersionDigest, StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The exact file-tree target changed after the accepted decision.");
        }

        _preparationFaultHook?.Invoke(AliFileTreePreparationCheckpoint.ExactTargetCaptured);
        RequireCapturedTargetStillCurrent(captured);
        var domainId = Guid.NewGuid().ToString("N");
        var domain = BuildDomainPlan(domainId, captured);
        var domainDigest = await _domainPlans.WriteAsync(domain, cancellationToken)
            .ConfigureAwait(false);
        var rootBinding = RootBinding(domain);
        var invocation = AliDurableInvocationPlan.Create(
            request,
            rootBinding,
            domainId,
            domainDigest);
        await _invocations.PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
        return new AliExecutionPreparation(
            invocation.Id,
            rootBinding,
            request.TargetVersionDigest);
    }

    private AliFileTreeDomainPlan BuildDomainPlan(
        string domainId,
        AliFileTreeCapturedTarget captured)
    {
        switch (captured.Operation)
        {
            case AliFileTreeOperation.Delete:
            {
                if (!captured.SourceBefore.Exists
                    || captured.SourceVirtualPath is null
                    || captured.SourcePhysicalPath is null)
                {
                    throw new FileNotFoundException(
                        "The exact file-tree delete source does not exist.",
                        captured.SourcePhysicalPath);
                }
                var source = captured.SourceResolution
                    ?? throw new InvalidDataException(
                        "The exact file-tree delete has no captured source resolution.");
                var trash = _store.ResolveExactTrashPath(source, domainId);
                RequirePathsDisjoint(
                    captured.SourcePhysicalPath,
                    _store.TrashRoot,
                    "The exact delete source overlaps Ali's recoverable-trash root.");
                RequirePathsDisjoint(
                    captured.SourcePhysicalPath,
                    trash,
                    "The exact delete source overlaps its recoverable-trash destination.");
                var trashBefore = AliFileTreeSnapshotter.CaptureStable(trash);
                if (trashBefore.Exists)
                {
                    throw new IOException(
                        "The exact recoverable-trash target already exists.");
                }
                var sourceParent = Path.GetDirectoryName(captured.SourcePhysicalPath)
                    ?? throw new InvalidDataException(
                        "The exact file-tree delete source has no parent directory.");
                var trashParent = Path.GetDirectoryName(trash)
                    ?? throw new InvalidDataException(
                        "The exact recoverable-trash destination has no parent directory.");
                var publicationExistingParent = DeepestExistingDirectory(
                    _store.TrashRoot,
                    trashParent);
                var sourceSpine = AliFileTreeWindowsBoundary.CaptureDirectorySpine(
                    source.MountRoot,
                    sourceParent);
                var publicationSpine = AliFileTreeWindowsBoundary.CaptureDirectorySpine(
                    _store.TrashRoot,
                    publicationExistingParent);
                return new AliFileTreeDomainPlan(
                    domainId,
                    captured.Operation,
                    captured.ToolName,
                    captured.SourceVirtualPath,
                    null,
                    captured.SourcePhysicalPath,
                    null,
                    trash,
                    null,
                    captured.SourceBefore,
                    AliFileTreeItemSnapshot.Absent,
                    trashBefore,
                    AliFileTreeItemSnapshot.Absent,
                    AliFileTreeItemSnapshot.Absent,
                    AliFileTreeItemSnapshot.Absent,
                    captured.SourceBefore,
                    AliFileTreeItemSnapshot.Absent,
                    [],
                    AliFileTreeWindowsBoundary.CaptureChildIdentity(
                        sourceSpine,
                        captured.SourcePhysicalPath,
                        captured.SourceBefore.Kind),
                    null,
                    sourceSpine[0].PhysicalPath,
                    sourceSpine[0].Identity,
                    sourceParent,
                    sourceSpine[^1].Identity,
                    publicationSpine[0].PhysicalPath,
                    publicationSpine[0].Identity,
                    trashParent,
                    null,
                    sourceSpine,
                    publicationSpine);
            }
            case AliFileTreeOperation.Move:
            case AliFileTreeOperation.Copy:
            {
                if (!captured.SourceBefore.Exists)
                {
                    throw new FileNotFoundException(
                        "The exact file-tree source does not exist.",
                        captured.SourcePhysicalPath);
                }
                if (captured.DestinationBefore.Exists)
                {
                    throw new IOException(
                        "The exact file-tree destination already exists.");
                }
                var destinationPath = captured.DestinationPhysicalPath
                    ?? throw new InvalidDataException(
                        "The exact file-tree transfer has no captured destination path.");
                var staging = captured.Operation == AliFileTreeOperation.Copy
                    ? Path.Combine(
                        Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidDataException(
                            "The exact copy destination has no parent directory."),
                        ".ali-durable-copy-" + domainId)
                    : null;
                if (string.Equals(captured.SourceBefore.Kind, "directory", StringComparison.Ordinal)
                    && staging is not null
                    && IsWithinOrEqual(staging, captured.SourcePhysicalPath!))
                {
                    throw new InvalidDataException(
                        "The exact copy staging target cannot be placed inside its source directory.");
                }
                var stagingBefore = staging is null
                    ? AliFileTreeItemSnapshot.Absent
                    : AliFileTreeSnapshotter.CaptureStable(staging);
                if (stagingBefore.Exists)
                {
                    throw new IOException(
                        "The exact copy staging target already exists.");
                }
                var source = captured.SourceResolution
                    ?? throw new InvalidDataException(
                        "The exact file-tree transfer has no source resolution.");
                var destination = captured.DestinationResolution
                    ?? throw new InvalidDataException(
                        "The exact file-tree transfer has no destination resolution.");
                var sourceParent = Path.GetDirectoryName(captured.SourcePhysicalPath!)
                    ?? throw new InvalidDataException(
                        "The exact file-tree transfer source has no parent directory.");
                var publicationParent = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidDataException(
                        "The exact file-tree destination has no parent directory.");
                var sourceSpine = AliFileTreeWindowsBoundary.CaptureDirectorySpine(
                    source.MountRoot,
                    sourceParent);
                var publicationSpine = AliFileTreeWindowsBoundary.CaptureDirectorySpine(
                    destination.MountRoot,
                    publicationParent);
                return new AliFileTreeDomainPlan(
                    domainId,
                    captured.Operation,
                    captured.ToolName,
                    captured.SourceVirtualPath,
                    captured.DestinationVirtualPath,
                    captured.SourcePhysicalPath,
                    captured.DestinationPhysicalPath,
                    null,
                    staging,
                    captured.SourceBefore,
                    captured.DestinationBefore,
                    AliFileTreeItemSnapshot.Absent,
                    stagingBefore,
                    captured.Operation == AliFileTreeOperation.Move
                        ? AliFileTreeItemSnapshot.Absent
                        : captured.SourceBefore,
                    captured.SourceBefore,
                    AliFileTreeItemSnapshot.Absent,
                    AliFileTreeItemSnapshot.Absent,
                    [],
                    AliFileTreeWindowsBoundary.CaptureChildIdentity(
                        sourceSpine,
                        captured.SourcePhysicalPath!,
                        captured.SourceBefore.Kind),
                    null,
                    sourceSpine[0].PhysicalPath,
                    sourceSpine[0].Identity,
                    sourceParent,
                    sourceSpine[^1].Identity,
                    publicationSpine[0].PhysicalPath,
                    publicationSpine[0].Identity,
                    publicationParent,
                    publicationSpine[^1].Identity,
                    sourceSpine,
                    publicationSpine);
            }
            case AliFileTreeOperation.CreateDirectory:
            {
                var before = captured.DestinationBefore;
                if (before.Exists && !string.Equals(before.Kind, "directory", StringComparison.Ordinal))
                {
                    throw new IOException(
                        "The exact directory creation target is an existing file.");
                }
                var after = before.Exists
                    ? before
                    : captured.DirectoryChain.LastOrDefault()?.After
                        ?? AliFileTreeSnapshotter.EmptyDirectory;
                var firstMissing = captured.DirectoryChain.FirstOrDefault();
                var staging = firstMissing is null
                    ? null
                    : Path.Combine(
                        Path.GetDirectoryName(firstMissing.PhysicalPath)
                        ?? throw new InvalidDataException(
                            "The exact directory chain has no existing parent."),
                        ".ali-durable-create-directory-" + domainId);
                var stagingBefore = staging is null
                    ? AliFileTreeItemSnapshot.Absent
                    : AliFileTreeSnapshotter.CaptureStable(staging);
                if (stagingBefore.Exists)
                {
                    throw new IOException(
                        "The exact directory staging target already exists.");
                }
                var destination = captured.DestinationResolution
                    ?? throw new InvalidDataException(
                        "The exact directory creation has no destination resolution.");
                var publicationParent = Path.GetDirectoryName(
                        firstMissing?.PhysicalPath ?? captured.DestinationPhysicalPath!)
                    ?? throw new InvalidDataException(
                        "The exact directory creation target has no publication parent.");
                var publicationSpine = AliFileTreeWindowsBoundary.CaptureDirectorySpine(
                    destination.MountRoot,
                    publicationParent);
                return new AliFileTreeDomainPlan(
                    domainId,
                    captured.Operation,
                    captured.ToolName,
                    null,
                    captured.DestinationVirtualPath,
                    null,
                    captured.DestinationPhysicalPath,
                    null,
                    staging,
                    AliFileTreeItemSnapshot.Absent,
                    before,
                    AliFileTreeItemSnapshot.Absent,
                    stagingBefore,
                    AliFileTreeItemSnapshot.Absent,
                    after,
                    AliFileTreeItemSnapshot.Absent,
                    AliFileTreeItemSnapshot.Absent,
                    captured.DirectoryChain,
                    null,
                    before.Exists
                        ? AliFileTreeWindowsBoundary.CaptureChildIdentity(
                            publicationSpine,
                            captured.DestinationPhysicalPath!,
                            before.Kind)
                        : null,
                    null,
                    null,
                    null,
                    null,
                    publicationSpine[0].PhysicalPath,
                    publicationSpine[0].Identity,
                    publicationParent,
                    publicationSpine[^1].Identity,
                    [],
                    publicationSpine);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(captured));
        }
    }

    private static void RequireCapturedTargetStillCurrent(
        AliFileTreeCapturedTarget captured)
    {
        if (captured.Operation is AliFileTreeOperation.Copy or AliFileTreeOperation.Move)
        {
            RequireExistingDestinationParent(
                captured.DestinationPhysicalPath
                ?? throw new AliExecutionPreparationException(
                    "The exact file-tree transfer has no destination path."));
        }
        if (captured.SourcePhysicalPath is not null
            && AliFileTreeSnapshotter.CaptureStable(captured.SourcePhysicalPath)
                != captured.SourceBefore)
        {
            throw new AliExecutionPreparationException(
                "The exact file-tree source changed during preparation.");
        }
        if (captured.DestinationPhysicalPath is not null
            && AliFileTreeSnapshotter.CaptureStable(captured.DestinationPhysicalPath)
                != captured.DestinationBefore)
        {
            throw new AliExecutionPreparationException(
                "The exact file-tree destination changed during preparation.");
        }
        foreach (var binding in captured.DirectoryChain)
        {
            if (AliFileTreeSnapshotter.CaptureStable(binding.PhysicalPath) != binding.Before)
            {
                throw new AliExecutionPreparationException(
                    "The exact directory chain changed during preparation.");
            }
        }
    }

    internal async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var invocation = await BeginAsync(
                Identity(AliCapabilityCatalog.FileDeleteName),
                cancellationToken)
            .ConfigureAwait(false);
        RequireRuntimePath(invocation.Domain.SourceVirtualPath, path, "delete source");
        RequirePreState(invocation.Domain);
        var domain = invocation.Domain;
        using var sourceSpine = OpenSourceSpine(domain, writableParent: true);
        using var source = OpenPreparedSource(domain, sourceSpine, renameable: true);
        RequireBoundSnapshot(source, domain.SourcePhysicalPath!, domain.SourceBefore);
        using var sourceClosure = AliFileTreeWindowsBoundary.AcquireExactTreeLease(
            source,
            domain.SourcePhysicalPath!,
            domain.SourceBefore,
            rootHasDeleteAccess: true);
        using var trashSpine = OpenPublicationSpine(domain);
        AliFileTreeWindowsBoundary.ExtendDirectorySpine(
            trashSpine,
            domain.PublicationParentPhysicalPath);
        _ = PersistExecutionBinding(invocation, trashSpine, staging: null);
        _executionFaultHook?.Invoke(
            AliFileTreeExecutionCheckpoint.DeleteExecutionBindingPersisted);
        cancellationToken.ThrowIfCancellationRequested();
        RequireAbsent(domain.TrashPhysicalPath!, "recoverable-trash destination");
        _executionFaultHook?.Invoke(AliFileTreeExecutionCheckpoint.DeleteBeforeHandleRename);
        RenameAndVerifyOrRollback(
            source,
            sourceSpine,
            domain.SourcePhysicalPath!,
            trashSpine,
            domain.TrashPhysicalPath!,
            domain.TrashAfter,
            AliFileTreeExecutionCheckpoint.DeleteAfterHandleRename,
            sourceClosure,
            cancellationToken);
        return true;
    }

    internal async Task<WorkstationFileMoveResult> MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var invocation = await BeginAsync(
                    Identity(AliCapabilityCatalog.FileMoveName),
                    cancellationToken)
                .ConfigureAwait(false);
            RequireRuntimePath(invocation.Domain.SourceVirtualPath, sourcePath, "move source");
            RequireRuntimePath(
                invocation.Domain.DestinationVirtualPath,
                destinationPath,
                "move destination");
            var domain = invocation.Domain;
            RequirePreState(domain);
            using var sourceSpine = OpenSourceSpine(domain, writableParent: true);
            using var publicationSpine = OpenPublicationSpine(domain);
            using var source = OpenPreparedSource(domain, sourceSpine, renameable: true);
            RequireBoundSnapshot(source, domain.SourcePhysicalPath!, domain.SourceBefore);
            using var sourceClosure = AliFileTreeWindowsBoundary.AcquireExactTreeLease(
                source,
                domain.SourcePhysicalPath!,
                domain.SourceBefore,
                rootHasDeleteAccess: true);
            RequireAbsent(domain.DestinationPhysicalPath!, "move destination");
            _executionFaultHook?.Invoke(AliFileTreeExecutionCheckpoint.MoveBeforeHandleRename);
            RenameAndVerifyOrRollback(
                source,
                sourceSpine,
                domain.SourcePhysicalPath!,
                publicationSpine,
                domain.DestinationPhysicalPath!,
                domain.DestinationAfter,
                AliFileTreeExecutionCheckpoint.MoveAfterHandleRename,
                sourceClosure,
                cancellationToken);
            return new WorkstationFileMoveResult(
                true,
                sourcePath,
                destinationPath,
                "The file or folder was moved successfully.");
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return new WorkstationFileMoveResult(
                false,
                sourcePath,
                destinationPath,
                exception.Message);
        }
    }

    internal async Task<WorkstationFileOperationResult> CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var invocation = await BeginAsync(
                    Identity(AliCapabilityCatalog.FileCopyName),
                    cancellationToken)
                .ConfigureAwait(false);
            RequireRuntimePath(invocation.Domain.SourceVirtualPath, sourcePath, "copy source");
            RequireRuntimePath(
                invocation.Domain.DestinationVirtualPath,
                destinationPath,
                "copy destination");
            RequirePreState(invocation.Domain);
            CopyExact(invocation, cancellationToken);
            return new WorkstationFileOperationResult(
                true,
                sourcePath,
                destinationPath,
                "The file or folder was copied successfully without overwriting anything.");
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return new WorkstationFileOperationResult(
                false,
                sourcePath,
                destinationPath,
                exception.Message);
        }
    }

    internal async Task<WorkstationFileOperationResult> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var invocation = await BeginAsync(
                    Identity(AliCapabilityCatalog.FileCreateDirectoryName),
                    cancellationToken)
                .ConfigureAwait(false);
            RequireRuntimePath(
                invocation.Domain.DestinationVirtualPath,
                path,
                "directory destination");
            RequirePreState(invocation.Domain);
            PublishDirectoryChain(invocation, cancellationToken);
            var existed = invocation.Domain.DestinationBefore.Exists;
            return new WorkstationFileOperationResult(
                true,
                string.Empty,
                path,
                existed
                    ? "The folder already existed."
                    : "The folder was created successfully.");
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return new WorkstationFileOperationResult(
                false,
                string.Empty,
                path,
                exception.Message);
        }
    }

    internal async ValueTask<ActionReconciliationResult> ReconcileAsync(
        AliExactExecutionAdapterIdentity exactIdentity,
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(intent);
        if (!exactIdentity.Matches(
                intent.ToolName,
                intent.CapabilityId,
                intent.ReconcilerId)
            || string.IsNullOrWhiteSpace(intent.PreparationIdentity))
        {
            return ActionReconciliationResult.Unknown(
                "file-tree-adapter-identity-mismatch");
        }
        if (!AliExecutionAuthorizationDigest.TryCompute(
                AliDurableInvocationStore.AuthorizationDomain,
                intent,
                out var authorizationDigest))
        {
            return ActionReconciliationResult.Unknown(
                "file-tree-authorization-identity-missing");
        }

        try
        {
            var recovery = await new AliDurableInvocationReconciler(
                    _invocations,
                    exactIdentity,
                    new StartedDomainReconciler(this, exactIdentity))
                .ReconcileAsync(
                    intent.PreparationIdentity!,
                    authorizationDigest,
                    cancellationToken)
                .ConfigureAwait(false);
            return recovery.Disposition switch
            {
                AliDurableInvocationRecoveryDisposition.Applied =>
                    ActionReconciliationResult.Applied(
                        recovery.OutcomeCode,
                        await AppendEvidenceAsync(
                                identity,
                                intent,
                                recovery.OutcomeCode,
                                cancellationToken)
                            .ConfigureAwait(false)),
                AliDurableInvocationRecoveryDisposition.Absent =>
                    ActionReconciliationResult.Absent(recovery.OutcomeCode),
                AliDurableInvocationRecoveryDisposition.Failed =>
                    ActionReconciliationResult.Absent(recovery.OutcomeCode),
                _ => ActionReconciliationResult.Unknown(recovery.OutcomeCode)
            };
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return ActionReconciliationResult.Unknown(
                "file-tree-reconcile-" + StableExceptionCode(exception));
        }
    }

    private async Task<AliFileTreeActiveInvocation> BeginAsync(
        AliExactExecutionAdapterIdentity exactIdentity,
        CancellationToken cancellationToken)
    {
        var started = await AliDurableInvocationGrantConsumer.ConsumeCurrentAndStartAsync(
                _invocations,
                exactIdentity,
                cancellationToken)
            .ConfigureAwait(false);
        var domain = await _domainPlans.ReadAsync(
                started.Plan.DomainPreparationIdentity,
                started.Plan.DomainPreparationDigest,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(domain.ToolName, exactIdentity.ToolName, StringComparison.Ordinal)
            || !string.Equals(RootBinding(domain), started.Plan.RootBinding, StringComparison.Ordinal))
        {
            await _invocations.MarkInDoubtAsync(
                    started.Plan.Id,
                    expectedRevision: 1,
                    "file-tree-domain-plan-mismatch",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The exact file-tree domain plan does not match its durable invocation.");
        }
        var startedReceipt = started.Receipt
            ?? throw new InvalidDataException(
                "The durable file-tree invocation has no Started receipt.");
        var participant = new CompletionParticipant(
            this,
            started.Plan.Id,
            started.Plan.DomainPreparationDigest,
            startedReceipt.AuthorizationDigest,
            domain);
        if (!AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                exactIdentity.ToolName,
                exactIdentity.CapabilityId,
                exactIdentity.ReconcilerId,
                started.Plan.Id,
                started.Plan.RootBinding,
                participant))
        {
            await _invocations.MarkInDoubtAsync(
                    started.Plan.Id,
                    expectedRevision: 1,
                    "file-tree-completion-registration-failed",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The exact file-tree completion participant could not be registered.");
        }
        return new AliFileTreeActiveInvocation(
            started.Plan,
            domain,
            started.Plan.DomainPreparationDigest,
            startedReceipt.AuthorizationDigest);
    }

    private static void RequireRuntimePath(string? expected, string actual, string label)
    {
        if (!string.Equals(expected, actual.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The exact {label} does not match the authenticated arguments.");
        }
    }

    private static void RequirePreState(AliFileTreeDomainPlan domain)
    {
        using var sourceSpine = domain.SourceNamespaceSpine.Count == 0
            ? null
            : AliFileTreeWindowsBoundary.OpenDirectorySpine(domain.SourceNamespaceSpine);
        using var publicationSpine = AliFileTreeWindowsBoundary.OpenDirectorySpine(
            domain.PublicationNamespaceSpine);
        RequirePreparedIdentity(
            domain.SourceAnchorPhysicalPath,
            "directory",
            domain.SourceAnchorIdentity,
            "source anchor");
        RequirePreparedIdentity(
            domain.SourceParentPhysicalPath,
            "directory",
            domain.SourceParentIdentity,
            "source parent");
        RequirePreparedIdentity(
            domain.PublicationAnchorPhysicalPath,
            "directory",
            domain.PublicationAnchorIdentity,
            "publication anchor");
        RequirePreparedIdentity(
            domain.PublicationParentPhysicalPath,
            "directory",
            domain.PublicationParentIdentity,
            "publication parent");
        if (domain.SourcePhysicalPath is not null)
        {
            RequirePreparedIdentity(
                domain.SourcePhysicalPath,
                domain.SourceBefore.Kind,
                domain.SourceObjectIdentity,
                "source object");
        }
        if (domain.DestinationBefore.Exists)
        {
            RequirePreparedIdentity(
                domain.DestinationPhysicalPath,
                domain.DestinationBefore.Kind,
                domain.DestinationObjectIdentity,
                "existing destination object");
        }
        if (domain.SourcePhysicalPath is not null
            && AliFileTreeSnapshotter.CaptureStable(domain.SourcePhysicalPath) != domain.SourceBefore)
        {
            throw new IOException("The exact file-tree source changed before execution.");
        }
        if (domain.DestinationPhysicalPath is not null
            && AliFileTreeSnapshotter.CaptureStable(domain.DestinationPhysicalPath)
                != domain.DestinationBefore)
        {
            throw new IOException("The exact file-tree destination changed before execution.");
        }
        if (domain.TrashPhysicalPath is not null
            && AliFileTreeSnapshotter.CaptureStable(domain.TrashPhysicalPath) != domain.TrashBefore)
        {
            throw new IOException("The exact recoverable-trash target changed before execution.");
        }
        if (domain.StagingPhysicalPath is not null
            && AliFileTreeSnapshotter.CaptureStable(domain.StagingPhysicalPath)
                != domain.StagingBefore)
        {
            throw new IOException("The exact copy staging target changed before execution.");
        }
        foreach (var binding in domain.DirectoryChain)
        {
            if (AliFileTreeSnapshotter.CaptureStable(binding.PhysicalPath) != binding.Before)
            {
                throw new IOException(
                    "The exact directory chain changed before execution.");
            }
        }
    }

    private static void RequirePreparedIdentity(
        string? path,
        string kind,
        string? expectedIdentity,
        string label)
    {
        if (expectedIdentity is null)
        {
            return;
        }
        if (path is null
            || !AliFileTreeWindowsBoundary.PathMatchesIdentity(path, kind, expectedIdentity))
        {
            throw new IOException(
                $"The exact file-tree {label} identity changed after preparation.");
        }
    }

    private void PublishDirectoryChain(
        AliFileTreeActiveInvocation invocation,
        CancellationToken cancellationToken)
    {
        var domain = invocation.Domain;
        if (domain.DirectoryChain.Count == 0)
        {
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                domain.DestinationPhysicalPath!,
                "The exact directory target is not a regular local directory.");
            if (domain.DestinationObjectIdentity is null
                || !AliFileTreeWindowsBoundary.PathMatchesIdentity(
                    domain.DestinationPhysicalPath!,
                    "directory",
                    domain.DestinationObjectIdentity))
            {
                throw new IOException(
                    "The exact existing directory target changed after authorization.");
            }
            return;
        }

        var staging = domain.StagingPhysicalPath
            ?? throw new InvalidDataException(
                "The exact directory plan has no authenticated staging target.");
        var firstMissing = domain.DirectoryChain[0];
        var stagingParent = Path.GetDirectoryName(staging)
            ?? throw new InvalidDataException(
                "The exact directory staging target has no parent.");
        if (!string.Equals(
                NormalizePhysicalPath(stagingParent),
                NormalizePhysicalPath(Path.GetDirectoryName(firstMissing.PhysicalPath)
                    ?? throw new InvalidDataException(
                        "The exact directory chain has no existing parent.")),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact directory staging target is not beside the first missing directory.");
        }

        using var publicationSpine = OpenPublicationSpine(domain);
        using var stagingObject = AliFileTreeWindowsBoundary.CreateBoundDirectory(
            publicationSpine,
            staging);
        var executionBinding = PersistExecutionBinding(
            invocation,
            publicationSpine,
            stagingObject);
        _executionFaultHook?.Invoke(
            AliFileTreeExecutionCheckpoint.DirectoryCreateExecutionBindingPersisted);
        var stagedChildren = new List<AliFileTreeBoundObject>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _executionFaultHook?.Invoke(
                AliFileTreeExecutionCheckpoint.DirectoryStagingRootCreated);

            var current = staging;
            var currentObject = stagingObject;
            for (var index = 1; index < domain.DirectoryChain.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedParent = Path.GetDirectoryName(
                    domain.DirectoryChain[index].PhysicalPath)
                    ?? throw new InvalidDataException(
                        "The exact directory chain entry has no parent.");
                if (!string.Equals(
                        NormalizePhysicalPath(expectedParent),
                        NormalizePhysicalPath(domain.DirectoryChain[index - 1].PhysicalPath),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The exact directory plan does not describe one contiguous chain.");
                }
                var child = Path.Combine(
                    current,
                    Path.GetFileName(domain.DirectoryChain[index].PhysicalPath));
                var childObject = AliFileTreeWindowsBoundary.CreateBoundChildDirectory(
                    currentObject,
                    Path.GetFileName(child));
                stagedChildren.Add(childObject);
                currentObject = childObject;
                current = child;
                _executionFaultHook?.Invoke(
                    AliFileTreeExecutionCheckpoint.DirectoryStagingChildCreated);
            }

            if (AliFileTreeWindowsBoundary.CaptureBoundSnapshot(stagingObject, staging)
                != firstMissing.After)
            {
                throw new IOException(
                    "The exact staged directory chain does not match its authenticated post-state.");
            }
            for (var index = stagedChildren.Count - 1; index >= 0; index--)
            {
                stagedChildren[index].Dispose();
            }
            stagedChildren.Clear();
            var stagingIdentity = stagingObject.Identity;
            stagingObject.Dispose();
            using var sealedStagingObject =
                AliFileTreeWindowsBoundary.OpenBoundRenameChild(
                    publicationSpine.ParentHandle,
                    Path.GetFileName(staging),
                    "directory",
                    stagingIdentity);
            using var stagingClosure =
                AliFileTreeWindowsBoundary.AcquireExactTreeLease(
                    sealedStagingObject,
                    staging,
                    firstMissing.After,
                    rootHasDeleteAccess: true);
            RequireDirectoryChainState(domain, useAfter: false);
            _executionFaultHook?.Invoke(
                AliFileTreeExecutionCheckpoint.DirectoryCreateBeforeHandleRename);
            RenameAndVerifyOrRollback(
                sealedStagingObject,
                publicationSpine,
                staging,
                publicationSpine,
                firstMissing.PhysicalPath,
                firstMissing.After,
                AliFileTreeExecutionCheckpoint.DirectoryCreateAfterHandleRename,
                stagingClosure,
                cancellationToken);
            _executionFaultHook?.Invoke(
                AliFileTreeExecutionCheckpoint.DirectoryChainPublished);
            RequireDirectoryChainState(domain, useAfter: true);
        }
        catch (AliFileTreeSimulatedInterruptionException)
        {
            throw;
        }
        catch
        {
            for (var index = stagedChildren.Count - 1; index >= 0; index--)
            {
                stagedChildren[index].Dispose();
            }
            stagedChildren.Clear();
            stagingObject.Dispose();
            _ = TryCompensateDirectoryStaging(domain, executionBinding);
            throw;
        }
        finally
        {
            for (var index = stagedChildren.Count - 1; index >= 0; index--)
            {
                stagedChildren[index].Dispose();
            }
        }
    }

    private void CopyExact(
        AliFileTreeActiveInvocation invocation,
        CancellationToken cancellationToken)
    {
        var domain = invocation.Domain;
        var destination = domain.DestinationPhysicalPath!;
        var staging = domain.StagingPhysicalPath
            ?? throw new InvalidDataException(
                "The exact copy plan has no authenticated staging target.");
        using var sourceSpine = OpenSourceSpine(domain, writableParent: false);
        using var publicationSpine = OpenPublicationSpine(domain);
        using var sourceObject = OpenPreparedSource(domain, sourceSpine, renameable: false);
        RequireBoundSnapshot(sourceObject, domain.SourcePhysicalPath!, domain.SourceBefore);
        using var writableStagingObject = string.Equals(
                domain.SourceBefore.Kind,
                "file",
                StringComparison.Ordinal)
            ? AliFileTreeWindowsBoundary.CreateBoundRegularFile(publicationSpine, staging)
            : AliFileTreeWindowsBoundary.CreateBoundDirectory(publicationSpine, staging);
        _ = PersistExecutionBinding(invocation, publicationSpine, writableStagingObject);
        _executionFaultHook?.Invoke(
            AliFileTreeExecutionCheckpoint.CopyExecutionBindingPersisted);

        if (string.Equals(domain.SourceBefore.Kind, "file", StringComparison.Ordinal))
        {
            AliFileTreeWindowsBoundary.CopyBoundRegularFile(
                sourceObject,
                writableStagingObject,
                CopyBufferSize,
                () => _executionFaultHook?.Invoke(
                    AliFileTreeExecutionCheckpoint.CopyChunkWritten),
                cancellationToken);
        }
        else
        {
            CopyBoundDirectoryContents(
                sourceObject,
                domain.SourcePhysicalPath!,
                writableStagingObject,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _executionFaultHook?.Invoke(AliFileTreeExecutionCheckpoint.CopyStagingPopulated);
        RequireBoundSnapshot(writableStagingObject, staging, domain.DestinationAfter);
        RequireBoundSnapshot(sourceObject, domain.SourcePhysicalPath!, domain.SourceBefore);
        var stagingIdentity = writableStagingObject.Identity;
        writableStagingObject.Dispose();
        using var stagingObject = AliFileTreeWindowsBoundary.OpenBoundRenameChild(
            publicationSpine.ParentHandle,
            Path.GetFileName(staging),
            domain.DestinationAfter.Kind,
            stagingIdentity);
        using var sourceClosure = AliFileTreeWindowsBoundary.AcquireExactTreeLease(
            sourceObject,
            domain.SourcePhysicalPath!,
            domain.SourceBefore,
            rootHasDeleteAccess: false);
        using var stagingClosure = AliFileTreeWindowsBoundary.AcquireExactTreeLease(
            stagingObject,
            staging,
            domain.DestinationAfter,
            rootHasDeleteAccess: true);
        RequireAbsent(destination, "copy destination");
        _executionFaultHook?.Invoke(AliFileTreeExecutionCheckpoint.CopyBeforeHandleRename);
        RenameAndVerifyOrRollback(
            stagingObject,
            publicationSpine,
            staging,
            publicationSpine,
            destination,
            domain.DestinationAfter,
            AliFileTreeExecutionCheckpoint.CopyAfterHandleRename,
            stagingClosure,
            cancellationToken);
        sourceClosure.RequireStable(
            domain.SourcePhysicalPath!,
            domain.SourceBefore);
    }

    private void CopyBoundDirectoryContents(
        AliFileTreeBoundObject sourceDirectory,
        string sourcePath,
        AliFileTreeBoundObject destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(sourceDirectory.Kind, "directory", StringComparison.Ordinal)
            || !string.Equals(destinationDirectory.Kind, "directory", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact recursive copy requires two held directory objects.");
        }
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            sourcePath,
            "The exact copy source is not a regular local directory.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourcePath)
                     .OrderBy(item => Path.GetFileName(item), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leaf = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    "The exact copy source contains a reparse point or device entry.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var sourceChild = AliFileTreeWindowsBoundary.OpenBoundChild(
                    sourceDirectory.Handle,
                    leaf,
                    "directory");
                _executionFaultHook?.Invoke(
                    AliFileTreeExecutionCheckpoint.CopySourceDirectoryChildOpened);
                using var destinationChild =
                    AliFileTreeWindowsBoundary.CreateBoundChildDirectory(
                        destinationDirectory,
                        leaf);
                _executionFaultHook?.Invoke(
                    AliFileTreeExecutionCheckpoint.CopyDestinationDirectoryChildCreated);
                CopyBoundDirectoryContents(
                    sourceChild,
                    entry,
                    destinationChild,
                    cancellationToken);
            }
            else
            {
                using var sourceChild = AliFileTreeWindowsBoundary.OpenBoundChild(
                    sourceDirectory.Handle,
                    leaf,
                    "file");
                using var destinationChild =
                    AliFileTreeWindowsBoundary.CreateBoundChildRegularFile(
                        destinationDirectory,
                        leaf);
                AliFileTreeWindowsBoundary.CopyBoundRegularFile(
                    sourceChild,
                    destinationChild,
                    CopyBufferSize,
                    () => _executionFaultHook?.Invoke(
                        AliFileTreeExecutionCheckpoint.CopyChunkWritten),
                    cancellationToken);
            }
        }
    }

    private AliFileTreeDirectorySpine OpenSourceSpine(
        AliFileTreeDomainPlan domain,
        bool writableParent)
    {
        if (domain.SourceNamespaceSpine.Count == 0)
        {
            throw new InvalidDataException(
                "The exact file-tree source has no authenticated namespace spine.");
        }
        return AliFileTreeWindowsBoundary.OpenDirectorySpine(
            domain.SourceNamespaceSpine,
            writableParent);
    }

    private static AliFileTreeDirectorySpine OpenPublicationSpine(
        AliFileTreeDomainPlan domain)
    {
        if (domain.PublicationNamespaceSpine.Count == 0)
        {
            throw new InvalidDataException(
                "The exact file-tree publication has no authenticated namespace spine.");
        }
        return AliFileTreeWindowsBoundary.OpenDirectorySpine(
            domain.PublicationNamespaceSpine,
            writableParent: true);
    }

    private static AliFileTreeBoundObject OpenPreparedSource(
        AliFileTreeDomainPlan domain,
        AliFileTreeDirectorySpine sourceParent,
        bool renameable)
    {
        var sourcePath = domain.SourcePhysicalPath
            ?? throw new InvalidDataException("The exact file-tree operation has no source path.");
        if (!string.Equals(
                NormalizePhysicalPath(sourceParent.ParentPath),
                NormalizePhysicalPath(Path.GetDirectoryName(sourcePath)
                    ?? throw new InvalidDataException(
                        "The exact file-tree source has no parent path.")),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The exact file-tree source is not beneath its held source parent.");
        }
        var expectedIdentity = domain.SourceObjectIdentity
            ?? throw new InvalidDataException(
                "The exact file-tree operation has no prepared source identity.");
        return renameable
            ? AliFileTreeWindowsBoundary.OpenBoundRenameChild(
                sourceParent.ParentHandle,
                Path.GetFileName(sourcePath),
                domain.SourceBefore.Kind,
                expectedIdentity)
            : AliFileTreeWindowsBoundary.OpenBoundChild(
                sourceParent.ParentHandle,
                Path.GetFileName(sourcePath),
                domain.SourceBefore.Kind,
                expectedIdentity);
    }

    private AliFileTreeExecutionBinding PersistExecutionBinding(
        AliFileTreeActiveInvocation invocation,
        AliFileTreeDirectorySpine publicationParent,
        AliFileTreeBoundObject? staging)
    {
        var domain = invocation.Domain;
        if (!string.Equals(
                NormalizePhysicalPath(publicationParent.ParentPath),
                NormalizePhysicalPath(domain.PublicationParentPhysicalPath),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The held publication parent does not match the durable file-tree plan.");
        }
        if (domain.PublicationParentIdentity is not null
            && !string.Equals(
                domain.PublicationParentIdentity,
                publicationParent.ParentIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The file-tree publication parent changed after preparation.");
        }
        var binding = new AliFileTreeExecutionBinding(
            1,
            domain.DomainId,
            invocation.DomainDigest,
            invocation.AuthorizationDigest,
            domain.PublicationParentPhysicalPath,
            publicationParent.ParentIdentity,
            publicationParent.Bindings,
            staging is null ? null : domain.StagingPhysicalPath,
            staging?.Identity);
        _executionBindings.WriteOnce(binding);
        return binding;
    }

    private static void RequireBoundSnapshot(
        AliFileTreeBoundObject item,
        string path,
        AliFileTreeItemSnapshot expected)
    {
        if (AliFileTreeWindowsBoundary.CaptureBoundSnapshot(item, path) != expected)
        {
            throw new IOException(
                "The exact held file-tree object does not match its authenticated state.");
        }
    }

    private static void RequireAbsent(string path, string label)
    {
        if (AliFileTreeSnapshotter.CaptureStable(path) != AliFileTreeItemSnapshot.Absent)
        {
            throw new IOException($"The exact {label} is no longer absent.");
        }
    }

    private void RenameAndVerifyOrRollback(
        AliFileTreeBoundObject item,
        AliFileTreeDirectorySpine originalParent,
        string originalPath,
        AliFileTreeDirectorySpine destinationParent,
        string destinationPath,
        AliFileTreeItemSnapshot expectedPostimage,
        AliFileTreeExecutionCheckpoint afterRenameCheckpoint,
        AliFileTreeExactTreeLease treeLease,
        CancellationToken cancellationToken)
    {
        AliFileTreeWindowsBoundary.RenameNoReplace(
            item,
            destinationParent,
            Path.GetFileName(destinationPath));
        try
        {
            _executionFaultHook?.Invoke(afterRenameCheckpoint);
            cancellationToken.ThrowIfCancellationRequested();
            treeLease.RequireStable(destinationPath, expectedPostimage);
            RequireAbsent(originalPath, "pre-publication location");
        }
        catch (AliFileTreeSimulatedInterruptionException)
        {
            throw;
        }
        catch (Exception commitException)
        {
            try
            {
                RequireAbsent(originalPath, "rollback destination");
                AliFileTreeWindowsBoundary.RenameNoReplace(
                    item,
                    originalParent,
                    Path.GetFileName(originalPath));
                if (!AliFileTreeWindowsBoundary.PathMatchesIdentity(
                        originalPath,
                        item.Kind,
                        item.Identity))
                {
                    throw new IOException(
                        "The exact file-tree rollback did not restore the held object identity.");
                }
                treeLease.RequireStable(originalPath, expectedPostimage);
            }
            catch (Exception rollbackException)
            {
                throw new IOException(
                    "The exact file-tree postimage failed and its held-object rollback also failed.",
                    new AggregateException(commitException, rollbackException));
            }
            throw;
        }
    }

    private static void RequireExistingDestinationParent(string destination)
    {
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidDataException(
                "The exact file-tree destination has no parent directory."),
            "The exact file-tree destination parent must already exist as a regular local directory.");
    }

    private static string DeepestExistingDirectory(string anchorPath, string targetPath)
    {
        var anchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchorPath));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        if (!IsWithinOrEqual(target, anchor))
        {
            throw new InvalidDataException(
                "The exact file-tree target escaped its durable publication anchor.");
        }
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            anchor,
            "The exact file-tree publication anchor is not a regular local directory.");
        var current = anchor;
        var relative = Path.GetRelativePath(anchor, target);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return current;
        }
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, component);
            if (!Directory.Exists(next))
            {
                if (File.Exists(next)
                    || AliFileTreeSnapshotter.CaptureStable(next).Exists)
                {
                    throw new InvalidDataException(
                        "A non-directory entry blocks the exact file-tree publication path.");
                }
                break;
            }
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                next,
                "The exact file-tree publication path contains a reparse point or non-regular directory.");
            current = next;
        }
        return current;
    }

    private static void RequireDirectoryChainState(
        AliFileTreeDomainPlan domain,
        bool useAfter)
    {
        if (domain.DirectoryChain.Count > 0)
        {
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                Path.GetDirectoryName(domain.DirectoryChain[0].PhysicalPath)
                ?? throw new InvalidDataException(
                    "The exact directory chain has no existing anchor."),
                "The exact directory chain anchor is not a regular local directory.");
        }
        foreach (var binding in domain.DirectoryChain)
        {
            var expected = useAfter ? binding.After : binding.Before;
            if (AliFileTreeSnapshotter.CaptureStable(binding.PhysicalPath) != expected)
            {
                throw new IOException(useAfter
                    ? "The exact directory chain was not published completely."
                    : "The exact directory chain changed before publication.");
            }
        }
    }

    private static bool DirectoryChainMatches(
        AliFileTreeDomainPlan domain,
        bool useAfter)
    {
        try
        {
            RequireDirectoryChainState(domain, useAfter);
            return true;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private static bool TryCompensateDirectoryStaging(
        AliFileTreeDomainPlan domain,
        AliFileTreeExecutionBinding? executionBinding)
    {
        if (domain.Operation != AliFileTreeOperation.CreateDirectory
            || domain.StagingPhysicalPath is null
            || domain.DirectoryChain.Count == 0
            || executionBinding?.StagingPhysicalPath is null
            || executionBinding.StagingObjectIdentity is null
            || !string.Equals(
                NormalizePhysicalPath(executionBinding.StagingPhysicalPath),
                NormalizePhysicalPath(domain.StagingPhysicalPath),
                StringComparison.Ordinal)
            || !string.Equals(
                NormalizePhysicalPath(executionBinding.PublicationParentPhysicalPath),
                NormalizePhysicalPath(domain.PublicationParentPhysicalPath),
                StringComparison.Ordinal))
        {
            return false;
        }
        if (!DirectoryChainMatches(domain, useAfter: false)
            && !DirectoryChainMatches(domain, useAfter: true))
        {
            return false;
        }

        var staging = domain.StagingPhysicalPath;
        try
        {
            if (AliFileTreeSnapshotter.CaptureStable(staging)
                == AliFileTreeItemSnapshot.Absent)
            {
                return true;
            }
            using var parent = AliFileTreeWindowsBoundary.OpenDirectorySpine(
                domain.PublicationNamespaceSpine,
                writableParent: true);
            if (!string.Equals(
                    parent.ParentIdentity,
                    executionBinding.PublicationParentIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
            var opened = new List<(AliFileTreeBoundObject Object, string Path)>();
            var root = AliFileTreeWindowsBoundary.OpenBoundChildForDelete(
                parent.ParentHandle,
                Path.GetFileName(staging),
                executionBinding.StagingObjectIdentity);
            opened.Add((root, staging));
            try
            {
                var current = root;
                var currentPath = staging;
                for (var index = 1; index < domain.DirectoryChain.Count; index++)
                {
                    var entries = Directory.EnumerateFileSystemEntries(currentPath).ToArray();
                    if (entries.Length == 0)
                    {
                        break;
                    }
                    var expectedName = Path.GetFileName(
                        domain.DirectoryChain[index].PhysicalPath);
                    if (entries.Length != 1
                        || !string.Equals(
                            Path.GetFileName(entries[0]),
                            expectedName,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                    current = AliFileTreeWindowsBoundary.OpenBoundChildForDelete(
                        current,
                        expectedName);
                    currentPath = entries[0];
                    opened.Add((current, currentPath));
                }
                if (Directory.EnumerateFileSystemEntries(currentPath).Any())
                {
                    return false;
                }
                for (var index = opened.Count - 1; index >= 0; index--)
                {
                    AliFileTreeWindowsBoundary.DeleteBoundEmptyDirectory(
                        opened[index].Object);
                }
                return AliFileTreeSnapshotter.CaptureStable(staging)
                    == AliFileTreeItemSnapshot.Absent;
            }
            finally
            {
                for (var index = opened.Count - 1; index >= 0; index--)
                {
                    opened[index].Object.Dispose();
                }
            }
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private AliFileTreeClassification Classify(
        AliFileTreeDomainPlan domain,
        string domainDigest,
        string authorizationDigest,
        bool allowIdentityBoundRollback)
    {
        var requiresBinding = RequiresExecutionBinding(domain);
        var binding = requiresBinding
            ? _executionBindings.TryRead(
                domain.DomainId,
                domainDigest,
                authorizationDigest)
            : null;
        if (!PreparedNamespaceSpinesMatch(domain))
        {
            return AliFileTreeClassification.Unknown;
        }
        if (requiresBinding && binding is null)
        {
            if (ExecutionArtifactExistsWithoutBinding(domain))
            {
                return AliFileTreeClassification.Unknown;
            }
            return PreStateMatches(domain) && AbsentIdentitiesMatch(domain)
                ? AliFileTreeClassification.Absent
                : AliFileTreeClassification.Unknown;
        }
        if (binding is not null
            && !ExecutionBindingMatchesDomain(
                domain,
                domainDigest,
                authorizationDigest,
                binding))
        {
            return AliFileTreeClassification.Unknown;
        }

        if (PostStateMatches(domain) && AppliedIdentitiesMatch(domain, binding))
        {
            return AliFileTreeClassification.Applied;
        }
        if (PreStateMatches(domain) && AbsentIdentitiesMatch(domain))
        {
            return AliFileTreeClassification.Absent;
        }
        if (allowIdentityBoundRollback
            && TryRollbackPublishedObject(domain, binding))
        {
            return Classify(
                domain,
                domainDigest,
                authorizationDigest,
                allowIdentityBoundRollback: false);
        }
        return AliFileTreeClassification.Unknown;
    }

    private static bool RequiresExecutionBinding(AliFileTreeDomainPlan domain) =>
        domain.Operation is AliFileTreeOperation.Copy or AliFileTreeOperation.Delete
        || domain.Operation == AliFileTreeOperation.CreateDirectory
        && domain.DirectoryChain.Count > 0;

    private static bool PreparedNamespaceSpinesMatch(AliFileTreeDomainPlan domain)
    {
        try
        {
            if (domain.SourceNamespaceSpine.Count > 0)
            {
                using var source = AliFileTreeWindowsBoundary.OpenDirectorySpine(
                    domain.SourceNamespaceSpine);
            }
            using var publication = AliFileTreeWindowsBoundary.OpenDirectorySpine(
                domain.PublicationNamespaceSpine);
            return true;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private static bool ExecutionBindingMatchesDomain(
        AliFileTreeDomainPlan domain,
        string domainDigest,
        string authorizationDigest,
        AliFileTreeExecutionBinding binding)
    {
        try
        {
            if (!string.Equals(binding.DomainId, domain.DomainId, StringComparison.Ordinal)
                || !string.Equals(binding.DomainDigest, domainDigest, StringComparison.Ordinal)
                || !string.Equals(
                    binding.AuthorizationDigest,
                    authorizationDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    NormalizePhysicalPath(binding.PublicationParentPhysicalPath),
                    NormalizePhysicalPath(domain.PublicationParentPhysicalPath),
                    StringComparison.Ordinal)
                || binding.PublicationNamespaceSpine.Count
                < domain.PublicationNamespaceSpine.Count
                || !binding.PublicationNamespaceSpine
                    .Take(domain.PublicationNamespaceSpine.Count)
                    .SequenceEqual(domain.PublicationNamespaceSpine)
                || !string.Equals(
                    binding.PublicationNamespaceSpine[^1].Identity,
                    binding.PublicationParentIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
            var expectsStaging = domain.Operation == AliFileTreeOperation.Copy
                                 || domain.Operation == AliFileTreeOperation.CreateDirectory
                                 && domain.DirectoryChain.Count > 0;
            if (expectsStaging != (binding.StagingPhysicalPath is not null)
                || expectsStaging != (binding.StagingObjectIdentity is not null)
                || expectsStaging && !string.Equals(
                    NormalizePhysicalPath(binding.StagingPhysicalPath!),
                    NormalizePhysicalPath(domain.StagingPhysicalPath!),
                    StringComparison.Ordinal))
            {
                return false;
            }
            using var publication = AliFileTreeWindowsBoundary.OpenDirectorySpine(
                binding.PublicationNamespaceSpine);
            return string.Equals(
                publication.ParentIdentity,
                binding.PublicationParentIdentity,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private static bool ExecutionArtifactExistsWithoutBinding(AliFileTreeDomainPlan domain)
    {
        if (domain.StagingPhysicalPath is not null
            && AliFileTreeSnapshotter.CaptureStable(domain.StagingPhysicalPath).Exists)
        {
            return true;
        }
        return domain.Operation == AliFileTreeOperation.Delete
               && AliFileTreeSnapshotter.CaptureStable(
                       domain.PublicationParentPhysicalPath)
                   .Exists;
    }

    private static bool PostStateMatches(AliFileTreeDomainPlan domain)
    {
        var source = CaptureOptional(domain.SourcePhysicalPath);
        var destination = CaptureOptional(domain.DestinationPhysicalPath);
        var trash = CaptureOptional(domain.TrashPhysicalPath);
        var staging = CaptureOptional(domain.StagingPhysicalPath);
        return source == domain.SourceAfter
               && destination == domain.DestinationAfter
               && trash == domain.TrashAfter
               && staging == domain.StagingAfter
               && DirectoryChainMatches(domain, useAfter: true)
               && (domain.Operation != AliFileTreeOperation.Delete
                   || domain.SourceBefore.Exists);
    }

    private static bool PreStateMatches(AliFileTreeDomainPlan domain) =>
        CaptureOptional(domain.SourcePhysicalPath) == domain.SourceBefore
        && CaptureOptional(domain.DestinationPhysicalPath) == domain.DestinationBefore
        && CaptureOptional(domain.TrashPhysicalPath) == domain.TrashBefore
        && CaptureOptional(domain.StagingPhysicalPath) == domain.StagingBefore
        && DirectoryChainMatches(domain, useAfter: false);

    private static AliFileTreeItemSnapshot CaptureOptional(string? path) => path is null
        ? AliFileTreeItemSnapshot.Absent
        : AliFileTreeSnapshotter.CaptureStable(path);

    private static bool AppliedIdentitiesMatch(
        AliFileTreeDomainPlan domain,
        AliFileTreeExecutionBinding? binding)
    {
        if (domain.SourceAfter.Exists
            && !ChildIdentityMatches(
                domain.SourceNamespaceSpine,
                domain.SourcePhysicalPath!,
                domain.SourceAfter.Kind,
                domain.SourceObjectIdentity!))
        {
            return false;
        }
        return domain.Operation switch
        {
            AliFileTreeOperation.Move => ChildIdentityMatches(
                domain.PublicationNamespaceSpine,
                domain.DestinationPhysicalPath!,
                domain.DestinationAfter.Kind,
                domain.SourceObjectIdentity!),
            AliFileTreeOperation.Copy => binding?.StagingObjectIdentity is not null
                && ChildIdentityMatches(
                    binding.PublicationNamespaceSpine,
                    domain.DestinationPhysicalPath!,
                    domain.DestinationAfter.Kind,
                    binding.StagingObjectIdentity),
            AliFileTreeOperation.Delete => binding is not null
                && ChildIdentityMatches(
                    binding.PublicationNamespaceSpine,
                    domain.TrashPhysicalPath!,
                    domain.TrashAfter.Kind,
                    domain.SourceObjectIdentity!),
            AliFileTreeOperation.CreateDirectory when domain.DirectoryChain.Count > 0 =>
                binding?.StagingObjectIdentity is not null
                && ChildIdentityMatches(
                    binding.PublicationNamespaceSpine,
                    domain.DirectoryChain[0].PhysicalPath,
                    domain.DirectoryChain[0].After.Kind,
                    binding.StagingObjectIdentity),
            AliFileTreeOperation.CreateDirectory =>
                domain.DestinationObjectIdentity is not null
                && ChildIdentityMatches(
                    domain.PublicationNamespaceSpine,
                    domain.DestinationPhysicalPath!,
                    domain.DestinationAfter.Kind,
                    domain.DestinationObjectIdentity),
            _ => false
        };
    }

    private static bool AbsentIdentitiesMatch(AliFileTreeDomainPlan domain)
    {
        if (domain.SourceBefore.Exists
            && !ChildIdentityMatches(
                domain.SourceNamespaceSpine,
                domain.SourcePhysicalPath!,
                domain.SourceBefore.Kind,
                domain.SourceObjectIdentity!))
        {
            return false;
        }
        return !domain.DestinationBefore.Exists
               || domain.DestinationObjectIdentity is not null
               && ChildIdentityMatches(
                   domain.PublicationNamespaceSpine,
                   domain.DestinationPhysicalPath!,
                   domain.DestinationBefore.Kind,
                   domain.DestinationObjectIdentity);
    }

    private static bool ChildIdentityMatches(
        IReadOnlyList<AliFileTreeNamespaceBinding> parentSpine,
        string childPath,
        string kind,
        string expectedIdentity)
    {
        try
        {
            using var parent = AliFileTreeWindowsBoundary.OpenDirectorySpine(parentSpine);
            var fullPath = Path.GetFullPath(childPath);
            if (!string.Equals(
                    NormalizePhysicalPath(parent.ParentPath),
                    NormalizePhysicalPath(Path.GetDirectoryName(fullPath) ?? string.Empty),
                    StringComparison.Ordinal))
            {
                return false;
            }
            using var child = AliFileTreeWindowsBoundary.OpenBoundChild(
                parent.ParentHandle,
                Path.GetFileName(fullPath),
                kind,
                expectedIdentity);
            return true;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private bool TryRollbackPublishedObject(
        AliFileTreeDomainPlan domain,
        AliFileTreeExecutionBinding? binding)
    {
        string? publishedPath;
        string? rollbackPath;
        string? expectedIdentity;
        string kind;
        AliFileTreeItemSnapshot expectedSnapshot;
        IReadOnlyList<AliFileTreeNamespaceBinding> publishedSpine;
        IReadOnlyList<AliFileTreeNamespaceBinding> rollbackSpine;
        switch (domain.Operation)
        {
            case AliFileTreeOperation.Move:
                publishedPath = domain.DestinationPhysicalPath;
                rollbackPath = domain.SourcePhysicalPath;
                expectedIdentity = domain.SourceObjectIdentity;
                kind = domain.SourceBefore.Kind;
                expectedSnapshot = domain.SourceBefore;
                publishedSpine = domain.PublicationNamespaceSpine;
                rollbackSpine = domain.SourceNamespaceSpine;
                break;
            case AliFileTreeOperation.Copy when binding?.StagingObjectIdentity is not null:
                publishedPath = domain.DestinationPhysicalPath;
                rollbackPath = domain.StagingPhysicalPath;
                expectedIdentity = binding.StagingObjectIdentity;
                kind = domain.DestinationAfter.Kind;
                expectedSnapshot = domain.DestinationAfter;
                publishedSpine = binding.PublicationNamespaceSpine;
                rollbackSpine = binding.PublicationNamespaceSpine;
                break;
            case AliFileTreeOperation.Delete when binding is not null:
                publishedPath = domain.TrashPhysicalPath;
                rollbackPath = domain.SourcePhysicalPath;
                expectedIdentity = domain.SourceObjectIdentity;
                kind = domain.SourceBefore.Kind;
                expectedSnapshot = domain.SourceBefore;
                publishedSpine = binding.PublicationNamespaceSpine;
                rollbackSpine = domain.SourceNamespaceSpine;
                break;
            case AliFileTreeOperation.CreateDirectory
                when domain.DirectoryChain.Count > 0
                     && binding?.StagingObjectIdentity is not null:
                publishedPath = domain.DirectoryChain[0].PhysicalPath;
                rollbackPath = domain.StagingPhysicalPath;
                expectedIdentity = binding.StagingObjectIdentity;
                kind = "directory";
                expectedSnapshot = domain.DirectoryChain[0].After;
                publishedSpine = binding.PublicationNamespaceSpine;
                rollbackSpine = binding.PublicationNamespaceSpine;
                break;
            default:
                return false;
        }
        if (publishedPath is null
            || rollbackPath is null
            || expectedIdentity is null
            || CaptureOptional(rollbackPath).Exists
            || !ChildIdentityMatches(
                publishedSpine,
                publishedPath,
                kind,
                expectedIdentity))
        {
            return false;
        }

        try
        {
            using var publishedParent = AliFileTreeWindowsBoundary.OpenDirectorySpine(
                publishedSpine);
            using var rollbackParent = AliFileTreeWindowsBoundary.OpenDirectorySpine(
                rollbackSpine,
                writableParent: true);
            using var item = AliFileTreeWindowsBoundary.OpenBoundRenameChild(
                publishedParent.ParentHandle,
                Path.GetFileName(publishedPath),
                kind,
                expectedIdentity);
            using var publishedClosure = AliFileTreeWindowsBoundary.AcquireExactTreeLease(
                item,
                publishedPath,
                expectedSnapshot,
                rootHasDeleteAccess: true);
            RequireAbsent(rollbackPath, "identity-bound restart rollback destination");
            _executionFaultHook?.Invoke(
                AliFileTreeExecutionCheckpoint.RecoveryBeforeHandleRollback);
            publishedClosure.RequireStable(publishedPath, expectedSnapshot);
            AliFileTreeWindowsBoundary.RenameNoReplace(
                item,
                rollbackParent,
                Path.GetFileName(rollbackPath));
            publishedClosure.RequireStable(rollbackPath, expectedSnapshot);
            return AliFileTreeWindowsBoundary.PathMatchesIdentity(
                rollbackPath,
                kind,
                expectedIdentity);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return false;
        }
    }

    private async Task<CommittedEvidenceReference> AppendEvidenceAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        var invocation = await _invocations.LoadAsync(
                intent.PreparationIdentity!,
                cancellationToken)
            .ConfigureAwait(false);
        var domain = await _domainPlans.ReadAsync(
                invocation.Plan.DomainPreparationIdentity,
                invocation.Plan.DomainPreparationDigest,
                cancellationToken)
            .ConfigureAwait(false);
        var resultBytes = Encoding.UTF8.GetBytes(outcomeCode);
        try
        {
            var draft = new EvidenceDraft
            {
                EvidenceId = HashText(
                    "ali-file-tree-reconciliation-evidence-v1\0"
                    + identity.StorageKey + "\0" + intent.IdempotencyKey),
                CallId = intent.AcceptedCallId ?? intent.IdempotencyKey,
                WorkItemId = intent.WorkItemId,
                ToolName = intent.ToolName,
                CapabilityGroup = "workstation-files",
                ProviderId = "ali-file-tree",
                RegistryRevision = intent.RegistryRevisionDigest,
                EffectKind = domain.Operation switch
                {
                    AliFileTreeOperation.Copy or AliFileTreeOperation.CreateDirectory => "create",
                    AliFileTreeOperation.Move => "mixed",
                    AliFileTreeOperation.Delete => "delete",
                    _ => throw new ArgumentOutOfRangeException(nameof(domain.Operation))
                },
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    intent.CanonicalArgumentsDigest,
                    intent.PreparationIdentity
                }),
                Result = JsonSerializer.SerializeToElement(new { outcomeCode }),
                NormalizedTarget = JsonSerializer.SerializeToElement(new
                {
                    domain.SourceVirtualPath,
                    domain.DestinationVirtualPath,
                    intent.TargetVersionDigest
                }),
                NormalizedEffectResult = JsonSerializer.SerializeToElement(new
                {
                    outcomeCode,
                    domain.SourceAfter,
                    domain.DestinationAfter,
                    domain.TrashAfter
                }),
                Outcome = ToolInvocationOutcome.Returned(resultBytes, reportedSuccess: true),
                StableOutcomeCode = outcomeCode,
                StartedAtUtc = invocation.Receipt?.StartedAtUtc ?? invocation.Plan.CreatedAtUtc,
                CompletedAtUtc = invocation.Receipt?.TerminalAtUtc
                    ?? invocation.Receipt?.StartedAtUtc
                    ?? invocation.Plan.CreatedAtUtc,
                Artifacts = EvidenceArtifacts(domain).ToArray(),
                Permission = new EvidencePermissionMetadata("unknown", "unknown"),
                ProtectedPermissionReceipt = JsonSerializer.SerializeToElement(new
                {
                    intent.PermissionReceiptDigest,
                    intent.RequiresApproval
                }),
                Source = new EvidenceSourceMetadata(
                    "file",
                    "ali-file-tree",
                    "trusted-local",
                    FreshAtUtc: null,
                    intent.RegistryRevisionDigest),
                ProtectedProvenance = JsonSerializer.SerializeToElement(new
                {
                    reconciler = intent.ReconcilerId,
                    domain.DomainId,
                    replayedEffect = false
                })
            };
            await _evidence.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var committed = await _evidence.AppendAsync(identity, draft, cancellationToken)
                .ConfigureAwait(false);
            return new CommittedEvidenceReference(
                committed.Evidence.EvidenceId,
                committed.Cursor,
                committed.Checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(resultBytes);
        }
    }

    private static IReadOnlyList<EvidenceArtifactDraft> EvidenceArtifacts(
        AliFileTreeDomainPlan domain)
    {
        var artifacts = new List<EvidenceArtifactDraft>();
        if (domain.SourceVirtualPath is not null)
        {
            artifacts.Add(new EvidenceArtifactDraft(
                domain.SourceVirtualPath,
                domain.SourceBefore.Kind,
                DigestOrNull(domain.SourceBefore),
                DigestOrNull(domain.SourceAfter)));
        }
        if (domain.DestinationVirtualPath is not null)
        {
            artifacts.Add(new EvidenceArtifactDraft(
                domain.DestinationVirtualPath,
                domain.DestinationAfter.Kind,
                DigestOrNull(domain.DestinationBefore),
                DigestOrNull(domain.DestinationAfter)));
        }
        return artifacts;
    }

    private static string? DigestOrNull(AliFileTreeItemSnapshot snapshot) =>
        snapshot.Exists ? snapshot.Digest : null;

    private static string RootBinding(AliFileTreeDomainPlan domain)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        if (domain.SourcePhysicalPath is not null)
        {
            roots["source"] = NormalizePhysicalPath(domain.SourcePhysicalPath);
        }
        if (domain.DestinationPhysicalPath is not null)
        {
            roots["destination"] = NormalizePhysicalPath(domain.DestinationPhysicalPath);
        }
        if (domain.TrashPhysicalPath is not null)
        {
            roots["trash"] = NormalizePhysicalPath(domain.TrashPhysicalPath);
        }
        if (domain.StagingPhysicalPath is not null)
        {
            roots["staging"] = NormalizePhysicalPath(domain.StagingPhysicalPath);
        }
        for (var index = 0; index < domain.DirectoryChain.Count; index++)
        {
            roots[$"directory-chain:{index:D4}"] = NormalizePhysicalPath(
                domain.DirectoryChain[index].PhysicalPath);
        }
        return WorkIdentityCanonicalizer.MapDigest("file-tree-root-binding-v1", roots);
    }

    private static string NormalizePhysicalPath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static bool IsWithinOrEqual(string candidatePath, string directoryPath)
    {
        var candidate = NormalizePhysicalPath(candidatePath);
        var directory = NormalizePhysicalPath(directoryPath);
        return string.Equals(candidate, directory, StringComparison.Ordinal)
               || candidate.StartsWith(
                   Path.EndsInDirectorySeparator(directory)
                       ? directory
                       : directory + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal);
    }

    private static void RequirePathsDisjoint(
        string firstPath,
        string secondPath,
        string message)
    {
        if (IsWithinOrEqual(firstPath, secondPath)
            || IsWithinOrEqual(secondPath, firstPath))
        {
            throw new InvalidDataException(message);
        }
    }

    internal static AliExactExecutionAdapterIdentity Identity(string toolName) =>
        new(toolName, CapabilityIdFor(toolName), ReconcilerIdFor(toolName));

    internal static string CapabilityIdFor(string toolName) => "ali.tool." + toolName;

    internal static string ReconcilerIdFor(string toolName) => "ali.reconcile." + toolName;

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string StableExceptionCode(Exception exception)
    {
        var filtered = new string(exception.GetType().Name
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(filtered)
            ? "failed"
            : filtered[..Math.Min(filtered.Length, 72)];
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record AliFileTreeActiveInvocation(
        AliDurableInvocationPlan Invocation,
        AliFileTreeDomainPlan Domain,
        string DomainDigest,
        string AuthorizationDigest);

    private enum AliFileTreeClassification
    {
        Applied,
        Absent,
        Unknown
    }

    private sealed class CompletionParticipant(
        AliFileTreeMutationCoordinator owner,
        string planId,
        string domainDigest,
        string authorizationDigest,
        AliFileTreeDomainPlan domain) : IAliInvocationCompletionParticipant
    {
        public async ValueTask CompleteAsync(
            object? result,
            CancellationToken cancellationToken)
        {
            var classification = owner.Classify(
                domain,
                domainDigest,
                authorizationDigest,
                allowIdentityBoundRollback: false);
            if (ResultSucceeded(domain.Operation, result)
                && classification == AliFileTreeClassification.Applied)
            {
                await owner._invocations.CompleteAsync(
                        planId,
                        expectedRevision: 1,
                        "file-tree-effect-applied",
                        ResultDigest(result),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (!ResultSucceeded(domain.Operation, result)
                && classification == AliFileTreeClassification.Absent)
            {
                await owner._invocations.FailAsync(
                        planId,
                        expectedRevision: 1,
                        "file-tree-effect-not-applied",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            await owner._invocations.MarkInDoubtAsync(
                    planId,
                    expectedRevision: 1,
                    "file-tree-result-state-mismatch",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask FailAsync(
            Exception exception,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (owner.Classify(
                    domain,
                    domainDigest,
                    authorizationDigest,
                    allowIdentityBoundRollback: false)
                == AliFileTreeClassification.Absent)
            {
                await owner._invocations.FailAsync(
                        planId,
                        expectedRevision: 1,
                        "file-tree-inner-invocation-failed",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            await owner._invocations.MarkInDoubtAsync(
                    planId,
                    expectedRevision: 1,
                    "file-tree-failure-state-ambiguous",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask MarkInDoubtAsync(
            string reasonCode,
            CancellationToken cancellationToken) =>
            new(owner._invocations.MarkInDoubtAsync(
                planId,
                expectedRevision: 1,
                reasonCode,
                cancellationToken));

        private static bool ResultSucceeded(AliFileTreeOperation operation, object? result) =>
            operation switch
            {
                AliFileTreeOperation.Delete => result is true,
                AliFileTreeOperation.Move => result is WorkstationFileMoveResult { Success: true },
                AliFileTreeOperation.Copy or AliFileTreeOperation.CreateDirectory =>
                    result is WorkstationFileOperationResult { Success: true },
                _ => false
            };

        private static string ResultDigest(object? result)
        {
            var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
                JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object)));
            try
            {
                return TurnStateIntegrity.Digest(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private sealed class StartedDomainReconciler(
        AliFileTreeMutationCoordinator owner,
        AliExactExecutionAdapterIdentity exactIdentity) : IAliStartedInvocationDomainReconciler
    {
        public AliExactExecutionAdapterIdentity ExactIdentity { get; } = exactIdentity;

        public async ValueTask<AliDurableInvocationRecoveryResult> ReconcileStartedAsync(
            AliDurableInvocationPlan plan,
            AliDurableInvocationReceipt startedReceipt,
            CancellationToken cancellationToken)
        {
            var domain = await owner._domainPlans.ReadAsync(
                    plan.DomainPreparationIdentity,
                    plan.DomainPreparationDigest,
                    cancellationToken)
                .ConfigureAwait(false);
            var executionBinding = RequiresExecutionBinding(domain)
                ? owner._executionBindings.TryRead(
                    domain.DomainId,
                    plan.DomainPreparationDigest,
                    startedReceipt.AuthorizationDigest)
                : null;
            var classification = owner.Classify(
                domain,
                plan.DomainPreparationDigest,
                startedReceipt.AuthorizationDigest,
                allowIdentityBoundRollback: true);
            if (classification == AliFileTreeClassification.Unknown
                && domain.Operation == AliFileTreeOperation.CreateDirectory
                && TryCompensateDirectoryStaging(domain, executionBinding))
            {
                classification = owner.Classify(
                    domain,
                    plan.DomainPreparationDigest,
                    startedReceipt.AuthorizationDigest,
                    allowIdentityBoundRollback: false);
            }
            return classification switch
            {
                AliFileTreeClassification.Applied =>
                    AliDurableInvocationRecoveryResult.Applied(
                        "file-tree-post-state-proved-applied"),
                AliFileTreeClassification.Absent =>
                    AliDurableInvocationRecoveryResult.Absent(
                        "file-tree-pre-state-proved-absent"),
                _ => AliDurableInvocationRecoveryResult.Unknown(
                    "file-tree-state-ambiguous")
            };
        }
    }
}

internal abstract class AliExactFileTreeExecutionAdapter : IAliExecutionEffectAdapter
{
    protected AliExactFileTreeExecutionAdapter(
        string toolName,
        AliFileTreeMutationCoordinator coordinator)
    {
        ToolName = toolName;
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    protected AliFileTreeMutationCoordinator Coordinator { get; }

    public string ToolName { get; }

    public string CapabilityId => AliFileTreeMutationCoordinator.CapabilityIdFor(ToolName);

    public string ReconcilerId => AliFileTreeMutationCoordinator.ReconcilerIdFor(ToolName);

    public abstract ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken);

    public ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken) =>
        Coordinator.ReconcileAsync(
            AliFileTreeMutationCoordinator.Identity(ToolName),
            identity,
            intent,
            cancellationToken);
}

internal sealed class AliFileDeleteExecutionAdapter(AliFileTreeMutationCoordinator coordinator) :
    AliExactFileTreeExecutionAdapter(AliCapabilityCatalog.FileDeleteName, coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareDeleteAsync(request, cancellationToken);
}

internal sealed class AliFileMoveExecutionAdapter(AliFileTreeMutationCoordinator coordinator) :
    AliExactFileTreeExecutionAdapter(AliCapabilityCatalog.FileMoveName, coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareMoveAsync(request, cancellationToken);
}

internal sealed class AliFileCopyExecutionAdapter(AliFileTreeMutationCoordinator coordinator) :
    AliExactFileTreeExecutionAdapter(AliCapabilityCatalog.FileCopyName, coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareCopyAsync(request, cancellationToken);
}

internal sealed class AliFileCreateDirectoryExecutionAdapter(
    AliFileTreeMutationCoordinator coordinator) :
    AliExactFileTreeExecutionAdapter(AliCapabilityCatalog.FileCreateDirectoryName, coordinator)
{
    public override ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken) =>
        Coordinator.PrepareCreateDirectoryAsync(request, cancellationToken);
}
