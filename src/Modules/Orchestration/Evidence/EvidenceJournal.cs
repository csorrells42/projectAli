using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Orchestration.Evidence;

internal enum EvidenceJournalCommitBoundary
{
    BodyFlushed,
    CommitMarkerFlushed,
    HeadCommitted
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

internal sealed class EvidenceJournal
{
    private const int MaximumLineBytes = 4 * 1024 * 1024;
    private const int MaximumHeadBytes = 16 * 1024;
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
    private readonly Func<bool>? _stampUnavailable;
    private EvidenceJournalVerificationStamp? _verifiedStamp;

    public EvidenceJournal(
        string directory,
        string turnStorageKey,
        Action<EvidenceJournalCommitBoundary>? faultInjector = null,
        Action<int>? tailReadObserver = null,
        Func<bool>? stampUnavailable = null)
    {
        _directory = Path.GetFullPath(directory);
        _turnStorageKey = turnStorageKey;
        _faultInjector = faultInjector;
        _tailReadObserver = tailReadObserver;
        _stampUnavailable = stampUnavailable;
    }

    public async Task<StoredEvidenceCursorRecord> AppendAsync(
        StoredEvidenceRecord record,
        Action<StoredEvidenceRecord> validateExistingRecord,
        Func<EvidenceJournalHeadUnsigned, string> signHead,
        Action<EvidenceJournalHead> validateHead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(validateExistingRecord);
        ArgumentNullException.ThrowIfNull(signHead);
        ArgumentNullException.ThrowIfNull(validateHead);
        Directory.CreateDirectory(_directory);
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadOrCreateHeadForAppendAsync(
            stream,
            signHead,
            validateHead,
            cancellationToken).ConfigureAwait(false);
        await RecoverUncommittedSuffixAsync(stream, head, cancellationToken).ConfigureAwait(false);

        EvidenceJournalEntry? tail = null;
        var currentStamp = TryCaptureVerificationStamp(stream, head);
        if (_verifiedStamp is null || currentStamp is null || _verifiedStamp != currentStamp)
        {
            await ReplayCommittedAsync(
                stream,
                head,
                validateExistingRecord,
                cancellationToken).ConfigureAwait(false);
            _verifiedStamp = TryCaptureVerificationStamp(stream, head);
        }
        else if (head.Sequence > 0)
        {
            tail = await ReadAndValidateTailAsync(
                stream,
                head,
                cancellationToken).ConfigureAwait(false);
            validateExistingRecord(DeserializeRecord(tail));
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
            _verifiedStamp = TryCaptureVerificationStamp(stream, committedHead);
            _faultInjector?.Invoke(EvidenceJournalCommitBoundary.HeadCommitted);
            return new StoredEvidenceCursorRecord(sequence, record, checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previousChecksumBytes);
        }
    }

    public async Task<IReadOnlyList<StoredEvidenceCursorRecord>> ReplayAsync(
        Action<StoredEvidenceRecord> validateExistingRecord,
        Action<EvidenceJournalHead> validateHead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateExistingRecord);
        ArgumentNullException.ThrowIfNull(validateHead);
        Directory.CreateDirectory(_directory);
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadForReplayAsync(stream, validateHead, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            _tailReadObserver?.Invoke(0);
            return [];
        }

        await RecoverUncommittedSuffixAsync(stream, head, cancellationToken).ConfigureAwait(false);
        var result = await ReplayCommittedAsync(
            stream,
            head,
            validateExistingRecord,
            cancellationToken).ConfigureAwait(false);
        _verifiedStamp = TryCaptureVerificationStamp(stream, head);
        return result;
    }

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
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumHeadBytes)
        {
            throw new InvalidDataException("The authenticated evidence journal head has an invalid size.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<EvidenceJournalHead>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The authenticated evidence journal head is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The authenticated evidence journal head is malformed.", ex);
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
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var result = new List<StoredEvidenceCursorRecord>();
        var expectedSequence = 1L;
        var expectedPreviousChecksum = FirstChecksumHex;
        var line = new ArrayBufferWriter<byte>(4096);
        var readBuffer = new byte[4096];
        long consumed = 0;
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
                result.Add(new StoredEvidenceCursorRecord(entry.Sequence, record, entry.Checksum));
                expectedSequence++;
                expectedPreviousChecksum = entry.Checksum;
                line.Clear();
                offset++;
            }
        }

        if (line.WrittenCount != 0)
        {
            throw new InvalidDataException(
                "The authenticated evidence journal ends with an unterminated committed entry.");
        }

        if (result.Count != head.Sequence
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
        new(
            GetJournalPath(),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

    private async Task<FileStream> AcquireWriteLeaseAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, ".writer.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

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
        return error is 32 or 33;
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
