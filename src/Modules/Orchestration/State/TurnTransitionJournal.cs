using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Orchestration.State;

internal enum TurnJournalCommitBoundary
{
    BodyFlushed,
    CommitMarkerFlushed,
    HeadCommitted,
    FactIndexGrowthRebuildStarted
}

public sealed record TurnJournalEntry(
    long Cursor,
    DateTimeOffset RecordedAtUtc,
    TurnTransition Transition,
    string ResultingStateDigest,
    string Checksum);

public sealed record TurnJournalReplayResult(
    TurnState? State,
    IReadOnlyList<TurnJournalEntry> Entries,
    bool RecoveredUncommittedTail);

public enum TurnUserResolutionOutcome
{
    ActionConfirmedApplied,
    ActionConfirmedAbsent,
    FinalPublicationConfirmedDisplayed,
    FinalPublicationConfirmedNotDisplayed
}

public enum TurnUserResolutionKind
{
    Action,
    FinalPublication
}

/// <summary>
/// Authenticated, non-text interpretation of an explicit structured recovery command.
/// The revision is the durable journal revision that committed the resolution.
/// </summary>
public sealed record TurnUserResolutionProjection(
    long StateRevision,
    string SourceCommandId,
    string PromptPublicationId,
    string PromptTextDigest,
    TurnUserResolutionKind Kind,
    InterimPublicationReason Reason,
    string SubjectId,
    long SubjectPreparedRevision,
    TurnUserResolutionOutcome Outcome);

/// <summary>
/// Bounded production projection used to resume a turn without materializing its full audit log.
/// Lists are retained in journal order and contain only the newest fixed-capacity window.
/// </summary>
public sealed record TurnResumeProjection(
    TurnState? State,
    IReadOnlyList<CommittedEvidenceReference> EvidenceReferences,
    IReadOnlyList<SteeringAppendedTransition> SteeringTransitions,
    IReadOnlyList<ProgressAttemptRecordedTransition> ProgressAttempts,
    IReadOnlyList<TurnUserResolutionProjection> UserResolutions,
    bool RecoveredUncommittedTail);

internal sealed record TurnJournalDiagnostics(
    long RecordsReadFromDisk,
    long RecoveredTailCount,
    int CachedEntryCount,
    int CachedCorrelationFactCount,
    int CachedActionFactCount,
    int CachedEvidenceFactCount,
    long ExactIndexProbeCount,
    long ExactIndexKeyCount,
    long ExactIndexCapacity);

internal sealed record TurnJournalVerificationStamp(
    string HeadMac,
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    long ChangeTimeTicks,
    long Length,
    long Sequence);

internal sealed record TurnFactIndexVerificationStamp(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    long ChangeTimeTicks,
    long Length);

internal sealed record UnsignedTurnFactIndexManifest(
    string TurnStorageKey,
    string Generation,
    long JournalSequence,
    string JournalChecksum,
    long Capacity,
    long KeyCount,
    long TableLength,
    long KeyFileLength,
    long AuthenticationTreeLength,
    string AuthenticationRoot);

internal sealed record TurnFactIndexManifest(
    string TurnStorageKey,
    string Generation,
    long JournalSequence,
    string JournalChecksum,
    long Capacity,
    long KeyCount,
    long TableLength,
    long KeyFileLength,
    long AuthenticationTreeLength,
    string AuthenticationRoot,
    string Mac);

internal sealed record UnsignedTurnJournalEntry(
    long Sequence,
    long TimestampUtcTicks,
    string PreviousChecksum,
    TurnIdentity Identity,
    TurnTransition Transition,
    string ResultingStateDigest);

internal sealed record TurnJournalEntryForMac(
    UnsignedTurnJournalEntry Entry,
    string Checksum);

internal sealed record StoredTurnJournalEntry(
    long Sequence,
    long TimestampUtcTicks,
    string PreviousChecksum,
    TurnIdentity Identity,
    TurnTransition Transition,
    string ResultingStateDigest,
    string Checksum,
    string RecordMac);

internal sealed record UnsignedTurnJournalHead(
    string TurnStorageKey,
    long CommittedLength,
    long Sequence,
    string Checksum,
    string StateDigest);

internal sealed record TurnJournalHead(
    string TurnStorageKey,
    long CommittedLength,
    long Sequence,
    string Checksum,
    string StateDigest,
    string Mac);

internal enum TurnJournalAppendStatus
{
    Committed,
    AlreadyRecorded,
    RevisionConflict
}

internal sealed record TurnJournalAppendResult(
    TurnJournalAppendStatus Status,
    TurnState? State,
    TurnJournalEntry? Entry);

internal sealed class TurnTransitionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumRecordBytes = 4 * 1024 * 1024;
    private const int MaximumHeadBytes = 64 * 1024;
    internal const int MaximumCachedEntries = 256;
    internal const int MaximumCachedFactsPerKind = 256;
    internal const int MaximumResumeEvidenceReferences = 64;
    internal const int MaximumResumeSteeringTransitions = 512;
    internal const int MaximumResumeProgressAttempts = 1024;
    internal const int MaximumResumeUserResolutions = 64;
    private const string JournalFileName = "turn.journal.jsonl";
    private const string HeadFileName = "turn.head.json";
    private const string FactIndexTableFileName = "turn.fact-index.table.bin";
    private const string FactIndexKeyFileName = "turn.fact-index.keys.bin";
    private const string FactIndexPageMacFileName = "turn.fact-index.auth-tree.bin";
    private const string FactIndexManifestFileName = "turn.fact-index.manifest.json";
    private readonly string _directory;
    private readonly string _turnStorageKey;
    private readonly Func<CancellationToken, Task<EvidenceKeySession>> _keySessionAccessor;
    private readonly Action<TurnJournalCommitBoundary>? _faultInjector;
    private JournalCache? _cache;
    private long _recordsReadFromDisk;
    private long _recoveredTailCount;

    public TurnTransitionJournal(
        string directory,
        string turnStorageKey,
        Func<CancellationToken, Task<EvidenceKeySession>> keySessionAccessor,
        Action<TurnJournalCommitBoundary>? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnStorageKey);
        _directory = Path.GetFullPath(directory);
        _turnStorageKey = turnStorageKey;
        _keySessionAccessor = keySessionAccessor
            ?? throw new ArgumentNullException(nameof(keySessionAccessor));
        _faultInjector = faultInjector;
    }

    public TurnJournalDiagnostics Diagnostics
    {
        get
        {
            var cache = Volatile.Read(ref _cache);
            return new TurnJournalDiagnostics(
                Interlocked.Read(ref _recordsReadFromDisk),
                Interlocked.Read(ref _recoveredTailCount),
                cache?.Entries.Count ?? 0,
                cache?.Facts.CorrelationCount ?? 0,
                cache?.Facts.ActionCount ?? 0,
                cache?.Facts.EvidenceCount ?? 0,
                cache?.Facts.ExactIndexProbeCount ?? 0,
                cache?.Facts.ExactIndexKeyCount ?? 0,
                cache?.Facts.ExactIndexCapacity ?? 0);
        }
    }

    public async Task<TurnJournalReplayResult> ReplayAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var keys = await _keySessionAccessor(cancellationToken).ConfigureAwait(false);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadAsync(stream, keys, cancellationToken).ConfigureAwait(false);
        var recovered = RecoverUncommittedSuffix(stream, head);
        var scan = await ScanCommittedAsync(
            stream,
            head,
            identity,
            keys,
            collectAuditEntries: true,
            cancellationToken).ConfigureAwait(false);
        SetCache(scan.Cache);
        return new TurnJournalReplayResult(
            CloneState(scan.Cache.State),
            Array.AsReadOnly(scan.AuditEntries!.Select(CloneEntry).ToArray()),
            recovered);
    }

    public async Task<TurnState?> ReadStateAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var keys = await _keySessionAccessor(cancellationToken).ConfigureAwait(false);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadAsync(stream, keys, cancellationToken).ConfigureAwait(false);
        _ = RecoverUncommittedSuffix(stream, head);
        var cache = await LoadCacheAsync(
            stream,
            head,
            identity,
            keys,
            cancellationToken).ConfigureAwait(false);
        return CloneState(cache.State);
    }

    public async Task<TurnResumeProjection> ReadResumeProjectionAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var keys = await _keySessionAccessor(cancellationToken).ConfigureAwait(false);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadAsync(stream, keys, cancellationToken).ConfigureAwait(false);
        var recovered = RecoverUncommittedSuffix(stream, head);
        var cache = await LoadCacheAsync(
            stream,
            head,
            identity,
            keys,
            cancellationToken).ConfigureAwait(false);
        return cache.Resume.CreateProjection(cache.State, recovered);
    }

    internal async Task<IReadOnlyDictionary<string, CommittedEvidenceReference>>
        ResolveEvidenceReferencesAsync(
            TurnIdentity identity,
            IReadOnlyCollection<string> evidenceIds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        if (evidenceIds.Count == 0)
        {
            return new Dictionary<string, CommittedEvidenceReference>(StringComparer.Ordinal);
        }

        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidenceId in evidenceIds)
        {
            TurnStateIntegrity.RequireBoundedValue(evidenceId, 256, nameof(evidenceIds));
            requested.Add(evidenceId);
        }

        var keys = await _keySessionAccessor(cancellationToken).ConfigureAwait(false);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadAsync(stream, keys, cancellationToken).ConfigureAwait(false);
        _ = RecoverUncommittedSuffix(stream, head);
        var cache = await LoadCacheAsync(
            stream,
            head,
            identity,
            keys,
            cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ResolveEvidenceReferencesWithCacheAsync(
                    stream,
                    head,
                    identity,
                    keys,
                    cache,
                    requested,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TurnFactIndexInvalidException) when (attempt == 0)
            {
                var scan = await ScanCommittedAsync(
                    stream,
                    head,
                    identity,
                    keys,
                    collectAuditEntries: false,
                    cancellationToken).ConfigureAwait(false);
                cache = scan.Cache;
                SetCache(cache);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, CommittedEvidenceReference>>
        ResolveEvidenceReferencesWithCacheAsync(
            FileStream stream,
            TurnJournalHead head,
            TurnIdentity identity,
            EvidenceKeySession keys,
            JournalCache cache,
            IReadOnlyCollection<string> requested,
            CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, CommittedEvidenceReference>(
            requested.Count,
            StringComparer.Ordinal);
        using var exactIndexSession = cache.Facts.OpenExactIndexSession();
        foreach (var evidenceId in requested)
        {
            if (!cache.Facts.TryLocateEvidenceReference(evidenceId, out var location))
            {
                continue;
            }

            if (location.Ambiguous)
            {
                throw new InvalidDataException(
                    "The durable turn journal contains a duplicate evidence ID.");
            }

            if (cache.Facts.TryGetEvidenceReference(evidenceId, out var hot))
            {
                resolved.Add(evidenceId, hot! with { });
                continue;
            }

            var stored = await ReadIndexedEntryAsync(
                stream,
                location,
                head.CommittedLength,
                head.Sequence,
                identity,
                keys,
                cancellationToken).ConfigureAwait(false);
            if (stored.Transition is not EvidenceReferencedTransition evidence
                || !string.Equals(
                    evidence.Evidence.EvidenceId,
                    evidenceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The durable turn fact index returned a mismatched evidence reference.");
            }

            var reference = evidence.Evidence with { };
            resolved.Add(evidenceId, reference);
            cache.Facts.WarmEvidenceReference(evidenceId, reference);
        }

        return resolved;
    }

    public async Task<TurnJournalAppendResult> TryAppendAsync(
        TurnIdentity identity,
        long expectedRevision,
        TurnTransition transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(transition);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        transition.ValidateShape();
        transition = CloneTransition(transition);
        var keys = await _keySessionAccessor(cancellationToken).ConfigureAwait(false);
        EnsureStorageDirectory();
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenJournal();
        var head = await LoadHeadAsync(stream, keys, cancellationToken).ConfigureAwait(false);
        _ = RecoverUncommittedSuffix(stream, head);
        var cache = await LoadCacheAsync(
            stream,
            head,
            identity,
            keys,
            cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryAppendWithCacheAsync(
                    stream,
                    head,
                    identity,
                    expectedRevision,
                    transition,
                    keys,
                    cache,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TurnFactIndexInvalidException) when (attempt == 0)
            {
                head = await LoadHeadAsync(stream, keys, cancellationToken)
                    .ConfigureAwait(false);
                _ = RecoverUncommittedSuffix(stream, head);
                var scan = await ScanCommittedAsync(
                    stream,
                    head,
                    identity,
                    keys,
                    collectAuditEntries: false,
                    cancellationToken).ConfigureAwait(false);
                cache = scan.Cache;
                SetCache(cache);
            }
        }
    }

    private async Task<TurnJournalAppendResult> TryAppendWithCacheAsync(
        FileStream stream,
        TurnJournalHead head,
        TurnIdentity identity,
        long expectedRevision,
        TurnTransition transition,
        EvidenceKeySession keys,
        JournalCache cache,
        CancellationToken cancellationToken)
    {
        using var exactIndexSession = cache.Facts.OpenExactIndexSession();

        var transitionBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes<TurnTransition>(transition);
        var transitionDigest = TurnStateIntegrity.Digest(transitionBytes);
        var actionKey = transition is ActionPreparedTransition preparedAction
            ? preparedAction.Intent.IdempotencyKey
            : null;
        EvidenceFactKey? evidenceKey = transition switch
        {
            EvidenceReferencedTransition evidence => EvidenceFactKey.From(evidence.Evidence),
            ActionCommittedTransition committed => new EvidenceFactKey(
                committed.EvidenceId,
                committed.EvidenceCursor),
            ActionReconciledTransition
                { Disposition: ActionReconciliationDisposition.Applied } reconciled =>
                new EvidenceFactKey(reconciled.EvidenceId!, reconciled.EvidenceCursor!.Value),
            _ => null
        };
        var historical = await ResolveHistoricalFactsAsync(
            stream,
            head.CommittedLength,
            head.Sequence,
            head.Checksum,
            identity,
            keys,
            cache.Facts,
            transition.CorrelationKey,
            actionKey,
            evidenceKey,
            cancellationToken).ConfigureAwait(false);
        if (historical.Correlation is { } recorded)
        {
            if (!string.Equals(
                    recorded.TransitionDigest,
                    transitionDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A durable correlation key cannot be rebound to a different transition.");
            }

            var recordedEntry = cache.Entries
                .FirstOrDefault(entry => entry.Cursor == recorded.Cursor)
                ?? recorded.Entry
                ?? await ReadCorrelationEntryAsync(
                    stream,
                    head,
                    identity,
                    keys,
                    cache.Facts,
                    transition.CorrelationKey,
                    cancellationToken).ConfigureAwait(false);
            return new TurnJournalAppendResult(
                TurnJournalAppendStatus.AlreadyRecorded,
                CloneState(cache.State),
                CloneEntry(recordedEntry));
        }

        if (transition is EvidenceReferencedTransition && historical.Evidence is not null)
        {
            throw new InvalidDataException(
                "A durable evidence identity and cursor cannot be recorded more than once.");
        }

        if (IsAlreadyAuthoritative(cache, transition, historical.Action))
        {
            return new TurnJournalAppendResult(
                TurnJournalAppendStatus.AlreadyRecorded,
                CloneState(cache.State),
                Entry: null);
        }

        var currentRevision = cache.State?.Revision ?? 0;
        if (currentRevision != expectedRevision)
        {
            return new TurnJournalAppendResult(
                TurnJournalAppendStatus.RevisionConflict,
                CloneState(cache.State),
                Entry: null);
        }

        var sequence = currentRevision + 1;
        var timestampTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var recordedAtUtc = new DateTimeOffset(timestampTicks, TimeSpan.Zero);
        var replayIndex = CreateReplayIndex(transition, historical);
        var nextState = TurnStateReducer.Reduce(
            identity,
            cache.State,
            transition,
            sequence,
            recordedAtUtc,
            replayIndex);
        var stateDigest = TurnStateIntegrity.Digest(
            CanonicalEvidenceJson.SerializeToUtf8Bytes(nextState));
        var unsigned = new UnsignedTurnJournalEntry(
            sequence,
            timestampTicks,
            head.Checksum,
            identity,
            transition,
            stateDigest);
        var unsignedBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned);
        var checksum = TurnStateIntegrity.Digest(unsignedBytes);
        var macMaterial = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            new TurnJournalEntryForMac(unsigned, checksum));
        var recordMac = keys.HmacHex(EvidenceKeyPurpose.RecordMac, macMaterial);
        var stored = new StoredTurnJournalEntry(
            unsigned.Sequence,
            unsigned.TimestampUtcTicks,
            unsigned.PreviousChecksum,
            unsigned.Identity,
            unsigned.Transition,
            unsigned.ResultingStateDigest,
            checksum,
            recordMac);
        var recordBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(stored);
        if (recordBytes.Length == 0 || recordBytes.Length > MaximumRecordBytes)
        {
            throw new InvalidOperationException(
                $"The durable turn transition is {recordBytes.Length} bytes; the maximum is {MaximumRecordBytes} bytes. The request was not truncated.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = head.CommittedLength;
        await stream.WriteAsync(recordBytes, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        _faultInjector?.Invoke(TurnJournalCommitBoundary.BodyFlushed);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        _faultInjector?.Invoke(TurnJournalCommitBoundary.CommitMarkerFlushed);

        var committedLength = head.CommittedLength + recordBytes.Length + 1L;
        var unsignedHead = new UnsignedTurnJournalHead(
            _turnStorageKey,
            committedLength,
            sequence,
            checksum,
            stateDigest);
        var committedHead = new TurnJournalHead(
            unsignedHead.TurnStorageKey,
            unsignedHead.CommittedLength,
            unsignedHead.Sequence,
            unsignedHead.Checksum,
            unsignedHead.StateDigest,
            SignHead(unsignedHead, keys));
        await WriteHeadAtomicallyAsync(committedHead).ConfigureAwait(false);
        _faultInjector?.Invoke(TurnJournalCommitBoundary.HeadCommitted);

        var projected = Project(stored);
        cache.Facts.Record(
            projected,
            transitionDigest,
            head.CommittedLength,
            recordBytes.Length);
        cache.Facts.CommitExactIndexHead(committedHead.Sequence, committedHead.Checksum);
        cache.Entries.Add(projected);
        cache.Resume.Record(projected);
        SetCache(cache with
        {
            Head = committedHead,
            VerificationStamp = TryCaptureVerificationStamp(stream, committedHead),
            State = CloneState(nextState)
        });
        return new TurnJournalAppendResult(
            TurnJournalAppendStatus.Committed,
            CloneState(nextState),
            CloneEntry(projected));
    }

    private async Task<JournalCache> LoadCacheAsync(
        FileStream stream,
        TurnJournalHead head,
        TurnIdentity identity,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var currentStamp = TryCaptureVerificationStamp(stream, head);
        if (_cache is { } exact
            && currentStamp is not null
            && exact.VerificationStamp == currentStamp
            && HeadsMatch(exact.Head, head)
            && exact.Facts.IsExactIndexCurrent())
        {
            return exact;
        }

        var scan = await ScanCommittedAsync(
            stream,
            head,
            identity,
            keys,
            collectAuditEntries: false,
            cancellationToken).ConfigureAwait(false);
        SetCache(scan.Cache);
        return scan.Cache;
    }

    private void SetCache(JournalCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        Volatile.Write(ref _cache, cache);
    }

    private async Task<JournalScanResult> ScanCommittedAsync(
        FileStream stream,
        TurnJournalHead head,
        TurnIdentity identity,
        EvidenceKeySession keys,
        bool collectAuditEntries,
        CancellationToken cancellationToken)
    {
        var startingStamp = TryCaptureVerificationStamp(stream, head);
        var facts = new BoundedReplayFacts(
            ExactReplayFactIndex.Rebuild(
                _directory,
                _turnStorageKey,
                head.Sequence,
                head.Checksum,
                keys,
                FactIndexTableFileName,
                FactIndexKeyFileName,
                FactIndexPageMacFileName,
                FactIndexManifestFileName,
                _faultInjector));
        using var exactIndexSession = facts.OpenExactIndexSession();
        var tail = new BoundedQueue<TurnJournalEntry>(MaximumCachedEntries);
        var resume = new BoundedResumeProjection();
        List<TurnJournalEntry>? auditEntries = collectAuditEntries ? [] : null;
        TurnState? state = null;
        var nextSequence = 1L;
        var previousChecksum = TurnStateIntegrity.EmptyDigest;
        long consumed = 0;
        await foreach (var line in ReadLinesAsync(
                           stream,
                           start: 0,
                           end: head.CommittedLength,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _recordsReadFromDisk);
            var stored = DeserializeCanonicalEntry(line);
            if (stored.Sequence != nextSequence
                || stored.Identity != identity
                || !string.Equals(stored.PreviousChecksum, previousChecksum, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The durable turn journal sequence, identity, or checksum chain is invalid.");
            }

            stored.Transition.ValidateShape();
            ValidateStoredEntry(stored, keys);
            var transitionBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes<TurnTransition>(stored.Transition);
            var transitionDigest = TurnStateIntegrity.Digest(transitionBytes);
            var actionKey = stored.Transition is ActionPreparedTransition prepared
                ? prepared.Intent.IdempotencyKey
                : null;
            EvidenceFactKey? evidenceKey = stored.Transition switch
            {
                EvidenceReferencedTransition evidence => EvidenceFactKey.From(evidence.Evidence),
                ActionCommittedTransition committed => new EvidenceFactKey(
                    committed.EvidenceId,
                    committed.EvidenceCursor),
                ActionReconciledTransition
                    { Disposition: ActionReconciliationDisposition.Applied } reconciled =>
                    new EvidenceFactKey(reconciled.EvidenceId!, reconciled.EvidenceCursor!.Value),
                _ => null
            };
            var historical = await ResolveHistoricalFactsAsync(
                stream,
                consumed,
                nextSequence - 1,
                previousChecksum,
                identity,
                keys,
                facts,
                stored.Transition.CorrelationKey,
                actionKey,
                evidenceKey,
                cancellationToken).ConfigureAwait(false);
            if (historical.Correlation is not null)
            {
                throw new InvalidDataException(
                    "The durable turn journal contains a duplicate correlation key.");
            }
            if (stored.Transition is EvidenceReferencedTransition && historical.Evidence is not null)
            {
                throw new InvalidDataException(
                    "The durable turn journal contains a duplicate evidence identity and cursor.");
            }

            var recordedAtUtc = CreateTimestamp(stored.TimestampUtcTicks);
            var replayIndex = CreateReplayIndex(stored.Transition, historical);
            var nextState = TurnStateReducer.Reduce(
                identity,
                state,
                stored.Transition,
                stored.Sequence,
                recordedAtUtc,
                replayIndex);
            var stateDigest = TurnStateIntegrity.Digest(
                CanonicalEvidenceJson.SerializeToUtf8Bytes(nextState));
            if (!string.Equals(stateDigest, stored.ResultingStateDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The durable turn journal resulting-state digest is invalid.");
            }

            var projected = Project(stored);
            facts.Record(
                projected,
                transitionDigest,
                consumed,
                line.Length);
            tail.Add(CloneEntry(projected));
            resume.Record(projected);
            auditEntries?.Add(projected);
            state = nextState;
            nextSequence++;
            previousChecksum = stored.Checksum;
            consumed = checked(consumed + line.Length + 1L);
        }

        if (consumed != head.CommittedLength
            || nextSequence - 1 != head.Sequence
            || (state?.Revision ?? 0) != head.Sequence
            || !string.Equals(previousChecksum, head.Checksum, StringComparison.Ordinal)
            || !string.Equals(
                state is null
                    ? TurnStateIntegrity.EmptyDigest
                    : TurnStateIntegrity.Digest(
                        CanonicalEvidenceJson.SerializeToUtf8Bytes(state)),
                head.StateDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The durable turn journal does not match its authenticated committed head.");
        }

        var endingHead = await LoadHeadAsync(stream, keys, cancellationToken).ConfigureAwait(false);
        if (!HeadsMatch(head, endingHead))
        {
            throw new InvalidDataException(
                "The durable turn journal head changed while its committed records were read.");
        }

        var endingStamp = TryCaptureVerificationStamp(stream, endingHead);
        if (startingStamp is not null
            && endingStamp is not null
            && startingStamp != endingStamp)
        {
            throw new InvalidDataException(
                "The durable turn journal changed while its committed records were read.");
        }

        facts.FinalizeExactIndexBuild(
            endingHead.Sequence,
            endingHead.Checksum,
            cancellationToken);

        var cache = new JournalCache(
            endingHead,
            endingStamp,
            CloneState(state),
            tail,
            facts,
            resume);
        return new JournalScanResult(cache, auditEntries?.AsReadOnly());
    }

    private async Task<HistoricalFacts> ResolveHistoricalFactsAsync(
        FileStream stream,
        long prefixLength,
        long prefixSequence,
        string prefixChecksum,
        TurnIdentity identity,
        EvidenceKeySession keys,
        BoundedReplayFacts facts,
        string? correlationKey,
        string? actionKey,
        EvidenceFactKey? evidenceKey,
        CancellationToken cancellationToken)
    {
        TurnStateIntegrity.RequireDigest(prefixChecksum, nameof(prefixChecksum));
        RecordedCorrelationFact? correlation = null;
        RecordedActionFact? action = null;
        RecordedEvidenceFact? evidence = null;

        if (correlationKey is not null)
        {
            if (!facts.TryGetCorrelation(correlationKey, out correlation)
                && facts.TryLocateCorrelation(correlationKey, out var location))
            {
                var stored = await ReadIndexedEntryAsync(
                    stream,
                    location,
                    prefixLength,
                    prefixSequence,
                    identity,
                    keys,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        stored.Transition.CorrelationKey,
                        correlationKey,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The durable turn fact index returned a mismatched correlation.");
                }

                var entry = Project(stored);
                correlation = new RecordedCorrelationFact(
                    entry.Cursor,
                    TransitionDigest(entry.Transition),
                    entry);
                facts.WarmCorrelation(correlationKey, correlation);
            }
        }

        if (actionKey is not null)
        {
            if (!facts.TryGetAction(actionKey, out action)
                && facts.TryLocateAction(actionKey, out var location))
            {
                var stored = await ReadIndexedEntryAsync(
                    stream,
                    location,
                    prefixLength,
                    prefixSequence,
                    identity,
                    keys,
                    cancellationToken).ConfigureAwait(false);
                if (!TryCreateActionFact(Project(stored), out action)
                    || !string.Equals(
                        action!.ActionIdempotencyKey,
                        actionKey,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The durable turn fact index returned a mismatched action.");
                }

                facts.WarmAction(actionKey, action);
            }
        }

        if (evidenceKey is not null)
        {
            if (!facts.TryGetEvidence(evidenceKey.Value, out evidence)
                && facts.TryLocateEvidence(evidenceKey.Value, out var location))
            {
                var stored = await ReadIndexedEntryAsync(
                    stream,
                    location,
                    prefixLength,
                    prefixSequence,
                    identity,
                    keys,
                    cancellationToken).ConfigureAwait(false);
                if (stored.Transition is not EvidenceReferencedTransition referenced
                    || EvidenceFactKey.From(referenced.Evidence) != evidenceKey.Value)
                {
                    throw new InvalidDataException(
                        "The durable turn fact index returned mismatched evidence.");
                }

                evidence = new RecordedEvidenceFact(
                    referenced with { Evidence = referenced.Evidence with { } },
                    stored.Sequence,
                    TransitionDigest(stored.Transition));
                facts.WarmEvidence(evidenceKey.Value, evidence);
            }
        }

        return new HistoricalFacts(correlation, action, evidence);
    }

    private async Task<StoredTurnJournalEntry> ReadIndexedEntryAsync(
        FileStream stream,
        ExactFactLocation location,
        long prefixLength,
        long prefixSequence,
        TurnIdentity identity,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        if (location.Ambiguous
            || location.JournalOffset < 0
            || location.JournalRecordLength <= 0
            || location.JournalRecordLength > MaximumRecordBytes)
        {
            throw new InvalidDataException("The durable turn fact index location is invalid.");
        }

        long committedEnd;
        try
        {
            committedEnd = checked(
                location.JournalOffset + location.JournalRecordLength + 1L);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "The durable turn fact index location overflowed its journal range.",
                ex);
        }

        if (committedEnd > prefixLength || prefixSequence <= 0)
        {
            throw new InvalidDataException(
                "The durable turn fact index points outside the authenticated journal prefix.");
        }

        var line = new byte[location.JournalRecordLength];
        await ReadExactlyAtAsync(
            stream.SafeFileHandle,
            line,
            location.JournalOffset,
            cancellationToken).ConfigureAwait(false);
        var marker = new byte[1];
        await ReadExactlyAtAsync(
            stream.SafeFileHandle,
            marker,
            location.JournalOffset + location.JournalRecordLength,
            cancellationToken).ConfigureAwait(false);
        if (marker[0] != (byte)'\n')
        {
            throw new InvalidDataException(
                "The durable turn fact index record has no committed journal marker.");
        }

        Interlocked.Increment(ref _recordsReadFromDisk);
        var stored = DeserializeCanonicalEntry(line);
        if (stored.Identity != identity
            || stored.Sequence <= 0
            || stored.Sequence > prefixSequence)
        {
            throw new InvalidDataException(
                "The durable turn fact index returned a record outside the authenticated prefix.");
        }

        stored.Transition.ValidateShape();
        ValidateStoredEntry(stored, keys);
        return stored;
    }

    private static async Task ReadExactlyAtAsync(
        SafeFileHandle handle,
        Memory<byte> destination,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        var consumed = 0;
        while (consumed < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                destination[consumed..],
                fileOffset + consumed,
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "The durable turn journal ended during an exact indexed read.");
            }

            consumed += read;
        }
    }

    private async Task<TurnJournalEntry> ReadCorrelationEntryAsync(
        FileStream stream,
        TurnJournalHead head,
        TurnIdentity identity,
        EvidenceKeySession keys,
        BoundedReplayFacts facts,
        string correlationKey,
        CancellationToken cancellationToken)
    {
        if (!facts.TryLocateCorrelation(correlationKey, out var location))
        {
            throw new InvalidDataException(
                "The durable correlation fact cache did not match its exact disk index.");
        }

        var stored = await ReadIndexedEntryAsync(
            stream,
            location,
            head.CommittedLength,
            head.Sequence,
            identity,
            keys,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                stored.Transition.CorrelationKey,
                correlationKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The durable turn fact index returned a mismatched correlation entry.");
        }

        return Project(stored);
    }

    private static TurnReplayIndex CreateReplayIndex(
        TurnTransition transition,
        HistoricalFacts historical)
    {
        var index = new TurnReplayIndex();
        if (transition is ActionPreparedTransition && historical.Action is { } action)
        {
            index.Record(action.Transition, action.Cursor, action.TransitionDigest);
        }

        if ((transition is ActionCommittedTransition
                or ActionReconciledTransition
                    { Disposition: ActionReconciliationDisposition.Applied })
            && historical.Evidence is { } evidence)
        {
            index.Record(evidence.Transition, evidence.Cursor, evidence.TransitionDigest);
        }

        return index;
    }

    private static string TransitionDigest(TurnTransition transition) =>
        TurnStateIntegrity.Digest(
            CanonicalEvidenceJson.SerializeToUtf8Bytes<TurnTransition>(transition));

    private static bool TryCreateActionFact(
        TurnJournalEntry entry,
        out RecordedActionFact? fact)
    {
        var (idempotencyKey, state) = entry.Transition switch
        {
            ActionPreparedTransition prepared =>
                (prepared.Intent.IdempotencyKey, (RecordedActionState?)RecordedActionState.Prepared),
            ActionMarkedInDoubtTransition inDoubt =>
                (inDoubt.ActionIdempotencyKey, (RecordedActionState?)RecordedActionState.InDoubt),
            ActionCommittedTransition committed =>
                (committed.ActionIdempotencyKey, (RecordedActionState?)RecordedActionState.Committed),
            ActionReconciledTransition reconciled =>
                (reconciled.ActionIdempotencyKey, reconciled.Disposition switch
                {
                    ActionReconciliationDisposition.Applied =>
                        (RecordedActionState?)RecordedActionState.Applied,
                    ActionReconciliationDisposition.Absent =>
                        RecordedActionState.Absent,
                    ActionReconciliationDisposition.Unknown =>
                        RecordedActionState.Unknown,
                    _ => null
                }),
            UnknownActionResolvedByUserTransition resolved =>
                (resolved.SubjectId, resolved.Resolution switch
                {
                    ActionUserResolution.ConfirmApplied =>
                        (RecordedActionState?)RecordedActionState.Applied,
                    ActionUserResolution.ConfirmAbsent =>
                        RecordedActionState.Absent,
                    _ => null
                }),
            _ => (null, null)
        };
        if (idempotencyKey is null || state is null)
        {
            fact = null;
            return false;
        }

        fact = new RecordedActionFact(
            idempotencyKey,
            state.Value,
            CloneTransition(entry.Transition),
            entry.Cursor,
            TransitionDigest(entry.Transition));
        return true;
    }

    private static void ValidateStoredEntry(
        StoredTurnJournalEntry stored,
        EvidenceKeySession keys)
    {
        TurnStateIntegrity.RequireDigest(stored.PreviousChecksum, nameof(stored.PreviousChecksum));
        TurnStateIntegrity.RequireDigest(stored.ResultingStateDigest, nameof(stored.ResultingStateDigest));
        TurnStateIntegrity.RequireDigest(stored.Checksum, nameof(stored.Checksum));
        TurnStateIntegrity.RequireDigest(stored.RecordMac, nameof(stored.RecordMac));
        var unsigned = new UnsignedTurnJournalEntry(
            stored.Sequence,
            stored.TimestampUtcTicks,
            stored.PreviousChecksum,
            stored.Identity,
            stored.Transition,
            stored.ResultingStateDigest);
        var checksum = TurnStateIntegrity.Digest(
            CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned));
        if (!FixedTimeEquals(checksum, stored.Checksum))
        {
            throw new InvalidDataException("A durable turn journal record failed checksum validation.");
        }

        var macMaterial = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            new TurnJournalEntryForMac(unsigned, stored.Checksum));
        if (!keys.VerifyHmac(EvidenceKeyPurpose.RecordMac, macMaterial, stored.RecordMac))
        {
            throw new InvalidDataException(
                "A durable turn journal record failed keyed authentication.");
        }
    }

    private async Task<TurnJournalHead> LoadHeadAsync(
        FileStream stream,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            _directory,
            "The durable turn storage directory must be a regular local directory.");
        var path = Path.Combine(_directory, HeadFileName);
        var bytes = await WindowsBoundedFileReader.TryReadExactlyAsync(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(path),
            minimumLength: 1,
            maximumLength: MaximumHeadBytes,
            invalidTargetMessage: "The durable turn journal head is not a regular local file.",
            invalidLengthMessage: "The durable turn journal head has an invalid size.",
            changedWhileReadingMessage: "The durable turn journal head changed while it was read.",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            if (stream.Length != 0)
            {
                throw new InvalidDataException(
                    "The durable turn journal has data but its authenticated head is missing.");
            }

            return InitialHead();
        }

        TurnJournalHead head;
        try
        {
            head = JsonSerializer.Deserialize<TurnJournalHead>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The durable turn journal head is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The durable turn journal head is malformed.", ex);
        }

        if (!bytes.AsSpan().SequenceEqual(CanonicalEvidenceJson.SerializeToUtf8Bytes(head)))
        {
            throw new InvalidDataException("The durable turn journal head is not canonical.");
        }

        ValidateHead(head, keys);
        return head;
    }

    private void ValidateHead(TurnJournalHead head, EvidenceKeySession keys)
    {
        if (!string.Equals(head.TurnStorageKey, _turnStorageKey, StringComparison.Ordinal)
            || head.CommittedLength < 0
            || head.Sequence < 0)
        {
            throw new InvalidDataException("The durable turn journal head is invalid.");
        }

        TurnStateIntegrity.RequireDigest(head.Checksum, nameof(head.Checksum));
        TurnStateIntegrity.RequireDigest(head.StateDigest, nameof(head.StateDigest));
        TurnStateIntegrity.RequireDigest(head.Mac, nameof(head.Mac));
        if (head.Sequence == 0
            && (head.CommittedLength != 0
                || !string.Equals(head.Checksum, TurnStateIntegrity.EmptyDigest, StringComparison.Ordinal)
                || !string.Equals(head.StateDigest, TurnStateIntegrity.EmptyDigest, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The empty durable turn journal head is invalid.");
        }

        var unsigned = new UnsignedTurnJournalHead(
            head.TurnStorageKey,
            head.CommittedLength,
            head.Sequence,
            head.Checksum,
            head.StateDigest);
        if (!keys.VerifyHmac(
                EvidenceKeyPurpose.JournalHead,
                CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned),
                head.Mac))
        {
            throw new InvalidDataException(
                "The durable turn journal head failed keyed authentication.");
        }
    }

    private bool RecoverUncommittedSuffix(FileStream stream, TurnJournalHead head)
    {
        if (stream.Length < head.CommittedLength)
        {
            throw new InvalidDataException(
                "The durable turn journal is shorter than its authenticated committed head.");
        }

        var recovered = false;
        if (stream.Length > head.CommittedLength)
        {
            var suffixLength = stream.Length - head.CommittedLength;
            if (suffixLength > MaximumRecordBytes + 1L)
            {
                throw new InvalidDataException(
                    "The durable turn journal has an oversized uncommitted suffix and was not modified.");
            }

            stream.SetLength(head.CommittedLength);
            stream.Flush(flushToDisk: true);
            recovered = true;
            Interlocked.Increment(ref _recoveredTailCount);
        }

        if (head.CommittedLength > 0)
        {
            stream.Position = head.CommittedLength - 1;
            if (stream.ReadByte() != '\n')
            {
                throw new InvalidDataException(
                    "The durable turn journal commit marker does not match its authenticated head.");
            }
        }

        return recovered;
    }

    private async Task WriteHeadAtomicallyAsync(TurnJournalHead head)
    {
        var finalPath = Path.Combine(_directory, HeadFileName);
        var temporaryPath = Path.Combine(_directory, $".{Guid.NewGuid():N}.head.tmp");
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(head);
        if (bytes.Length == 0 || bytes.Length > MaximumHeadBytes)
        {
            throw new InvalidDataException("The durable turn journal head is too large.");
        }

        try
        {
            await using (var stream = OpenRegularFile(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             writeThrough: true,
                             "The durable turn journal temporary head is not a regular local file."))
            {
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporaryPath,
                finalPath,
                replaceExisting: true,
                "The durable turn journal head is not a regular local file.");
        }
        finally
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                "The durable turn journal temporary head is not a regular local file.");
        }
    }

    private async IAsyncEnumerable<byte[]> ReadLinesAsync(
        FileStream stream,
        long start,
        long end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (start < 0 || end < start || end > stream.Length)
        {
            throw new InvalidDataException("The durable turn journal committed range is invalid.");
        }

        stream.Position = start;
        var remaining = end - start;
        var buffer = new byte[8192];
        var line = new ArrayBufferWriter<byte>();
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("The durable turn journal ended before its committed head.");
            }

            remaining -= read;
            var segmentStart = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != '\n')
                {
                    continue;
                }

                AppendSegment(line, buffer.AsSpan(segmentStart, index - segmentStart));
                if (line.WrittenCount == 0)
                {
                    throw new InvalidDataException("The durable turn journal contains an empty record.");
                }

                yield return line.WrittenSpan.ToArray();
                line = new ArrayBufferWriter<byte>();
                segmentStart = index + 1;
            }

            AppendSegment(line, buffer.AsSpan(segmentStart, read - segmentStart));
        }

        if (line.WrittenCount != 0)
        {
            throw new InvalidDataException("The durable turn journal has an incomplete committed record.");
        }
    }

    private static void AppendSegment(ArrayBufferWriter<byte> line, ReadOnlySpan<byte> segment)
    {
        if (line.WrittenCount + segment.Length > MaximumRecordBytes)
        {
            throw new InvalidDataException("A durable turn journal record exceeds its bounded size.");
        }

        line.Write(segment);
    }

    private static StoredTurnJournalEntry DeserializeCanonicalEntry(ReadOnlySpan<byte> line)
    {
        if (line.Length <= 0 || line.Length > MaximumRecordBytes)
        {
            throw new InvalidDataException("A durable turn journal record has an invalid size.");
        }

        StoredTurnJournalEntry stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredTurnJournalEntry>(line, JsonOptions)
                ?? throw new InvalidDataException("A durable turn journal record is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("A durable turn journal record is malformed.", ex);
        }

        var canonical = CanonicalEvidenceJson.SerializeToUtf8Bytes(stored);
        if (!line.SequenceEqual(canonical))
        {
            throw new InvalidDataException("A durable turn journal record is not canonical.");
        }

        return stored;
    }

    private async Task<FileStream> AcquireWriteLeaseAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, ".writer.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return OpenRegularFile(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    writeThrough: true,
                    "The durable turn journal writer lease is not a regular local file.");
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private FileStream OpenJournal() =>
        OpenRegularFile(
            Path.Combine(_directory, JournalFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            writeThrough: true,
            "The durable turn journal is not a regular local file.");

    private void EnsureStorageDirectory() =>
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            _directory,
            "The durable turn storage directory must be a regular local directory.");

    private static FileStream OpenRegularFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        bool writeThrough,
        string invalidTargetMessage) =>
        WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            mode,
            access,
            share,
            writeThrough,
            invalidTargetMessage);

    private static TurnJournalVerificationStamp? TryCaptureVerificationStamp(
        FileStream stream,
        TurnJournalHead head)
    {
        try
        {
            if (!GetFileInformationByHandleEx(
                    stream.SafeFileHandle,
                    FileInfoByHandleClass.FileBasicInfo,
                    out FileBasicInfo basicInfo,
                    (uint)Marshal.SizeOf<FileBasicInfo>())
                || !GetFileInformationByHandleEx(
                    stream.SafeFileHandle,
                    FileInfoByHandleClass.FileIdInfo,
                    out FileIdInfo fileIdInfo,
                    (uint)Marshal.SizeOf<FileIdInfo>()))
            {
                return null;
            }

            return new TurnJournalVerificationStamp(
                head.Mac,
                fileIdInfo.VolumeSerialNumber,
                fileIdInfo.FileId.Low,
                fileIdInfo.FileId.High,
                basicInfo.ChangeTime,
                RandomAccess.GetLength(stream.SafeFileHandle),
                head.Sequence);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return null;
        }
    }

    private static TurnFactIndexVerificationStamp? TryCaptureFactIndexVerificationStamp(
        FileStream stream)
    {
        try
        {
            if (!GetFileInformationByHandleEx(
                    stream.SafeFileHandle,
                    FileInfoByHandleClass.FileBasicInfo,
                    out FileBasicInfo basicInfo,
                    (uint)Marshal.SizeOf<FileBasicInfo>())
                || !GetFileInformationByHandleEx(
                    stream.SafeFileHandle,
                    FileInfoByHandleClass.FileIdInfo,
                    out FileIdInfo fileIdInfo,
                    (uint)Marshal.SizeOf<FileIdInfo>()))
            {
                return null;
            }

            return new TurnFactIndexVerificationStamp(
                fileIdInfo.VolumeSerialNumber,
                fileIdInfo.FileId.Low,
                fileIdInfo.FileId.High,
                basicInfo.ChangeTime,
                RandomAccess.GetLength(stream.SafeFileHandle));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return null;
        }
    }

    private TurnJournalHead InitialHead() =>
        new(
            _turnStorageKey,
            0,
            0,
            TurnStateIntegrity.EmptyDigest,
            TurnStateIntegrity.EmptyDigest,
            string.Empty);

    private static string SignHead(
        UnsignedTurnJournalHead head,
        EvidenceKeySession keys) =>
        keys.HmacHex(
            EvidenceKeyPurpose.JournalHead,
            CanonicalEvidenceJson.SerializeToUtf8Bytes(head));

    private static TurnJournalEntry Project(StoredTurnJournalEntry stored) =>
        new(
            stored.Sequence,
            CreateTimestamp(stored.TimestampUtcTicks),
            stored.Transition,
            stored.ResultingStateDigest,
            stored.Checksum);

    private static TurnJournalEntry CloneEntry(TurnJournalEntry entry) =>
        entry with { Transition = CloneTransition(entry.Transition) };

    private static TurnTransition CloneTransition(TurnTransition transition)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes<TurnTransition>(transition);
        try
        {
            var clone = JsonSerializer.Deserialize<TurnTransition>(bytes, JsonOptions)
                ?? throw new InvalidDataException("A durable turn transition clone is empty.");
            clone.ValidateShape();
            return clone;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("A durable turn transition clone is malformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static TurnState? CloneState(TurnState? state) =>
        state is null
            ? null
            : state with
            {
                OriginalRequest = state.OriginalRequest with { },
                AcceptedPriorConversation = state.AcceptedPriorConversation
                    .Select(item => item with { Payload = item.Payload with { } })
                    .ToArray(),
                Bindings = state.Bindings with { },
                PendingActions = state.PendingActions
                    .Select(item => item with { Intent = item.Intent with { } })
                    .ToArray(),
                PendingAcceptedCall = state.PendingAcceptedCall is null
                    ? null
                    : state.PendingAcceptedCall with
                    {
                        Payload = state.PendingAcceptedCall.Payload with { }
                    },
                InterimPublication = state.InterimPublication is null
                    ? null
                    : state.InterimPublication with
                    {
                        TextPayload = state.InterimPublication.TextPayload with { }
                    },
                FinalPublication = state.FinalPublication is null
                    ? null
                    : state.FinalPublication with
                    {
                        AnswerPayload = state.FinalPublication.AnswerPayload with { }
                    }
            };

    private static DateTimeOffset CreateTimestamp(long utcTicks)
    {
        try
        {
            return new DateTimeOffset(utcTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("A durable turn journal timestamp is invalid.", ex);
        }
    }

    private static bool HeadsMatch(TurnJournalHead left, TurnJournalHead right) =>
        left.CommittedLength == right.CommittedLength
        && left.Sequence == right.Sequence
        && string.Equals(left.TurnStorageKey, right.TurnStorageKey, StringComparison.Ordinal)
        && string.Equals(left.Checksum, right.Checksum, StringComparison.Ordinal)
        && string.Equals(left.StateDigest, right.StateDigest, StringComparison.Ordinal)
        && string.Equals(left.Mac, right.Mac, StringComparison.Ordinal);

    private static bool IsAlreadyAuthoritative(
        JournalCache cache,
        TurnTransition transition,
        RecordedActionFact? historicalAction)
    {
        if (cache.State is null)
        {
            return false;
        }

        if (transition is ActionPreparedTransition prepared)
        {
            if (cache.State.TryGetPendingAction(prepared.Intent.IdempotencyKey, out var pending))
            {
                if (pending!.Intent != prepared.Intent)
                {
                    throw new InvalidDataException(
                        "An action idempotency key cannot be rebound to a different prepared intent.");
                }

                return true;
            }

            if (historicalAction?.State is RecordedActionState.Committed
                    or RecordedActionState.Applied)
            {
                return true;
            }
        }

        if (transition is FinalPublicationPreparedTransition preparedPublication
            && cache.State.FinalPublication is
                { Status: FinalPublicationStatus.Prepared } existingPrepared)
        {
            EnsureSamePublication(existingPrepared, preparedPublication.PublicationId,
                preparedPublication.AssistantMessageId, preparedPublication.AnswerDigest,
                preparedPublication.AnswerPayload);
            return true;
        }

        if (transition is InterimPublicationPreparedTransition preparedInterim
            && cache.State.InterimPublication is { } existingInterim)
        {
            EnsureSameInterimPublication(
                existingInterim,
                preparedInterim.PublicationId,
                preparedInterim.Kind,
                preparedInterim.Reason,
                preparedInterim.SubjectId,
                preparedInterim.TextDigest,
                preparedInterim.TextPayload);
            return true;
        }

        if (transition is InterimPublicationCommittedTransition committedInterim
            && cache.State.InterimPublication is
                { Status: InterimPublicationStatus.Committed } existingCommittedInterim)
        {
            EnsureSameInterimPublication(
                existingCommittedInterim,
                committedInterim.PublicationId,
                committedInterim.Kind,
                reason: null,
                subjectId: null,
                textDigest: committedInterim.TextDigest);
            return true;
        }

        if (transition is FinalPublicationCommittedTransition committedPublication
            && cache.State.FinalPublication is { Status: FinalPublicationStatus.Committed } existingCommitted)
        {
            EnsureSamePublication(existingCommitted, committedPublication.PublicationId,
                committedPublication.AssistantMessageId, committedPublication.AnswerDigest);
            return true;
        }

        return false;
    }

    private static void EnsureSameInterimPublication(
        InterimPublicationState existing,
        string publicationId,
        InterimPublicationKind kind,
        InterimPublicationReason? reason,
        string? subjectId,
        string textDigest,
        ProtectedTurnInputReference? textPayload = null)
    {
        if (!string.Equals(existing.PublicationId, publicationId, StringComparison.Ordinal)
            || existing.Kind != kind
            || (reason is not null && existing.Reason != reason.Value)
            || (subjectId is not null
                && !string.Equals(existing.SubjectId, subjectId, StringComparison.Ordinal))
            || !string.Equals(existing.TextDigest, textDigest, StringComparison.Ordinal)
            || (textPayload is not null && existing.TextPayload != textPayload))
        {
            throw new InvalidDataException(
                "An interim publication identity cannot be rebound to different visible content.");
        }
    }

    private static void EnsureSamePublication(
        FinalPublicationState existing,
        string publicationId,
        string assistantMessageId,
        string answerDigest,
        ProtectedTurnInputReference? answerPayload = null)
    {
        if (!string.Equals(existing.PublicationId, publicationId, StringComparison.Ordinal)
            || !string.Equals(existing.AssistantMessageId, assistantMessageId, StringComparison.Ordinal)
            || !string.Equals(existing.AnswerDigest, answerDigest, StringComparison.Ordinal)
            || (answerPayload is not null && existing.AnswerPayload != answerPayload))
        {
            throw new InvalidDataException(
                "A final publication identity cannot be rebound to different visible content.");
        }
    }

    private static bool FixedTimeEquals(string leftHex, string rightHex)
    {
        try
        {
            var left = Convert.FromHexString(leftHex);
            var right = Convert.FromHexString(rightHex);
            try
            {
                return left.Length == right.Length
                       && CryptographicOperations.FixedTimeEquals(left, right);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(left);
                CryptographicOperations.ZeroMemory(right);
            }
        }
        catch (FormatException)
        {
            return false;
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

    private sealed record JournalScanResult(
        JournalCache Cache,
        IReadOnlyList<TurnJournalEntry>? AuditEntries);

    private sealed record JournalCache(
        TurnJournalHead Head,
        TurnJournalVerificationStamp? VerificationStamp,
        TurnState? State,
        BoundedQueue<TurnJournalEntry> Entries,
        BoundedReplayFacts Facts,
        BoundedResumeProjection Resume);

    private sealed record HistoricalFacts(
        RecordedCorrelationFact? Correlation,
        RecordedActionFact? Action,
        RecordedEvidenceFact? Evidence);

    private sealed record RecordedCorrelationFact(
        long Cursor,
        string TransitionDigest,
        TurnJournalEntry? Entry);

    private sealed record RecordedActionFact(
        string ActionIdempotencyKey,
        RecordedActionState State,
        TurnTransition Transition,
        long Cursor,
        string TransitionDigest);

    private sealed record RecordedEvidenceFact(
        EvidenceReferencedTransition Transition,
        long Cursor,
        string TransitionDigest);

    private readonly record struct EvidenceFactKey(string EvidenceId, long Cursor)
    {
        internal static EvidenceFactKey From(CommittedEvidenceReference reference) =>
            new(reference.EvidenceId, reference.Cursor);
    }

    /// <summary>
    /// Fixed-memory hot facts backed by an exact, derived disk index. The index stores complete
    /// keys and journal locations, so an LRU miss never falls back to rescanning the journal.
    /// It is not authority: a new journal instance always rebuilds it from authenticated records,
    /// and a warm cache is reusable only while both no-follow index files retain their exact file
    /// identities and change stamps.
    /// </summary>
    private sealed class BoundedReplayFacts
    {
        private readonly ExactReplayFactIndex _exactIndex;
        private readonly FixedLruCache<string, RecordedCorrelationFact> _correlations =
            new(MaximumCachedFactsPerKind, StringComparer.Ordinal);
        private readonly FixedLruCache<string, RecordedActionFact> _actions =
            new(MaximumCachedFactsPerKind, StringComparer.Ordinal);
        private readonly FixedLruCache<EvidenceFactKey, RecordedEvidenceFact> _evidence =
            new(MaximumCachedFactsPerKind);
        private readonly FixedLruCache<string, CommittedEvidenceReference> _evidenceReferences =
            new(MaximumCachedFactsPerKind, StringComparer.Ordinal);

        internal BoundedReplayFacts(ExactReplayFactIndex exactIndex)
        {
            _exactIndex = exactIndex ?? throw new ArgumentNullException(nameof(exactIndex));
        }

        internal int CorrelationCount => _correlations.Count;
        internal int ActionCount => _actions.Count;
        internal int EvidenceCount => _evidence.Count;
        internal long ExactIndexProbeCount => _exactIndex.ProbeCount;
        internal long ExactIndexKeyCount => _exactIndex.KeyCount;
        internal long ExactIndexCapacity => _exactIndex.Capacity;

        internal IDisposable OpenExactIndexSession() => _exactIndex.OpenSession();

        internal bool IsExactIndexCurrent() => _exactIndex.IsCurrent();

        internal void FinalizeExactIndexBuild(
            long journalSequence,
            string journalChecksum,
            CancellationToken cancellationToken) =>
            _exactIndex.FinalizeBuild(
                journalSequence,
                journalChecksum,
                cancellationToken);

        internal void CommitExactIndexHead(long journalSequence, string journalChecksum) =>
            _exactIndex.CommitJournalHead(journalSequence, journalChecksum);

        internal bool TryGetCorrelation(string key, out RecordedCorrelationFact? value) =>
            _correlations.TryGetValue(key, out value);

        internal bool TryGetAction(string key, out RecordedActionFact? value) =>
            _actions.TryGetValue(key, out value);

        internal bool TryGetEvidence(EvidenceFactKey key, out RecordedEvidenceFact? value) =>
            _evidence.TryGetValue(key, out value);

        internal bool TryGetEvidenceReference(
            string evidenceId,
            out CommittedEvidenceReference? value) =>
            _evidenceReferences.TryGetValue(evidenceId, out value);

        internal bool TryLocateCorrelation(string key, out ExactFactLocation location) =>
            _exactIndex.TryGet(ExactFactKind.Correlation, key, out location);

        internal bool TryLocateAction(string key, out ExactFactLocation location) =>
            _exactIndex.TryGet(ExactFactKind.Action, key, out location);

        internal bool TryLocateEvidence(
            EvidenceFactKey key,
            out ExactFactLocation location) =>
            _exactIndex.TryGetEvidence(key, out location);

        internal bool TryLocateEvidenceReference(
            string evidenceId,
            out ExactFactLocation location) =>
            _exactIndex.TryGet(ExactFactKind.EvidenceId, evidenceId, out location);

        internal void WarmCorrelation(string key, RecordedCorrelationFact value) =>
            _correlations.Set(key, value with { Entry = null });

        internal void WarmAction(string key, RecordedActionFact value) =>
            _actions.Set(key, value);

        internal void WarmEvidence(EvidenceFactKey key, RecordedEvidenceFact value) =>
            _evidence.Set(key, value);

        internal void WarmEvidenceReference(
            string evidenceId,
            CommittedEvidenceReference value) =>
            _evidenceReferences.Set(evidenceId, value with { });

        internal void Record(
            TurnJournalEntry entry,
            string transitionDigest,
            long journalOffset,
            int journalRecordLength)
        {
            var location = new ExactFactLocation(
                journalOffset,
                journalRecordLength,
                Ambiguous: false);
            _exactIndex.InsertUnique(
                ExactFactKind.Correlation,
                entry.Transition.CorrelationKey,
                location,
                "The durable turn journal contains a duplicate correlation key.");
            _correlations.Set(
                entry.Transition.CorrelationKey,
                new RecordedCorrelationFact(entry.Cursor, transitionDigest, Entry: null));

            if (TryCreateActionFact(entry, out var action))
            {
                _exactIndex.Upsert(
                    ExactFactKind.Action,
                    action!.ActionIdempotencyKey,
                    location);
                _actions.Set(action.ActionIdempotencyKey, action);
            }

            if (entry.Transition is EvidenceReferencedTransition evidence)
            {
                var key = EvidenceFactKey.From(evidence.Evidence);
                _exactIndex.InsertUniqueEvidence(
                    key,
                    location,
                    "The durable turn journal contains a duplicate evidence identity and cursor.");
                var evidenceIdWasDuplicated = _exactIndex.InsertOrMarkAmbiguous(
                    ExactFactKind.EvidenceId,
                    evidence.Evidence.EvidenceId,
                    location);
                _evidence.Set(
                    key,
                    new RecordedEvidenceFact(
                        evidence with { Evidence = evidence.Evidence with { } },
                        entry.Cursor,
                        transitionDigest));
                if (evidenceIdWasDuplicated)
                {
                    _evidenceReferences.Remove(evidence.Evidence.EvidenceId);
                }
                else
                {
                    _evidenceReferences.Set(
                        evidence.Evidence.EvidenceId,
                        evidence.Evidence with { });
                }
            }
        }
    }

    private enum ExactFactKind : byte
    {
        Correlation = 1,
        Action = 2,
        Evidence = 3,
        EvidenceId = 4
    }

    private readonly record struct ExactFactLocation(
        long JournalOffset,
        int JournalRecordLength,
        bool Ambiguous);

    /// <summary>
    /// A bounded-RAM open-addressed hash table whose complete keys live in a second derived file.
    /// Only authenticated journal records populate it. The table is retained between operations,
    /// but never trusted across journal instances and never treated as authority. Exact file IDs,
    /// change stamps, lengths, exclusive no-follow opens, and the authenticated journal head guard
    /// every warm reuse. Any mismatch causes a linear rebuild from the journal.
    /// </summary>
    private sealed class TurnFactIndexInvalidException : IOException
    {
        internal TurnFactIndexInvalidException(string message)
            : base(message)
        {
        }

        internal TurnFactIndexInvalidException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class ExactReplayFactIndex
    {
        private const int SlotBytes = 32;
        private const int AuthenticationTagBytes = 32;
        private const int TablePageBytes = 4096;
        private const int SlotsPerPage = TablePageBytes / SlotBytes;
        private const int AuthenticationFanout = 128;
        private const int MaximumExactKeyBytes = 1024;
        private const int MaximumManifestBytes = 64 * 1024;
        private const long MinimumCapacity = 1024;
        private const string InvalidIndexFileMessage =
            "The durable turn fact index is not a regular local file.";
        private static readonly byte[] PageAuthenticationDomain =
            Encoding.UTF8.GetBytes("Ali.TurnFactIndex.Page.v1");
        private static readonly byte[] NodeAuthenticationDomain =
            Encoding.UTF8.GetBytes("Ali.TurnFactIndex.Node.v1");

        private readonly string _tablePath;
        private readonly string _keyPath;
        private readonly string _authenticationTreePath;
        private readonly string _manifestPath;
        private readonly string _turnStorageKey;
        private readonly string _generation;
        private readonly EvidenceKeySession _keys;
        private readonly Action<TurnJournalCommitBoundary>? _faultInjector;
        private FileStream? _tableStream;
        private FileStream? _keyStream;
        private FileStream? _authenticationTreeStream;
        private TurnFactIndexVerificationStamp? _tableStamp;
        private TurnFactIndexVerificationStamp? _keyStamp;
        private TurnFactIndexVerificationStamp? _authenticationTreeStamp;
        private TurnFactIndexVerificationStamp? _manifestStamp;
        private long _capacity;
        private long _keyCount;
        private long _keyFileLength;
        private long _journalSequence;
        private string _journalChecksum;
        private AuthenticationTreeLayout _authenticationLayout;
        private byte[] _authenticationRoot;
        private long _probeCount;
        private bool _dirty;
        private bool _manifestDirty;
        private bool _requiresBuildFinalization;
        private long _cachedPageIndex = -1;
        private byte[]? _cachedPage;

        private ExactReplayFactIndex(
            string tablePath,
            string keyPath,
            string authenticationTreePath,
            string manifestPath,
            string turnStorageKey,
            string generation,
            EvidenceKeySession keys,
            long capacity,
            long keyCount,
            long keyFileLength,
            long journalSequence,
            string journalChecksum,
            AuthenticationTreeLayout authenticationLayout,
            byte[] authenticationRoot,
            TurnFactIndexVerificationStamp? tableStamp,
            TurnFactIndexVerificationStamp? keyStamp,
            TurnFactIndexVerificationStamp? authenticationTreeStamp,
            TurnFactIndexVerificationStamp? manifestStamp,
            bool requiresBuildFinalization,
            Action<TurnJournalCommitBoundary>? faultInjector)
        {
            _tablePath = tablePath;
            _keyPath = keyPath;
            _authenticationTreePath = authenticationTreePath;
            _manifestPath = manifestPath;
            _turnStorageKey = turnStorageKey;
            _generation = generation;
            _keys = keys;
            _faultInjector = faultInjector;
            _capacity = capacity;
            _keyCount = keyCount;
            _keyFileLength = keyFileLength;
            _journalSequence = journalSequence;
            _journalChecksum = journalChecksum;
            _authenticationLayout = authenticationLayout;
            _authenticationRoot = authenticationRoot;
            _tableStamp = tableStamp;
            _keyStamp = keyStamp;
            _authenticationTreeStamp = authenticationTreeStamp;
            _manifestStamp = manifestStamp;
            _requiresBuildFinalization = requiresBuildFinalization;
        }

        internal long ProbeCount => Interlocked.Read(ref _probeCount);
        internal long KeyCount => Interlocked.Read(ref _keyCount);
        internal long Capacity => Interlocked.Read(ref _capacity);

        internal static ExactReplayFactIndex Rebuild(
            string directory,
            string turnStorageKey,
            long expectedRecordCount,
            string journalChecksum,
            EvidenceKeySession evidenceKeys,
            string tableFileName,
            string keyFileName,
            string authenticationTreeFileName,
            string manifestFileName,
            Action<TurnJournalCommitBoundary>? faultInjector)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(turnStorageKey);
            ArgumentNullException.ThrowIfNull(evidenceKeys);
            if (expectedRecordCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedRecordCount));
            }
            TurnStateIntegrity.RequireDigest(journalChecksum, nameof(journalChecksum));

            var tablePath = Path.Combine(directory, tableFileName);
            var keyPath = Path.Combine(directory, keyFileName);
            var authenticationTreePath = Path.Combine(directory, authenticationTreeFileName);
            var manifestPath = Path.Combine(directory, manifestFileName);
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                tablePath,
                InvalidIndexFileMessage);
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                keyPath,
                InvalidIndexFileMessage);
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                authenticationTreePath,
                InvalidIndexFileMessage);
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                manifestPath,
                InvalidIndexFileMessage);
            DeleteVerifiedLegacyBuildingFiles(directory, tableFileName);

            var capacity = InitialCapacity(expectedRecordCount);
            var authenticationLayout = AuthenticationTreeLayout.Create(capacity / SlotsPerPage);
            var generation = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant();
            FileStream? table = null;
            FileStream? keys = null;
            FileStream? authenticationTree = null;
            try
            {
                table = OpenIndexFile(tablePath, FileMode.CreateNew);
                table.SetLength(checked(capacity * SlotBytes));
                table.Flush(flushToDisk: false);

                keys = OpenIndexFile(keyPath, FileMode.CreateNew);
                keys.SetLength(1);
                keys.Flush(flushToDisk: false);

                authenticationTree = OpenIndexFile(authenticationTreePath, FileMode.CreateNew);
                authenticationTree.SetLength(authenticationLayout.FileLength);
                authenticationTree.Flush(flushToDisk: false);

                return new ExactReplayFactIndex(
                    tablePath,
                    keyPath,
                    authenticationTreePath,
                    manifestPath,
                    turnStorageKey,
                    generation,
                    evidenceKeys,
                    capacity,
                    keyCount: 0,
                    keyFileLength: 1,
                    journalSequence: expectedRecordCount,
                    journalChecksum: journalChecksum,
                    authenticationLayout: authenticationLayout,
                    authenticationRoot: new byte[AuthenticationTagBytes],
                    tableStamp: TryCaptureFactIndexVerificationStamp(table),
                    keyStamp: TryCaptureFactIndexVerificationStamp(keys),
                    authenticationTreeStamp:
                        TryCaptureFactIndexVerificationStamp(authenticationTree),
                    manifestStamp: null,
                    requiresBuildFinalization: true,
                    faultInjector: faultInjector);
            }
            catch
            {
                table?.Dispose();
                keys?.Dispose();
                authenticationTree?.Dispose();
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    tablePath,
                    InvalidIndexFileMessage);
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    keyPath,
                    InvalidIndexFileMessage);
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    authenticationTreePath,
                    InvalidIndexFileMessage);
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    manifestPath,
                    InvalidIndexFileMessage);
                throw;
            }
            finally
            {
                table?.Dispose();
                keys?.Dispose();
                authenticationTree?.Dispose();
            }
        }

        internal bool IsCurrent()
        {
            if (_tableStream is not null
                || _keyStream is not null
                || _authenticationTreeStream is not null)
            {
                return true;
            }

            if (_requiresBuildFinalization
                || _tableStamp is null
                || _keyStamp is null
                || _authenticationTreeStamp is null
                || _manifestStamp is null)
            {
                return false;
            }

            try
            {
                using var table = OpenIndexFile(_tablePath, FileMode.Open);
                using var keys = OpenIndexFile(_keyPath, FileMode.Open);
                using var authenticationTree = OpenIndexFile(
                    _authenticationTreePath,
                    FileMode.Open);
                using var manifest = OpenIndexFile(_manifestPath, FileMode.Open);
                return table.Length == checked(_capacity * SlotBytes)
                       && keys.Length == _keyFileLength
                       && authenticationTree.Length == _authenticationLayout.FileLength
                       && TryCaptureFactIndexVerificationStamp(table) == _tableStamp
                       && TryCaptureFactIndexVerificationStamp(keys) == _keyStamp
                       && TryCaptureFactIndexVerificationStamp(authenticationTree)
                       == _authenticationTreeStamp
                       && TryCaptureFactIndexVerificationStamp(manifest) == _manifestStamp
                       && ManifestMatches(manifest);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                return false;
            }
        }

        internal IDisposable OpenSession()
        {
            if (_tableStream is not null
                || _keyStream is not null
                || _authenticationTreeStream is not null)
            {
                throw new InvalidOperationException(
                    "The durable turn fact index already has an active session.");
            }

            FileStream? table = null;
            FileStream? keys = null;
            FileStream? authenticationTree = null;
            FileStream? manifest = null;
            try
            {
                table = OpenIndexFile(_tablePath, FileMode.Open);
                keys = OpenIndexFile(_keyPath, FileMode.Open);
                authenticationTree = OpenIndexFile(
                    _authenticationTreePath,
                    FileMode.Open);
                if (_tableStamp is null
                    || _keyStamp is null
                    || _authenticationTreeStamp is null
                    || table.Length != checked(_capacity * SlotBytes)
                    || keys.Length != _keyFileLength
                    || authenticationTree.Length != _authenticationLayout.FileLength
                    || TryCaptureFactIndexVerificationStamp(table) != _tableStamp
                    || TryCaptureFactIndexVerificationStamp(keys) != _keyStamp
                    || TryCaptureFactIndexVerificationStamp(authenticationTree)
                    != _authenticationTreeStamp)
                {
                    throw new TurnFactIndexInvalidException(
                        "The durable turn fact index changed before it could be used.");
                }

                if (!_requiresBuildFinalization)
                {
                    if (_manifestStamp is null)
                    {
                        throw new TurnFactIndexInvalidException(
                            "The durable turn fact index authentication manifest is unavailable.");
                    }

                    manifest = OpenIndexFile(_manifestPath, FileMode.Open);
                    if (TryCaptureFactIndexVerificationStamp(manifest) != _manifestStamp
                        || !ManifestMatches(manifest))
                    {
                        throw new TurnFactIndexInvalidException(
                            "The durable turn fact index authentication manifest changed before use.");
                    }
                }

                _tableStream = table;
                _keyStream = keys;
                _authenticationTreeStream = authenticationTree;
                _dirty = false;
                _manifestDirty = false;
                _cachedPageIndex = -1;
                _cachedPage = null;
                table = null;
                keys = null;
                authenticationTree = null;
                return new ExactIndexSession(this);
            }
            catch (TurnFactIndexInvalidException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                throw new TurnFactIndexInvalidException(
                    "The durable turn fact index could not be authenticated before use.",
                    ex);
            }
            finally
            {
                table?.Dispose();
                keys?.Dispose();
                authenticationTree?.Dispose();
                manifest?.Dispose();
            }
        }

        internal void FinalizeBuild(
            long journalSequence,
            string journalChecksum,
            CancellationToken cancellationToken)
        {
            RequireSession();
            if (!_requiresBuildFinalization)
            {
                throw new InvalidOperationException(
                    "The durable turn fact index build is already finalized.");
            }

            ValidateJournalBinding(journalSequence, journalChecksum);
            if (journalSequence != _journalSequence
                || !string.Equals(
                    journalChecksum,
                    _journalChecksum,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The durable turn fact index build does not match its authenticated journal head.");
            }

            BuildAuthenticationTree(cancellationToken);
            _requiresBuildFinalization = false;
            _dirty = true;
            _manifestDirty = true;
        }

        internal void CommitJournalHead(long journalSequence, string journalChecksum)
        {
            RequireSession();
            if (_requiresBuildFinalization)
            {
                throw new InvalidOperationException(
                    "The durable turn fact index build is not finalized.");
            }

            ValidateJournalBinding(journalSequence, journalChecksum);
            if (journalSequence != checked(_journalSequence + 1))
            {
                throw new InvalidDataException(
                    "The durable turn fact index journal sequence is not contiguous.");
            }

            _journalSequence = journalSequence;
            _journalChecksum = journalChecksum;
            _manifestDirty = true;
        }

        internal bool TryGet(
            ExactFactKind kind,
            string value,
            out ExactFactLocation location)
        {
            var key = CreateStringKey(kind, value);
            return TryGet(key, out location);
        }

        internal bool TryGetEvidence(
            EvidenceFactKey evidence,
            out ExactFactLocation location)
        {
            var key = CreateEvidenceKey(evidence);
            return TryGet(key, out location);
        }

        internal void InsertUnique(
            ExactFactKind kind,
            string value,
            ExactFactLocation location,
            string duplicateMessage)
        {
            var key = CreateStringKey(kind, value);
            InsertUnique(key, location, duplicateMessage);
        }

        internal void InsertUniqueEvidence(
            EvidenceFactKey evidence,
            ExactFactLocation location,
            string duplicateMessage)
        {
            var key = CreateEvidenceKey(evidence);
            InsertUnique(key, location, duplicateMessage);
        }

        internal void Upsert(
            ExactFactKind kind,
            string value,
            ExactFactLocation location)
        {
            ValidateLocation(location);
            var key = CreateStringKey(kind, value);
            var hash = Hash(key);
            if (TryFindSlot(key, hash, out var slotIndex, out var slot))
            {
                WriteAuthenticatedSlot(
                    slotIndex,
                    slot with
                    {
                        JournalOffset = location.JournalOffset,
                        JournalRecordLength = location.JournalRecordLength
                    });
                _dirty = true;
                return;
            }

            EnsureCapacityForOneMore();
            if (_capacity != slot.Capacity)
            {
                _ = TryFindSlot(key, hash, out slotIndex, out slot);
            }

            WriteNewSlot(slotIndex, hash, key, location);
        }

        /// <returns>True when the exact key was already present and is now ambiguous.</returns>
        internal bool InsertOrMarkAmbiguous(
            ExactFactKind kind,
            string value,
            ExactFactLocation location)
        {
            ValidateLocation(location);
            var key = CreateStringKey(kind, value);
            var hash = Hash(key);
            if (TryFindSlot(key, hash, out var slotIndex, out var slot))
            {
                var recordLength = slot.JournalRecordLength > 0
                    ? -slot.JournalRecordLength
                    : slot.JournalRecordLength;
                WriteAuthenticatedSlot(
                    slotIndex,
                    slot with { JournalRecordLength = recordLength });
                _dirty = true;
                return true;
            }

            EnsureCapacityForOneMore();
            if (_capacity != slot.Capacity)
            {
                _ = TryFindSlot(key, hash, out slotIndex, out slot);
            }

            WriteNewSlot(slotIndex, hash, key, location);
            return false;
        }

        private bool TryGet(byte[] key, out ExactFactLocation location)
        {
            var hash = Hash(key);
            if (!TryFindSlot(key, hash, out _, out var slot))
            {
                location = default;
                return false;
            }

            location = new ExactFactLocation(
                slot.JournalOffset,
                Math.Abs(slot.JournalRecordLength),
                slot.JournalRecordLength < 0);
            return true;
        }

        private void InsertUnique(
            byte[] key,
            ExactFactLocation location,
            string duplicateMessage)
        {
            ValidateLocation(location);
            var hash = Hash(key);
            if (TryFindSlot(key, hash, out var slotIndex, out var slot))
            {
                throw new InvalidDataException(duplicateMessage);
            }

            EnsureCapacityForOneMore();
            if (_capacity != slot.Capacity)
            {
                if (TryFindSlot(key, hash, out slotIndex, out slot))
                {
                    throw new InvalidDataException(duplicateMessage);
                }
            }

            WriteNewSlot(slotIndex, hash, key, location);
        }

        private bool TryFindSlot(
            byte[] key,
            ulong hash,
            out long slotIndex,
            out ExactIndexSlot slot)
        {
            RequireSession();
            var index = (long)(hash & (ulong)(_capacity - 1));
            for (long probe = 0; probe < _capacity; probe++)
            {
                Interlocked.Increment(ref _probeCount);
                slot = ReadAuthenticatedSlot(index);
                if (slot.KeyOffset == 0)
                {
                    slotIndex = index;
                    return false;
                }

                if (slot.Hash == hash
                    && slot.KeyLength == key.Length
                    && KeyEquals(slot.KeyOffset, key))
                {
                    slotIndex = index;
                    return true;
                }

                index = (index + 1) & (_capacity - 1);
            }

            throw new InvalidDataException(
                "The durable turn fact index has no available exact-key slot.");
        }

        private bool KeyEquals(long keyOffset, ReadOnlySpan<byte> key)
        {
            var buffer = new byte[key.Length];
            KeyStream.Position = keyOffset;
            ReadExactly(KeyStream, buffer);
            return buffer.AsSpan().SequenceEqual(key);
        }

        private void WriteNewSlot(
            long slotIndex,
            ulong hash,
            byte[] key,
            ExactFactLocation location)
        {
            RequireExactKey(key);
            var keyOffset = _keyFileLength;
            KeyStream.Position = keyOffset;
            KeyStream.Write(key);
            _keyFileLength = checked(_keyFileLength + key.Length);
            WriteAuthenticatedSlot(
                slotIndex,
                new ExactIndexSlot(
                    hash,
                    keyOffset,
                    location.JournalOffset,
                    key.Length,
                    location.JournalRecordLength,
                    _capacity));
            Interlocked.Increment(ref _keyCount);
            _dirty = true;
        }

        private void EnsureCapacityForOneMore()
        {
            // At most three distinct exact facts are generated by one journal record. Keeping
            // the table at or below 75% occupancy bounds expected probes without bounding turns.
            var maximumOccupied = (_capacity / 4) * 3;
            if (_keyCount + 1 <= maximumOccupied)
            {
                return;
            }

            Grow();
        }

        private void Grow()
        {
            RequireSession();
            if (_capacity > (long.MaxValue / 2)
                || _capacity > (long.MaxValue / (3L * SlotBytes)))
            {
                throw new IOException(
                    "The durable turn fact index cannot grow within the platform file-size range.");
            }

            var oldCapacity = _capacity;
            var nextCapacity = _capacity * 2;
            var oldTableLength = checked(oldCapacity * SlotBytes);
            var nextTableLength = checked(nextCapacity * SlotBytes);
            var relocationOffset = nextTableLength;
            var expandedLength = checked(relocationOffset + oldTableLength);
            var page = new byte[TablePageBytes];
            var zeros = new byte[TablePageBytes];
            try
            {
                // Preserve the authenticated source pages in a non-overlapping tail. This is
                // deliberately in-place: a crash can leave only disposable sidecars, never a
                // durable rename or an unbounded family of orphaned build files.
                Table.SetLength(expandedLength);
                _dirty = true;
                var oldPageCount = oldCapacity / SlotsPerPage;
                for (long pageIndex = 0; pageIndex < oldPageCount; pageIndex++)
                {
                    var authenticated = LoadPage(pageIndex);
                    Table.Position = checked(
                        relocationOffset + (pageIndex * TablePageBytes));
                    Table.Write(authenticated);
                }

                _cachedPageIndex = -1;
                _cachedPage = null;
                Table.Position = 0;
                for (long offset = 0; offset < nextTableLength; offset += zeros.Length)
                {
                    Table.Write(zeros);
                }

                _faultInjector?.Invoke(
                    TurnJournalCommitBoundary.FactIndexGrowthRebuildStarted);

                for (long index = 0; index < oldCapacity; index++)
                {
                    Table.Position = checked(relocationOffset + (index * SlotBytes));
                    ReadExactly(Table, page.AsSpan(0, SlotBytes));
                    var slot = ParseSlot(
                        page.AsSpan(0, SlotBytes),
                        oldCapacity,
                        _keyFileLength);
                    if (slot.KeyOffset == 0)
                    {
                        continue;
                    }

                    var destination = (long)(slot.Hash & (ulong)(nextCapacity - 1));
                    while (ReadRawSlot(
                               Table,
                               destination,
                               nextCapacity,
                               _keyFileLength).KeyOffset != 0)
                    {
                        destination = (destination + 1) & (nextCapacity - 1);
                    }

                    WriteRawSlot(
                        Table,
                        destination,
                        slot with { Capacity = nextCapacity });
                }

                Table.SetLength(nextTableLength);
                _capacity = nextCapacity;
                _authenticationLayout = AuthenticationTreeLayout.Create(
                    nextCapacity / SlotsPerPage);
                AuthenticationTree.SetLength(_authenticationLayout.FileLength);
                _cachedPageIndex = -1;
                _cachedPage = null;
                BuildAuthenticationTree(CancellationToken.None);
                _dirty = true;
                _manifestDirty = true;
            }
            catch
            {
                // No partially rebuilt sidecar is ever published as authenticated. The next
                // operation discards these disposable files and reconstructs them from the
                // authenticated journal while holding the writer lease.
                _requiresBuildFinalization = true;
                _manifestDirty = false;
                _authenticationRoot = new byte[AuthenticationTagBytes];
                throw;
            }
        }

        private void CloseSession()
        {
            var table = _tableStream;
            var keys = _keyStream;
            var authenticationTree = _authenticationTreeStream;
            _tableStream = null;
            _keyStream = null;
            _authenticationTreeStream = null;
            try
            {
                if (table is null || keys is null || authenticationTree is null)
                {
                    return;
                }

                if (_dirty)
                {
                    // These files are a disposable acceleration structure, not authority.
                    // A process crash loses the in-memory stamps and forces an authenticated
                    // journal rebuild, so forcing either sidecar through the disk cache would
                    // add unnecessary durability barriers to every transition.
                    keys.Flush(flushToDisk: false);
                    table.Flush(flushToDisk: false);
                    authenticationTree.Flush(flushToDisk: false);
                }

                _tableStamp = TryCaptureFactIndexVerificationStamp(table);
                _keyStamp = TryCaptureFactIndexVerificationStamp(keys);
                _authenticationTreeStamp =
                    TryCaptureFactIndexVerificationStamp(authenticationTree);
                if (_requiresBuildFinalization)
                {
                    _manifestStamp = null;
                    return;
                }

                if (_manifestDirty)
                {
                    _manifestStamp = WriteManifest();
                }
            }
            catch
            {
                _tableStamp = null;
                _keyStamp = null;
                _authenticationTreeStamp = null;
                _manifestStamp = null;
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    _manifestPath,
                    InvalidIndexFileMessage);
                throw;
            }
            finally
            {
                table?.Dispose();
                keys?.Dispose();
                authenticationTree?.Dispose();
                _dirty = false;
                _manifestDirty = false;
                _cachedPageIndex = -1;
                _cachedPage = null;
            }
        }

        private void RequireSession()
        {
            if (_tableStream is null
                || _keyStream is null
                || _authenticationTreeStream is null)
            {
                throw new InvalidOperationException(
                    "The durable turn fact index has no active session.");
            }
        }

        private FileStream Table
        {
            get
            {
                RequireSession();
                return _tableStream!;
            }
        }

        private FileStream KeyStream
        {
            get
            {
                RequireSession();
                return _keyStream!;
            }
        }

        private FileStream AuthenticationTree
        {
            get
            {
                RequireSession();
                return _authenticationTreeStream!;
            }
        }

        private ExactIndexSlot ReadAuthenticatedSlot(long index)
        {
            if (index < 0 || index >= _capacity)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index slot is outside the table.");
            }

            var pageIndex = index / SlotsPerPage;
            var slotWithinPage = checked((int)(index % SlotsPerPage));
            var page = LoadPage(pageIndex);
            return ParseSlot(
                page.AsSpan(slotWithinPage * SlotBytes, SlotBytes),
                _capacity,
                _keyFileLength);
        }

        private byte[] LoadPage(long pageIndex)
        {
            RequireSession();
            if (pageIndex < 0 || pageIndex >= _authenticationLayout.PageCount)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index page is outside the table.");
            }

            if (_cachedPageIndex == pageIndex && _cachedPage is not null)
            {
                return _cachedPage;
            }

            var page = ReadTablePage(pageIndex);
            if (!_requiresBuildFinalization)
            {
                VerifyPageAuthentication(pageIndex, page);
            }

            _cachedPageIndex = pageIndex;
            _cachedPage = page;
            return page;
        }

        private byte[] ReadTablePage(long pageIndex)
        {
            var page = new byte[TablePageBytes];
            Table.Position = checked(pageIndex * TablePageBytes);
            try
            {
                ReadExactly(Table, page);
                return page;
            }
            catch (EndOfStreamException ex)
            {
                throw new TurnFactIndexInvalidException(
                    "The durable turn fact index table page is incomplete.",
                    ex);
            }
        }

        private void WriteAuthenticatedSlot(long index, ExactIndexSlot slot)
        {
            RequireSession();
            if (index < 0 || index >= _capacity || slot.Capacity != _capacity)
            {
                throw new InvalidDataException(
                    "A durable turn fact index write is outside the current table.");
            }

            var pageIndex = index / SlotsPerPage;
            var slotWithinPage = checked((int)(index % SlotsPerPage));
            var page = LoadPage(pageIndex);
            SerializeSlot(
                page.AsSpan(slotWithinPage * SlotBytes, SlotBytes),
                slot);
            Table.Position = checked(index * SlotBytes);
            Table.Write(page.AsSpan(slotWithinPage * SlotBytes, SlotBytes));
            _dirty = true;
            if (_requiresBuildFinalization)
            {
                return;
            }

            try
            {
                UpdatePageAuthentication(pageIndex, page);
                _manifestDirty = true;
            }
            catch
            {
                _requiresBuildFinalization = true;
                _manifestDirty = false;
                _authenticationRoot = new byte[AuthenticationTagBytes];
                throw;
            }
        }

        private void BuildAuthenticationTree(CancellationToken cancellationToken)
        {
            RequireSession();
            AuthenticationTree.SetLength(_authenticationLayout.FileLength);
            for (long pageIndex = 0;
                 pageIndex < _authenticationLayout.PageCount;
                 pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = ReadTablePage(pageIndex);
                var tag = ComputePageAuthentication(pageIndex, page);
                WriteAuthenticationTag(level: 0, pageIndex, tag);
            }

            for (var level = 1; level < _authenticationLayout.LevelCount; level++)
            {
                for (long nodeIndex = 0;
                     nodeIndex < _authenticationLayout.LevelCounts[level];
                     nodeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tag = ComputeNodeAuthentication(level, nodeIndex);
                    WriteAuthenticationTag(level, nodeIndex, tag);
                }
            }

            _authenticationRoot = ReadAuthenticationTag(
                _authenticationLayout.LevelCount - 1,
                index: 0);
        }

        private void VerifyPageAuthentication(long pageIndex, ReadOnlySpan<byte> page)
        {
            var currentIndex = pageIndex;
            var expected = ComputePageAuthentication(pageIndex, page);
            for (var level = 0; level < _authenticationLayout.LevelCount; level++)
            {
                var stored = ReadAuthenticationTag(level, currentIndex);
                if (!CryptographicOperations.FixedTimeEquals(expected, stored))
                {
                    throw new TurnFactIndexInvalidException(
                        "A durable turn fact index page failed keyed authentication.");
                }

                if (level == _authenticationLayout.LevelCount - 1)
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            stored,
                            _authenticationRoot))
                    {
                        throw new TurnFactIndexInvalidException(
                            "The durable turn fact index authentication root is invalid.");
                    }

                    return;
                }

                currentIndex /= AuthenticationFanout;
                expected = ComputeNodeAuthentication(level + 1, currentIndex);
            }

            throw new TurnFactIndexInvalidException(
                "The durable turn fact index authentication path is incomplete.");
        }

        private void UpdatePageAuthentication(long pageIndex, ReadOnlySpan<byte> page)
        {
            var tag = ComputePageAuthentication(pageIndex, page);
            WriteAuthenticationTag(level: 0, pageIndex, tag);
            var currentIndex = pageIndex;
            for (var level = 1; level < _authenticationLayout.LevelCount; level++)
            {
                currentIndex /= AuthenticationFanout;
                tag = ComputeNodeAuthentication(level, currentIndex);
                WriteAuthenticationTag(level, currentIndex, tag);
            }

            _authenticationRoot = tag;
        }

        private byte[] ComputePageAuthentication(
            long pageIndex,
            ReadOnlySpan<byte> page)
        {
            if (page.Length != TablePageBytes)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index page has an invalid length.");
            }

            var material = new ArrayBufferWriter<byte>(TablePageBytes + 512);
            material.Write(PageAuthenticationDomain);
            WriteBoundedString(material, _turnStorageKey);
            WriteBoundedString(material, _generation);
            WriteInt64(material, pageIndex);
            WriteInt64(material, _capacity);
            // A page tag must remain stable when an unrelated exact key is appended on
            // another page. The authenticated manifest owns the global key-file length;
            // this page tag owns the exact slot bytes and exact key bytes referenced by
            // those slots. Including the mutable global length here would invalidate every
            // untouched page after each normal key append and force a spurious rebuild.
            material.Write(page);
            for (var slotIndex = 0; slotIndex < SlotsPerPage; slotIndex++)
            {
                var slot = ParseSlot(
                    page.Slice(slotIndex * SlotBytes, SlotBytes),
                    _capacity,
                    _keyFileLength);
                if (slot.KeyOffset == 0)
                {
                    continue;
                }

                var exactKey = ReadExactKey(slot.KeyOffset, slot.KeyLength);
                WriteInt32(material, slotIndex);
                WriteInt32(material, exactKey.Length);
                material.Write(exactKey);
            }

            return ComputeAuthenticationTag(material.WrittenSpan);
        }

        private byte[] ComputeNodeAuthentication(int level, long nodeIndex)
        {
            if (level <= 0 || level >= _authenticationLayout.LevelCount)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index authentication level is invalid.");
            }

            var childLevel = level - 1;
            var firstChild = checked(nodeIndex * AuthenticationFanout);
            var remaining = _authenticationLayout.LevelCounts[childLevel] - firstChild;
            var childCount = checked((int)Math.Min(AuthenticationFanout, remaining));
            if (childCount <= 0)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index authentication node has no children.");
            }

            var material = new ArrayBufferWriter<byte>(
                256 + (childCount * AuthenticationTagBytes));
            material.Write(NodeAuthenticationDomain);
            WriteBoundedString(material, _turnStorageKey);
            WriteBoundedString(material, _generation);
            WriteInt32(material, level);
            WriteInt64(material, nodeIndex);
            WriteInt64(material, _capacity);
            WriteInt32(material, childCount);
            for (var child = 0; child < childCount; child++)
            {
                material.Write(ReadAuthenticationTag(
                    childLevel,
                    firstChild + child));
            }

            return ComputeAuthenticationTag(material.WrittenSpan);
        }

        private byte[] ComputeAuthenticationTag(ReadOnlySpan<byte> material) =>
            Convert.FromHexString(
                _keys.HmacHex(EvidenceKeyPurpose.TurnFactIndex, material));

        private byte[] ReadAuthenticationTag(int level, long index)
        {
            var tag = new byte[AuthenticationTagBytes];
            AuthenticationTree.Position = _authenticationLayout.GetTagOffset(level, index);
            try
            {
                ReadExactly(AuthenticationTree, tag);
                return tag;
            }
            catch (EndOfStreamException ex)
            {
                throw new TurnFactIndexInvalidException(
                    "The durable turn fact index authentication tree is incomplete.",
                    ex);
            }
        }

        private void WriteAuthenticationTag(int level, long index, ReadOnlySpan<byte> tag)
        {
            if (tag.Length != AuthenticationTagBytes)
            {
                throw new InvalidDataException(
                    "A durable turn fact index authentication tag has an invalid length.");
            }

            AuthenticationTree.Position = _authenticationLayout.GetTagOffset(level, index);
            AuthenticationTree.Write(tag);
        }

        private byte[] ReadExactKey(long keyOffset, int keyLength)
        {
            if (keyOffset < 1 || keyLength <= 1 || keyLength > MaximumExactKeyBytes)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index exact key has an invalid range.");
            }

            long keyEnd;
            try
            {
                keyEnd = checked(keyOffset + keyLength);
            }
            catch (OverflowException ex)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index exact key range overflowed.",
                    ex);
            }

            if (keyEnd > _keyFileLength)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index exact key is outside its key file.");
            }

            var key = new byte[keyLength];
            KeyStream.Position = keyOffset;
            try
            {
                ReadExactly(KeyStream, key);
                return key;
            }
            catch (EndOfStreamException ex)
            {
                throw new TurnFactIndexInvalidException(
                    "The durable turn fact index exact key is incomplete.",
                    ex);
            }
        }

        private bool ManifestMatches(FileStream stream)
        {
            if (stream.Length is <= 0 or > MaximumManifestBytes)
            {
                return false;
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.Position = 0;
            ReadExactly(stream, bytes);
            TurnFactIndexManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<TurnFactIndexManifest>(bytes, JsonOptions)
                    ?? throw new InvalidDataException(
                        "The durable turn fact index manifest is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "The durable turn fact index manifest is malformed.",
                    ex);
            }

            if (!bytes.AsSpan().SequenceEqual(
                    CanonicalEvidenceJson.SerializeToUtf8Bytes(manifest)))
            {
                return false;
            }

            var root = Convert.ToHexString(_authenticationRoot).ToLowerInvariant();
            if (!IsLowerHexDigest(manifest.JournalChecksum)
                || !IsLowerHexDigest(manifest.AuthenticationRoot)
                || !IsLowerHexDigest(manifest.Mac)
                || !string.Equals(manifest.TurnStorageKey, _turnStorageKey, StringComparison.Ordinal)
                || !string.Equals(manifest.Generation, _generation, StringComparison.Ordinal)
                || manifest.JournalSequence != _journalSequence
                || !string.Equals(
                    manifest.JournalChecksum,
                    _journalChecksum,
                    StringComparison.Ordinal)
                || manifest.Capacity != _capacity
                || manifest.KeyCount != _keyCount
                || manifest.TableLength != checked(_capacity * SlotBytes)
                || manifest.KeyFileLength != _keyFileLength
                || manifest.AuthenticationTreeLength != _authenticationLayout.FileLength
                || !string.Equals(manifest.AuthenticationRoot, root, StringComparison.Ordinal))
            {
                return false;
            }

            var unsigned = new UnsignedTurnFactIndexManifest(
                manifest.TurnStorageKey,
                manifest.Generation,
                manifest.JournalSequence,
                manifest.JournalChecksum,
                manifest.Capacity,
                manifest.KeyCount,
                manifest.TableLength,
                manifest.KeyFileLength,
                manifest.AuthenticationTreeLength,
                manifest.AuthenticationRoot);
            return _keys.VerifyHmac(
                EvidenceKeyPurpose.TurnFactIndex,
                CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned),
                manifest.Mac);
        }

        private static bool IsLowerHexDigest(string? value) =>
            value is { Length: 64 }
            && value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');

        private TurnFactIndexVerificationStamp? WriteManifest()
        {
            var root = Convert.ToHexString(_authenticationRoot).ToLowerInvariant();
            var unsigned = new UnsignedTurnFactIndexManifest(
                _turnStorageKey,
                _generation,
                _journalSequence,
                _journalChecksum,
                _capacity,
                _keyCount,
                checked(_capacity * SlotBytes),
                _keyFileLength,
                _authenticationLayout.FileLength,
                root);
            var manifest = new TurnFactIndexManifest(
                unsigned.TurnStorageKey,
                unsigned.Generation,
                unsigned.JournalSequence,
                unsigned.JournalChecksum,
                unsigned.Capacity,
                unsigned.KeyCount,
                unsigned.TableLength,
                unsigned.KeyFileLength,
                unsigned.AuthenticationTreeLength,
                unsigned.AuthenticationRoot,
                _keys.HmacHex(
                    EvidenceKeyPurpose.TurnFactIndex,
                    CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned)));
            var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(manifest);
            if (bytes.Length is <= 0 or > MaximumManifestBytes)
            {
                throw new InvalidDataException(
                    "The durable turn fact index manifest has an invalid size.");
            }

            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                _manifestPath,
                InvalidIndexFileMessage);
            FileStream? stream = null;
            try
            {
                stream = OpenIndexFile(_manifestPath, FileMode.CreateNew);
                stream.Write(bytes);
                stream.Flush(flushToDisk: false);
                return TryCaptureFactIndexVerificationStamp(stream);
            }
            catch
            {
                stream?.Dispose();
                stream = null;
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    _manifestPath,
                    InvalidIndexFileMessage);
                throw;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        private static void ValidateJournalBinding(
            long journalSequence,
            string journalChecksum)
        {
            if (journalSequence < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(journalSequence));
            }

            TurnStateIntegrity.RequireDigest(journalChecksum, nameof(journalChecksum));
        }

        private static void WriteBoundedString(
            ArrayBufferWriter<byte> destination,
            string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 4096)
            {
                throw new InvalidDataException(
                    "A durable turn fact index authentication field is too large.");
            }

            WriteInt32(destination, bytes.Length);
            destination.Write(bytes);
        }

        private static void WriteInt32(ArrayBufferWriter<byte> destination, int value)
        {
            var span = destination.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            destination.Advance(sizeof(int));
        }

        private static void WriteInt64(ArrayBufferWriter<byte> destination, long value)
        {
            var span = destination.GetSpan(sizeof(long));
            BinaryPrimitives.WriteInt64LittleEndian(span, value);
            destination.Advance(sizeof(long));
        }

        private static FileStream OpenIndexFile(string path, FileMode mode) =>
            OpenRegularFile(
                path,
                mode,
                FileAccess.ReadWrite,
                FileShare.None,
                writeThrough: false,
                InvalidIndexFileMessage);

        private static long InitialCapacity(long expectedRecordCount)
        {
            long desired;
            try
            {
                desired = checked(expectedRecordCount * 4L);
            }
            catch (OverflowException ex)
            {
                throw new IOException(
                    "The durable turn fact index cannot represent the journal record count.",
                    ex);
            }

            desired = Math.Max(MinimumCapacity, desired);
            var capacity = MinimumCapacity;
            while (capacity < desired)
            {
                if (capacity > (long.MaxValue / 2)
                    || capacity * 2 > (long.MaxValue / SlotBytes))
                {
                    throw new IOException(
                        "The durable turn fact index cannot fit within the platform file-size range.");
                }

                capacity *= 2;
            }

            return capacity;
        }

        private static byte[] CreateStringKey(ExactFactKind kind, string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            var key = new byte[checked(valueBytes.Length + 1)];
            key[0] = (byte)kind;
            valueBytes.CopyTo(key, 1);
            RequireExactKey(key);
            return key;
        }

        private static byte[] CreateEvidenceKey(EvidenceFactKey evidence)
        {
            var evidenceId = Encoding.UTF8.GetBytes(evidence.EvidenceId);
            var key = new byte[checked(1 + sizeof(int) + evidenceId.Length + sizeof(long))];
            key[0] = (byte)ExactFactKind.Evidence;
            BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(1, sizeof(int)), evidenceId.Length);
            evidenceId.CopyTo(key, 1 + sizeof(int));
            BinaryPrimitives.WriteInt64LittleEndian(
                key.AsSpan(1 + sizeof(int) + evidenceId.Length, sizeof(long)),
                evidence.Cursor);
            RequireExactKey(key);
            return key;
        }

        private static ulong Hash(ReadOnlySpan<byte> key)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(key, digest);
            return BinaryPrimitives.ReadUInt64LittleEndian(digest);
        }

        private static void ValidateLocation(ExactFactLocation location)
        {
            if (location.Ambiguous
                || location.JournalOffset < 0
                || location.JournalRecordLength <= 0
                || location.JournalRecordLength > MaximumRecordBytes)
            {
                throw new InvalidDataException(
                    "A durable turn fact index location is invalid.");
            }
        }

        private static ExactIndexSlot ReadRawSlot(
            FileStream stream,
            long index,
            long capacity,
            long keyFileLength)
        {
            if (index < 0 || index >= capacity)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index slot is outside the table.");
            }

            Span<byte> bytes = stackalloc byte[SlotBytes];
            stream.Position = checked(index * SlotBytes);
            try
            {
                ReadExactly(stream, bytes);
            }
            catch (EndOfStreamException ex)
            {
                throw new TurnFactIndexInvalidException(
                    "The durable turn fact index slot is incomplete.",
                    ex);
            }

            return ParseSlot(bytes, capacity, keyFileLength);
        }

        private static ExactIndexSlot ParseSlot(
            ReadOnlySpan<byte> bytes,
            long capacity,
            long keyFileLength)
        {
            if (bytes.Length != SlotBytes)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index slot has an invalid length.");
            }

            var slot = new ExactIndexSlot(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(8, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(16, 8)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4)),
                capacity);
            if (slot.KeyOffset == 0)
            {
                if (slot != new ExactIndexSlot(0, 0, 0, 0, 0, capacity))
                {
                    throw new TurnFactIndexInvalidException(
                        "An empty durable turn fact index slot contains data.");
                }

                return slot;
            }

            long keyEnd;
            try
            {
                keyEnd = checked(slot.KeyOffset + slot.KeyLength);
            }
            catch (OverflowException ex)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index key location overflowed.",
                    ex);
            }

            if (slot.KeyOffset < 1
                || slot.KeyLength <= 1
                || slot.KeyLength > MaximumExactKeyBytes
                || keyEnd > keyFileLength
                || slot.JournalOffset < 0
                || slot.JournalRecordLength == 0
                || slot.JournalRecordLength == int.MinValue
                || Math.Abs(slot.JournalRecordLength) > MaximumRecordBytes)
            {
                throw new TurnFactIndexInvalidException(
                    "A durable turn fact index slot is malformed.");
            }

            return slot;
        }

        private static void WriteRawSlot(
            FileStream stream,
            long index,
            ExactIndexSlot slot)
        {
            Span<byte> bytes = stackalloc byte[SlotBytes];
            SerializeSlot(bytes, slot);
            stream.Position = checked(index * SlotBytes);
            stream.Write(bytes);
        }

        private static void SerializeSlot(Span<byte> bytes, ExactIndexSlot slot)
        {
            if (bytes.Length != SlotBytes)
            {
                throw new ArgumentException(
                    "A durable turn fact index slot buffer has an invalid length.",
                    nameof(bytes));
            }

            BinaryPrimitives.WriteUInt64LittleEndian(bytes[..8], slot.Hash);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(8, 8), slot.KeyOffset);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(16, 8), slot.JournalOffset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(24, 4), slot.KeyLength);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(28, 4), slot.JournalRecordLength);
        }

        private static void RequireExactKey(ReadOnlySpan<byte> key)
        {
            if (key.Length <= 1 || key.Length > MaximumExactKeyBytes)
            {
                throw new InvalidDataException(
                    $"A durable turn fact key must contain between 2 and {MaximumExactKeyBytes} bytes.");
            }
        }

        private static void ReadExactly(FileStream stream, Span<byte> destination)
        {
            var consumed = 0;
            while (consumed < destination.Length)
            {
                var read = stream.Read(destination[consumed..]);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        "The durable turn fact index ended before a complete value was read.");
                }

                consumed += read;
            }
        }

        private readonly record struct ExactIndexSlot(
            ulong Hash,
            long KeyOffset,
            long JournalOffset,
            int KeyLength,
            int JournalRecordLength,
            long Capacity);

        private sealed class AuthenticationTreeLayout
        {
            private AuthenticationTreeLayout(
                long[] levelOffsets,
                long[] levelCounts,
                long fileLength)
            {
                LevelOffsets = levelOffsets;
                LevelCounts = levelCounts;
                FileLength = fileLength;
            }

            internal long[] LevelOffsets { get; }
            internal long[] LevelCounts { get; }
            internal int LevelCount => LevelCounts.Length;
            internal long PageCount => LevelCounts[0];
            internal long FileLength { get; }

            internal static AuthenticationTreeLayout Create(long pageCount)
            {
                if (pageCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageCount));
                }

                var counts = new List<long>();
                var count = pageCount;
                while (true)
                {
                    counts.Add(count);
                    if (count == 1)
                    {
                        break;
                    }

                    count = checked(
                        (count + AuthenticationFanout - 1) / AuthenticationFanout);
                }

                var offsets = new long[counts.Count];
                long length = 0;
                try
                {
                    for (var level = 0; level < counts.Count; level++)
                    {
                        offsets[level] = length;
                        length = checked(
                            length + checked(counts[level] * AuthenticationTagBytes));
                    }
                }
                catch (OverflowException ex)
                {
                    throw new IOException(
                        "The durable turn fact index authentication tree is too large.",
                        ex);
                }

                return new AuthenticationTreeLayout(
                    offsets,
                    counts.ToArray(),
                    length);
            }

            internal long GetTagOffset(int level, long index)
            {
                if (level < 0
                    || level >= LevelCount
                    || index < 0
                    || index >= LevelCounts[level])
                {
                    throw new TurnFactIndexInvalidException(
                        "A durable turn fact index authentication tag is outside the tree.");
                }

                return checked(
                    LevelOffsets[level] + checked(index * AuthenticationTagBytes));
            }
        }

        private static void DeleteVerifiedLegacyBuildingFiles(
            string directory,
            string tableFileName)
        {
            var prefix = tableFileName + ".building-";
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var name = Path.GetFileName(path);
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)
                        || name.Length != prefix.Length + 32
                        || !IsHexadecimal(name.AsSpan(prefix.Length)))
                    {
                        continue;
                    }

                    try
                    {
                        _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                            path,
                            InvalidIndexFileMessage);
                    }
                    catch (Exception ex) when (ex is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
                    {
                        // A legacy orphan is disposable. A locked or reparse-point entry is
                        // safely ignored instead of broadening deletion or following it.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                // Directory cleanup is best effort and never required for correctness.
            }
        }

        private static bool IsHexadecimal(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9')
                      || (character >= 'a' && character <= 'f')
                      || (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class ExactIndexSession(ExactReplayFactIndex owner) : IDisposable
        {
            private ExactReplayFactIndex? _owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.CloseSession();
            }
        }
    }

    private sealed class BoundedResumeProjection
    {
        private readonly BoundedQueue<CommittedEvidenceReference> _evidence =
            new(MaximumResumeEvidenceReferences);
        private readonly BoundedQueue<SteeringAppendedTransition> _steering =
            new(MaximumResumeSteeringTransitions);
        private readonly BoundedQueue<ProgressAttemptRecordedTransition> _progress =
            new(MaximumResumeProgressAttempts);
        private readonly BoundedQueue<TurnUserResolutionProjection> _userResolutions =
            new(MaximumResumeUserResolutions);

        internal void Record(TurnJournalEntry entry)
        {
            switch (entry.Transition)
            {
                case EvidenceReferencedTransition evidence:
                    _evidence.Add(evidence.Evidence with { });
                    break;
                case SteeringAppendedTransition steering:
                    _steering.Add(steering with { Steering = steering.Steering with { } });
                    break;
                case ProgressAttemptRecordedTransition progress:
                    _progress.Add(progress with { });
                    break;
                case UnknownActionResolvedByUserTransition resolved:
                    _userResolutions.Add(new TurnUserResolutionProjection(
                        entry.Cursor,
                        resolved.SourceCommandId,
                        resolved.PromptPublicationId,
                        resolved.PromptTextDigest,
                        TurnUserResolutionKind.Action,
                        resolved.Reason,
                        resolved.SubjectId,
                        resolved.SubjectPreparedRevision,
                        resolved.Resolution switch
                        {
                            ActionUserResolution.ConfirmApplied =>
                                TurnUserResolutionOutcome.ActionConfirmedApplied,
                            ActionUserResolution.ConfirmAbsent =>
                                TurnUserResolutionOutcome.ActionConfirmedAbsent,
                            _ => throw new InvalidDataException(
                                "The durable action resolution is invalid.")
                        }));
                    break;
                case UnknownFinalPublicationResolvedByUserTransition resolved:
                    _userResolutions.Add(new TurnUserResolutionProjection(
                        entry.Cursor,
                        resolved.SourceCommandId,
                        resolved.PromptPublicationId,
                        resolved.PromptTextDigest,
                        TurnUserResolutionKind.FinalPublication,
                        resolved.Reason,
                        resolved.SubjectId,
                        resolved.SubjectPreparedRevision,
                        resolved.Resolution switch
                        {
                            FinalPublicationUserResolution.ConfirmDisplayed =>
                                TurnUserResolutionOutcome.FinalPublicationConfirmedDisplayed,
                            FinalPublicationUserResolution.ConfirmNotDisplayed =>
                                TurnUserResolutionOutcome.FinalPublicationConfirmedNotDisplayed,
                            _ => throw new InvalidDataException(
                                "The durable final-publication resolution is invalid.")
                        }));
                    break;
            }
        }

        internal TurnResumeProjection CreateProjection(TurnState? state, bool recovered) =>
            new(
                CloneState(state),
                Array.AsReadOnly(_evidence.Select(item => item with { }).ToArray()),
                Array.AsReadOnly(_steering
                    .Select(item => item with { Steering = item.Steering with { } })
                    .ToArray()),
                Array.AsReadOnly(_progress
                    .Select(item => item with { })
                    .ToArray()),
                Array.AsReadOnly(_userResolutions
                    .Select(item => item with { })
                    .ToArray()),
                recovered);
    }

    private sealed class BoundedQueue<T> : IEnumerable<T>
    {
        private readonly int _capacity;
        private readonly Queue<T> _items;

        internal BoundedQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
            _items = new Queue<T>(capacity);
        }

        internal int Count => _items.Count;

        internal void Add(T item)
        {
            if (_items.Count == _capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class FixedLruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _items;
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _lru = new();

        internal FixedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
            _items = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
        }

        internal int Count => _items.Count;

        internal bool TryGetValue(TKey key, out TValue? value)
        {
            if (!_items.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        internal void Set(TKey key, TValue value)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                existing.Value = new KeyValuePair<TKey, TValue>(key, value);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            if (_items.Count == _capacity)
            {
                var oldest = _lru.Last
                    ?? throw new InvalidOperationException("The bounded journal fact cache is inconsistent.");
                _lru.RemoveLast();
                _items.Remove(oldest.Value.Key);
            }

            var node = _lru.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
            _items.Add(key, node);
        }

        internal bool Remove(TKey key)
        {
            if (!_items.Remove(key, out var node))
            {
                return false;
            }

            _lru.Remove(node);
            return true;
        }
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
