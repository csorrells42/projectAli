using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Orchestration.Work;

public enum WorkGraphCommitStatus
{
    Committed,
    AlreadyRecorded
}

public sealed record CommittedWorkGraphSnapshot(
    WorkGraphSnapshot Snapshot,
    CommittedWorkGraphReference Reference);

public sealed record WorkGraphCommitResult(
    WorkGraphCommitStatus Status,
    CommittedWorkGraphSnapshot? Current)
{
    public bool Advanced => Status == WorkGraphCommitStatus.Committed;
}

public sealed record WorkGraphPruneResult(
    int RetainedCandidates,
    int RemovedOrphanCandidates,
    long RemovedBytes);

public sealed record WorkGraphStoreDiagnostics(
    long RecordsReadFromDisk,
    long ValidatedDeltaCommits,
    long ValidatedDeltaNodesSerialized,
    long ValidatedCheckpointNodesSerialized,
    long ParentSnapshotReconstructions,
    long NewCandidateReconstructions,
    long FullSnapshotDiffPasses,
    long FullSnapshotCloneValidationPasses)
{
    public WorkGraphStoreDiagnostics(long recordsReadFromDisk)
        : this(recordsReadFromDisk, 0, 0, 0, 0, 0, 0, 0)
    {
    }
}

/// <summary>
/// Persists current-user-protected, authenticated work-graph deltas per turn. Candidates are
/// immutable; the authoritative State journal selects one by its exact committed reference.
/// Every selected revision retains an exact parent reference, so unchanged nodes are not copied
/// into every revision. Authenticated compact checkpoints bound reconstruction to 64 records
/// without limiting revision count. A per-turn writer lease makes candidate creation and orphan
/// pruning atomic across instances.
/// </summary>
public sealed class DurableWorkGraphStore : IDisposable
{
    private const string WriterLockFileName = ".work-graph.writer.lock";
    private const int FormatVersion = 1;
    private const int MaximumProtectedSnapshotBytes = 64 * 1024 * 1024;
    internal const int MaximumDeltaChainLength = 64;
    internal const int MaximumCandidateEnumerationCount = 4_096;
    private const int MaximumTemporaryFileCreateAttempts = 4;
    private const int StorageKeyHexChars = 64;
    private const int StorageKeyDigestBytes = 32;
    private const int DigestBytes = 32;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileTypeDisk = 0x0001;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int HeaderBytes = 8 + sizeof(int) + sizeof(long) + StorageKeyDigestBytes +
        DigestBytes + sizeof(int);
    private static readonly byte[] FileMagic = [0x41, 0x4c, 0x49, 0x57, 0x4f, 0x52, 0x4b, 0x00];
    private const string InvalidDirectoryBoundaryMessage =
        "The durable work-graph directory boundary is not a regular local directory.";
    private const string InvalidCandidateBoundaryMessage =
        "The durable work-graph candidate is not a regular local file.";
    private const string InvalidLeaseBoundaryMessage =
        "The durable work-graph writer lease is not a regular local file.";
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _rootDirectory;
    private readonly WindowsCurrentUserEvidenceProtector _protector;
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private readonly ConditionalWeakTable<WorkGraphSnapshot, CommittedSnapshotProvenance>
        _committedSnapshotProvenance = new();
    private EvidenceKeySession? _keySession;
    private long _recordsReadFromDisk;
    private long _validatedDeltaCommits;
    private long _validatedDeltaNodesSerialized;
    private long _validatedCheckpointNodesSerialized;
    private long _parentSnapshotReconstructions;
    private long _newCandidateReconstructions;
    private long _fullSnapshotDiffPasses;
    private long _fullSnapshotCloneValidationPasses;
    private int _disposed;

    public DurableWorkGraphStore(string rootDirectory, string assistantProfileBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _protector = new WindowsCurrentUserEvidenceProtector(
            _rootDirectory,
            assistantProfileBinding);
    }

    public WorkGraphStoreDiagnostics Diagnostics =>
        new(
            Interlocked.Read(ref _recordsReadFromDisk),
            Interlocked.Read(ref _validatedDeltaCommits),
            Interlocked.Read(ref _validatedDeltaNodesSerialized),
            Interlocked.Read(ref _validatedCheckpointNodesSerialized),
            Interlocked.Read(ref _parentSnapshotReconstructions),
            Interlocked.Read(ref _newCandidateReconstructions),
            Interlocked.Read(ref _fullSnapshotDiffPasses),
            Interlocked.Read(ref _fullSnapshotCloneValidationPasses));

    public async Task<CommittedWorkGraphSnapshot?> ReadAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference reference,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reference);
        reference.Validate();

        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireTurnLeaseAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        return await ReadUnderLeaseAsync(identity, reference, keys, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkGraphCommitResult> CommitAsync(
        TurnIdentity identity,
        long expectedRevision,
        WorkGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                "The expected work-graph revision cannot be negative.");
        }

        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireTurnLeaseAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        var parent = expectedRevision == 0
            ? null
            : await ResolveUnambiguousParentUnderLeaseAsync(
                    identity,
                    expectedRevision,
                    keys,
                    cancellationToken)
                .ConfigureAwait(false);
        return await CommitUnderLeaseAsync(
                identity,
                expectedRevision,
                parent,
                snapshot,
                keys,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a candidate against the exact work-graph reference selected by durable turn state.
    /// Callers must use this overload once revision zero has advanced; it prevents an orphan branch
    /// from becoming the implicit parent when concurrent candidates exist.
    /// </summary>
    public async Task<WorkGraphCommitResult> CommitAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference? expectedParent,
        WorkGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(snapshot);
        expectedParent?.Validate();
        var expectedRevision = expectedParent?.Revision ?? 0;
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireTurnLeaseAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        return await CommitUnderLeaseAsync(
                identity,
                expectedRevision,
                expectedParent,
                snapshot,
                keys,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the exact canonical delta produced by <see cref="WorkGraphApplier"/>. The
    /// mutation ticket is not caller-constructible, and its base snapshot must carry this store
    /// instance's provenance for the exact expected parent reference. Any caller without both
    /// proofs must use the full validation/diff overload above.
    /// </summary>
    internal async Task<WorkGraphCommitResult> CommitValidatedAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference? expectedParent,
        WorkGraphApplyResult applied,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(applied);
        expectedParent?.Validate();
        var mutation = applied.Mutation;
        if (!applied.Accepted
            || !applied.Changed
            || mutation is null
            || !ReferenceEquals(applied.Snapshot, mutation.Snapshot))
        {
            throw new InvalidDataException(
                "A validated work-graph commit requires the exact accepted mutation ticket.");
        }

        ValidateMutationProvenance(expectedParent, mutation);
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireTurnLeaseAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        return await CommitValidatedUnderLeaseAsync(
                identity,
                expectedParent,
                mutation,
                keys,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkGraphCommitResult> CommitUnderLeaseAsync(
        TurnIdentity identity,
        long expectedRevision,
        CommittedWorkGraphReference? expectedParent,
        WorkGraphSnapshot snapshot,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        if ((expectedRevision == 0) != (expectedParent is null)
            || expectedParent is not null && expectedParent.Revision != expectedRevision)
        {
            throw new InvalidDataException(
                "A work-graph delta must bind the exact preceding revision.");
        }

        if (expectedRevision == long.MaxValue || snapshot.Revision != expectedRevision + 1)
        {
            throw new InvalidDataException(
                "A committed work graph must advance the expected revision by exactly one.");
        }

        WorkGraphSnapshot? parentSnapshot = null;
        if (expectedParent is not null)
        {
            if (IsCheckpointRevision(snapshot.Revision))
            {
                _ = await ReadStoredDeltaUnderLeaseAsync(
                        identity,
                        expectedParent,
                        keys,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "The exact parent work-graph reference is not committed.");
            }
            else
            {
                Interlocked.Increment(ref _parentSnapshotReconstructions);
                parentSnapshot = (await ReadUnderLeaseAsync(
                        identity,
                        expectedParent,
                        keys,
                        cancellationToken).ConfigureAwait(false))?.Snapshot
                    ?? throw new InvalidDataException(
                        "The exact parent work-graph reference is not committed.");
            }
        }

        Interlocked.Increment(ref _fullSnapshotCloneValidationPasses);
        var acceptedSnapshot = CloneAndValidate(snapshot);
        Interlocked.Increment(ref _fullSnapshotDiffPasses);
        var stored = ToStoredDelta(
            identity,
            expectedRevision,
            expectedParent,
            parentSnapshot,
            acceptedSnapshot);
        return await PersistCandidateUnderLeaseAsync(
                identity,
                acceptedSnapshot,
                stored,
                keys,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkGraphCommitResult> CommitValidatedUnderLeaseAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference? expectedParent,
        WorkGraphApplier.ValidatedMutation mutation,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var expectedRevision = expectedParent?.Revision ?? 0;
        if ((expectedRevision == 0) != (expectedParent is null)
            || expectedRevision == long.MaxValue
            || mutation.BaseSnapshot.Revision != expectedRevision
            || mutation.Snapshot.Revision != expectedRevision + 1)
        {
            throw new InvalidDataException(
                "A validated work-graph mutation crossed its exact parent revision.");
        }

        if (expectedParent is not null)
        {
            _ = await ReadStoredDeltaUnderLeaseAsync(
                    identity,
                    expectedParent,
                    keys,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The exact parent work-graph reference is not committed.");
        }

        var analysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            mutation.Snapshot,
            out var analysisCacheHit);
        if (!analysisCacheHit || !analysis.IsValid)
        {
            throw new InvalidDataException(
                "The validated work-graph mutation lost its exact cached validation proof.");
        }

        var isCheckpoint = IsCheckpointRevision(mutation.Snapshot.Revision);
        StoredWorkNode[] upserts;
        if (isCheckpoint)
        {
            upserts = analysis.OrderedNodeIds
                .Select(id => ToStoredNode(analysis.CanonicalNodes[id]))
                .ToArray();
        }
        else
        {
            upserts = mutation.ChangedUpserts
                .Select(ToStoredNode)
                .ToArray();
        }

        if (upserts.Length == 0)
        {
            throw new InvalidDataException(
                "A changed validated work graph must persist at least one canonical upsert.");
        }

        var stored = new StoredWorkGraphDelta(
            FormatVersion,
            identity.StorageKey,
            expectedRevision,
            mutation.Snapshot.Revision,
            isCheckpoint,
            expectedParent is null
                ? null
                : new StoredWorkGraphReference(
                    expectedParent.Revision,
                    expectedParent.RecordDigest),
            upserts,
            []);
        var committed = await PersistCandidateUnderLeaseAsync(
                identity,
                mutation.Snapshot,
                stored,
                keys,
                cancellationToken)
            .ConfigureAwait(false);
        if (committed.Status == WorkGraphCommitStatus.Committed)
        {
            Interlocked.Increment(ref _validatedDeltaCommits);
            if (isCheckpoint)
            {
                Interlocked.Add(ref _validatedCheckpointNodesSerialized, upserts.LongLength);
            }
            else
            {
                Interlocked.Add(ref _validatedDeltaNodesSerialized, upserts.LongLength);
            }
        }

        return committed;
    }

    private async Task<WorkGraphCommitResult> PersistCandidateUnderLeaseAsync(
        TurnIdentity identity,
        WorkGraphSnapshot acceptedSnapshot,
        StoredWorkGraphDelta stored,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var plaintext = CanonicalEvidenceJson.SerializeToUtf8Bytes(stored);
        if (plaintext.Length == 0 || plaintext.Length > MaximumProtectedSnapshotBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException(
                $"A protected work-graph snapshot must not exceed {MaximumProtectedSnapshotBytes} bytes.");
        }

        try
        {
            var digest = keys.HmacHex(EvidenceKeyPurpose.WorkGraphRecord, plaintext);
            var reference = new CommittedWorkGraphReference(acceptedSnapshot.Revision, digest);
            var path = GetCandidatePath(identity, reference);
            if (RegularFileExistsNoFollow(path))
            {
                Interlocked.Increment(ref _newCandidateReconstructions);
                var existing = await ReadUnderLeaseAsync(
                        identity,
                        reference,
                        keys,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "An immutable work-graph candidate disappeared during idempotent lookup.");
                return new WorkGraphCommitResult(
                    WorkGraphCommitStatus.AlreadyRecorded,
                    existing);
            }

            var header = BuildHeader(identity.StorageKey, acceptedSnapshot.Revision, digest, plaintext.Length + 40);
            var protectedPayload = keys.Protect(plaintext, header);
            try
            {
                if (protectedPayload.Length != plaintext.Length + 40)
                {
                    throw new InvalidDataException("The protected work-graph envelope length is invalid.");
                }

                var fileBytes = new byte[header.Length + protectedPayload.Length];
                try
                {
                    header.CopyTo(fileBytes, 0);
                    protectedPayload.CopyTo(fileBytes, header.Length);
                    var created = await WriteImmutableAsync(path, fileBytes).ConfigureAwait(false);
                    if (!created)
                    {
                        Interlocked.Increment(ref _newCandidateReconstructions);
                        var existingAfterCollision = await ReadUnderLeaseAsync(
                                identity,
                                reference,
                                keys,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (existingAfterCollision is null)
                        {
                            throw new InvalidDataException(
                                "An immutable work-graph candidate disappeared during idempotent creation.");
                        }

                        return new WorkGraphCommitResult(
                            WorkGraphCommitStatus.AlreadyRecorded,
                            existingAfterCollision);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(fileBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(header);
                CryptographicOperations.ZeroMemory(protectedPayload);
            }

            var committed = new CommittedWorkGraphSnapshot(
                acceptedSnapshot,
                reference);
            RegisterCommittedSnapshot(committed);
            return new WorkGraphCommitResult(
                WorkGraphCommitStatus.Committed,
                committed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Validates the exact revision and keyed content digest used by
    /// <see cref="CommittedWorkGraphReference"/>. This is the work-graph side of the
    /// State transition writer's committed-reference validation adapter.
    /// </summary>
    public async ValueTask<bool> IsCommittedAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference reference,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reference);
        reference.Validate();
        return await ReadAsync(identity, reference, cancellationToken).ConfigureAwait(false)
            is not null;
    }

    /// <summary>
    /// Removes only candidates that are not reachable from the exact reference selected by the
    /// durable turn journal. The complete selected parent chain is authenticated before deletion,
    /// so a missing/tampered selected record leaves every candidate untouched. Passing null is
    /// valid only for a turn whose durable work-graph revision is still zero.
    /// </summary>
    public async Task<WorkGraphPruneResult> PruneUnreachableAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference? selectedReference,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        selectedReference?.Validate();
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquireTurnLeaseAsync(identity, cancellationToken)
            .ConfigureAwait(false);

        var retained = selectedReference is null
            ? new HashSet<CommittedWorkGraphReference>()
            : await ReadReachableReferencesUnderLeaseAsync(
                    identity,
                    selectedReference,
                    keys,
                    cancellationToken)
                .ConfigureAwait(false);
        var directory = GetCandidatesDirectory(identity);
        var candidates = EnumerateCandidatePathsBounded(
            directory,
            "revision-*.protected",
            cancellationToken);
        var removed = 0;
        long removedBytes = 0;
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseCandidateFileName(Path.GetFileName(path), out var candidate)
                || retained.Contains(candidate))
            {
                continue;
            }

            using var deletion = TryOpenCandidateForDeletionNoFollow(path);
            if (deletion is null)
            {
                continue;
            }

            var updatedRemovedBytes = checked(removedBytes + deletion.Length);
            deletion.Delete();
            removed = checked(removed + 1);
            removedBytes = updatedRemovedBytes;
        }

        return new WorkGraphPruneResult(retained.Count, removed, removedBytes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _keySession?.Dispose();
        _keySession = null;
        _keyGate.Dispose();
    }

    private async Task<CommittedWorkGraphSnapshot?> ReadUnderLeaseAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference expectedReference,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, WorkNode?>(StringComparer.Ordinal);
        var visited = new HashSet<CommittedWorkGraphReference>();
        var current = expectedReference;
        var chainComplete = false;
        for (var depth = 0; depth < MaximumDeltaChainLength; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current))
            {
                throw new InvalidDataException("The work-graph parent chain contains a cycle.");
            }

            var stored = await ReadStoredDeltaUnderLeaseAsync(
                    identity,
                    current,
                    keys,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stored is null)
            {
                return null;
            }

            foreach (var node in stored.UpsertedNodes)
            {
                if (node is null)
                {
                    throw new InvalidDataException("A protected work-graph node cannot be null.");
                }

                resolved.TryAdd(node.Id, FromStoredNode(node));
            }

            foreach (var removedId in stored.RemovedNodeIds)
            {
                if (string.IsNullOrWhiteSpace(removedId))
                {
                    throw new InvalidDataException("A protected work-graph removal identifier is invalid.");
                }

                resolved.TryAdd(removedId, null);
            }

            if (stored.IsCheckpoint || stored.Parent is null)
            {
                chainComplete = true;
                break;
            }

            current = ToReference(stored.Parent);
        }

        if (!chainComplete)
        {
            throw new InvalidDataException(
                $"The work-graph parent chain exceeded {MaximumDeltaChainLength} authenticated records.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        foreach (var pair in resolved.Where(static pair => pair.Value is not null))
        {
            builder.Add(pair.Key, pair.Value!);
        }

        var committed = new CommittedWorkGraphSnapshot(
            CloneAndValidate(new WorkGraphSnapshot(expectedReference.Revision, builder.ToImmutable())),
            expectedReference with { });
        RegisterCommittedSnapshot(committed);
        return committed;
    }

    private void ValidateMutationProvenance(
        CommittedWorkGraphReference? expectedParent,
        WorkGraphApplier.ValidatedMutation mutation)
    {
        if (expectedParent is null)
        {
            if (!ReferenceEquals(mutation.BaseSnapshot, WorkGraphSnapshot.Empty)
                || mutation.BaseSnapshot.Revision != 0
                || mutation.BaseSnapshot.Nodes.Count != 0)
            {
                throw new InvalidDataException(
                    "A root validated work-graph mutation must start from the canonical empty snapshot.");
            }

            return;
        }

        if (!_committedSnapshotProvenance.TryGetValue(
                mutation.BaseSnapshot,
                out var provenance)
            || provenance.Reference != expectedParent)
        {
            throw new InvalidDataException(
                "A validated work-graph mutation is not based on this store's exact committed parent snapshot.");
        }
    }

    private void RegisterCommittedSnapshot(CommittedWorkGraphSnapshot committed)
    {
        var selected = _committedSnapshotProvenance.GetValue(
            committed.Snapshot,
            _ => new CommittedSnapshotProvenance(committed.Reference));
        if (selected.Reference != committed.Reference)
        {
            throw new InvalidDataException(
                "One in-memory work-graph snapshot crossed committed-reference provenance.");
        }
    }

    private static WorkGraphSnapshot CloneAndValidate(WorkGraphSnapshot snapshot)
    {
        if (snapshot.Revision <= 0)
        {
            throw new InvalidDataException("A committed work-graph revision must be positive.");
        }

        if (snapshot.Nodes is null)
        {
            throw new InvalidDataException("A committed work graph must contain an initialized node map.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Nodes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var node = pair.Value ?? throw new InvalidDataException(
                $"Work-graph node '{pair.Key}' cannot be null.");
            builder.Add(
                pair.Key,
                node with
                {
                    DependsOn = Clone(node.DependsOn),
                    EvidenceIds = Clone(node.EvidenceIds)
                });
        }

        var clone = new WorkGraphSnapshot(snapshot.Revision, builder.ToImmutable());
        var knownEvidence = clone.Nodes.Values
            .Where(static node => !node.EvidenceIds.IsDefault)
            .SelectMany(static node => node.EvidenceIds)
            .ToHashSet(StringComparer.Ordinal);
        var validation = WorkGraphApplier.Apply(
            clone,
            new WorkGraphDelta(clone.Revision, ImmutableArray<WorkNode>.Empty),
            knownEvidence);
        if (!validation.Accepted || validation.Changed)
        {
            throw new InvalidDataException(
                "The work-graph snapshot failed structural validation: " +
                string.Join(" ", validation.Errors));
        }

        return clone;
    }

    private static StoredWorkGraphDelta ToStoredDelta(
        TurnIdentity identity,
        long expectedRevision,
        CommittedWorkGraphReference? parent,
        WorkGraphSnapshot? parentSnapshot,
        WorkGraphSnapshot snapshot)
    {
        var isCheckpoint = IsCheckpointRevision(snapshot.Revision);
        if (parent is null && parentSnapshot is not null
            || parent is not null && parentSnapshot is null && !isCheckpoint)
        {
            throw new InvalidDataException(
                "A work-graph delta parent reference and snapshot must be supplied together.");
        }

        var previousNodes = isCheckpoint
            ? ImmutableDictionary<string, WorkNode>.Empty.WithComparers(StringComparer.Ordinal)
            : parentSnapshot?.Nodes
            ?? ImmutableDictionary<string, WorkNode>.Empty.WithComparers(StringComparer.Ordinal);
        var upserts = snapshot.Nodes.Values
            .Where(node => !previousNodes.TryGetValue(node.Id, out var previous)
                           || !WorkNodeEquals(node, previous))
            .OrderBy(static node => node.Id, StringComparer.Ordinal)
            .Select(ToStoredNode)
            .ToArray();
        var removed = previousNodes.Keys
            .Where(id => !snapshot.Nodes.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        return new StoredWorkGraphDelta(
            FormatVersion,
            identity.StorageKey,
            expectedRevision,
            snapshot.Revision,
            isCheckpoint,
            parent is null
                ? null
                : new StoredWorkGraphReference(parent.Revision, parent.RecordDigest),
            upserts,
            isCheckpoint ? [] : removed);
    }

    private async Task<StoredWorkGraphDelta?> ReadStoredDeltaUnderLeaseAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference expectedReference,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var path = GetCandidatePath(identity, expectedReference);
        var fileBytes = await WindowsBoundedFileReader.TryReadExactlyAsync(
                WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(path),
                minimumLength: HeaderBytes + 41,
                maximumLength: HeaderBytes + MaximumProtectedSnapshotBytes + 40,
                invalidTargetMessage: InvalidCandidateBoundaryMessage,
                invalidLengthMessage: "The protected work-graph delta length is invalid.",
                changedWhileReadingMessage: "The protected work-graph delta changed while it was being read.",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (fileBytes is null)
        {
            return null;
        }

        Interlocked.Increment(ref _recordsReadFromDisk);
        try
        {
            var header = fileBytes.AsSpan(0, HeaderBytes);
            var parsed = ParseHeader(header, identity.StorageKey, fileBytes.Length - HeaderBytes);
            if (parsed.Revision != expectedReference.Revision ||
                !FixedTimeHexEquals(parsed.RecordDigest, expectedReference.RecordDigest))
            {
                throw new InvalidDataException(
                    "The protected work-graph candidate does not match the requested committed reference.");
            }

            byte[] plaintext;
            try
            {
                plaintext = keys.Unprotect(fileBytes.AsSpan(HeaderBytes), header);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "The protected work-graph delta failed current-user authentication.",
                    ex);
            }

            try
            {
                if (plaintext.Length == 0 || plaintext.Length > MaximumProtectedSnapshotBytes ||
                    !keys.VerifyHmac(EvidenceKeyPurpose.WorkGraphRecord, plaintext, parsed.RecordDigest))
                {
                    throw new InvalidDataException(
                        "The protected work-graph delta does not match its committed content digest.");
                }

                StoredWorkGraphDelta stored;
                try
                {
                    stored = JsonSerializer.Deserialize<StoredWorkGraphDelta>(plaintext, ReadOptions)
                        ?? throw new InvalidDataException("The protected work-graph delta is empty.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        "The protected work-graph delta payload is malformed.",
                        ex);
                }

                ValidateStoredDelta(identity, expectedReference, stored);
                var canonical = CanonicalEvidenceJson.SerializeToUtf8Bytes(stored);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(plaintext, canonical))
                    {
                        throw new InvalidDataException(
                            "The protected work-graph delta payload is not canonical.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(canonical);
                }

                return stored;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileBytes);
        }
    }

    private static void ValidateStoredDelta(
        TurnIdentity identity,
        CommittedWorkGraphReference expectedReference,
        StoredWorkGraphDelta stored)
    {
        if (stored.FormatVersion != FormatVersion ||
            !string.Equals(stored.TurnStorageKey, identity.StorageKey, StringComparison.Ordinal) ||
            stored.Revision != expectedReference.Revision ||
            stored.ExpectedRevision < 0 ||
            stored.ExpectedRevision == long.MaxValue ||
            stored.Revision != stored.ExpectedRevision + 1 ||
            stored.IsCheckpoint != IsCheckpointRevision(stored.Revision) ||
            stored.UpsertedNodes is null ||
            stored.RemovedNodeIds is null ||
            (stored.Parent is null) != (stored.ExpectedRevision == 0))
        {
            throw new InvalidDataException(
                "The protected work-graph delta crossed its format, turn, revision, or parent boundary.");
        }

        if (stored.Parent is not null)
        {
            var parent = ToReference(stored.Parent);
            parent.Validate();
            if (parent.Revision != stored.ExpectedRevision)
            {
                throw new InvalidDataException(
                    "The protected work-graph delta does not reference its exact preceding revision.");
            }
        }

        if (!stored.UpsertedNodes
                .Select(static node => node?.Id)
                .SequenceEqual(
                    stored.UpsertedNodes
                        .Select(static node => node?.Id)
                        .OrderBy(static id => id, StringComparer.Ordinal),
                    StringComparer.Ordinal)
            || stored.UpsertedNodes.Any(static node => node is null)
            || stored.UpsertedNodes.Select(static node => node!.Id).Distinct(StringComparer.Ordinal).Count()
                != stored.UpsertedNodes.Length
            || !stored.RemovedNodeIds.SequenceEqual(
                stored.RemovedNodeIds.OrderBy(static id => id, StringComparer.Ordinal),
                StringComparer.Ordinal)
            || stored.RemovedNodeIds.Any(string.IsNullOrWhiteSpace)
            || stored.RemovedNodeIds.Distinct(StringComparer.Ordinal).Count()
                != stored.RemovedNodeIds.Length
            || stored.IsCheckpoint && stored.RemovedNodeIds.Length != 0
            || stored.UpsertedNodes.Select(static node => node!.Id)
                .Intersect(stored.RemovedNodeIds, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException(
                "The protected work-graph delta contains unordered, duplicate, or conflicting changes.");
        }
    }

    private static StoredWorkNode ToStoredNode(WorkNode node) =>
        new(
            node.Id,
            node.Objective,
            node.ParentId,
            node.Status,
            node.DependsOn.IsDefault ? null : node.DependsOn.ToArray(),
            node.EvidenceIds.IsDefault ? null : node.EvidenceIds.ToArray(),
            node.SupersededById);

    private static WorkNode FromStoredNode(StoredWorkNode node) =>
        new(
            node.Id,
            node.Objective,
            node.ParentId,
            node.Status,
            node.DependsOn is null ? default : ImmutableArray.CreateRange(node.DependsOn),
            node.EvidenceIds is null ? default : ImmutableArray.CreateRange(node.EvidenceIds),
            node.SupersededById);

    private static bool WorkNodeEquals(WorkNode left, WorkNode right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Objective, right.Objective, StringComparison.Ordinal)
        && string.Equals(left.ParentId, right.ParentId, StringComparison.Ordinal)
        && left.Status == right.Status
        && ImmutableArraysEqual(left.DependsOn, right.DependsOn)
        && ImmutableArraysEqual(left.EvidenceIds, right.EvidenceIds)
        && string.Equals(left.SupersededById, right.SupersededById, StringComparison.Ordinal);

    private static bool ImmutableArraysEqual(
        ImmutableArray<string> left,
        ImmutableArray<string> right) =>
        left.IsDefault == right.IsDefault
        && (left.IsDefault || left.SequenceEqual(right, StringComparer.Ordinal));

    private static CommittedWorkGraphReference ToReference(StoredWorkGraphReference stored) =>
        new(stored.Revision, stored.RecordDigest);

    private static bool IsCheckpointRevision(long revision) =>
        revision > 0 && revision % MaximumDeltaChainLength == 0;

    private static byte[] BuildHeader(
        string turnStorageKey,
        long revision,
        string recordDigest,
        int protectedPayloadLength)
    {
        if (turnStorageKey.Length != StorageKeyHexChars || recordDigest.Length != DigestBytes * 2 ||
            protectedPayloadLength <= 40 ||
            protectedPayloadLength > MaximumProtectedSnapshotBytes + 40)
        {
            throw new InvalidDataException("The protected work-graph header values are invalid.");
        }

        var header = new byte[HeaderBytes];
        var offset = 0;
        FileMagic.CopyTo(header, offset);
        offset += FileMagic.Length;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset, sizeof(int)), FormatVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(offset, sizeof(long)), revision);
        offset += sizeof(long);
        Convert.FromHexString(turnStorageKey).CopyTo(header, offset);
        offset += StorageKeyDigestBytes;
        Convert.FromHexString(recordDigest).CopyTo(header, offset);
        offset += DigestBytes;
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(offset, sizeof(int)),
            protectedPayloadLength);
        return header;
    }

    private static ParsedHeader ParseHeader(
        ReadOnlySpan<byte> header,
        string expectedTurnStorageKey,
        int actualProtectedPayloadLength)
    {
        if (header.Length != HeaderBytes || !header[..FileMagic.Length].SequenceEqual(FileMagic))
        {
            throw new InvalidDataException("The protected work-graph snapshot header is invalid.");
        }

        var offset = FileMagic.Length;
        var version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        var revision = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        var storageKey = Convert.ToHexString(header.Slice(offset, StorageKeyDigestBytes)).ToLowerInvariant();
        offset += StorageKeyDigestBytes;
        var digest = Convert.ToHexString(header.Slice(offset, DigestBytes)).ToLowerInvariant();
        offset += DigestBytes;
        var declaredPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            header.Slice(offset, sizeof(int)));
        if (version != FormatVersion || revision <= 0 ||
            !FixedTimeHexEquals(storageKey, expectedTurnStorageKey) ||
            declaredPayloadLength != actualProtectedPayloadLength ||
            declaredPayloadLength <= 40 ||
            declaredPayloadLength > MaximumProtectedSnapshotBytes + 40)
        {
            throw new InvalidDataException(
                "The protected work-graph snapshot crossed its format, turn, revision, or length boundary.");
        }

        return new ParsedHeader(revision, digest);
    }

    private async Task<EvidenceKeySession> GetKeySessionAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _keySession);
        if (current is not null)
        {
            return current;
        }

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _keySession;
            if (current is null)
            {
                var keyDirectories = new[]
                {
                    _rootDirectory,
                    Path.Combine(_rootDirectory, "turns")
                };
                var boundaries = new List<SafeFileHandle>(keyDirectories.Length);
                try
                {
                    foreach (var directory in keyDirectories)
                    {
                        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                            directory,
                            InvalidDirectoryBoundaryMessage);
                        boundaries.Add(OpenPinnedRegularDirectory(directory));
                    }

                    current = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _keySession, current);
                }
                finally
                {
                    DisposeDirectoryBoundaries(boundaries);
                }
            }

            return current;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private async Task<TurnLease> AcquireTurnLeaseAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken)
    {
        var storageKeyDirectory = Path.Combine(_rootDirectory, "turns", identity.StorageKey);
        var turnDirectory = GetTurnDirectory(identity);
        var directories = new[]
        {
            _rootDirectory,
            Path.Combine(_rootDirectory, "turns"),
            storageKeyDirectory,
            turnDirectory,
            GetCandidatesDirectory(identity)
        };
        var boundaries = new List<SafeFileHandle>(directories.Length);
        try
        {
            foreach (var directory in directories)
            {
                WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                    directory,
                    InvalidDirectoryBoundaryMessage);
                boundaries.Add(OpenPinnedRegularDirectory(directory));
            }

            var path = Path.Combine(turnDirectory, WriterLockFileName);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        writeThrough: true,
                        invalidMessage: InvalidLeaseBoundaryMessage);
                    return new TurnLease(stream, boundaries);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new InvalidDataException(InvalidLeaseBoundaryMessage, ex);
                }
            }
        }
        catch
        {
            DisposeDirectoryBoundaries(boundaries);
            throw;
        }
    }

    private static async Task<bool> WriteImmutableAsync(string finalPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The work-graph snapshot path has no directory.");
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            directory,
            InvalidDirectoryBoundaryMessage);
        string? temporaryPath = null;
        FileStream? temporaryStream = null;
        for (var attempt = 0; attempt < MaximumTemporaryFileCreateAttempts; attempt++)
        {
            temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.work-graph.tmp");
            try
            {
                temporaryStream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    writeThrough: true,
                    invalidMessage: InvalidCandidateBoundaryMessage);
                break;
            }
            catch (IOException ex) when (IsAlreadyExists(ex))
            {
                temporaryPath = null;
            }
        }

        if (temporaryStream is null || temporaryPath is null)
        {
            throw new IOException(
                $"A unique work-graph temporary file could not be created after " +
                $"{MaximumTemporaryFileCreateAttempts} attempts.");
        }

        try
        {
            await using (temporaryStream)
            {
                await temporaryStream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await temporaryStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                temporaryStream.Flush(flushToDisk: true);
            }

            try
            {
                WindowsOrchestrationFileBoundary.MoveRegularFile(
                    temporaryPath,
                    finalPath,
                    replaceExisting: false,
                    invalidMessage: InvalidCandidateBoundaryMessage);
                return true;
            }
            catch (IOException)
            {
                if (RegularFileExistsNoFollow(finalPath))
                {
                    return false;
                }

                throw;
            }
        }
        finally
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                InvalidCandidateBoundaryMessage);
        }
    }

    private async Task<CommittedWorkGraphReference> ResolveUnambiguousParentUnderLeaseAsync(
        TurnIdentity identity,
        long expectedRevision,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var directory = GetCandidatesDirectory(identity);
        CommittedWorkGraphReference? selected = null;
        foreach (var path in EnumerateCandidatePathsBounded(
                     directory,
                     $"revision-{expectedRevision:D20}.*.protected",
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseCandidateFileName(Path.GetFileName(path), out var candidate)
                || candidate.Revision != expectedRevision)
            {
                continue;
            }

            if (selected is not null && selected != candidate)
            {
                throw new InvalidOperationException(
                    "The preceding work-graph revision has multiple immutable candidates; " +
                    "the caller must supply the exact journal-selected parent reference.");
            }

            selected = candidate;
        }

        if (selected is null
            || await ReadStoredDeltaUnderLeaseAsync(
                    identity,
                    selected,
                    keys,
                    cancellationToken)
                .ConfigureAwait(false) is null)
        {
            throw new InvalidDataException("The preceding work-graph revision is not committed.");
        }

        return selected;
    }

    private async Task<HashSet<CommittedWorkGraphReference>> ReadReachableReferencesUnderLeaseAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference selectedReference,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var retained = new HashSet<CommittedWorkGraphReference>();
        var current = selectedReference;
        for (var depth = 0; depth < MaximumDeltaChainLength; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!retained.Add(current))
            {
                throw new InvalidDataException("The selected work-graph parent chain contains a cycle.");
            }

            var stored = await ReadStoredDeltaUnderLeaseAsync(
                    identity,
                    current,
                    keys,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The selected work-graph parent chain is incomplete.");
            // A checkpoint contains the complete graph at its revision. Its authenticated
            // parent reference remains provenance, but reconstruction deliberately stops here;
            // older delta files can therefore be reclaimed without walking the entire turn on
            // every prune. This keeps pruning bounded by MaximumDeltaChainLength while leaving
            // advancing revision count unbounded.
            if (stored.IsCheckpoint || stored.Parent is null)
            {
                return retained;
            }

            current = ToReference(stored.Parent);
        }

        throw new InvalidDataException(
            $"The selected work-graph parent chain exceeded {MaximumDeltaChainLength} authenticated records.");
    }

    private static string[] EnumerateCandidatePathsBounded(
        string directory,
        string searchPattern,
        CancellationToken cancellationToken)
    {
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            directory,
            InvalidDirectoryBoundaryMessage);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var paths = MaterializeCandidateEnumerationBounded(
                Directory.EnumerateFileSystemEntries(
                    fullDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly),
                cancellationToken)
            .Where(path => FileSystemName.MatchesSimpleExpression(
                searchPattern,
                Path.GetFileName(path),
                ignoreCase: false))
            .ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            var fullPath = Path.GetFullPath(paths[index]);
            var parent = Path.GetDirectoryName(fullPath);
            if (parent is null
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(parent),
                    fullDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A work-graph candidate escaped its exact final directory boundary.");
            }

            paths[index] = fullPath;
        }

        return paths;
    }

    internal static string[] MaterializeCandidateEnumerationBounded(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var bounded = new List<string>(Math.Min(MaximumCandidateEnumerationCount, 256));
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bounded.Count >= MaximumCandidateEnumerationCount)
            {
                throw new InvalidDataException(
                    $"A work-graph candidate enumeration exceeded " +
                    $"{MaximumCandidateEnumerationCount} entries.");
            }

            bounded.Add(path);
        }

        return bounded.ToArray();
    }

    private static SafeFileHandle OpenPinnedRegularDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var handle = CreateFileW(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(fullPath),
            FileReadAttributes,
            FileShare.ReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new InvalidDataException(
                InvalidDirectoryBoundaryMessage,
                new Win32Exception(error));
        }

        try
        {
            var attributes = File.GetAttributes(handle);
            if (GetFileType(handle) != FileTypeDisk
                || (attributes & FileAttributes.Directory) == 0
                || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(InvalidDirectoryBoundaryMessage);
            }

            return handle;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            handle.Dispose();
            throw new InvalidDataException(InvalidDirectoryBoundaryMessage, ex);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void DisposeDirectoryBoundaries(List<SafeFileHandle> boundaries)
    {
        for (var index = boundaries.Count - 1; index >= 0; index--)
        {
            boundaries[index].Dispose();
        }

        boundaries.Clear();
    }

    private static bool RegularFileExistsNoFollow(string path)
    {
        try
        {
            using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                writeThrough: false,
                invalidMessage: InvalidCandidateBoundaryMessage);
            return true;
        }
        catch (IOException ex) when (IsMissing(ex))
        {
            return false;
        }
    }

    private static PendingCandidateDeletion? TryOpenCandidateForDeletionNoFollow(string path)
    {
        var fullPath = Path.GetFullPath(path);
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(InvalidDirectoryBoundaryMessage),
            InvalidDirectoryBoundaryMessage);
        var handle = CreateFileW(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(fullPath),
            DeleteAccess | FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw new InvalidDataException(
                InvalidCandidateBoundaryMessage,
                new Win32Exception(error));
        }

        try
        {
            ValidateRegularCandidateHandle(handle);
            return new PendingCandidateDeletion(handle, RandomAccess.GetLength(handle));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            handle.Dispose();
            throw new InvalidDataException(InvalidCandidateBoundaryMessage, ex);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ValidateRegularCandidateHandle(SafeFileHandle handle)
    {
        var attributes = File.GetAttributes(handle);
        if (GetFileType(handle) != FileTypeDisk
            || (attributes & (FileAttributes.Device
                              | FileAttributes.Directory
                              | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(InvalidCandidateBoundaryMessage);
        }
    }

    private string GetTurnDirectory(TurnIdentity identity) =>
        Path.Combine(_rootDirectory, "turns", identity.StorageKey, "work-graph");

    private string GetCandidatesDirectory(TurnIdentity identity) =>
        Path.Combine(GetTurnDirectory(identity), "candidates");

    private string GetCandidatePath(
        TurnIdentity identity,
        CommittedWorkGraphReference reference) =>
        Path.Combine(
            GetCandidatesDirectory(identity),
            $"revision-{reference.Revision:D20}.{reference.RecordDigest}.protected");

    private static bool TryParseCandidateFileName(
        string fileName,
        out CommittedWorkGraphReference reference)
    {
        const string prefix = "revision-";
        const string suffix = ".protected";
        reference = null!;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)
            || fileName.Length != prefix.Length + 20 + 1 + DigestBytes * 2 + suffix.Length)
        {
            return false;
        }

        var revisionText = fileName.AsSpan(prefix.Length, 20);
        var digestStart = prefix.Length + 21;
        var digest = fileName.Substring(digestStart, DigestBytes * 2);
        if (fileName[prefix.Length + 20] != '.'
            || !long.TryParse(
                revisionText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var revision)
            || revision <= 0
            || digest.Any(static character => !Uri.IsHexDigit(character))
            || !string.Equals(digest, digest.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return false;
        }

        reference = new CommittedWorkGraphReference(revision, digest);
        return true;
    }

    private static bool IsSharingViolation(IOException exception) =>
        GetNativeError(exception) is 32 or 33;

    private static bool IsMissing(IOException exception) =>
        GetNativeError(exception) is ErrorFileNotFound or ErrorPathNotFound;

    private static bool IsAlreadyExists(IOException exception) =>
        GetNativeError(exception) is ErrorFileExists or ErrorAlreadyExists;

    private static int GetNativeError(IOException exception) =>
        exception.InnerException is Win32Exception win32
            ? win32.NativeErrorCode
            : exception.HResult & 0xffff;

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
            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static ImmutableArray<string> Clone(ImmutableArray<string> values)
    {
        if (values.IsDefault)
        {
            return default;
        }

        return ImmutableArray.CreateRange(values);
    }

    private sealed class TurnLease(
        FileStream stream,
        List<SafeFileHandle> directoryBoundaries) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                DisposeDirectoryBoundaries(directoryBoundaries);
            }
        }
    }

    private sealed class PendingCandidateDeletion(
        SafeFileHandle handle,
        long length) : IDisposable
    {
        private bool _deleted;

        public long Length { get; } = length;

        public void Delete()
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            if (_deleted)
            {
                throw new InvalidOperationException(
                    "The exact work-graph candidate has already been deleted.");
            }

            var disposition = new FileDispositionInformation { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    handle,
                    FileInfoByHandleClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInformation>()))
            {
                throw new InvalidDataException(
                    "The exact regular work-graph candidate could not be deleted safely.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            _deleted = true;
        }

        public void Dispose() => handle.Dispose();
    }

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
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
    private static extern uint GetFileType(SafeFileHandle fileHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    private sealed record StoredWorkGraphDelta(
        int FormatVersion,
        string TurnStorageKey,
        long ExpectedRevision,
        long Revision,
        bool IsCheckpoint,
        StoredWorkGraphReference? Parent,
        StoredWorkNode[] UpsertedNodes,
        string[] RemovedNodeIds);

    private sealed record StoredWorkGraphReference(
        long Revision,
        string RecordDigest);

    private sealed record StoredWorkNode(
        string Id,
        string Objective,
        string? ParentId,
        WorkNodeStatus Status,
        string[]? DependsOn,
        string[]? EvidenceIds,
        string? SupersededById);

    private sealed record CommittedSnapshotProvenance(
        CommittedWorkGraphReference Reference);

    private readonly record struct ParsedHeader(long Revision, string RecordDigest);
}
