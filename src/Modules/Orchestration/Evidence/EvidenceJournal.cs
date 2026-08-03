using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Orchestration.Evidence;

internal enum EvidenceJournalCommitBoundary
{
    BodyFlushed,
    CommitMarkerFlushed,
    HeadCommitted
}

/// <summary>
/// Windows-only boundary for orchestration files that must never follow a final-component
/// reparse point. Directory components are checked individually before and after creation, and
/// destructive removal is issued against the already-validated file handle.
/// </summary>
internal static class WindowsOrchestrationFileBoundary
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileTypeDisk = 0x0001;
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    internal static void EnsureRegularDirectoryPath(string directory, string invalidMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException(invalidMessage);
        ValidateRegularDirectory(root, invalidMessage);

        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        if (!string.Equals(relative, ".", StringComparison.Ordinal))
        {
            foreach (var component in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var parent = current;
                current = Path.Combine(current, component);
                if (!TryValidateRegularDirectory(current, invalidMessage))
                {
                    // The parent is revalidated immediately before and after creation. This
                    // rejects a pre-existing junction/symlink before the new entry is accepted.
                    ValidateRegularDirectory(parent, invalidMessage);
                    Directory.CreateDirectory(current);
                    ValidateRegularDirectory(current, invalidMessage);
                    ValidateRegularDirectory(parent, invalidMessage);
                }
            }
        }

        ValidateRegularDirectory(fullPath, invalidMessage);
    }

    internal static bool RegularDirectoryExists(string directory, string invalidMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException(invalidMessage);
        ValidateRegularDirectory(root, invalidMessage);

        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!TryValidateRegularDirectory(current, invalidMessage))
            {
                return false;
            }
        }

        return true;
    }

    internal static void ValidateRegularDirectoryPath(string directory, string invalidMessage)
    {
        if (!RegularDirectoryExists(directory, invalidMessage))
        {
            throw new DirectoryNotFoundException(invalidMessage);
        }
    }

    internal static FileStream OpenRegularFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        bool writeThrough,
        string invalidMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        ValidateRegularDirectoryPath(
            Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException(invalidMessage),
            invalidMessage);

        var desiredAccess = access switch
        {
            FileAccess.Read => GenericRead,
            FileAccess.Write => GenericWrite,
            FileAccess.ReadWrite => GenericRead | GenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        var creationDisposition = mode switch
        {
            FileMode.CreateNew => CreateNew,
            FileMode.Open => OpenExisting,
            FileMode.OpenOrCreate => OpenAlways,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                "The orchestration boundary uses only non-destructive file open modes.")
        };
        var flags = FileFlagOpenReparsePoint | FileFlagOverlapped | FileFlagSequentialScan;
        if (writeThrough)
        {
            flags |= FileFlagWriteThrough;
        }

        var handle = CreateFileW(
            ToExtendedLengthWin32Path(path),
            desiredAccess,
            share,
            IntPtr.Zero,
            creationDisposition,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            ThrowIo(invalidMessage, Marshal.GetLastWin32Error(), handle);
        }

        try
        {
            ValidateRegularFileHandle(handle, invalidMessage);
            return new FileStream(handle, access, 4096, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void MoveRegularFile(
        string sourcePath,
        string destinationPath,
        bool replaceExisting,
        string invalidMessage,
        bool writeThrough = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var sourceDirectory = Path.GetDirectoryName(source)
            ?? throw new InvalidDataException(invalidMessage);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException(invalidMessage);
        ValidateRegularDirectoryPath(sourceDirectory, invalidMessage);
        ValidateRegularDirectoryPath(destinationDirectory, invalidMessage);
        ValidateRegularFile(source, invalidMessage);
        _ = TryValidateRegularFile(destination, invalidMessage);

        var flags = (writeThrough ? MoveFileWriteThrough : 0u)
                    | (replaceExisting ? MoveFileReplaceExisting : 0u);
        if (!MoveFileExW(
                ToExtendedLengthWin32Path(source),
                ToExtendedLengthWin32Path(destination),
                flags))
        {
            ThrowIo(invalidMessage, Marshal.GetLastWin32Error());
        }

        ValidateRegularDirectoryPath(destinationDirectory, invalidMessage);
        ValidateRegularFile(destination, invalidMessage);
    }

    internal static bool DeleteRegularFileNoFollow(string path, string invalidMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(invalidMessage);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(invalidMessage);
        ValidateRegularDirectoryPath(directory, invalidMessage);
        using var handle = CreateFileW(
            ToExtendedLengthWin32Path(fullPath),
            DeleteAccess | FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return false;
            }

            ThrowIo(invalidMessage, error, handle);
        }

        ValidateRegularFileHandle(handle, invalidMessage);
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            ThrowIo(invalidMessage, Marshal.GetLastWin32Error());
        }

        return true;
    }

    private static bool TryValidateRegularDirectory(string path, string invalidMessage)
    {
        using var handle = CreateFileW(
            ToExtendedLengthWin32Path(path),
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return false;
            }

            ThrowIo(invalidMessage, error, handle);
        }

        var attributes = File.GetAttributes(handle);
        if (GetFileType(handle) != FileTypeDisk
            || (attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(invalidMessage);
        }

        return true;
    }

    private static void ValidateRegularDirectory(string path, string invalidMessage)
    {
        if (!TryValidateRegularDirectory(path, invalidMessage))
        {
            throw new DirectoryNotFoundException(invalidMessage);
        }
    }

    private static bool TryValidateRegularFile(string path, string invalidMessage)
    {
        using var handle = CreateFileW(
            ToExtendedLengthWin32Path(path),
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return false;
            }

            ThrowIo(invalidMessage, error, handle);
        }

        ValidateRegularFileHandle(handle, invalidMessage);
        return true;
    }

    private static void ValidateRegularFile(string path, string invalidMessage)
    {
        if (!TryValidateRegularFile(path, invalidMessage))
        {
            throw new FileNotFoundException(invalidMessage, path);
        }
    }

    private static void ValidateRegularFileHandle(
        SafeFileHandle handle,
        string invalidMessage)
    {
        var attributes = File.GetAttributes(handle);
        if (GetFileType(handle) != FileTypeDisk
            || (attributes & (FileAttributes.Device
                              | FileAttributes.Directory
                              | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(invalidMessage);
        }
    }

    internal static string ToExtendedLengthWin32Path(string path)
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

    private static void ThrowIo(
        string message,
        int error,
        SafeFileHandle? handle = null)
    {
        handle?.Dispose();
        throw new IOException(message, new Win32Exception(error));
    }

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string newFileName,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle fileHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}

internal sealed record EvidenceJournalEntry(
    long Sequence,
    long TimestampUtcTicks,
    string PreviousChecksum,
    byte[] Payload,
    string Checksum);

internal sealed record EvidenceJournalHeadUnsigned(
    string TurnStorageKey,
    long CommittedLength,
    long Sequence,
    string Checksum);

internal sealed record EvidenceJournalHead(
    string TurnStorageKey,
    long CommittedLength,
    long Sequence,
    string Checksum,
    string Mac);

internal sealed record EvidenceJournalVerificationStamp(
    string HeadMac,
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    long ChangeTimeTicks,
    long Length,
    long Sequence);

internal enum EvidenceJournalAppendStatus
{
    Committed,
    AlreadyRecorded
}

internal sealed record EvidenceJournalAppendResult(
    EvidenceJournalAppendStatus Status,
    StoredEvidenceCursorRecord Record,
    EvidenceJournalVerificationStamp? VerificationStamp);

internal sealed record EvidenceJournalReplayResult(
    IReadOnlyList<StoredEvidenceCursorRecord> Records,
    EvidenceJournalVerificationStamp? VerificationStamp);

internal sealed record EvidenceJournalLookupResult(
    StoredEvidenceCursorRecord? Record,
    EvidenceJournalVerificationStamp? VerificationStamp);

internal sealed record EvidenceJournalBatchLookupResult(
    IReadOnlyList<StoredEvidenceCursorRecord> Records,
    EvidenceJournalVerificationStamp? VerificationStamp);

internal sealed class EvidenceJournal
{
    private const int MaximumLineBytes = 4 * 1024 * 1024;
    internal const int MaximumHeadBytes = 16 * 1024;
    private const int EvidenceIdBloomBytes = 64 * 1024;
    private const int EvidenceIdBloomHashCount = 7;
    private static readonly byte[] FirstChecksum = new byte[32];
    private static readonly string FirstChecksumHex =
        Convert.ToHexString(FirstChecksum).ToLowerInvariant();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _directory;
    private readonly string _turnStorageKey;
    private readonly Action<EvidenceJournalCommitBoundary>? _faultInjector;
    private readonly Action<int>? _tailReadObserver;
    private readonly Action<int>? _indexedRecordReadObserver;
    private readonly Func<bool>? _stampUnavailable;
    private readonly Action? _exactIndexMaintenanceFaultInjector;
    private EvidenceJournalVerificationStamp? _verifiedStamp;
    private EvidenceJournalVerificationStamp? _membershipStamp;
    private EvidenceIdBloomFilter? _evidenceIdMembership;

    public EvidenceJournal(
        string directory,
        string turnStorageKey,
        Action<EvidenceJournalCommitBoundary>? faultInjector = null,
        Action<int>? tailReadObserver = null,
        Func<bool>? stampUnavailable = null,
        Action<int>? indexedRecordReadObserver = null,
        Action? exactIndexMaintenanceFaultInjector = null)
    {
        _directory = Path.GetFullPath(directory);
        _turnStorageKey = turnStorageKey;
        _faultInjector = faultInjector;
        _tailReadObserver = tailReadObserver;
        _stampUnavailable = stampUnavailable;
        _indexedRecordReadObserver = indexedRecordReadObserver;
        _exactIndexMaintenanceFaultInjector = exactIndexMaintenanceFaultInjector;
    }

    public async Task<EvidenceJournalAppendResult> AppendAsync(
        StoredEvidenceRecord record,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Func<EvidenceJournalHeadUnsigned, string> signHead,
        Action<EvidenceJournalHead> validateHead,
        Func<byte[], string> authenticateExactIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(validateExistingRecord);
        ArgumentNullException.ThrowIfNull(signHead);
        ArgumentNullException.ThrowIfNull(validateHead);
        ArgumentNullException.ThrowIfNull(authenticateExactIndex);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadOrCreateHeadForAppendAsync(
            stream,
            signHead,
            validateHead,
            cancellationToken).ConfigureAwait(false);
        await RecoverUncommittedSuffixAsync(stream, head, cancellationToken).ConfigureAwait(false);

        var currentStamp = TryCaptureVerificationStamp(stream, head);
        StoredEvidenceCursorRecord? existing = null;
        if (!MembershipIsCurrent(currentStamp)
            || _evidenceIdMembership!.MightContain(record.EvidenceId))
        {
            var lookup = await FindUsingExactIndexOrRebuildAsync(
                stream,
                head,
                currentStamp,
                record.EvidenceId,
                validateExistingRecord,
                authenticateExactIndex,
                cancellationToken).ConfigureAwait(false);
            existing = lookup.Record;
            currentStamp = lookup.VerificationStamp;
        }
        else if (head.Sequence > 0)
        {
            var tail = await ReadAndValidateTailAsync(
                stream,
                head,
                cancellationToken).ConfigureAwait(false);
            validateExistingRecord(DeserializeRecord(tail));
        }

        if (existing is not null)
        {
            return new EvidenceJournalAppendResult(
                EvidenceJournalAppendStatus.AlreadyRecorded,
                existing,
                currentStamp);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sequence = head.Sequence + 1;
        var previousChecksumBytes = DecodeChecksum(
            head.Checksum,
            "The evidence journal head checksum is invalid.");
        try
        {
            var payload = CanonicalEvidenceJson.SerializeToUtf8Bytes(record);
            var timestampUtcTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            var checksum = ComputeChecksum(
                sequence,
                timestampUtcTicks,
                previousChecksumBytes,
                payload);
            var entry = new EvidenceJournalEntry(
                sequence,
                timestampUtcTicks,
                head.Checksum,
                payload,
                checksum);
            var line = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            if (line.Length == 0 || line.Length > MaximumLineBytes)
            {
                throw new InvalidDataException("An evidence journal entry exceeds the supported metadata size.");
            }

            stream.Position = head.CommittedLength;
            await stream.WriteAsync(line, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            _faultInjector?.Invoke(EvidenceJournalCommitBoundary.BodyFlushed);

            await stream.WriteAsync("\n"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            _faultInjector?.Invoke(EvidenceJournalCommitBoundary.CommitMarkerFlushed);

            var unsignedHead = new EvidenceJournalHeadUnsigned(
                _turnStorageKey,
                stream.Length,
                sequence,
                checksum);
            var committedHead = new EvidenceJournalHead(
                unsignedHead.TurnStorageKey,
                unsignedHead.CommittedLength,
                unsignedHead.Sequence,
                unsignedHead.Checksum,
                signHead(unsignedHead));
            await WriteHeadAtomicallyAsync(committedHead).ConfigureAwait(false);
            var committedStamp = TryCaptureVerificationStamp(stream, committedHead);
            _verifiedStamp = committedStamp;
            _faultInjector?.Invoke(EvidenceJournalCommitBoundary.HeadCommitted);
            await MaintainExactIndexAfterAppendAsync(
                stream,
                head,
                currentStamp,
                committedHead,
                committedStamp,
                record.EvidenceId,
                new EvidenceExactIndexLocation(
                    head.CommittedLength,
                    line.Length,
                    sequence),
                validateExistingRecord,
                authenticateExactIndex).ConfigureAwait(false);
            if (committedStamp is not null
                && _membershipStamp == committedStamp
                && _evidenceIdMembership is not null)
            {
                // A capacity-growth rebuild already populated membership through the new head.
            }
            else if (currentStamp is not null
                && committedStamp is not null
                && _membershipStamp == currentStamp
                && _evidenceIdMembership is not null)
            {
                _evidenceIdMembership.Add(record.EvidenceId);
                _membershipStamp = committedStamp;
            }
            else
            {
                _evidenceIdMembership = null;
                _membershipStamp = null;
            }
            return new EvidenceJournalAppendResult(
                EvidenceJournalAppendStatus.Committed,
                new StoredEvidenceCursorRecord(sequence, record, checksum),
                committedStamp);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previousChecksumBytes);
        }
    }

    public async Task<EvidenceJournalReplayResult> ReplayAsync(
        Action<StoredEvidenceRecord> validateExistingRecord,
        Action<EvidenceJournalHead> validateHead,
        Func<byte[], string> authenticateExactIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateExistingRecord);
        ArgumentNullException.ThrowIfNull(validateHead);
        ArgumentNullException.ThrowIfNull(authenticateExactIndex);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadForReplayAsync(stream, validateHead, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            _tailReadObserver?.Invoke(0);
            _verifiedStamp = null;
            _membershipStamp = null;
            _evidenceIdMembership = null;
            return new EvidenceJournalReplayResult([], VerificationStamp: null);
        }

        await RecoverUncommittedSuffixAsync(stream, head, cancellationToken).ConfigureAwait(false);
        var rebuilt = await RebuildExactIndexAsync(
            stream,
            head,
            requestedEvidenceIds: null,
            validateExistingRecord,
            authenticateExactIndex,
            collectRecords: true,
            cancellationToken).ConfigureAwait(false);
        return new EvidenceJournalReplayResult(rebuilt.Records, rebuilt.VerificationStamp);
    }

    public async Task<EvidenceJournalLookupResult> FindByEvidenceIdAsync(
        string evidenceId,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Action<EvidenceJournalHead> validateHead,
        Func<byte[], string> authenticateExactIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentNullException.ThrowIfNull(validateExistingRecord);
        ArgumentNullException.ThrowIfNull(validateHead);
        ArgumentNullException.ThrowIfNull(authenticateExactIndex);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadForReplayAsync(stream, validateHead, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            _verifiedStamp = null;
            _membershipStamp = null;
            _evidenceIdMembership = null;
            return new EvidenceJournalLookupResult(Record: null, VerificationStamp: null);
        }

        await RecoverUncommittedSuffixAsync(stream, head, cancellationToken).ConfigureAwait(false);
        var currentStamp = TryCaptureVerificationStamp(stream, head);
        if (MembershipIsCurrent(currentStamp)
            && !_evidenceIdMembership!.MightContain(evidenceId))
        {
            return new EvidenceJournalLookupResult(
                Record: null,
                VerificationStamp: currentStamp);
        }

        return await FindUsingExactIndexOrRebuildAsync(
            stream,
            head,
            currentStamp,
            evidenceId,
            validateExistingRecord,
            authenticateExactIndex,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EvidenceJournalBatchLookupResult> FindByEvidenceIdsAsync(
        IReadOnlySet<string> evidenceIds,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Action<EvidenceJournalHead> validateHead,
        Func<byte[], string> authenticateExactIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceIds);
        ArgumentNullException.ThrowIfNull(validateExistingRecord);
        ArgumentNullException.ThrowIfNull(validateHead);
        ArgumentNullException.ThrowIfNull(authenticateExactIndex);
        if (evidenceIds.Count == 0)
        {
            return new EvidenceJournalBatchLookupResult([], VerificationStamp: null);
        }

        foreach (var evidenceId in evidenceIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        }

        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadForReplayAsync(stream, validateHead, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            _verifiedStamp = null;
            _membershipStamp = null;
            _evidenceIdMembership = null;
            return new EvidenceJournalBatchLookupResult([], VerificationStamp: null);
        }

        await RecoverUncommittedSuffixAsync(stream, head, cancellationToken).ConfigureAwait(false);
        var currentStamp = TryCaptureVerificationStamp(stream, head);
        if (MembershipIsCurrent(currentStamp)
            && evidenceIds.All(evidenceId => !_evidenceIdMembership!.MightContain(evidenceId)))
        {
            return new EvidenceJournalBatchLookupResult([], currentStamp);
        }

        return await FindManyUsingExactIndexOrRebuildAsync(
            stream,
            head,
            currentStamp,
            evidenceIds,
            validateExistingRecord,
            authenticateExactIndex,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EvidenceJournalVerificationStamp?> CaptureVerificationStampAsync(
        Action<EvidenceJournalHead> validateHead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateHead);
        if (!WindowsOrchestrationFileBoundary.RegularDirectoryExists(
                _directory,
                "The evidence journal directory is not a regular local directory."))
        {
            return null;
        }

        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadForReplayAsync(stream, validateHead, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        await RecoverUncommittedSuffixAsync(stream, head, cancellationToken).ConfigureAwait(false);
        return TryCaptureVerificationStamp(stream, head);
    }

    private bool MembershipIsCurrent(EvidenceJournalVerificationStamp? currentStamp) =>
        currentStamp is not null
        && _verifiedStamp == currentStamp
        && _membershipStamp == currentStamp
        && _evidenceIdMembership is not null;

    private async Task<EvidenceJournalLookupResult> FindUsingExactIndexOrRebuildAsync(
        FileStream stream,
        EvidenceJournalHead head,
        EvidenceJournalVerificationStamp? currentStamp,
        string evidenceId,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Func<byte[], string> authenticateExactIndex,
        CancellationToken cancellationToken)
    {
        if (currentStamp is not null)
        {
            try
            {
                using var index = await EvidenceExactIndex.TryOpenAsync(
                    _directory,
                    _turnStorageKey,
                    head,
                    currentStamp,
                    authenticateExactIndex,
                    cancellationToken).ConfigureAwait(false);
                if (index is not null)
                {
                    var record = FindIndexedRecord(
                        stream,
                        head,
                        index,
                        evidenceId,
                        validateExistingRecord);
                    _verifiedStamp = currentStamp;
                    return new EvidenceJournalLookupResult(record, currentStamp);
                }
            }
            catch (Exception ex) when (IsDisposableIndexFailure(ex))
            {
                // The index is disposable. Rebuild it from the authenticated journal below.
            }
        }

        var requested = new HashSet<string>(StringComparer.Ordinal) { evidenceId };
        var rebuilt = await RebuildExactIndexAsync(
            stream,
            head,
            requested,
            validateExistingRecord,
            authenticateExactIndex,
            collectRecords: false,
            cancellationToken).ConfigureAwait(false);
        rebuilt.Matches.TryGetValue(evidenceId, out var found);
        return new EvidenceJournalLookupResult(found, rebuilt.VerificationStamp);
    }

    private async Task<EvidenceJournalBatchLookupResult> FindManyUsingExactIndexOrRebuildAsync(
        FileStream stream,
        EvidenceJournalHead head,
        EvidenceJournalVerificationStamp? currentStamp,
        IReadOnlySet<string> evidenceIds,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Func<byte[], string> authenticateExactIndex,
        CancellationToken cancellationToken)
    {
        if (currentStamp is not null)
        {
            try
            {
                using var index = await EvidenceExactIndex.TryOpenAsync(
                    _directory,
                    _turnStorageKey,
                    head,
                    currentStamp,
                    authenticateExactIndex,
                    cancellationToken).ConfigureAwait(false);
                if (index is not null)
                {
                    var found = new List<StoredEvidenceCursorRecord>(evidenceIds.Count);
                    foreach (var evidenceId in evidenceIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var record = FindIndexedRecord(
                            stream,
                            head,
                            index,
                            evidenceId,
                            validateExistingRecord);
                        if (record is not null)
                        {
                            found.Add(record);
                        }
                    }

                    _verifiedStamp = currentStamp;
                    return new EvidenceJournalBatchLookupResult(found, currentStamp);
                }
            }
            catch (Exception ex) when (IsDisposableIndexFailure(ex))
            {
                // The index is disposable. Rebuild it from the authenticated journal below.
            }
        }

        var rebuilt = await RebuildExactIndexAsync(
            stream,
            head,
            evidenceIds,
            validateExistingRecord,
            authenticateExactIndex,
            collectRecords: false,
            cancellationToken).ConfigureAwait(false);
        return new EvidenceJournalBatchLookupResult(
            rebuilt.Matches.Values.ToArray(),
            rebuilt.VerificationStamp);
    }

    private StoredEvidenceCursorRecord? FindIndexedRecord(
        FileStream stream,
        EvidenceJournalHead head,
        EvidenceExactIndex index,
        string evidenceId,
        Action<StoredEvidenceRecord> validateExistingRecord)
    {
        StoredEvidenceCursorRecord? found = null;
        index.VisitCandidateLocations(
            evidenceId,
            location =>
            {
                StoredEvidenceCursorRecord candidate;
                try
                {
                    candidate = ReadIndexedRecord(
                        stream,
                        head,
                        location,
                        validateExistingRecord);
                }
                catch (EvidenceExactIndexInvalidException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    throw new EvidenceExactIndexInvalidException(
                        "The disposable evidence exact index selected an invalid journal record.",
                        ex);
                }

                if (!string.Equals(
                        candidate.Evidence.EvidenceId,
                        evidenceId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (found is not null)
                {
                    throw new InvalidDataException(
                        "The evidence journal contains a duplicate evidence ID.");
                }

                found = candidate;
            });
        return found;
    }

    private StoredEvidenceCursorRecord ReadIndexedRecord(
        FileStream stream,
        EvidenceJournalHead head,
        EvidenceExactIndexLocation location,
        Action<StoredEvidenceRecord> validateExistingRecord)
    {
        if (location.Sequence <= 0
            || location.Sequence > head.Sequence
            || location.JournalOffset < 0
            || location.LineLength <= 0
            || location.LineLength > MaximumLineBytes
            || location.JournalOffset > head.CommittedLength - location.LineLength - 1L)
        {
            throw new EvidenceExactIndexInvalidException(
                "The disposable evidence exact index contains an invalid journal location.");
        }

        var line = new byte[location.LineLength];
        ReadExactlyAt(stream.SafeFileHandle, line, location.JournalOffset);
        Span<byte> marker = stackalloc byte[1];
        ReadExactlyAt(
            stream.SafeFileHandle,
            marker,
            checked(location.JournalOffset + location.LineLength));
        _indexedRecordReadObserver?.Invoke(checked(location.LineLength + 1));
        if (marker[0] != (byte)'\n')
        {
            throw new EvidenceExactIndexInvalidException(
                "The disposable evidence exact index does not select a committed journal frame.");
        }

        var entry = DeserializeEntry(line);
        ValidateEntrySelf(entry);
        if (entry.Sequence != location.Sequence)
        {
            throw new EvidenceExactIndexInvalidException(
                "The disposable evidence exact index selects the wrong journal cursor.");
        }

        var record = DeserializeRecord(entry);
        validateExistingRecord(record);
        return new StoredEvidenceCursorRecord(entry.Sequence, record, entry.Checksum);
    }

    private async Task<ExactIndexRebuildResult> RebuildExactIndexAsync(
        FileStream stream,
        EvidenceJournalHead head,
        IReadOnlySet<string>? requestedEvidenceIds,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Func<byte[], string> authenticateExactIndex,
        bool collectRecords,
        CancellationToken cancellationToken)
    {
        _verifiedStamp = null;
        _membershipStamp = null;
        _evidenceIdMembership = null;
        var membership = new EvidenceIdBloomFilter();
        var found = new Dictionary<string, StoredEvidenceCursorRecord>(
            requestedEvidenceIds?.Count ?? 0,
            StringComparer.Ordinal);
        EvidenceExactIndex.Builder? builder = null;
        try
        {
            builder = EvidenceExactIndex.CreateBuilder(
                _directory,
                _turnStorageKey,
                head,
                authenticateExactIndex);
        }
        catch (Exception ex) when (IsDisposableIndexFailure(ex))
        {
            builder?.Dispose();
            builder = null;
        }

        IReadOnlyList<StoredEvidenceCursorRecord> records;
        try
        {
            records = await ReplayCommittedAsync(
                stream,
                head,
                existing =>
                {
                    validateExistingRecord(existing);
                    membership.Add(existing.EvidenceId);
                },
                (cursor, journalOffset, lineLength) =>
                {
                    if (builder is not null)
                    {
                        try
                        {
                            builder.Add(
                                cursor.Evidence.EvidenceId,
                                new EvidenceExactIndexLocation(
                                    journalOffset,
                                    lineLength,
                                    cursor.Cursor));
                        }
                        catch (Exception ex) when (IsDisposableIndexFailure(ex))
                        {
                            builder.Dispose();
                            builder = null;
                        }
                    }

                    var evidenceId = cursor.Evidence.EvidenceId;
                    if (requestedEvidenceIds is null
                        || !requestedEvidenceIds.Contains(evidenceId))
                    {
                        return;
                    }

                    if (!found.TryAdd(evidenceId, cursor))
                    {
                        throw new InvalidDataException(
                            "The evidence journal contains a duplicate evidence ID.");
                    }
                },
                collectRecords,
                cancellationToken).ConfigureAwait(false);
            var stamp = TryCaptureVerificationStamp(stream, head);
            if (builder is not null && stamp is not null)
            {
                try
                {
                    await builder.CompleteAsync(stamp, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsDisposableIndexFailure(ex))
                {
                    // The authenticated journal remains authoritative. A later lookup retries.
                }
            }

            _verifiedStamp = stamp;
            _membershipStamp = stamp;
            _evidenceIdMembership = stamp is null ? null : membership;
            return new ExactIndexRebuildResult(records, found, stamp);
        }
        finally
        {
            builder?.Dispose();
        }
    }

    private async Task MaintainExactIndexAfterAppendAsync(
        FileStream stream,
        EvidenceJournalHead previousHead,
        EvidenceJournalVerificationStamp? previousStamp,
        EvidenceJournalHead committedHead,
        EvidenceJournalVerificationStamp? committedStamp,
        string evidenceId,
        EvidenceExactIndexLocation location,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Func<byte[], string> authenticateExactIndex)
    {
        if (previousStamp is null || committedStamp is null)
        {
            return;
        }

        try
        {
            _exactIndexMaintenanceFaultInjector?.Invoke();
            using var index = await EvidenceExactIndex.TryOpenAsync(
                _directory,
                _turnStorageKey,
                previousHead,
                previousStamp,
                authenticateExactIndex,
                CancellationToken.None).ConfigureAwait(false);
            if (index is null)
            {
                await RebuildExactIndexAsync(
                    stream,
                    committedHead,
                    requestedEvidenceIds: null,
                    validateExistingRecord,
                    authenticateExactIndex,
                    collectRecords: false,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var advanced = await index.TryInsertAndAdvanceAsync(
                evidenceId,
                location,
                committedHead,
                committedStamp).ConfigureAwait(false);
            if (!advanced)
            {
                index.Dispose();
                await RebuildExactIndexAsync(
                    stream,
                    committedHead,
                    requestedEvidenceIds: null,
                    validateExistingRecord,
                    authenticateExactIndex,
                    collectRecords: false,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsOrdinaryPostCommitSidecarFailure(ex))
        {
            // The head is already durable. Any partial sidecar change is stale or fails its
            // authenticated Merkle root and will be rebuilt on the next exact cold lookup.
            // Cancellation is intentionally included: the authoritative commit crossed its
            // non-cancellable boundary before this disposable work began.
        }
    }

    private static bool IsDisposableIndexFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException;

    private static bool IsOrdinaryPostCommitSidecarFailure(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not BadImageFormatException
            and not InvalidProgramException;

    private static void ReadExactlyAt(
        SafeFileHandle handle,
        Span<byte> destination,
        long fileOffset)
    {
        var consumed = 0;
        while (consumed < destination.Length)
        {
            var read = RandomAccess.Read(
                handle,
                destination[consumed..],
                checked(fileOffset + consumed));
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The evidence journal ended before the indexed record was complete.");
            }

            consumed += read;
        }
    }

    private sealed record ExactIndexRebuildResult(
        IReadOnlyList<StoredEvidenceCursorRecord> Records,
        IReadOnlyDictionary<string, StoredEvidenceCursorRecord> Matches,
        EvidenceJournalVerificationStamp? VerificationStamp);

    private async Task<EvidenceJournalHead> LoadOrCreateHeadForAppendAsync(
        FileStream stream,
        Func<EvidenceJournalHeadUnsigned, string> signHead,
        Action<EvidenceJournalHead> validateHead,
        CancellationToken cancellationToken)
    {
        var existing = await ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            validateHead(existing);
            ValidateHeadShape(existing);
            return existing;
        }

        if (stream.Length != 0)
        {
            throw new InvalidDataException(
                "The evidence journal has data but its authenticated head is missing.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var unsigned = new EvidenceJournalHeadUnsigned(
            _turnStorageKey,
            0,
            0,
            FirstChecksumHex);
        var created = new EvidenceJournalHead(
            unsigned.TurnStorageKey,
            unsigned.CommittedLength,
            unsigned.Sequence,
            unsigned.Checksum,
            signHead(unsigned));
        await WriteHeadAtomicallyAsync(created).ConfigureAwait(false);
        return created;
    }

    private async Task<EvidenceJournalHead?> LoadHeadForReplayAsync(
        FileStream stream,
        Action<EvidenceJournalHead> validateHead,
        CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            if (stream.Length == 0)
            {
                return null;
            }

            throw new InvalidDataException(
                "The evidence journal has data but its authenticated head is missing.");
        }

        validateHead(head);
        ValidateHeadShape(head);
        return head;
    }

    private async Task<EvidenceJournalHead?> ReadHeadAsync(CancellationToken cancellationToken)
    {
        var path = GetHeadPath();
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            _directory,
            "The evidence journal directory is not a regular local directory.");
        var bytes = await WindowsBoundedFileReader.TryReadExactlyAsync(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(path),
            minimumLength: 1,
            MaximumHeadBytes,
            "The authenticated evidence journal head is not a regular local file.",
            "The authenticated evidence journal head has an invalid size.",
            "The authenticated evidence journal head changed while it was being read.",
            cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EvidenceJournalHead>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The authenticated evidence journal head is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The authenticated evidence journal head is malformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task WriteHeadAtomicallyAsync(EvidenceJournalHead head)
    {
        var finalPath = GetHeadPath();
        var temporaryPath = Path.Combine(_directory, $".{Guid.NewGuid():N}.head.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(head, JsonOptions);
        if (bytes.Length == 0 || bytes.Length > MaximumHeadBytes)
        {
            throw new InvalidDataException("The authenticated evidence journal head is too large.");
        }

        try
        {
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                _directory,
                "The evidence journal directory is not a regular local directory.");
            await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             writeThrough: true,
                             "The evidence journal temporary head is not a regular local file."))
            {
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporaryPath,
                finalPath,
                replaceExisting: true,
                "The authenticated evidence journal head is not a regular local file.");
        }
        finally
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                "The evidence journal temporary head is not a regular local file.");

            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task RecoverUncommittedSuffixAsync(
        FileStream stream,
        EvidenceJournalHead head,
        CancellationToken cancellationToken)
    {
        if (stream.Length < head.CommittedLength)
        {
            throw new InvalidDataException(
                "The evidence journal is shorter than its authenticated committed head.");
        }

        if (stream.Length > head.CommittedLength)
        {
            var suffixLength = stream.Length - head.CommittedLength;
            if (suffixLength > MaximumLineBytes + 1L)
            {
                throw new InvalidDataException(
                    "The evidence journal has an oversized uncommitted suffix.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            stream.SetLength(head.CommittedLength);
            stream.Flush(flushToDisk: true);
        }

        if (head.Sequence == 0)
        {
            if (head.CommittedLength != 0 || !string.Equals(head.Checksum, FirstChecksumHex, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The empty evidence journal head is invalid.");
            }
            return;
        }

        if (head.CommittedLength <= 0)
        {
            throw new InvalidDataException("The evidence journal head has an invalid committed length.");
        }

        stream.Position = head.CommittedLength - 1;
        if (stream.ReadByte() != '\n')
        {
            throw new InvalidDataException(
                "The evidence journal commit marker does not match its authenticated head.");
        }
    }

    private async Task<IReadOnlyList<StoredEvidenceCursorRecord>> ReplayCommittedAsync(
        FileStream stream,
        EvidenceJournalHead head,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Action<StoredEvidenceCursorRecord, long, int>? observeRecord,
        bool collectRecords,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var result = new List<StoredEvidenceCursorRecord>();
        long validatedCount = 0;
        var expectedSequence = 1L;
        var expectedPreviousChecksum = FirstChecksumHex;
        var line = new ArrayBufferWriter<byte>(4096);
        var readBuffer = new byte[4096];
        long consumed = 0;
        long lineStart = 0;
        while (consumed < head.CommittedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(readBuffer.Length, head.CommittedLength - consumed);
            var read = await stream.ReadAsync(
                readBuffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "The evidence journal ended before its authenticated committed length.");
            }

            consumed += read;
            var bufferStart = consumed - read;
            var offset = 0;
            while (offset < read)
            {
                var newline = readBuffer.AsSpan(offset, read - offset).IndexOf((byte)'\n');
                var segmentLength = newline < 0 ? read - offset : newline;
                if (line.WrittenCount + segmentLength > MaximumLineBytes)
                {
                    throw new InvalidDataException(
                        "The evidence journal contains an oversized committed line.");
                }

                if (segmentLength > 0)
                {
                    readBuffer.AsSpan(offset, segmentLength).CopyTo(line.GetSpan(segmentLength));
                    line.Advance(segmentLength);
                }
                offset += segmentLength;
                if (newline < 0)
                {
                    break;
                }

                if (line.WrittenCount == 0)
                {
                    throw new InvalidDataException(
                        "The evidence journal contains an empty committed line.");
                }

                var entry = DeserializeEntry(line.WrittenSpan);
                ValidateEntry(entry, expectedSequence, expectedPreviousChecksum);
                var record = DeserializeRecord(entry);
                validateExistingRecord(record);
                var cursor = new StoredEvidenceCursorRecord(entry.Sequence, record, entry.Checksum);
                observeRecord?.Invoke(cursor, lineStart, line.WrittenCount);
                if (collectRecords)
                {
                    result.Add(cursor);
                }
                validatedCount++;
                expectedSequence++;
                expectedPreviousChecksum = entry.Checksum;
                line.Clear();
                offset++;
                lineStart = checked(bufferStart + offset);
            }
        }

        if (line.WrittenCount != 0)
        {
            throw new InvalidDataException(
                "The authenticated evidence journal ends with an unterminated committed entry.");
        }

        if (validatedCount != head.Sequence
            || !string.Equals(expectedPreviousChecksum, head.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The evidence journal does not match its authenticated committed head.");
        }

        return result;
    }

    private async Task<EvidenceJournalEntry> ReadAndValidateTailAsync(
        FileStream stream,
        EvidenceJournalHead head,
        CancellationToken cancellationToken)
    {
        var buffer = await ReadLastCommittedLineAsync(
            stream,
            head.CommittedLength - 1,
            cancellationToken).ConfigureAwait(false);
        var entry = DeserializeEntry(buffer);
        ValidateEntrySelf(entry);
        if (entry.Sequence != head.Sequence
            || !string.Equals(entry.Checksum, head.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The evidence journal tail does not match its authenticated committed head.");
        }
        return entry;
    }

    private async Task<byte[]> ReadLastCommittedLineAsync(
        FileStream stream,
        long endExclusive,
        CancellationToken cancellationToken)
    {
        const int blockSize = 4096;
        var pieces = new List<byte[]>();
        var cursor = endExclusive;
        var lineLength = 0;
        var totalRead = 0;
        while (cursor > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(blockSize, cursor);
            cursor -= count;
            var block = new byte[count];
            stream.Position = cursor;
            await stream.ReadExactlyAsync(block, cancellationToken).ConfigureAwait(false);
            totalRead += count;
            var newline = block.AsSpan().LastIndexOf((byte)'\n');
            var offset = newline + 1;
            var pieceLength = count - offset;
            if (pieceLength > 0)
            {
                pieces.Add(block.AsSpan(offset, pieceLength).ToArray());
                lineLength += pieceLength;
            }

            if (lineLength > MaximumLineBytes)
            {
                throw new InvalidDataException("The evidence journal tail exceeds the supported metadata size.");
            }

            if (newline >= 0 || cursor == 0)
            {
                break;
            }
        }

        _tailReadObserver?.Invoke(totalRead);
        if (lineLength == 0)
        {
            throw new InvalidDataException("The evidence journal contains an empty committed tail.");
        }

        var line = new byte[lineLength];
        var destination = 0;
        for (var index = pieces.Count - 1; index >= 0; index--)
        {
            pieces[index].CopyTo(line, destination);
            destination += pieces[index].Length;
        }
        return line;
    }

    private static EvidenceJournalEntry DeserializeEntry(ReadOnlySpan<byte> line)
    {
        if (line.Length <= 0 || line.Length > MaximumLineBytes)
        {
            throw new InvalidDataException("The evidence journal contains an invalid committed line.");
        }

        try
        {
            return JsonSerializer.Deserialize<EvidenceJournalEntry>(line, JsonOptions)
                ?? throw new InvalidDataException("The evidence journal contains an empty committed entry.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The evidence journal contains malformed committed JSON.", ex);
        }
    }

    private static void ValidateEntry(
        EvidenceJournalEntry entry,
        long expectedSequence,
        string expectedPreviousChecksum)
    {
        ValidateEntrySelf(entry);
        if (entry.Sequence != expectedSequence)
        {
            throw new InvalidDataException("The evidence journal sequence is discontinuous.");
        }

        if (!string.Equals(entry.PreviousChecksum, expectedPreviousChecksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The evidence journal checksum chain is broken.");
        }
    }

    private static void ValidateEntrySelf(EvidenceJournalEntry entry)
    {
        if (entry.Sequence <= 0
            || entry.TimestampUtcTicks <= 0
            || string.IsNullOrWhiteSpace(entry.PreviousChecksum)
            || entry.Payload is null
            || entry.Payload.Length == 0
            || string.IsNullOrWhiteSpace(entry.Checksum))
        {
            throw new InvalidDataException("The evidence journal entry metadata is invalid.");
        }

        var previous = DecodeChecksum(
            entry.PreviousChecksum,
            "The evidence journal previous checksum is invalid.");
        try
        {
            var expected = ComputeChecksum(
                entry.Sequence,
                entry.TimestampUtcTicks,
                previous,
                entry.Payload);
            if (!FixedTimeHexEquals(expected, entry.Checksum))
            {
                throw new InvalidDataException("The evidence journal entry failed checksum validation.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previous);
        }
    }

    private static StoredEvidenceRecord DeserializeRecord(EvidenceJournalEntry entry)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredEvidenceRecord>(entry.Payload, JsonOptions)
                ?? throw new InvalidDataException("The evidence journal entry has no evidence record.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The evidence journal entry payload is malformed.", ex);
        }
    }

    private static void ValidateHeadShape(EvidenceJournalHead head)
    {
        if (string.IsNullOrWhiteSpace(head.TurnStorageKey)
            || head.CommittedLength < 0
            || head.Sequence < 0
            || string.IsNullOrWhiteSpace(head.Checksum)
            || string.IsNullOrWhiteSpace(head.Mac))
        {
            throw new InvalidDataException("The authenticated evidence journal head is invalid.");
        }
    }

    private string GetHeadPath() => Path.Combine(_directory, "evidence.head.json");

    private string GetJournalPath() => Path.Combine(_directory, "evidence.journal.jsonl");

    private EvidenceJournalVerificationStamp? TryCaptureVerificationStamp(
        FileStream stream,
        EvidenceJournalHead head)
    {
        if (_stampUnavailable?.Invoke() == true)
        {
            return null;
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    stream.SafeFileHandle,
                    FileInfoByHandleClass.FileBasicInfo,
                    out FileBasicInfo basicInfo,
                    (uint)Marshal.SizeOf<FileBasicInfo>()))
            {
                return null;
            }

            if (!GetFileInformationByHandleEx(
                    stream.SafeFileHandle,
                    FileInfoByHandleClass.FileIdInfo,
                    out FileIdInfo fileIdInfo,
                    (uint)Marshal.SizeOf<FileIdInfo>()))
            {
                return null;
            }

            return new EvidenceJournalVerificationStamp(
                head.Mac,
                fileIdInfo.VolumeSerialNumber,
                fileIdInfo.FileId.Low,
                fileIdInfo.FileId.High,
                basicInfo.ChangeTime,
                RandomAccess.GetLength(stream.SafeFileHandle),
                head.Sequence);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private FileStream OpenJournal() =>
        WindowsOrchestrationFileBoundary.OpenRegularFile(
            GetJournalPath(),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            writeThrough: true,
            "The evidence journal is not a regular local file.");

    private async Task<FileStream> AcquireWriteLeaseAsync(CancellationToken cancellationToken)
    {
        EnsureStorageDirectory();
        var path = Path.Combine(_directory, ".writer.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return WindowsOrchestrationFileBoundary.OpenRegularFile(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    writeThrough: true,
                    "The evidence journal writer lease is not a regular local file.");
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void EnsureStorageDirectory() =>
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            _directory,
            "The evidence journal directory is not a regular local directory.");

    private static string ComputeChecksum(
        long sequence,
        long timestampUtcTicks,
        ReadOnlySpan<byte> previousChecksum,
        ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[sizeof(long) + sizeof(long) + sizeof(int)];
        BinaryPrimitives.WriteInt64LittleEndian(header, sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header[sizeof(long)..], timestampUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(header[(sizeof(long) * 2)..], payload.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header[..(sizeof(long) * 2)]);
        hash.AppendData(previousChecksum);
        hash.AppendData(header[(sizeof(long) * 2)..]);
        hash.AppendData(payload);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte[] DecodeChecksum(string value, string message)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length == 32)
            {
                return bytes;
            }
            CryptographicOperations.ZeroMemory(bytes);
        }
        catch (FormatException)
        {
        }
        throw new InvalidDataException(message);
    }

    private static bool FixedTimeHexEquals(string left, string right)
    {
        byte[] leftBytes;
        byte[] rightBytes;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return leftBytes.Length == rightBytes.Length
                   && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33
               || exception.InnerException is Win32Exception
               {
                   NativeErrorCode: 32 or 33
               };
    }

    /// <summary>
    /// Fixed-size negative-membership accelerator. False positives fall back to an exact
    /// authenticated streaming scan; false negatives are impossible while the filter's
    /// verification stamp matches the journal. Saturation can therefore cost time, never
    /// correctness or an artificial execution limit.
    /// </summary>
    private sealed class EvidenceIdBloomFilter
    {
        private readonly byte[] _bits = new byte[EvidenceIdBloomBytes];

        public void Add(string evidenceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(evidenceId));
            try
            {
                for (var index = 0; index < EvidenceIdBloomHashCount; index++)
                {
                    SetBit(BitIndex(digest, index));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        public bool MightContain(string evidenceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(evidenceId));
            try
            {
                for (var index = 0; index < EvidenceIdBloomHashCount; index++)
                {
                    if (!IsSet(BitIndex(digest, index)))
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        private static int BitIndex(ReadOnlySpan<byte> digest, int index)
        {
            var first = BinaryPrimitives.ReadUInt32LittleEndian(
                digest.Slice((index % 4) * sizeof(uint), sizeof(uint)));
            var second = BinaryPrimitives.ReadUInt32LittleEndian(
                digest.Slice((4 + (index % 4)) * sizeof(uint), sizeof(uint)));
            var mixed = first + ((uint)index * second) + ((uint)index * (uint)index);
            return (int)(mixed % (uint)(EvidenceIdBloomBytes * 8));
        }

        private void SetBit(int bitIndex) =>
            _bits[bitIndex >> 3] |= (byte)(1 << (bitIndex & 7));

        private bool IsSet(int bitIndex) =>
            (_bits[bitIndex >> 3] & (1 << (bitIndex & 7))) != 0;
    }

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);
}
