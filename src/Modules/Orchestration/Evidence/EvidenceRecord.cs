using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Orchestration.Evidence;

internal enum EvidenceLedgerCommitBoundary
{
    ProtectedPayloadFlushed
}

internal sealed record EvidenceMetadataSeed(
    string EvidenceId,
    string TurnBindingDigest,
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

public sealed class EvidenceLedger
{
    private const int MaximumCachedTurnJournals = 64;
    private static readonly HashSet<string> AllowedEffectKinds = new(
        ["none", "read", "create", "update", "delete", "execute", "external", "mixed"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedArtifactKinds = new(
        ["unknown", "file", "directory", "process", "service", "uri", "symbol", "project"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedPermissionDecisions = new(
        ["unknown", "not-required", "approved-once", "approved-standing", "denied", "policy-blocked"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedPermissionScopes = new(
        ["unknown", "none", "once", "exact-arguments", "tool"],
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
    private readonly object _journalCacheSync = new();
    private readonly Dictionary<string, (EvidenceJournal Journal, LinkedListNode<string> Node)> _journalCache =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _journalLru = [];

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
        Func<bool>? journalStampUnavailable = null)
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
    }

    public async Task<EvidenceCursorRecord> AppendAsync(
        TurnIdentity identity,
        EvidenceDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(draft);
        var snapshot = SnapshotAndValidate(draft);
        var argumentsBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(snapshot.Arguments);
        var resultBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(snapshot.Result);
        var normalizedTargetBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(snapshot.NormalizedTarget);
        var normalizedResultBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            snapshot.NormalizedEffectResult);
        var permissionBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            snapshot.ProtectedPermissionReceipt);
        var protectedIdentity = new ProtectedEvidenceIdentity(
            snapshot.CallId,
            snapshot.ToolName,
            snapshot.CapabilityGroup,
            snapshot.ProviderId,
            snapshot.RegistryRevision,
            snapshot.StableOutcomeCode,
            snapshot.Outcome.FailureCode,
            snapshot.Artifacts,
            snapshot.Source);
        var protectedPayloadBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            new ProtectedEvidencePayload(
                protectedIdentity,
                snapshot.Arguments,
                snapshot.Result,
                snapshot.NormalizedTarget,
                snapshot.NormalizedEffectResult,
                snapshot.ProtectedPermissionReceipt,
                snapshot.ProtectedProvenance));
        var turnBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(new
        {
            identity.UserId,
            identity.ConversationId,
            identity.AssistantMessageId
        });
        try
        {
            using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            var evidenceId = Guid.NewGuid().ToString("N");
            var recordedAtUtc = DateTimeOffset.UtcNow;
            var turnBindingDigest = keys.HmacHex(EvidenceKeyPurpose.TurnBinding, turnBytes);
            var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
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
                    var stored = await GetJournal(turnStorageKey).AppendAsync(
                        record,
                        existing => ValidateRecord(turnBindingDigest, existing, keys),
                        head => SignJournalHead(head, keys),
                        head => ValidateJournalHead(turnStorageKey, head, keys),
                        cancellationToken).ConfigureAwait(false);
                    return ToPublicCursor(stored);
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
            var replay = await GetJournal(turnStorageKey).ReplayAsync(
                existing => ValidateRecord(turnBindingDigest, existing, keys),
                head => ValidateJournalHead(turnStorageKey, head, keys),
                cancellationToken).ConfigureAwait(false);
            var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in replay)
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

            return Array.AsReadOnly(replay.Select(ToPublicCursor).ToArray());
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
        var record = (await ReplayAsync(identity, cancellationToken).ConfigureAwait(false))
            .Select(item => item.Evidence)
            .SingleOrDefault(item => string.Equals(item.EvidenceId, evidenceId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Evidence '{evidenceId}' was not found for this turn.");
        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var turnBytes = SerializeTurnIdentity(identity);
        var turnStorageKey = keys.HmacHex(EvidenceKeyPurpose.TurnStorage, turnBytes);
        CryptographicOperations.ZeroMemory(turnBytes);
        var plaintext = await _payloadStore.ReadAsync(
            turnStorageKey,
            record,
            keys,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.Deserialize<ProtectedEvidencePayload>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The protected evidence payload is empty.");
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

    private EvidenceJournal GetJournal(string turnStorageKey)
    {
        var key = turnStorageKey;
        lock (_journalCacheSync)
        {
            if (_journalCache.TryGetValue(key, out var cached))
            {
                _journalLru.Remove(cached.Node);
                _journalLru.AddFirst(cached.Node);
                return cached.Journal;
            }

            if (_journalCache.Count >= MaximumCachedTurnJournals)
            {
                var oldest = _journalLru.Last
                    ?? throw new InvalidOperationException("The evidence journal cache is inconsistent.");
                _journalLru.RemoveLast();
                _journalCache.Remove(oldest.Value);
            }

            var node = _journalLru.AddFirst(key);
            var journal = new EvidenceJournal(
                Path.Combine(_rootDirectory, "turns", key),
                key,
                _journalFaultInjector,
                _journalTailReadObserver,
                _journalStampUnavailable);
            _journalCache.Add(key, (journal, node));
            return journal;
        }
    }

    private static EvidenceDraft SnapshotAndValidate(EvidenceDraft draft)
    {
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

        var artifacts = (draft.Artifacts ?? [])
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
            CallId = draft.CallId.Trim(),
            ToolName = draft.ToolName.Trim(),
            CapabilityGroup = draft.CapabilityGroup.Trim(),
            ProviderId = draft.ProviderId.Trim(),
            RegistryRevision = draft.RegistryRevision.Trim(),
            EffectKind = draft.EffectKind.Trim(),
            StableOutcomeCode = draft.StableOutcomeCode.Trim(),
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
                StateRevision = draft.Source.StateRevision.Trim()
            }
        };
    }

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
            if (!Guid.TryParseExact(record.EvidenceId, "N", out _))
            {
                throw new ArgumentException("The evidence ID is invalid.", nameof(record.EvidenceId));
            }
            RequireDigest(record.TurnBindingDigest, nameof(record.TurnBindingDigest));
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

    private static void RequireProtectedValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            throw new ArgumentException(
                "A protected evidence value must be present and bounded.",
                parameterName);
        }
    }
}
