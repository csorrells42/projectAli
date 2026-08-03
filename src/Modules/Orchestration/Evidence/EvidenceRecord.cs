using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration.Evidence;

internal enum EvidenceLedgerCommitBoundary
{
    ProtectedPayloadFlushed
}

internal sealed record EvidenceMetadataSeed(
    string EvidenceId,
    string TurnBindingDigest,
    string? WorkItemIdDigest,
    string CallIdDigest,
    string ToolNameDigest,
    string CapabilityGroupDigest,
    string ProviderIdDigest,
    string RegistryRevisionDigest,
    string EffectKind,
    string ArgumentsDigest,
    string TargetDigest,
    string NormalizedResultDigest,
    InvocationStatus InvocationStatus,
    DomainOutcome DomainOutcome,
    string? FailureCodeDigest,
    string StableOutcomeCodeDigest,
    string ResultDigest,
    string NoEffectFingerprint,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset RecordedAtUtc,
    EvidenceArtifactReference[] Artifacts,
    EvidencePermissionMetadata Permission,
    string PermissionReceiptDigest,
    EvidenceSourceProjection Source);

internal sealed record EvidenceUnsignedRecord(
    string EvidenceId,
    string TurnBindingDigest,
    string? WorkItemIdDigest,
    string CallIdDigest,
    string ToolNameDigest,
    string CapabilityGroupDigest,
    string ProviderIdDigest,
    string RegistryRevisionDigest,
    string EffectKind,
    string ArgumentsDigest,
    string TargetDigest,
    string NormalizedResultDigest,
    InvocationStatus InvocationStatus,
    DomainOutcome DomainOutcome,
    string? FailureCodeDigest,
    string StableOutcomeCodeDigest,
    string ResultDigest,
    string NoEffectFingerprint,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset RecordedAtUtc,
    EvidenceArtifactReference[] Artifacts,
    EvidencePermissionMetadata Permission,
    string PermissionReceiptDigest,
    EvidenceSourceProjection Source,
    string ProtectedPayloadReference,
    string ProtectedPayloadDigest,
    string MetadataDigest);

internal sealed record CommittedProtectedEvidence(
    EvidenceCursorRecord Cursor,
    ProtectedEvidenceContent ProtectedContent);

public sealed class EvidenceLedger
{
    private const int MaximumCachedTurnJournals = 64;
    internal const int MaximumHotEvidenceRecordsPerTurn = 256;
    internal const int MaximumCommittedEvidenceLookupBatchSize = 256;
    private const int MaximumArtifactCount = 256;
    private const int MaximumEvidenceComponentBytes = 4 * 1024 * 1024;
    private const int ProtectedPayloadJsonOverheadReserve = 4096;
    private static readonly HashSet<string> AllowedEffectKinds = new(
        ["none", "read", "create", "update", "delete", "execute", "external", "mixed"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedArtifactKinds = new(
        ["unknown", "file", "directory", "process", "service", "uri", "symbol", "project"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedPermissionDecisions = new(
        ["unknown", "not-required", "approved-once", "approved-standing", "approved-policy", "denied", "policy-blocked"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedPermissionScopes = new(
        ["unknown", "none", "once", "exact-arguments", "tool", "policy"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedSourceKinds = new(
        ["tool", "file", "web", "mcp", "process", "runtime", "user"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedTrustBoundaries = new(
        ["unknown", "trusted-local", "untrusted-local", "untrusted-external", "user-provided"],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _rootDirectory;
    private readonly WindowsCurrentUserEvidenceProtector _protector;
    private readonly ProtectedEvidencePayloadStore _payloadStore;
    private readonly Action<EvidenceLedgerCommitBoundary>? _faultInjector;
    private readonly Action<EvidenceJournalCommitBoundary>? _journalFaultInjector;
    private readonly Action<int>? _journalTailReadObserver;
    private readonly Func<bool>? _journalStampUnavailable;
    private readonly Action<int>? _journalIndexedRecordReadObserver;
    private readonly Action? _exactIndexMaintenanceFaultInjector;
    private readonly object _journalCacheSync = new();
    private readonly Dictionary<string, CachedTurnJournal> _journalCache =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _journalLru = [];
    private readonly Dictionary<string, StableEvidenceTurnIndex> _stableEvidenceTurns =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StableEvidenceTurnGate> _stableEvidenceGates =
        new(StringComparer.Ordinal);

    private sealed record CachedTurnJournal(
        EvidenceJournal Journal,
        LinkedListNode<string> Node,
        string StableTurnKey,
        StableEvidenceTurnIndex StableEvidence);

    private sealed class StableEvidenceTurnIndex
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, HotEvidenceRecord> _records =
            new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = [];
        private EvidenceJournalVerificationStamp? _verificationStamp;

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _records.Count;
                }
            }
        }

        public bool TryGetVerified(
            string evidenceId,
            EvidenceJournalVerificationStamp? currentStamp,
            out EvidenceCursorRecord? record)
        {
            lock (_sync)
            {
                if (currentStamp is null
                    || _verificationStamp != currentStamp
                    || !_records.TryGetValue(evidenceId, out var hot))
                {
                    record = null;
                    return false;
                }

                _lru.Remove(hot.Node);
                _lru.AddFirst(hot.Node);
                record = hot.Record;
                return true;
            }
        }

        public void SetVerified(
            EvidenceCursorRecord record,
            EvidenceJournalVerificationStamp? verificationStamp)
        {
            ArgumentNullException.ThrowIfNull(record);
            lock (_sync)
            {
                _verificationStamp = verificationStamp;
                UpsertUnderLock(record);
            }
        }

        public void MarkVerified(EvidenceJournalVerificationStamp? verificationStamp)
        {
            lock (_sync)
            {
                _verificationStamp = verificationStamp;
            }
        }

        public void ReplaceVerified(
            IEnumerable<EvidenceCursorRecord> records,
            EvidenceJournalVerificationStamp? verificationStamp)
        {
            ArgumentNullException.ThrowIfNull(records);
            lock (_sync)
            {
                _records.Clear();
                _lru.Clear();
                _verificationStamp = verificationStamp;
                foreach (var record in records)
                {
                    UpsertUnderLock(record);
                }
            }
        }

        private void UpsertUnderLock(EvidenceCursorRecord record)
        {
            var evidenceId = record.Evidence.EvidenceId;
            if (_records.TryGetValue(evidenceId, out var existing))
            {
                _lru.Remove(existing.Node);
                _records.Remove(evidenceId);
            }

            var node = _lru.AddFirst(evidenceId);
            _records.Add(evidenceId, new HotEvidenceRecord(record, node));
            while (_records.Count > MaximumHotEvidenceRecordsPerTurn)
            {
                var oldest = _lru.Last
                    ?? throw new InvalidOperationException(
                        "The bounded evidence-record cache is inconsistent.");
                _lru.RemoveLast();
                _records.Remove(oldest.Value);
            }
        }

        private sealed record HotEvidenceRecord(
            EvidenceCursorRecord Record,
            LinkedListNode<string> Node);
    }

    private sealed class StableEvidenceTurnGate
    {
        private readonly object _sync = new();
        private int _references;
        private bool _retired;

        public SemaphoreSlim InvocationGate { get; } = new(1, 1);

        public bool TryRetain()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }

                checked
                {
                    _references++;
                }

                return true;
            }
        }

        public bool ReleaseAndRetireIfUnused()
        {
            lock (_sync)
            {
                if (_references <= 0)
                {
                    throw new InvalidOperationException(
                        "The stable evidence turn gate reference count is inconsistent.");
                }

                _references--;
                if (_references != 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private sealed class StableEvidenceGateLease(
        EvidenceLedger owner,
        string stableTurnKey,
        StableEvidenceTurnGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            gate.InvocationGate.Release();
            owner.ReleaseStableEvidenceGate(stableTurnKey, gate);
        }
    }

    public EvidenceLedger(string rootDirectory, string assistantProfileBinding)
        : this(rootDirectory, assistantProfileBinding, null, null, null, null)
    {
    }

    internal EvidenceLedger(
        string rootDirectory,
        string assistantProfileBinding,
        Action<EvidenceLedgerCommitBoundary>? faultInjector,
        Action<EvidenceJournalCommitBoundary>? journalFaultInjector,
        Action<int>? journalTailReadObserver,
        Func<bool>? journalStampUnavailable = null,
        Action<int>? journalIndexedRecordReadObserver = null,
        Action? exactIndexMaintenanceFaultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _protector = new WindowsCurrentUserEvidenceProtector(
            _rootDirectory,
            assistantProfileBinding);
        _payloadStore = new ProtectedEvidencePayloadStore(
            _rootDirectory,
            assistantProfileBinding);
        _faultInjector = faultInjector;
        _journalFaultInjector = journalFaultInjector;
        _journalTailReadObserver = journalTailReadObserver;
        _journalStampUnavailable = journalStampUnavailable;
        _journalIndexedRecordReadObserver = journalIndexedRecordReadObserver;
        _exactIndexMaintenanceFaultInjector = exactIndexMaintenanceFaultInjector;
    }

    internal async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        // Establish the protected current-user key before any turn-scoped journal
        // directory is materialized. Missing-key detection must remain fail-closed
        // once durable turn data exists, but a genuinely fresh store must be able to
        // create its first key before ReplayAsync creates that first directory.
        using var keys = await _protector.OpenSessionAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EvidenceCursorRecord> AppendAsync(
        TurnIdentity identity,
        EvidenceDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(draft);
        var snapshot = SnapshotAndValidate(draft);
        using var stableEvidenceLease = snapshot.EvidenceId is null
            ? null
            : await AcquireStableEvidenceGateAsync(identity.StorageKey, cancellationToken)
                .ConfigureAwait(false);
        if (snapshot.EvidenceId is not null)
        {
            var existing = await FindEvidenceRecordAsync(
                    identity,
                    snapshot.EvidenceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                var protectedExisting = await ReadProtectedRecordAsync(
                    identity,
                    existing.Evidence,
                    cancellationToken).ConfigureAwait(false);
                RequireIdempotentEvidenceMatches(existing, protectedExisting, snapshot);
                return existing;
            }
        }

        using var sensitiveBuffers = new SensitiveBufferScope();
        long payloadComponentBytes = 0;
        var argumentsBytes = SerializePayloadComponent(
            sensitiveBuffers,
            snapshot.Arguments,
            ref payloadComponentBytes,
            "arguments");
        var resultBytes = SerializePayloadComponent(
            sensitiveBuffers,
            snapshot.Result,
            ref payloadComponentBytes,
            "result");
        var normalizedTargetBytes = SerializePayloadComponent(
            sensitiveBuffers,
            snapshot.NormalizedTarget,
            ref payloadComponentBytes,
            "normalized target");
        var normalizedResultBytes = SerializePayloadComponent(
            sensitiveBuffers,
            snapshot.NormalizedEffectResult,
            ref payloadComponentBytes,
            "normalized result");
        var permissionBytes = SerializePayloadComponent(
            sensitiveBuffers,
            snapshot.ProtectedPermissionReceipt,
            ref payloadComponentBytes,
            "permission receipt");
        var provenanceBytes = SerializePayloadComponent(
            sensitiveBuffers,
            snapshot.ProtectedProvenance,
            ref payloadComponentBytes,
            "provenance");
        var protectedIdentity = new ProtectedEvidenceIdentity(
            snapshot.CallId,
            snapshot.WorkItemId,
            snapshot.ToolName,
            snapshot.CapabilityGroup,
            snapshot.ProviderId,
            snapshot.RegistryRevision,
            snapshot.StableOutcomeCode,
            snapshot.Outcome.FailureCode,
            snapshot.Artifacts,
            snapshot.Source);
        var protectedIdentityBytes = SerializePayloadComponent(
            sensitiveBuffers,
            protectedIdentity,
            ref payloadComponentBytes,
            "identity");
        var protectedPayloadBytes = sensitiveBuffers.Track(
            CanonicalEvidenceJson.SerializeToUtf8Bytes(
            new ProtectedEvidencePayload(
                protectedIdentity,
                snapshot.Arguments,
                snapshot.Result,
                snapshot.NormalizedTarget,
                snapshot.NormalizedEffectResult,
                snapshot.ProtectedPermissionReceipt,
                snapshot.ProtectedProvenance),
            ProtectedEvidencePayloadStore.MaximumPlaintextBytes));
        var turnBytes = sensitiveBuffers.Track(
            CanonicalEvidenceJson.SerializeToUtf8Bytes(new
            {
                identity.UserId,
                identity.ConversationId,
                identity.AssistantMessageId
            }, 4096));
        try
        {
            using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            var evidenceId = snapshot.EvidenceId ?? Guid.NewGuid().ToString("N");
            var recordedAtUtc = DateTimeOffset.UtcNow;
            var turnBindingDigest = keys.HmacHex(EvidenceKeyPurpose.TurnBinding, turnBytes);
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
            var workItemIdDigest = snapshot.WorkItemId is null
                ? null
                : IdentifierDigest(keys, "work-item-id", snapshot.WorkItemId);
            var callIdDigest = IdentifierDigest(keys, "call-id", snapshot.CallId);
            var toolNameDigest = IdentifierDigest(keys, "tool-name", snapshot.ToolName);
            var capabilityGroupDigest = IdentifierDigest(
                keys,
                "capability-group",
                snapshot.CapabilityGroup);
            var providerIdDigest = IdentifierDigest(keys, "provider-id", snapshot.ProviderId);
            var registryRevisionDigest = IdentifierDigest(
                keys,
                "registry-revision",
                snapshot.RegistryRevision);
            var argumentsDigest = keys.HmacHex(EvidenceKeyPurpose.Arguments, argumentsBytes);
            var targetDigest = keys.HmacHex(
                EvidenceKeyPurpose.NormalizedTarget,
                normalizedTargetBytes);
            var normalizedResultDigest = keys.HmacHex(
                EvidenceKeyPurpose.NormalizedResult,
                normalizedResultBytes);
            var resultDigest = keys.HmacHex(EvidenceKeyPurpose.Result, resultBytes);
            var permissionReceiptDigest = keys.HmacHex(
                EvidenceKeyPurpose.PermissionReceipt,
                permissionBytes);
            var stableOutcomeCodeDigest = IdentifierDigest(
                keys,
                "stable-outcome-code",
                snapshot.StableOutcomeCode);
            var failureCodeDigest = snapshot.Outcome.FailureCode is null
                ? null
                : IdentifierDigest(keys, "failure-code", snapshot.Outcome.FailureCode);
            var projectedArtifacts = snapshot.Artifacts
                .Select(artifact => new EvidenceArtifactReference(
                    IdentifierDigest(keys, "artifact-id", artifact.ArtifactId),
                    artifact.Kind,
                    artifact.BeforeVersion is null
                        ? null
                        : IdentifierDigest(keys, "artifact-before-version", artifact.BeforeVersion),
                    artifact.AfterVersion is null
                        ? null
                        : IdentifierDigest(keys, "artifact-after-version", artifact.AfterVersion)))
                .ToArray();
            var projectedSource = new EvidenceSourceProjection(
                snapshot.Source.Kind,
                IdentifierDigest(keys, "source-provider-id", snapshot.Source.ProviderId),
                snapshot.Source.TrustBoundary,
                snapshot.Source.FreshAtUtc,
                IdentifierDigest(keys, "source-state-revision", snapshot.Source.StateRevision));
            var noEffectBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(new
            {
                snapshot.EffectKind,
                targetDigest,
                normalizedResultDigest,
                registryRevisionDigest,
                artifacts = projectedArtifacts.Select(artifact => new
                {
                    artifact.ArtifactIdDigest,
                    artifact.Kind,
                    artifact.BeforeVersionDigest,
                    artifact.AfterVersionDigest
                }).ToArray(),
                snapshot.Outcome.InvocationStatus,
                snapshot.Outcome.DomainOutcome,
                stableOutcomeCodeDigest,
                permission = snapshot.Permission,
                sourceStateRevisionDigest = projectedSource.StateRevisionDigest
            });
            string noEffectFingerprint;
            try
            {
                noEffectFingerprint = keys.HmacHex(EvidenceKeyPurpose.NoEffect, noEffectBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(noEffectBytes);
            }

            var metadata = new EvidenceMetadataSeed(
                evidenceId,
                turnBindingDigest,
                workItemIdDigest,
                callIdDigest,
                toolNameDigest,
                capabilityGroupDigest,
                providerIdDigest,
                registryRevisionDigest,
                snapshot.EffectKind,
                argumentsDigest,
                targetDigest,
                normalizedResultDigest,
                snapshot.Outcome.InvocationStatus,
                snapshot.Outcome.DomainOutcome,
                failureCodeDigest,
                stableOutcomeCodeDigest,
                resultDigest,
                noEffectFingerprint,
                snapshot.StartedAtUtc,
                snapshot.CompletedAtUtc,
                recordedAtUtc,
                projectedArtifacts,
                snapshot.Permission,
                permissionReceiptDigest,
                projectedSource);
            var metadataBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(metadata);
            try
            {
                var metadataDigest = Sha256Hex(metadataBytes);
                var protectedReference = await _payloadStore.WriteAsync(
                    turnStorageKey,
                    evidenceId,
                    capabilityGroupDigest,
                    toolNameDigest,
                    providerIdDigest,
                    metadataDigest,
                    protectedPayloadBytes,
                    keys,
                    cancellationToken).ConfigureAwait(false);
                _faultInjector?.Invoke(EvidenceLedgerCommitBoundary.ProtectedPayloadFlushed);

                var unsigned = CreateUnsignedRecord(
                    metadata,
                    protectedReference.Reference,
                    protectedReference.Digest,
                    metadataDigest);
                var projectionBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned);
                try
                {
                    var projectionDigest = Sha256Hex(projectionBytes);
                    var recordMac = keys.HmacHex(EvidenceKeyPurpose.RecordMac, projectionBytes);
                    var record = CreateStoredRecord(unsigned, projectionDigest, recordMac);
                    var cachedTurn = GetJournal(
                        turnStorageKey,
                        identity.StorageKey);
                    cachedTurn.StableEvidence.MarkVerified(verificationStamp: null);
                    var append = await cachedTurn.Journal.AppendAsync(
                        record,
                        existing => ValidateRecord(turnBindingDigest, existing, keys),
                        head => SignJournalHead(head, keys),
                        head => ValidateJournalHead(turnStorageKey, head, keys),
                        bytes => keys.HmacHex(EvidenceKeyPurpose.EvidenceExactIndex, bytes),
                        cancellationToken).ConfigureAwait(false);
                    var publicStored = ToPublicCursor(append.Record);
                    cachedTurn.StableEvidence.SetVerified(
                        publicStored,
                        append.VerificationStamp);

                    if (append.Status == EvidenceJournalAppendStatus.AlreadyRecorded)
                    {
                        var protectedExisting = await ReadProtectedRecordAsync(
                            turnStorageKey,
                            publicStored.Evidence,
                            keys,
                            cancellationToken).ConfigureAwait(false);
                        RequireIdempotentEvidenceMatches(
                            publicStored,
                            protectedExisting,
                            snapshot);
                    }

                    return publicStored;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(projectionBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(argumentsBytes);
            CryptographicOperations.ZeroMemory(resultBytes);
            CryptographicOperations.ZeroMemory(normalizedTargetBytes);
            CryptographicOperations.ZeroMemory(normalizedResultBytes);
            CryptographicOperations.ZeroMemory(permissionBytes);
            CryptographicOperations.ZeroMemory(provenanceBytes);
            CryptographicOperations.ZeroMemory(protectedIdentityBytes);
            CryptographicOperations.ZeroMemory(protectedPayloadBytes);
            CryptographicOperations.ZeroMemory(turnBytes);
        }
    }

    public async Task<IReadOnlyList<EvidenceCursorRecord>> ReplayAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var turnBytes = SerializeTurnIdentity(identity);
        try
        {
            var turnBindingDigest = keys.HmacHex(EvidenceKeyPurpose.TurnBinding, turnBytes);
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
            var cachedTurn = GetJournal(turnStorageKey, identity.StorageKey);
            cachedTurn.StableEvidence.MarkVerified(verificationStamp: null);
            var replay = await cachedTurn.Journal.ReplayAsync(
                existing => ValidateRecord(turnBindingDigest, existing, keys),
                head => ValidateJournalHead(turnStorageKey, head, keys),
                bytes => keys.HmacHex(EvidenceKeyPurpose.EvidenceExactIndex, bytes),
                cancellationToken).ConfigureAwait(false);
            var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in replay.Records)
            {
                if (!evidenceIds.Add(item.Evidence.EvidenceId))
                {
                    throw new InvalidDataException("The evidence journal contains a duplicate evidence ID.");
                }

                await _payloadStore.ValidateAsync(
                    turnStorageKey,
                    new ProtectedEvidencePayloadReference(
                        item.Evidence.ProtectedPayloadReference,
                        item.Evidence.ProtectedPayloadDigest),
                    cancellationToken).ConfigureAwait(false);
            }

            var projected = replay.Records.Select(ToPublicCursor).ToArray();
            cachedTurn.StableEvidence.ReplaceVerified(
                projected,
                replay.VerificationStamp);
            return Array.AsReadOnly(projected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(turnBytes);
        }
    }

    internal async ValueTask<bool> IsCommittedAsync(
        TurnIdentity identity,
        CommittedEvidenceReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reference);
        reference.Validate();
        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var turnBytes = SerializeTurnIdentity(identity);
        try
        {
            var turnBindingDigest = keys.HmacHex(EvidenceKeyPurpose.TurnBinding, turnBytes);
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
            var record = await FindEvidenceRecordAsync(
                    identity.StorageKey,
                    reference.EvidenceId,
                    turnBindingDigest,
                    turnStorageKey,
                    keys,
                    cancellationToken)
                .ConfigureAwait(false);
            if (record is null || !MatchesCommittedReference(record, reference))
            {
                return false;
            }

            await _payloadStore.ValidateAsync(
                turnStorageKey,
                new ProtectedEvidencePayloadReference(
                    record.Evidence.ProtectedPayloadReference,
                    record.Evidence.ProtectedPayloadDigest),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(turnBytes);
        }
    }

    internal async Task<IReadOnlyDictionary<string, CommittedProtectedEvidence>>
        ReadCommittedBatchAsync(
            TurnIdentity identity,
            IReadOnlyCollection<CommittedEvidenceReference> references,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count == 0)
        {
            return new Dictionary<string, CommittedProtectedEvidence>(StringComparer.Ordinal);
        }

        if (references.Count > MaximumCommittedEvidenceLookupBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(references),
                $"A committed evidence lookup batch cannot exceed {MaximumCommittedEvidenceLookupBatchSize} references.");
        }

        var requested = new Dictionary<string, CommittedEvidenceReference>(
            references.Count,
            StringComparer.Ordinal);
        foreach (var reference in references)
        {
            ArgumentNullException.ThrowIfNull(reference);
            reference.Validate();
            if (requested.TryGetValue(reference.EvidenceId, out var existing))
            {
                if (existing != reference)
                {
                    throw new InvalidDataException(
                        "A committed evidence batch contains conflicting references for one evidence ID.");
                }

                continue;
            }

            requested.Add(reference.EvidenceId, reference);
        }

        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var turnBytes = SerializeTurnIdentity(identity);
        try
        {
            var turnBindingDigest = keys.HmacHex(EvidenceKeyPurpose.TurnBinding, turnBytes);
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
            var cachedTurn = GetJournal(turnStorageKey, identity.StorageKey);
            cachedTurn.StableEvidence.MarkVerified(verificationStamp: null);
            var lookup = await cachedTurn.Journal.FindByEvidenceIdsAsync(
                requested.Keys.ToHashSet(StringComparer.Ordinal),
                existing => ValidateRecord(turnBindingDigest, existing, keys),
                head => ValidateJournalHead(turnStorageKey, head, keys),
                bytes => keys.HmacHex(EvidenceKeyPurpose.EvidenceExactIndex, bytes),
                cancellationToken).ConfigureAwait(false);
            cachedTurn.StableEvidence.MarkVerified(lookup.VerificationStamp);

            var committed = new Dictionary<string, EvidenceCursorRecord>(
                lookup.Records.Count,
                StringComparer.Ordinal);
            foreach (var stored in lookup.Records)
            {
                var projected = ToPublicCursor(stored);
                if (!committed.TryAdd(projected.Evidence.EvidenceId, projected))
                {
                    throw new InvalidDataException(
                        "The evidence journal contains a duplicate evidence ID.");
                }

                cachedTurn.StableEvidence.SetVerified(projected, lookup.VerificationStamp);
            }

            if (committed.Count != requested.Count)
            {
                throw new InvalidDataException(
                    "A committed evidence batch references evidence that is absent from the authenticated journal.");
            }

            var result = new Dictionary<string, CommittedProtectedEvidence>(
                committed.Count,
                StringComparer.Ordinal);
            foreach (var (evidenceId, reference) in requested)
            {
                var cursor = committed[evidenceId];
                if (!MatchesCommittedReference(cursor, reference))
                {
                    throw new InvalidDataException(
                        "A committed evidence batch reference does not select its exact authenticated record.");
                }

                var protectedContent = await ReadProtectedRecordAsync(
                    turnStorageKey,
                    cursor.Evidence,
                    keys,
                    cancellationToken).ConfigureAwait(false);
                result.Add(
                    evidenceId,
                    new CommittedProtectedEvidence(cursor, protectedContent));
            }

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(turnBytes);
        }
    }

    public async Task<ProtectedEvidenceContent> ReadProtectedAsync(
        TurnIdentity identity,
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        TurnStateIntegrity.RequireBoundedValue(evidenceId, 256, nameof(evidenceId));
        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var turnBytes = SerializeTurnIdentity(identity);
        try
        {
            var turnBindingDigest = keys.HmacHex(EvidenceKeyPurpose.TurnBinding, turnBytes);
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
            var record = (await FindEvidenceRecordAsync(
                        identity.StorageKey,
                        evidenceId,
                        turnBindingDigest,
                        turnStorageKey,
                        keys,
                        cancellationToken)
                    .ConfigureAwait(false))
                ?.Evidence
                ?? throw new KeyNotFoundException($"Evidence '{evidenceId}' was not found for this turn.");
            return await ReadProtectedRecordAsync(
                turnStorageKey,
                record,
                keys,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(turnBytes);
        }
    }

    private async Task<EvidenceCursorRecord?> FindEvidenceRecordAsync(
        TurnIdentity identity,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        TurnStateIntegrity.RequireBoundedValue(evidenceId, 256, nameof(evidenceId));
        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var turnBytes = SerializeTurnIdentity(identity);
        try
        {
            var turnBindingDigest = keys.HmacHex(EvidenceKeyPurpose.TurnBinding, turnBytes);
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
            return await FindEvidenceRecordAsync(
                identity.StorageKey,
                evidenceId,
                turnBindingDigest,
                turnStorageKey,
                keys,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(turnBytes);
        }
    }

    private async Task<EvidenceCursorRecord?> FindEvidenceRecordAsync(
        string stableTurnKey,
        string evidenceId,
        string turnBindingDigest,
        string turnStorageKey,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var cachedTurn = GetJournal(turnStorageKey, stableTurnKey);
        var currentStamp = await cachedTurn.Journal.CaptureVerificationStampAsync(
            head => ValidateJournalHead(turnStorageKey, head, keys),
            cancellationToken).ConfigureAwait(false);
        if (cachedTurn.StableEvidence.TryGetVerified(
                evidenceId,
                currentStamp,
                out var cached))
        {
            return cached;
        }

        cachedTurn.StableEvidence.MarkVerified(verificationStamp: null);
        var lookup = await cachedTurn.Journal.FindByEvidenceIdAsync(
            evidenceId,
            existing => ValidateRecord(turnBindingDigest, existing, keys),
            head => ValidateJournalHead(turnStorageKey, head, keys),
            bytes => keys.HmacHex(EvidenceKeyPurpose.EvidenceExactIndex, bytes),
            cancellationToken).ConfigureAwait(false);
        cachedTurn.StableEvidence.MarkVerified(lookup.VerificationStamp);
        if (lookup.Record is null)
        {
            return null;
        }

        var projected = ToPublicCursor(lookup.Record);
        cachedTurn.StableEvidence.SetVerified(projected, lookup.VerificationStamp);
        return projected;
    }

    private async Task<ProtectedEvidenceContent> ReadProtectedRecordAsync(
        TurnIdentity identity,
        EvidenceRecord record,
        CancellationToken cancellationToken)
    {
        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var turnBytes = SerializeTurnIdentity(identity);
        try
        {
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
            return await ReadProtectedRecordAsync(
                turnStorageKey,
                record,
                keys,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(turnBytes);
        }
    }

    private async Task<ProtectedEvidenceContent> ReadProtectedRecordAsync(
        string turnStorageKey,
        EvidenceRecord record,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var plaintext = await _payloadStore.ReadAsync(
            turnStorageKey,
            record,
            keys,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.Deserialize<ProtectedEvidencePayload>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The protected evidence payload is empty.");
            ValidateProtectedWorkItemIdentity(record, payload.Identity, keys);
            return new ProtectedEvidenceContent(
                payload.Identity with
                {
                    Artifacts = payload.Identity.Artifacts.Select(item => item with { }).ToArray(),
                    Source = payload.Identity.Source with { }
                },
                payload.Arguments.Clone(),
                payload.Result.Clone(),
                payload.NormalizedTarget.Clone(),
                payload.NormalizedEffectResult.Clone(),
                payload.PermissionReceipt.Clone(),
                payload.Provenance.Clone());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The protected evidence payload is malformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal (
        int TurnJournals,
        int StableEvidenceTurns,
        int StableEvidenceRecords,
        int ActiveStableEvidenceGates)
        CaptureCacheCounts()
    {
        lock (_journalCacheSync)
        {
            return (
                _journalCache.Count,
                _stableEvidenceTurns.Count,
                _stableEvidenceTurns.Values.Sum(turn => turn.Count),
                _stableEvidenceGates.Count);
        }
    }

    private async Task<StableEvidenceGateLease> AcquireStableEvidenceGateAsync(
        string stableTurnKey,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = _stableEvidenceGates.GetOrAdd(
                stableTurnKey,
                static _ => new StableEvidenceTurnGate());
            if (!gate.TryRetain())
            {
                ((ICollection<KeyValuePair<string, StableEvidenceTurnGate>>)_stableEvidenceGates)
                    .Remove(new KeyValuePair<string, StableEvidenceTurnGate>(stableTurnKey, gate));
                continue;
            }

            try
            {
                await gate.InvocationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new StableEvidenceGateLease(this, stableTurnKey, gate);
            }
            catch
            {
                ReleaseStableEvidenceGate(stableTurnKey, gate);
                throw;
            }
        }
    }

    private void ReleaseStableEvidenceGate(
        string stableTurnKey,
        StableEvidenceTurnGate gate)
    {
        if (!gate.ReleaseAndRetireIfUnused())
        {
            return;
        }

        ((ICollection<KeyValuePair<string, StableEvidenceTurnGate>>)_stableEvidenceGates)
            .Remove(new KeyValuePair<string, StableEvidenceTurnGate>(stableTurnKey, gate));
        gate.InvocationGate.Dispose();
    }

    private CachedTurnJournal GetJournal(string turnStorageKey, string stableTurnKey)
    {
        var key = turnStorageKey;
        lock (_journalCacheSync)
        {
            if (_journalCache.TryGetValue(key, out var cached))
            {
                _journalLru.Remove(cached.Node);
                _journalLru.AddFirst(cached.Node);
                if (!string.Equals(cached.StableTurnKey, stableTurnKey, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The evidence journal cache resolved to a different turn identity.");
                }

                return cached;
            }

            if (_journalCache.Count >= MaximumCachedTurnJournals)
            {
                var oldest = _journalLru.Last
                    ?? throw new InvalidOperationException("The evidence journal cache is inconsistent.");
                _journalLru.RemoveLast();
                var evicted = _journalCache[oldest.Value];
                _journalCache.Remove(oldest.Value);
                if (_stableEvidenceTurns.TryGetValue(evicted.StableTurnKey, out var current)
                    && ReferenceEquals(current, evicted.StableEvidence))
                {
                    _stableEvidenceTurns.Remove(evicted.StableTurnKey);
                }
            }

            var node = _journalLru.AddFirst(key);
            var journal = new EvidenceJournal(
                Path.Combine(_rootDirectory, "turns", key),
                key,
                _journalFaultInjector,
                _journalTailReadObserver,
                _journalStampUnavailable,
                _journalIndexedRecordReadObserver,
                _exactIndexMaintenanceFaultInjector);
            var stableEvidence = new StableEvidenceTurnIndex();
            var added = new CachedTurnJournal(
                journal,
                node,
                stableTurnKey,
                stableEvidence);
            _journalCache.Add(key, added);
            _stableEvidenceTurns.Add(stableTurnKey, stableEvidence);
            return added;
        }
    }

    private static byte[] SerializePayloadComponent<T>(
        SensitiveBufferScope buffers,
        T value,
        ref long accumulatedBytes,
        string componentName)
    {
        var remaining = ProtectedEvidencePayloadStore.MaximumPlaintextBytes
                        - ProtectedPayloadJsonOverheadReserve
                        - accumulatedBytes;
        if (remaining <= 0)
        {
            throw new InvalidDataException(
                "Protected orchestration evidence exceeds its aggregate payload budget.");
        }

        var maximum = (int)Math.Min(MaximumEvidenceComponentBytes, remaining);
        try
        {
            var bytes = buffers.Track(
                CanonicalEvidenceJson.SerializeToUtf8Bytes(value, maximum));
            accumulatedBytes += bytes.LongLength;
            return bytes;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"The protected evidence {componentName} exceeds its accepted payload budget.",
                ex);
        }
    }

    private static EvidenceDraft SnapshotAndValidate(EvidenceDraft draft)
    {
        if (draft.EvidenceId is not null)
        {
            TurnStateIntegrity.RequireBoundedValue(
                draft.EvidenceId,
                256,
                nameof(draft.EvidenceId));
        }

        RequireOptionalWorkItemId(draft.WorkItemId, nameof(draft.WorkItemId));
        RequireProtectedValue(draft.CallId, nameof(draft.CallId));
        RequireProtectedValue(draft.ToolName, nameof(draft.ToolName));
        RequireProtectedValue(draft.CapabilityGroup, nameof(draft.CapabilityGroup));
        RequireProtectedValue(draft.ProviderId, nameof(draft.ProviderId));
        RequireProtectedValue(draft.RegistryRevision, nameof(draft.RegistryRevision));
        RequireAllowedValue(draft.EffectKind, AllowedEffectKinds, nameof(draft.EffectKind));
        ArgumentNullException.ThrowIfNull(draft.Outcome);
        ArgumentNullException.ThrowIfNull(draft.Permission);
        ArgumentNullException.ThrowIfNull(draft.Source);
        RequireProtectedValue(draft.StableOutcomeCode, nameof(draft.StableOutcomeCode));
        if (draft.Outcome.FailureCode is not null)
        {
            RequireProtectedValue(draft.Outcome.FailureCode, nameof(draft.Outcome.FailureCode));
        }
        if (draft.CompletedAtUtc < draft.StartedAtUtc)
        {
            throw new ArgumentException("Evidence completion cannot precede its start.", nameof(draft));
        }

        var draftArtifacts = draft.Artifacts ?? [];
        if (draftArtifacts.Length > MaximumArtifactCount)
        {
            throw new ArgumentException(
                $"Evidence can reference at most {MaximumArtifactCount} artifacts.",
                nameof(draft));
        }

        var artifacts = draftArtifacts
            .Select(artifact =>
            {
                ArgumentNullException.ThrowIfNull(artifact);
                RequireProtectedValue(artifact.ArtifactId, nameof(artifact.ArtifactId));
                RequireAllowedValue(artifact.Kind, AllowedArtifactKinds, nameof(artifact.Kind));
                RequireOptionalProtectedValue(artifact.BeforeVersion, nameof(artifact.BeforeVersion));
                RequireOptionalProtectedValue(artifact.AfterVersion, nameof(artifact.AfterVersion));
                return artifact with { };
            })
            .OrderBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Kind, StringComparer.Ordinal)
            .ToArray();
        RequireAllowedValue(
            draft.Permission.Decision,
            AllowedPermissionDecisions,
            nameof(draft.Permission.Decision));
        RequireAllowedValue(
            draft.Permission.Scope,
            AllowedPermissionScopes,
            nameof(draft.Permission.Scope));
        RequireAllowedValue(draft.Source.Kind, AllowedSourceKinds, nameof(draft.Source.Kind));
        RequireProtectedValue(draft.Source.ProviderId, nameof(draft.Source.ProviderId));
        RequireAllowedValue(
            draft.Source.TrustBoundary,
            AllowedTrustBoundaries,
            nameof(draft.Source.TrustBoundary));
        RequireProtectedValue(draft.Source.StateRevision, nameof(draft.Source.StateRevision));

        return draft with
        {
            EvidenceId = draft.EvidenceId?.Trim(),
            WorkItemId = draft.WorkItemId,
            CallId = draft.CallId.Trim(),
            ToolName = draft.ToolName.Trim(),
            CapabilityGroup = draft.CapabilityGroup.Trim(),
            ProviderId = draft.ProviderId.Trim(),
            RegistryRevision = draft.RegistryRevision.Trim(),
            EffectKind = draft.EffectKind.Trim(),
            StableOutcomeCode = draft.StableOutcomeCode.Trim(),
            StartedAtUtc = draft.StartedAtUtc.ToUniversalTime(),
            CompletedAtUtc = draft.CompletedAtUtc.ToUniversalTime(),
            Arguments = CanonicalEvidenceJson.CloneOrNull(draft.Arguments),
            Result = CanonicalEvidenceJson.CloneOrNull(draft.Result),
            NormalizedTarget = CanonicalEvidenceJson.CloneOrNull(draft.NormalizedTarget),
            NormalizedEffectResult = CanonicalEvidenceJson.CloneOrNull(
                draft.NormalizedEffectResult),
            ProtectedPermissionReceipt = CanonicalEvidenceJson.CloneOrNull(
                draft.ProtectedPermissionReceipt),
            ProtectedProvenance = CanonicalEvidenceJson.CloneOrNull(draft.ProtectedProvenance),
            Artifacts = artifacts,
            Permission = draft.Permission with
            {
                Decision = draft.Permission.Decision.Trim(),
                Scope = draft.Permission.Scope.Trim()
            },
            Source = draft.Source with
            {
                Kind = draft.Source.Kind.Trim(),
                ProviderId = draft.Source.ProviderId.Trim(),
                TrustBoundary = draft.Source.TrustBoundary.Trim(),
                FreshAtUtc = draft.Source.FreshAtUtc?.ToUniversalTime(),
                StateRevision = draft.Source.StateRevision.Trim()
            }
        };
    }

    private static void RequireIdempotentEvidenceMatches(
        EvidenceCursorRecord existing,
        ProtectedEvidenceContent protectedExisting,
        EvidenceDraft requested)
    {
        var identity = protectedExisting.Identity;
        var exactIdentity = string.Equals(identity.CallId, requested.CallId, StringComparison.Ordinal)
            && string.Equals(identity.WorkItemId, requested.WorkItemId, StringComparison.Ordinal)
            && string.Equals(identity.ToolName, requested.ToolName, StringComparison.Ordinal)
            && string.Equals(identity.CapabilityGroup, requested.CapabilityGroup, StringComparison.Ordinal)
            && string.Equals(identity.ProviderId, requested.ProviderId, StringComparison.Ordinal)
            && string.Equals(identity.RegistryRevision, requested.RegistryRevision, StringComparison.Ordinal)
            && string.Equals(identity.StableOutcomeCode, requested.StableOutcomeCode, StringComparison.Ordinal)
            && string.Equals(identity.FailureCode, requested.Outcome.FailureCode, StringComparison.Ordinal);
        var exactPayload = JsonElement.DeepEquals(protectedExisting.Arguments, requested.Arguments)
            && JsonElement.DeepEquals(protectedExisting.Result, requested.Result)
            && JsonElement.DeepEquals(protectedExisting.NormalizedTarget, requested.NormalizedTarget)
            && JsonElement.DeepEquals(
                protectedExisting.NormalizedEffectResult,
                requested.NormalizedEffectResult)
            && JsonElement.DeepEquals(
                protectedExisting.PermissionReceipt,
                requested.ProtectedPermissionReceipt)
            && JsonElement.DeepEquals(protectedExisting.Provenance, requested.ProtectedProvenance);
        var exactProjection = existing.Evidence.InvocationStatus == requested.Outcome.InvocationStatus
            && existing.Evidence.DomainOutcome == requested.Outcome.DomainOutcome
            && string.Equals(
                existing.Evidence.EffectKind,
                requested.EffectKind,
                StringComparison.Ordinal)
            && existing.Evidence.StartedAtUtc == requested.StartedAtUtc
            && existing.Evidence.CompletedAtUtc == requested.CompletedAtUtc
            && existing.Evidence.Permission == requested.Permission
            && identity.Artifacts.SequenceEqual(requested.Artifacts)
            && identity.Source == requested.Source;
        if (!exactIdentity || !exactPayload || !exactProjection)
        {
            throw new InvalidDataException(
                "A stable evidence ID was reused for different invocation evidence.");
        }
    }

    private static bool MatchesCommittedReference(
        EvidenceCursorRecord evidence,
        CommittedEvidenceReference reference) =>
        evidence.Cursor == reference.Cursor
        && string.Equals(
            evidence.Evidence.EvidenceId,
            reference.EvidenceId,
            StringComparison.Ordinal)
        && FixedTimeHexEquals(evidence.Checksum, reference.RecordDigest);

    private static void ValidateRecord(
        string expectedTurnBindingDigest,
        StoredEvidenceRecord record,
        EvidenceKeySession keys)
    {
        ValidatePersistedRecordShape(record);
        if (!FixedTimeHexEquals(expectedTurnBindingDigest, record.TurnBindingDigest))
        {
            throw new InvalidDataException("Persisted evidence belongs to another turn.");
        }

        var metadata = CreateMetadataSeed(record);
        var metadataBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(metadata);
        try
        {
            if (!FixedTimeHexEquals(Sha256Hex(metadataBytes), record.MetadataDigest))
            {
                throw new InvalidDataException("The evidence metadata digest is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataBytes);
        }

        if (!string.Equals(
                record.ProtectedPayloadReference,
                record.ProtectedPayloadDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The protected evidence reference does not match its digest.");
        }

        var unsigned = CreateUnsignedRecord(record);
        var projectionBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned);
        try
        {
            if (!FixedTimeHexEquals(Sha256Hex(projectionBytes), record.ProjectionDigest))
            {
                throw new InvalidDataException("The evidence projection digest is invalid.");
            }

            if (!keys.VerifyHmac(EvidenceKeyPurpose.RecordMac, projectionBytes, record.RecordMac))
            {
                throw new InvalidDataException("The evidence record failed keyed authentication.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(projectionBytes);
        }
    }

    private static EvidenceMetadataSeed CreateMetadataSeed(StoredEvidenceRecord record) =>
        new(
            record.EvidenceId,
            record.TurnBindingDigest,
            record.WorkItemIdDigest,
            record.CallIdDigest,
            record.ToolNameDigest,
            record.CapabilityGroupDigest,
            record.ProviderIdDigest,
            record.RegistryRevisionDigest,
            record.EffectKind,
            record.ArgumentsDigest,
            record.TargetDigest,
            record.NormalizedResultDigest,
            record.InvocationStatus,
            record.DomainOutcome,
            record.FailureCodeDigest,
            record.StableOutcomeCodeDigest,
            record.ResultDigest,
            record.NoEffectFingerprint,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.RecordedAtUtc,
            record.Artifacts,
            record.Permission,
            record.PermissionReceiptDigest,
            record.Source);

    private static EvidenceUnsignedRecord CreateUnsignedRecord(
        EvidenceMetadataSeed metadata,
        string protectedPayloadReference,
        string protectedPayloadDigest,
        string metadataDigest) =>
        new(
            metadata.EvidenceId,
            metadata.TurnBindingDigest,
            metadata.WorkItemIdDigest,
            metadata.CallIdDigest,
            metadata.ToolNameDigest,
            metadata.CapabilityGroupDigest,
            metadata.ProviderIdDigest,
            metadata.RegistryRevisionDigest,
            metadata.EffectKind,
            metadata.ArgumentsDigest,
            metadata.TargetDigest,
            metadata.NormalizedResultDigest,
            metadata.InvocationStatus,
            metadata.DomainOutcome,
            metadata.FailureCodeDigest,
            metadata.StableOutcomeCodeDigest,
            metadata.ResultDigest,
            metadata.NoEffectFingerprint,
            metadata.StartedAtUtc,
            metadata.CompletedAtUtc,
            metadata.RecordedAtUtc,
            metadata.Artifacts,
            metadata.Permission,
            metadata.PermissionReceiptDigest,
            metadata.Source,
            protectedPayloadReference,
            protectedPayloadDigest,
            metadataDigest);

    private static EvidenceUnsignedRecord CreateUnsignedRecord(StoredEvidenceRecord record) =>
        CreateUnsignedRecord(
            CreateMetadataSeed(record),
            record.ProtectedPayloadReference,
            record.ProtectedPayloadDigest,
            record.MetadataDigest);

    private static StoredEvidenceRecord CreateStoredRecord(
        EvidenceUnsignedRecord unsigned,
        string projectionDigest,
        string recordMac) =>
        new(
            unsigned.EvidenceId,
            unsigned.TurnBindingDigest,
            unsigned.WorkItemIdDigest,
            unsigned.CallIdDigest,
            unsigned.ToolNameDigest,
            unsigned.CapabilityGroupDigest,
            unsigned.ProviderIdDigest,
            unsigned.RegistryRevisionDigest,
            unsigned.EffectKind,
            unsigned.ArgumentsDigest,
            unsigned.TargetDigest,
            unsigned.NormalizedResultDigest,
            unsigned.InvocationStatus,
            unsigned.DomainOutcome,
            unsigned.FailureCodeDigest,
            unsigned.StableOutcomeCodeDigest,
            unsigned.ResultDigest,
            unsigned.NoEffectFingerprint,
            unsigned.StartedAtUtc,
            unsigned.CompletedAtUtc,
            unsigned.RecordedAtUtc,
            unsigned.Artifacts,
            unsigned.Permission,
            unsigned.PermissionReceiptDigest,
            unsigned.Source,
            unsigned.ProtectedPayloadReference,
            unsigned.ProtectedPayloadDigest,
            unsigned.MetadataDigest,
            projectionDigest,
            recordMac);

    private static EvidenceCursorRecord ToPublicCursor(StoredEvidenceCursorRecord stored) =>
        new(stored.Cursor, new EvidenceRecord(stored.Evidence), stored.Checksum);

    private static string SignJournalHead(
        EvidenceJournalHeadUnsigned head,
        EvidenceKeySession keys)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(head);
        try
        {
            return keys.HmacHex(EvidenceKeyPurpose.JournalHead, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateJournalHead(
        string turnStorageKey,
        EvidenceJournalHead head,
        EvidenceKeySession keys)
    {
        if (!string.Equals(head.TurnStorageKey, turnStorageKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The evidence journal head belongs to another turn.");
        }

        var unsigned = new EvidenceJournalHeadUnsigned(
            head.TurnStorageKey,
            head.CommittedLength,
            head.Sequence,
            head.Checksum);
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(unsigned);
        try
        {
            if (!keys.VerifyHmac(EvidenceKeyPurpose.JournalHead, bytes, head.Mac))
            {
                throw new InvalidDataException(
                    "The evidence journal head failed keyed authentication.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidatePersistedRecordShape(StoredEvidenceRecord record)
    {
        try
        {
            // Evidence IDs may be caller-supplied stable idempotency keys. Replay must
            // enforce the same bounded-value contract as AppendAsync instead of silently
            // narrowing persisted records to the random GUID format used by the default.
            TurnStateIntegrity.RequireBoundedValue(
                record.EvidenceId,
                256,
                nameof(record.EvidenceId));
            RequireDigest(record.TurnBindingDigest, nameof(record.TurnBindingDigest));
            RequireOptionalDigest(record.WorkItemIdDigest, nameof(record.WorkItemIdDigest));
            RequireDigest(record.CallIdDigest, nameof(record.CallIdDigest));
            RequireDigest(record.ToolNameDigest, nameof(record.ToolNameDigest));
            RequireDigest(record.CapabilityGroupDigest, nameof(record.CapabilityGroupDigest));
            RequireDigest(record.ProviderIdDigest, nameof(record.ProviderIdDigest));
            RequireDigest(record.RegistryRevisionDigest, nameof(record.RegistryRevisionDigest));
            RequireAllowedValue(record.EffectKind, AllowedEffectKinds, nameof(record.EffectKind));
            RequireDigest(record.ArgumentsDigest, nameof(record.ArgumentsDigest));
            RequireDigest(record.TargetDigest, nameof(record.TargetDigest));
            RequireDigest(record.NormalizedResultDigest, nameof(record.NormalizedResultDigest));
            RequireOptionalDigest(record.FailureCodeDigest, nameof(record.FailureCodeDigest));
            RequireDigest(record.StableOutcomeCodeDigest, nameof(record.StableOutcomeCodeDigest));
            RequireDigest(record.ResultDigest, nameof(record.ResultDigest));
            RequireDigest(record.NoEffectFingerprint, nameof(record.NoEffectFingerprint));
            ArgumentNullException.ThrowIfNull(record.Artifacts);
            ArgumentNullException.ThrowIfNull(record.Permission);
            ArgumentNullException.ThrowIfNull(record.Source);
            foreach (var artifact in record.Artifacts)
            {
                ArgumentNullException.ThrowIfNull(artifact);
                RequireDigest(artifact.ArtifactIdDigest, nameof(artifact.ArtifactIdDigest));
                RequireAllowedValue(artifact.Kind, AllowedArtifactKinds, nameof(artifact.Kind));
                RequireOptionalDigest(artifact.BeforeVersionDigest, nameof(artifact.BeforeVersionDigest));
                RequireOptionalDigest(artifact.AfterVersionDigest, nameof(artifact.AfterVersionDigest));
            }
            RequireAllowedValue(
                record.Permission.Decision,
                AllowedPermissionDecisions,
                nameof(record.Permission.Decision));
            RequireAllowedValue(
                record.Permission.Scope,
                AllowedPermissionScopes,
                nameof(record.Permission.Scope));
            RequireAllowedValue(record.Source.Kind, AllowedSourceKinds, nameof(record.Source.Kind));
            RequireDigest(record.Source.ProviderIdDigest, nameof(record.Source.ProviderIdDigest));
            RequireAllowedValue(
                record.Source.TrustBoundary,
                AllowedTrustBoundaries,
                nameof(record.Source.TrustBoundary));
            RequireDigest(record.Source.StateRevisionDigest, nameof(record.Source.StateRevisionDigest));
            RequireDigest(record.PermissionReceiptDigest, nameof(record.PermissionReceiptDigest));
            RequireDigest(record.ProtectedPayloadReference, nameof(record.ProtectedPayloadReference));
            RequireDigest(record.ProtectedPayloadDigest, nameof(record.ProtectedPayloadDigest));
            RequireDigest(record.MetadataDigest, nameof(record.MetadataDigest));
            RequireDigest(record.ProjectionDigest, nameof(record.ProjectionDigest));
            RequireDigest(record.RecordMac, nameof(record.RecordMac));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("The persisted evidence record shape is invalid.", ex);
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

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

    private static string IdentifierDigest(
        EvidenceKeySession keys,
        string field,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(field + "\0" + value);
        try
        {
            return keys.HmacHex(EvidenceKeyPurpose.Identifier, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateProtectedWorkItemIdentity(
        EvidenceRecord record,
        ProtectedEvidenceIdentity identity,
        EvidenceKeySession keys)
    {
        if (identity.WorkItemId is null)
        {
            if (record.WorkItemIdDigest is not null)
            {
                throw new InvalidDataException(
                    "The protected evidence work-item identity does not match its authenticated projection.");
            }

            return;
        }

        try
        {
            RequireOptionalWorkItemId(identity.WorkItemId, nameof(identity.WorkItemId));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("The protected evidence work-item identity is invalid.", ex);
        }

        var expectedDigest = IdentifierDigest(keys, "work-item-id", identity.WorkItemId);
        if (record.WorkItemIdDigest is null
            || !FixedTimeHexEquals(expectedDigest, record.WorkItemIdDigest))
        {
            throw new InvalidDataException(
                "The protected evidence work-item identity does not match its authenticated projection.");
        }
    }

    private static byte[] SerializeTurnIdentity(TurnIdentity identity) =>
        CanonicalEvidenceJson.SerializeToUtf8Bytes(new
        {
            identity.UserId,
            identity.ConversationId,
            identity.AssistantMessageId
        });

    private static void RequireOptionalDigest(string? value, string parameterName)
    {
        if (value is not null)
        {
            RequireDigest(value, parameterName);
        }
    }

    private static void RequireDigest(string value, string parameterName)
    {
        if (value is null
            || value.Length != 64
            || value.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new ArgumentException(
                "Evidence plaintext digests must be lowercase SHA-256 or HMAC-SHA-256 values.",
                parameterName);
        }
    }

    private static void RequireAllowedValue(
        string value,
        IReadOnlySet<string> allowed,
        string parameterName)
    {
        if (value is null || !allowed.Contains(value))
        {
            throw new ArgumentException(
                "Evidence plaintext metadata must use a closed vocabulary.",
                parameterName);
        }
    }

    private static void RequireOptionalProtectedValue(string? value, string parameterName)
    {
        if (value is not null)
        {
            RequireProtectedValue(value, parameterName);
        }
    }

    private static void RequireOptionalWorkItemId(string? value, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        TurnStateIntegrity.RequireBoundedValue(value, 256, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A work-item evidence binding must be canonical and cannot have surrounding whitespace.",
                parameterName);
        }
    }

    private static void RequireProtectedValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            throw new ArgumentException(
                "A protected evidence value must be present and bounded.",
                parameterName);
        }
    }

    private sealed class SensitiveBufferScope : IDisposable
    {
        private readonly List<byte[]> _buffers = [];

        public byte[] Track(byte[] buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            _buffers.Add(buffer);
            return buffer;
        }

        public void Dispose()
        {
            foreach (var buffer in _buffers)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }

            _buffers.Clear();
        }
    }
}
