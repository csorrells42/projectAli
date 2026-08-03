using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Orchestration.Evidence;

internal readonly record struct EvidenceExactIndexLocation(
    long JournalOffset,
    int LineLength,
    long Sequence);

internal sealed class EvidenceExactIndexInvalidException : IOException
{
    internal EvidenceExactIndexInvalidException(string message)
        : base(message)
    {
    }

    internal EvidenceExactIndexInvalidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record EvidenceExactIndexManifestUnsigned(
    string TurnStorageKey,
    long JournalCommittedLength,
    long JournalSequence,
    string JournalChecksum,
    string JournalHeadMac,
    ulong JournalVolumeSerialNumber,
    ulong JournalFileIdLow,
    ulong JournalFileIdHigh,
    long JournalChangeTimeTicks,
    long JournalPhysicalLength,
    long Capacity,
    long Count,
    long TableLength,
    long AuthenticationTreeLength,
    string AuthenticationRoot);

internal sealed record EvidenceExactIndexManifest(
    string TurnStorageKey,
    long JournalCommittedLength,
    long JournalSequence,
    string JournalChecksum,
    string JournalHeadMac,
    ulong JournalVolumeSerialNumber,
    ulong JournalFileIdLow,
    ulong JournalFileIdHigh,
    long JournalChangeTimeTicks,
    long JournalPhysicalLength,
    long Capacity,
    long Count,
    long TableLength,
    long AuthenticationTreeLength,
    string AuthenticationRoot,
    string Mac);

/// <summary>
/// Disposable exact lookup accelerator for the authenticated evidence journal. The journal is
/// always authoritative: this index is accepted only when its keyed manifest names the exact
/// journal head and the exact Windows file verification stamp established by a full replay.
/// Table pages are covered by a SHA-256 Merkle tree whose root is in that keyed manifest.
/// </summary>
internal sealed class EvidenceExactIndex : IDisposable
{
    internal const string TableFileName = "evidence.exact-index.table.bin";
    internal const string AuthenticationTreeFileName = "evidence.exact-index.auth-tree.bin";
    internal const string ManifestFileName = "evidence.exact-index.manifest.json";

    private const int SlotBytes = 64;
    private const int TablePageBytes = 4096;
    private const int SlotsPerPage = TablePageBytes / SlotBytes;
    private const int AuthenticationTagBytes = 32;
    private const int AuthenticationFanout = 128;
    private const int MaximumManifestBytes = 64 * 1024;
    private const string ManifestUpdateTemporaryFileName =
        ".evidence.exact-index.manifest.update.tmp";
    private const string InvalidIndexMessage =
        "The disposable evidence exact index is invalid and must be rebuilt.";

    private static readonly byte[] PageHashDomain =
        Encoding.UTF8.GetBytes("Ali.EvidenceExactIndex.Page.v1");
    private static readonly byte[] NodeHashDomain =
        Encoding.UTF8.GetBytes("Ali.EvidenceExactIndex.Node.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _directory;
    private readonly Func<byte[], string> _authenticate;
    private readonly FileStream _table;
    private readonly FileStream _authenticationTree;
    private readonly AuthenticationTreeLayout _authenticationLayout;
    private EvidenceExactIndexManifest _manifest;
    private byte[] _authenticationRoot;
    private long _cachedPageIndex = -1;
    private byte[]? _cachedPage;
    private bool _disposed;

    private EvidenceExactIndex(
        string directory,
        Func<byte[], string> authenticate,
        FileStream table,
        FileStream authenticationTree,
        AuthenticationTreeLayout authenticationLayout,
        EvidenceExactIndexManifest manifest,
        byte[] authenticationRoot)
    {
        _directory = directory;
        _authenticate = authenticate;
        _table = table;
        _authenticationTree = authenticationTree;
        _authenticationLayout = authenticationLayout;
        _manifest = manifest;
        _authenticationRoot = authenticationRoot;
    }

    internal long Count => _manifest.Count;

    internal static async Task<EvidenceExactIndex?> TryOpenAsync(
        string directory,
        string turnStorageKey,
        EvidenceJournalHead head,
        EvidenceJournalVerificationStamp? journalStamp,
        Func<byte[], string> authenticate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnStorageKey);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(authenticate);
        if (journalStamp is null)
        {
            return null;
        }

        var fullDirectory = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(fullDirectory, ManifestFileName);
        var bytes = await WindowsBoundedFileReader.TryReadExactlyAsync(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(manifestPath),
            minimumLength: 1,
            MaximumManifestBytes,
            "The evidence exact-index manifest is not a regular local file.",
            "The evidence exact-index manifest has an invalid size.",
            "The evidence exact-index manifest changed while it was being read.",
            cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        EvidenceExactIndexManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<EvidenceExactIndexManifest>(bytes, JsonOptions)
                ?? throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }
        catch (JsonException ex)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        ValidateManifestAuthentication(manifest, authenticate);
        if (!ManifestNamesJournal(manifest, turnStorageKey, head, journalStamp))
        {
            return null;
        }

        var layout = AuthenticationTreeLayout.Create(manifest.Capacity / SlotsPerPage);
        FileStream? table = null;
        FileStream? authenticationTree = null;
        try
        {
            table = OpenIndexFile(
                Path.Combine(fullDirectory, TableFileName),
                FileMode.Open,
                FileAccess.ReadWrite);
            authenticationTree = OpenIndexFile(
                Path.Combine(fullDirectory, AuthenticationTreeFileName),
                FileMode.Open,
                FileAccess.ReadWrite);
            if (table.Length != manifest.TableLength
                || authenticationTree.Length != manifest.AuthenticationTreeLength
                || manifest.TableLength != checked(manifest.Capacity * SlotBytes)
                || manifest.AuthenticationTreeLength != layout.FileLength)
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            var root = DecodeDigest(manifest.AuthenticationRoot);
            var opened = new EvidenceExactIndex(
                fullDirectory,
                authenticate,
                table,
                authenticationTree,
                layout,
                manifest,
                root);
            table = null;
            authenticationTree = null;
            return opened;
        }
        catch (EvidenceExactIndexInvalidException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage, ex);
        }
        finally
        {
            table?.Dispose();
            authenticationTree?.Dispose();
        }
    }

    internal static Builder CreateBuilder(
        string directory,
        string turnStorageKey,
        EvidenceJournalHead head,
        Func<byte[], string> authenticate) =>
        new(directory, turnStorageKey, head, authenticate);

    internal void VisitCandidateLocations(
        string evidenceId,
        Action<EvidenceExactIndexLocation> visitor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentNullException.ThrowIfNull(visitor);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(evidenceId));
        try
        {
            var start = checked((long)(BinaryPrimitives.ReadUInt64LittleEndian(digest) &
                                       (ulong)(_manifest.Capacity - 1)));
            for (long probe = 0; probe < _manifest.Capacity; probe++)
            {
                var slotIndex = (start + probe) & (_manifest.Capacity - 1);
                var slot = ReadAuthenticatedSlot(slotIndex);
                if (slot.Sequence == 0)
                {
                    return;
                }

                if (CryptographicOperations.FixedTimeEquals(slot.EvidenceIdDigest, digest))
                {
                    visitor(new EvidenceExactIndexLocation(
                        slot.JournalOffset,
                        slot.LineLength,
                        slot.Sequence));
                }
            }

            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal async Task<bool> TryInsertAndAdvanceAsync(
        string evidenceId,
        EvidenceExactIndexLocation location,
        EvidenceJournalHead committedHead,
        EvidenceJournalVerificationStamp committedStamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentNullException.ThrowIfNull(committedHead);
        ArgumentNullException.ThrowIfNull(committedStamp);
        if (_manifest.Count + 1 > _manifest.Capacity / 2)
        {
            return false;
        }

        if (committedHead.Sequence != _manifest.JournalSequence + 1
            || committedHead.Sequence != _manifest.Count + 1
            || location.Sequence != committedHead.Sequence)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(evidenceId));
        try
        {
            var start = checked((long)(BinaryPrimitives.ReadUInt64LittleEndian(digest) &
                                       (ulong)(_manifest.Capacity - 1)));
            for (long probe = 0; probe < _manifest.Capacity; probe++)
            {
                var slotIndex = (start + probe) & (_manifest.Capacity - 1);
                var slot = ReadAuthenticatedSlot(slotIndex);
                if (slot.Sequence != 0)
                {
                    continue;
                }

                var pageIndex = slotIndex / SlotsPerPage;
                var slotWithinPage = checked((int)(slotIndex % SlotsPerPage));
                var page = LoadAuthenticatedPage(pageIndex);
                SerializeSlot(
                    page.AsSpan(slotWithinPage * SlotBytes, SlotBytes),
                    new ExactIndexSlot(
                        digest.ToArray(),
                        location.JournalOffset,
                        location.LineLength,
                        location.Sequence));
                _table.Position = checked(pageIndex * TablePageBytes);
                await _table.WriteAsync(page, CancellationToken.None).ConfigureAwait(false);
                UpdatePageAuthentication(pageIndex, page);

                var manifest = CreateManifest(
                    _manifest.TurnStorageKey,
                    committedHead,
                    committedStamp,
                    _manifest.Capacity,
                    _manifest.Count + 1,
                    _authenticationLayout.FileLength,
                    _authenticationRoot,
                    _authenticate);
                await WriteManifestAtomicallyAsync(
                    _directory,
                    manifest,
                    CancellationToken.None).ConfigureAwait(false);
                _manifest = manifest;
                return true;
            }

            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cachedPage = null;
        _cachedPageIndex = -1;
        CryptographicOperations.ZeroMemory(_authenticationRoot);
        _authenticationTree.Dispose();
        _table.Dispose();
    }

    private ExactIndexSlot ReadAuthenticatedSlot(long slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _manifest.Capacity)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        var pageIndex = slotIndex / SlotsPerPage;
        var slotWithinPage = checked((int)(slotIndex % SlotsPerPage));
        return ParseSlot(
            LoadAuthenticatedPage(pageIndex)
                .AsSpan(slotWithinPage * SlotBytes, SlotBytes),
            _manifest);
    }

    private byte[] LoadAuthenticatedPage(long pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _authenticationLayout.PageCount)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        if (_cachedPageIndex == pageIndex && _cachedPage is not null)
        {
            return _cachedPage;
        }

        var page = new byte[TablePageBytes];
        _table.Position = checked(pageIndex * TablePageBytes);
        ReadExactly(_table, page);
        VerifyPageAuthentication(pageIndex, page);
        _cachedPageIndex = pageIndex;
        _cachedPage = page;
        return page;
    }

    private void VerifyPageAuthentication(long pageIndex, ReadOnlySpan<byte> page)
    {
        var expected = HashPage(pageIndex, page);
        try
        {
            var stored = ReadAuthenticationTag(level: 0, pageIndex);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expected, stored))
                {
                    throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }

            var currentIndex = pageIndex;
            for (var level = 1; level < _authenticationLayout.LevelCount; level++)
            {
                currentIndex /= AuthenticationFanout;
                CryptographicOperations.ZeroMemory(expected);
                expected = HashNode(level, currentIndex);
                stored = ReadAuthenticationTag(level, currentIndex);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(expected, stored))
                    {
                        throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(stored);
                }
            }

            if (!CryptographicOperations.FixedTimeEquals(expected, _authenticationRoot))
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private void UpdatePageAuthentication(long pageIndex, ReadOnlySpan<byte> page)
    {
        var tag = HashPage(pageIndex, page);
        try
        {
            WriteAuthenticationTag(level: 0, pageIndex, tag);
            var currentIndex = pageIndex;
            for (var level = 1; level < _authenticationLayout.LevelCount; level++)
            {
                currentIndex /= AuthenticationFanout;
                CryptographicOperations.ZeroMemory(tag);
                tag = HashNode(level, currentIndex);
                WriteAuthenticationTag(level, currentIndex, tag);
            }

            CryptographicOperations.ZeroMemory(_authenticationRoot);
            _authenticationRoot = tag.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private byte[] HashNode(int level, long nodeIndex)
    {
        if (level <= 0 || level >= _authenticationLayout.LevelCount)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        var previousCount = _authenticationLayout.LevelCounts[level - 1];
        var firstChild = checked(nodeIndex * AuthenticationFanout);
        var childCount = checked((int)Math.Min(AuthenticationFanout, previousCount - firstChild));
        if (childCount <= 0)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(NodeHashDomain);
        Span<byte> header = stackalloc byte[sizeof(int) + sizeof(long) + sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, level);
        BinaryPrimitives.WriteInt64LittleEndian(header[sizeof(int)..], nodeIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header[(sizeof(int) + sizeof(long))..], childCount);
        hash.AppendData(header);
        for (var child = 0; child < childCount; child++)
        {
            var childTag = ReadAuthenticationTag(level - 1, firstChild + child);
            try
            {
                hash.AppendData(childTag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(childTag);
            }
        }

        return hash.GetHashAndReset();
    }

    private static byte[] HashPage(long pageIndex, ReadOnlySpan<byte> page)
    {
        if (page.Length != TablePageBytes)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(PageHashDomain);
        Span<byte> index = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(index, pageIndex);
        hash.AppendData(index);
        hash.AppendData(page);
        return hash.GetHashAndReset();
    }

    private byte[] ReadAuthenticationTag(int level, long index)
    {
        var tag = new byte[AuthenticationTagBytes];
        _authenticationTree.Position = _authenticationLayout.GetTagOffset(level, index);
        ReadExactly(_authenticationTree, tag);
        return tag;
    }

    private void WriteAuthenticationTag(int level, long index, ReadOnlySpan<byte> tag)
    {
        if (tag.Length != AuthenticationTagBytes)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        _authenticationTree.Position = _authenticationLayout.GetTagOffset(level, index);
        _authenticationTree.Write(tag);
    }

    private static EvidenceExactIndexManifest CreateManifest(
        string turnStorageKey,
        EvidenceJournalHead head,
        EvidenceJournalVerificationStamp journalStamp,
        long capacity,
        long count,
        long authenticationTreeLength,
        ReadOnlySpan<byte> authenticationRoot,
        Func<byte[], string> authenticate)
    {
        var rootHex = Convert.ToHexString(authenticationRoot).ToLowerInvariant();
        var unsigned = new EvidenceExactIndexManifestUnsigned(
            turnStorageKey,
            head.CommittedLength,
            head.Sequence,
            head.Checksum,
            head.Mac,
            journalStamp.VolumeSerialNumber,
            journalStamp.FileIdLow,
            journalStamp.FileIdHigh,
            journalStamp.ChangeTimeTicks,
            journalStamp.Length,
            capacity,
            count,
            checked(capacity * SlotBytes),
            authenticationTreeLength,
            rootHex);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions);
        try
        {
            return new EvidenceExactIndexManifest(
                unsigned.TurnStorageKey,
                unsigned.JournalCommittedLength,
                unsigned.JournalSequence,
                unsigned.JournalChecksum,
                unsigned.JournalHeadMac,
                unsigned.JournalVolumeSerialNumber,
                unsigned.JournalFileIdLow,
                unsigned.JournalFileIdHigh,
                unsigned.JournalChangeTimeTicks,
                unsigned.JournalPhysicalLength,
                unsigned.Capacity,
                unsigned.Count,
                unsigned.TableLength,
                unsigned.AuthenticationTreeLength,
                unsigned.AuthenticationRoot,
                authenticate(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateManifestAuthentication(
        EvidenceExactIndexManifest manifest,
        Func<byte[], string> authenticate)
    {
        if (string.IsNullOrWhiteSpace(manifest.TurnStorageKey)
            || manifest.JournalCommittedLength < 0
            || manifest.JournalSequence < 0
            || !IsHexDigest(manifest.JournalChecksum)
            || !IsHexDigest(manifest.JournalHeadMac)
            || manifest.JournalChangeTimeTicks <= 0
            || manifest.JournalPhysicalLength < manifest.JournalCommittedLength
            || manifest.Capacity < SlotsPerPage
            || (manifest.Capacity & (manifest.Capacity - 1)) != 0
            || manifest.Capacity % SlotsPerPage != 0
            || manifest.Count < 0
            || manifest.Count != manifest.JournalSequence
            || manifest.Count > manifest.Capacity / 2
            || manifest.Capacity > long.MaxValue / SlotBytes
            || manifest.TableLength != checked(manifest.Capacity * SlotBytes)
            || manifest.AuthenticationTreeLength <= 0
            || !IsHexDigest(manifest.AuthenticationRoot)
            || !IsHexDigest(manifest.Mac))
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        var unsigned = new EvidenceExactIndexManifestUnsigned(
            manifest.TurnStorageKey,
            manifest.JournalCommittedLength,
            manifest.JournalSequence,
            manifest.JournalChecksum,
            manifest.JournalHeadMac,
            manifest.JournalVolumeSerialNumber,
            manifest.JournalFileIdLow,
            manifest.JournalFileIdHigh,
            manifest.JournalChangeTimeTicks,
            manifest.JournalPhysicalLength,
            manifest.Capacity,
            manifest.Count,
            manifest.TableLength,
            manifest.AuthenticationTreeLength,
            manifest.AuthenticationRoot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions);
        try
        {
            if (!FixedTimeHexEquals(authenticate(bytes), manifest.Mac))
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool ManifestNamesJournal(
        EvidenceExactIndexManifest manifest,
        string turnStorageKey,
        EvidenceJournalHead head,
        EvidenceJournalVerificationStamp stamp) =>
        string.Equals(manifest.TurnStorageKey, turnStorageKey, StringComparison.Ordinal)
        && manifest.JournalCommittedLength == head.CommittedLength
        && manifest.JournalSequence == head.Sequence
        && string.Equals(manifest.JournalChecksum, head.Checksum, StringComparison.Ordinal)
        && string.Equals(manifest.JournalHeadMac, head.Mac, StringComparison.Ordinal)
        && manifest.JournalVolumeSerialNumber == stamp.VolumeSerialNumber
        && manifest.JournalFileIdLow == stamp.FileIdLow
        && manifest.JournalFileIdHigh == stamp.FileIdHigh
        && manifest.JournalChangeTimeTicks == stamp.ChangeTimeTicks
        && manifest.JournalPhysicalLength == stamp.Length
        && stamp.Sequence == head.Sequence
        && string.Equals(stamp.HeadMac, head.Mac, StringComparison.Ordinal);

    private static async Task WriteManifestAtomicallyAsync(
        string directory,
        EvidenceExactIndexManifest manifest,
        CancellationToken cancellationToken)
    {
        var finalPath = Path.Combine(directory, ManifestFileName);
        var temporaryPath = Path.Combine(directory, ManifestUpdateTemporaryFileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        try
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                "The evidence exact-index temporary manifest is not a regular local file.");
            await using (var stream = OpenIndexFile(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporaryPath,
                finalPath,
                replaceExisting: true,
                "The evidence exact-index manifest is not a regular local file.",
                writeThrough: false);
        }
        finally
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                "The evidence exact-index temporary manifest is not a regular local file.");
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static ExactIndexSlot ParseSlot(
        ReadOnlySpan<byte> bytes,
        EvidenceExactIndexManifest manifest)
    {
        if (bytes.Length != SlotBytes)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        var digest = bytes[..32].ToArray();
        var journalOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]);
        var lineLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]);
        var reserved = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]);
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]);
        var reservedTail = BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]);
        if (sequence == 0)
        {
            if (journalOffset != 0
                || lineLength != 0
                || reserved != 0
                || reservedTail != 0
                || digest.Any(value => value != 0))
            {
                CryptographicOperations.ZeroMemory(digest);
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            return new ExactIndexSlot(digest, 0, 0, 0);
        }

        if (reserved != 0
            || reservedTail != 0
            || sequence < 1
            || sequence > manifest.JournalSequence
            || journalOffset < 0
            || lineLength <= 0
            || lineLength > 4 * 1024 * 1024
            || journalOffset > manifest.JournalCommittedLength - lineLength - 1L)
        {
            CryptographicOperations.ZeroMemory(digest);
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        return new ExactIndexSlot(digest, journalOffset, lineLength, sequence);
    }

    private static void SerializeSlot(Span<byte> bytes, ExactIndexSlot slot)
    {
        if (bytes.Length != SlotBytes || slot.EvidenceIdDigest.Length != 32)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        bytes.Clear();
        slot.EvidenceIdDigest.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], slot.JournalOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[40..], slot.LineLength);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], slot.Sequence);
    }

    private static byte[] DecodeDigest(string value)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length == AuthenticationTagBytes)
            {
                return bytes;
            }

            CryptographicOperations.ZeroMemory(bytes);
        }
        catch (FormatException)
        {
        }

        throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
    }

    private static bool IsHexDigest(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != AuthenticationTagBytes * 2)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            CryptographicOperations.ZeroMemory(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
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

    private static FileStream OpenIndexFile(
        string path,
        FileMode mode,
        FileAccess access) =>
        WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            mode,
            access,
            FileShare.Read,
            writeThrough: false,
            "The evidence exact index is not a regular local file.");

    private static void ReadExactly(FileStream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            offset += read;
        }
    }

    private readonly record struct ExactIndexSlot(
        byte[] EvidenceIdDigest,
        long JournalOffset,
        int LineLength,
        long Sequence);

    internal sealed class Builder : IDisposable
    {
        private readonly string _directory;
        private readonly string _turnStorageKey;
        private readonly EvidenceJournalHead _head;
        private readonly Func<byte[], string> _authenticate;
        private readonly long _capacity;
        private readonly AuthenticationTreeLayout _authenticationLayout;
        private readonly string _tableTemporaryPath;
        private readonly string _authenticationTemporaryPath;
        private readonly string _manifestTemporaryPath;
        private FileStream? _table;
        private FileStream? _authenticationTree;
        private long _count;
        private bool _committed;

        internal Builder(
            string directory,
            string turnStorageKey,
            EvidenceJournalHead head,
            Func<byte[], string> authenticate)
        {
            _directory = Path.GetFullPath(directory);
            _turnStorageKey = turnStorageKey;
            _head = head;
            _authenticate = authenticate;
            _capacity = CalculateCapacity(head.Sequence);
            _authenticationLayout = AuthenticationTreeLayout.Create(_capacity / SlotsPerPage);
            _tableTemporaryPath = Path.Combine(
                _directory,
                ".evidence.exact-index.table.rebuild.tmp");
            _authenticationTemporaryPath = Path.Combine(
                _directory,
                ".evidence.exact-index.auth.rebuild.tmp");
            _manifestTemporaryPath = Path.Combine(
                _directory,
                ".evidence.exact-index.manifest.rebuild.tmp");
            try
            {
                DeleteTemporary(_tableTemporaryPath);
                DeleteTemporary(_authenticationTemporaryPath);
                DeleteTemporary(_manifestTemporaryPath);
                _table = OpenIndexFile(
                    _tableTemporaryPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite);
                _table.SetLength(checked(_capacity * SlotBytes));
                _authenticationTree = OpenIndexFile(
                    _authenticationTemporaryPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite);
                _authenticationTree.SetLength(_authenticationLayout.FileLength);
            }
            catch
            {
                _table?.Dispose();
                _table = null;
                _authenticationTree?.Dispose();
                _authenticationTree = null;
                DeleteTemporary(_tableTemporaryPath);
                DeleteTemporary(_authenticationTemporaryPath);
                DeleteTemporary(_manifestTemporaryPath);
                throw;
            }
        }

        internal void Add(string evidenceId, EvidenceExactIndexLocation location)
        {
            ObjectDisposedException.ThrowIf(_table is null, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
            if (location.Sequence <= 0
                || location.Sequence > _head.Sequence
                || location.JournalOffset < 0
                || location.LineLength <= 0
                || location.LineLength > 4 * 1024 * 1024
                || location.JournalOffset > _head.CommittedLength - location.LineLength - 1L)
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(evidenceId));
            try
            {
                var start = checked((long)(BinaryPrimitives.ReadUInt64LittleEndian(digest) &
                                           (ulong)(_capacity - 1)));
                Span<byte> slotBytes = stackalloc byte[SlotBytes];
                for (long probe = 0; probe < _capacity; probe++)
                {
                    var slotIndex = (start + probe) & (_capacity - 1);
                    _table!.Position = checked(slotIndex * SlotBytes);
                    ReadExactly(_table, slotBytes);
                    if (BinaryPrimitives.ReadInt64LittleEndian(slotBytes[48..]) != 0)
                    {
                        continue;
                    }

                    SerializeSlot(
                        slotBytes,
                        new ExactIndexSlot(
                            digest.ToArray(),
                            location.JournalOffset,
                            location.LineLength,
                            location.Sequence));
                    _table.Position = checked(slotIndex * SlotBytes);
                    _table.Write(slotBytes);
                    _count++;
                    return;
                }

                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        internal async Task CompleteAsync(
            EvidenceJournalVerificationStamp journalStamp,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_table is null || _authenticationTree is null, this);
            ArgumentNullException.ThrowIfNull(journalStamp);
            if (_count != _head.Sequence)
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            await _table!.FlushAsync(cancellationToken).ConfigureAwait(false);
            BuildAuthenticationTree(cancellationToken);
            await _authenticationTree!.FlushAsync(cancellationToken).ConfigureAwait(false);
            var root = ReadAuthenticationTag(
                _authenticationTree,
                _authenticationLayout,
                _authenticationLayout.LevelCount - 1,
                0);
            EvidenceExactIndexManifest manifest;
            try
            {
                manifest = CreateManifest(
                    _turnStorageKey,
                    _head,
                    journalStamp,
                    _capacity,
                    _count,
                    _authenticationLayout.FileLength,
                    root,
                    _authenticate);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(root);
            }

            _table.Dispose();
            _table = null;
            _authenticationTree.Dispose();
            _authenticationTree = null;
            await WriteTemporaryManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

            WindowsOrchestrationFileBoundary.MoveRegularFile(
                _tableTemporaryPath,
                Path.Combine(_directory, TableFileName),
                replaceExisting: true,
                "The evidence exact-index table is not a regular local file.",
                writeThrough: false);
            WindowsOrchestrationFileBoundary.MoveRegularFile(
                _authenticationTemporaryPath,
                Path.Combine(_directory, AuthenticationTreeFileName),
                replaceExisting: true,
                "The evidence exact-index authentication tree is not a regular local file.",
                writeThrough: false);
            WindowsOrchestrationFileBoundary.MoveRegularFile(
                _manifestTemporaryPath,
                Path.Combine(_directory, ManifestFileName),
                replaceExisting: true,
                "The evidence exact-index manifest is not a regular local file.",
                writeThrough: false);
            _committed = true;
        }

        public void Dispose()
        {
            _table?.Dispose();
            _table = null;
            _authenticationTree?.Dispose();
            _authenticationTree = null;
            if (_committed)
            {
                return;
            }

            DeleteTemporary(_tableTemporaryPath);
            DeleteTemporary(_authenticationTemporaryPath);
            DeleteTemporary(_manifestTemporaryPath);
        }

        private void BuildAuthenticationTree(CancellationToken cancellationToken)
        {
            var page = new byte[TablePageBytes];
            for (long pageIndex = 0; pageIndex < _authenticationLayout.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _table!.Position = checked(pageIndex * TablePageBytes);
                ReadExactly(_table, page);
                var tag = HashPage(pageIndex, page);
                try
                {
                    WriteAuthenticationTag(
                        _authenticationTree!,
                        _authenticationLayout,
                        level: 0,
                        pageIndex,
                        tag);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(tag);
                }
            }

            for (var level = 1; level < _authenticationLayout.LevelCount; level++)
            {
                for (long nodeIndex = 0;
                     nodeIndex < _authenticationLayout.LevelCounts[level];
                     nodeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tag = HashNode(
                        _authenticationTree!,
                        _authenticationLayout,
                        level,
                        nodeIndex);
                    try
                    {
                        WriteAuthenticationTag(
                            _authenticationTree!,
                            _authenticationLayout,
                            level,
                            nodeIndex,
                            tag);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(tag);
                    }
                }
            }
        }

        private async Task WriteTemporaryManifestAsync(
            EvidenceExactIndexManifest manifest,
            CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            if (bytes.Length is <= 0 or > MaximumManifestBytes)
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            try
            {
                await using var stream = OpenIndexFile(
                    _manifestTemporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private static void DeleteTemporary(string path)
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                path,
                "The evidence exact-index temporary file is not a regular local file.");
        }
    }

    private static long CalculateCapacity(long count)
    {
        if (count < 0 || count > (long.MaxValue / 2))
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        var required = Math.Max((long)SlotsPerPage, checked(Math.Max(1L, count) * 2L));
        long capacity = SlotsPerPage;
        while (capacity < required)
        {
            if (capacity > (long.MaxValue / 2))
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            capacity *= 2;
        }

        return capacity;
    }

    private static byte[] HashNode(
        FileStream authenticationTree,
        AuthenticationTreeLayout layout,
        int level,
        long nodeIndex)
    {
        var previousCount = layout.LevelCounts[level - 1];
        var firstChild = checked(nodeIndex * AuthenticationFanout);
        var childCount = checked((int)Math.Min(AuthenticationFanout, previousCount - firstChild));
        if (childCount <= 0)
        {
            throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(NodeHashDomain);
        Span<byte> header = stackalloc byte[sizeof(int) + sizeof(long) + sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, level);
        BinaryPrimitives.WriteInt64LittleEndian(header[sizeof(int)..], nodeIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header[(sizeof(int) + sizeof(long))..], childCount);
        hash.AppendData(header);
        for (var child = 0; child < childCount; child++)
        {
            var childTag = ReadAuthenticationTag(
                authenticationTree,
                layout,
                level - 1,
                firstChild + child);
            try
            {
                hash.AppendData(childTag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(childTag);
            }
        }

        return hash.GetHashAndReset();
    }

    private static byte[] ReadAuthenticationTag(
        FileStream stream,
        AuthenticationTreeLayout layout,
        int level,
        long index)
    {
        var tag = new byte[AuthenticationTagBytes];
        stream.Position = layout.GetTagOffset(level, index);
        ReadExactly(stream, tag);
        return tag;
    }

    private static void WriteAuthenticationTag(
        FileStream stream,
        AuthenticationTreeLayout layout,
        int level,
        long index,
        ReadOnlySpan<byte> tag)
    {
        stream.Position = layout.GetTagOffset(level, index);
        stream.Write(tag);
    }

    private sealed class AuthenticationTreeLayout
    {
        private AuthenticationTreeLayout(long[] levelCounts, long[] levelOffsets, long fileLength)
        {
            LevelCounts = levelCounts;
            LevelOffsets = levelOffsets;
            FileLength = fileLength;
        }

        internal long[] LevelCounts { get; }

        internal long[] LevelOffsets { get; }

        internal int LevelCount => LevelCounts.Length;

        internal long PageCount => LevelCounts[0];

        internal long FileLength { get; }

        internal static AuthenticationTreeLayout Create(long pageCount)
        {
            if (pageCount <= 0)
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            var counts = new List<long> { pageCount };
            while (counts[^1] > 1)
            {
                counts.Add(checked((counts[^1] + AuthenticationFanout - 1) / AuthenticationFanout));
            }

            var offsets = new long[counts.Count];
            long length = 0;
            for (var level = 0; level < counts.Count; level++)
            {
                offsets[level] = length;
                length = checked(length + checked(counts[level] * AuthenticationTagBytes));
            }

            return new AuthenticationTreeLayout(counts.ToArray(), offsets, length);
        }

        internal long GetTagOffset(int level, long index)
        {
            if (level < 0
                || level >= LevelCounts.Length
                || index < 0
                || index >= LevelCounts[level])
            {
                throw new EvidenceExactIndexInvalidException(InvalidIndexMessage);
            }

            return checked(LevelOffsets[level] + checked(index * AuthenticationTagBytes));
        }
    }
}
