using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.State;

public interface ITurnCommittedReferenceValidator
{
    ValueTask<bool> IsEvidenceCommittedAsync(
        TurnIdentity identity,
        CommittedEvidenceReference evidence,
        CancellationToken cancellationToken);

    ValueTask<bool> IsWorkGraphCommittedAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference workGraph,
        CancellationToken cancellationToken);
}

public enum TurnTransitionWriteStatus
{
    Committed,
    AlreadyRecorded,
    RevisionConflict
}

public sealed record TurnTransitionWriteResult(
    TurnTransitionWriteStatus Status,
    TurnState? State,
    TurnJournalEntry? Entry)
{
    public bool Advanced => Status == TurnTransitionWriteStatus.Committed;
}

/// <summary>
/// The only authoritative transition writer. Every accepted transition uses the same
/// per-turn journal lease and expected-revision compare-and-swap boundary.
/// </summary>
public sealed class TurnTransitionWriter : IDisposable
{
    private const int MaximumCachedTurnJournals = 64;
    private readonly string _rootDirectory;
    private readonly WindowsCurrentUserEvidenceProtector _integrityProtector;
    private readonly ProtectedTurnInputStore _inputStore;
    private readonly ITurnCommittedReferenceValidator _referenceValidator;
    private readonly Action<TurnJournalCommitBoundary>? _faultInjector;
    private readonly object _journalCacheSync = new();
    private readonly Dictionary<string, (TurnTransitionJournal Journal, LinkedListNode<string> Node)> _journals =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _journalLru = [];
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private EvidenceKeySession? _keySession;
    private int _disposed;

    public TurnTransitionWriter(
        string rootDirectory,
        string assistantProfileBinding,
        ITurnCommittedReferenceValidator? referenceValidator = null)
        : this(rootDirectory, assistantProfileBinding, referenceValidator, faultInjector: null)
    {
    }

    internal TurnTransitionWriter(
        string rootDirectory,
        string assistantProfileBinding,
        ITurnCommittedReferenceValidator? referenceValidator,
        Action<TurnJournalCommitBoundary>? faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _integrityProtector = new WindowsCurrentUserEvidenceProtector(
            _rootDirectory,
            assistantProfileBinding);
        _inputStore = new ProtectedTurnInputStore(_rootDirectory);
        _referenceValidator = referenceValidator ?? RejectingReferenceValidator.Instance;
        _faultInjector = faultInjector;
    }

    public Task<TurnTransitionWriteResult> StartAsync(
        TurnIdentity identity,
        string originalRequest,
        TurnRuntimeBindings bindings,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        StartAsync(
            identity,
            originalRequest,
            bindings,
            correlationKey,
            acceptedPriorConversation: null,
            cancellationToken);

    public async Task<TurnTransitionWriteResult> StartAsync(
        TurnIdentity identity,
        string originalRequest,
        TurnRuntimeBindings bindings,
        string correlationKey,
        IEnumerable<AcceptedConversationInput>? acceptedPriorConversation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(bindings);
        var acceptedEntries = ValidateAcceptedConversation(acceptedPriorConversation);
        var reference = await StoreProtectedInputAsync(
            identity,
            TurnInputPurposes.OriginalRequest,
            correlationKey,
            originalRequest,
            cancellationToken).ConfigureAwait(false);
        var acceptedReferences = new AcceptedConversationEntryReference[acceptedEntries.Length];
        for (var index = 0; index < acceptedEntries.Length; index++)
        {
            var entry = acceptedEntries[index];
            var contentDigest = TurnStateIntegrity.Digest(entry.Text);
            var binding = TurnInputPurposes.AcceptedConversationBinding(
                entry.SourceMessageId,
                entry.Sequence,
                entry.Role,
                contentDigest);
            var payload = await StoreProtectedInputAsync(
                identity,
                TurnInputPurposes.AcceptedConversationEntry,
                binding,
                entry.Text,
                cancellationToken).ConfigureAwait(false);
            acceptedReferences[index] = new AcceptedConversationEntryReference(
                entry.SourceMessageId,
                entry.Sequence,
                entry.Role,
                contentDigest,
                payload);
        }

        return await WriteAsync(
            identity,
            expectedRevision: 0,
            new TurnStartedTransition(correlationKey, reference, bindings, acceptedReferences),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TurnState?> ReadAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        return await GetJournal(identity)
            .ReadStateAsync(identity, cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task<TurnResumeProjection> ReadResumeProjectionAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        return GetJournal(identity).ReadResumeProjectionAsync(identity, cancellationToken);
    }

    internal Task<IReadOnlyDictionary<string, CommittedEvidenceReference>>
        ResolveEvidenceReferencesAsync(
            TurnIdentity identity,
            IReadOnlyCollection<string> evidenceIds,
            CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        return GetJournal(identity).ResolveEvidenceReferencesAsync(
            identity,
            evidenceIds,
            cancellationToken);
    }

    public async Task<string> ReadOriginalRequestAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        var state = await ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The requested turn does not exist.");
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        return await _inputStore.OpenAsync(
            identity,
            TurnInputPurposes.OriginalRequest,
            state.OriginalRequest,
            keys,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadSteeringAsync(
        TurnIdentity identity,
        SteeringAppendedTransition transition,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(transition);
        transition.ValidateShape();
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        return await _inputStore.OpenAsync(
            identity,
            TurnInputPurposes.Steering,
            transition.Steering,
            keys,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AcceptedConversationInput>> ReadAcceptedPriorConversationAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        var state = await ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The requested turn does not exist.");
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        var recovered = new List<AcceptedConversationInput>(state.AcceptedPriorConversation.Length);
        foreach (var entry in state.AcceptedPriorConversation.OrderBy(item => item.Sequence))
        {
            entry.Validate();
            var text = await _inputStore.OpenAsync(
                identity,
                TurnInputPurposes.AcceptedConversationEntry,
                entry.Payload,
                keys,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeDigestEquals(TurnStateIntegrity.Digest(text), entry.ContentDigest))
            {
                throw new InvalidDataException(
                    "Recovered accepted conversation text differs from its committed digest.");
            }

            recovered.Add(new AcceptedConversationInput(
                entry.SourceMessageId,
                entry.Sequence,
                text,
                entry.Role));
        }

        return recovered.AsReadOnly();
    }

    public Task<TurnJournalReplayResult> ReplayAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        return GetJournal(identity).ReplayAsync(identity, cancellationToken);
    }

    public async Task<TurnTransitionWriteResult> WriteAsync(
        TurnIdentity identity,
        long expectedRevision,
        TurnTransition transition,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(transition);
        await ValidateCommittedReferenceAsync(
            identity,
            transition,
            cancellationToken).ConfigureAwait(false);
        var append = await GetJournal(identity)
            .TryAppendAsync(identity, expectedRevision, transition, cancellationToken)
            .ConfigureAwait(false);
        return new TurnTransitionWriteResult(
            append.Status switch
            {
                TurnJournalAppendStatus.Committed => TurnTransitionWriteStatus.Committed,
                TurnJournalAppendStatus.AlreadyRecorded => TurnTransitionWriteStatus.AlreadyRecorded,
                TurnJournalAppendStatus.RevisionConflict => TurnTransitionWriteStatus.RevisionConflict,
                _ => throw new InvalidDataException("The turn journal returned an invalid append status.")
            },
            append.State,
            append.Entry);
    }

    public async Task<TurnTransitionWriteResult> AppendSteeringAsync(
        TurnIdentity identity,
        long expectedRevision,
        string steeringText,
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        var current = await ReadAsync(identity, cancellationToken).ConfigureAwait(false);
        if (current?.Revision == expectedRevision
            && current.InterimPublication?.Reason is
                InterimPublicationReason.ActionReconciliationRequired
                or InterimPublicationReason.FinalPublicationReconciliationRequired)
        {
            throw new InvalidDataException(
                "Ordinary steering cannot resolve a structured reconciliation decision.");
        }
        var reference = await StoreProtectedInputAsync(
            identity,
            TurnInputPurposes.Steering,
            correlationKey,
            steeringText,
            cancellationToken).ConfigureAwait(false);
        return await WriteAsync(
            identity,
            expectedRevision,
            new SteeringAppendedTransition(correlationKey, reference),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<TurnTransitionWriteResult> RecordPlanningDecisionAcceptedAsync(
        TurnIdentity identity,
        long expectedRevision,
        string decisionDigest,
        PlanningAcceptedActionKind actionKind,
        string? callId,
        string? toolName,
        long workGraphRevision,
        string materialClaimsDigest,
        string correlationKey,
        CommittedWorkGraphReference? workGraph = null,
        CancellationToken cancellationToken = default) =>
        RecordPlanningDecisionAcceptedAsync(
            identity,
            expectedRevision,
            decisionDigest,
            actionKind,
            callId,
            toolName,
            workGraphRevision,
            materialClaimsDigest,
            correlationKey,
            acceptedCall: null,
            workGraph,
            cancellationToken);

    public async Task<TurnTransitionWriteResult> RecordPlanningDecisionAcceptedAsync(
        TurnIdentity identity,
        long expectedRevision,
        string decisionDigest,
        PlanningAcceptedActionKind actionKind,
        string? callId,
        string? toolName,
        long workGraphRevision,
        string materialClaimsDigest,
        string correlationKey,
        AcceptedCallRecoveryPayload? acceptedCall,
        CommittedWorkGraphReference? workGraph,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        AcceptedCallRecoveryReference? acceptedCallReference = null;
        if (actionKind == PlanningAcceptedActionKind.CallTool)
        {
            if (acceptedCall is null)
            {
                throw new InvalidDataException(
                    "A tool-call decision requires exact protected recovery material.");
            }

            acceptedCall.Validate();
            if (!string.Equals(callId, acceptedCall.CallId, StringComparison.Ordinal)
                || !string.Equals(toolName, acceptedCall.ToolName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Accepted call recovery material differs from the decision call identity.");
            }

            var canonicalPayload = CanonicalEvidenceJson.SerializeToUtf8Bytes(
                acceptedCall,
                ProtectedTurnInputStore.MaximumPlaintextBytes);
            try
            {
                var binding = TurnInputPurposes.AcceptedCallBinding(
                    acceptedCall.CallId,
                    acceptedCall.WorkItemId,
                    acceptedCall.ToolName,
                    acceptedCall.CapabilityId,
                    acceptedCall.RegistryRevisionDigest,
                    acceptedCall.ArgumentsDigest,
                    acceptedCall.ActionFingerprint,
                    acceptedCall.ExecutionClass,
                    acceptedCall.ReconcilerId);
                var payload = await StoreProtectedInputAsync(
                    identity,
                    TurnInputPurposes.AcceptedCall,
                    binding,
                    Encoding.UTF8.GetString(canonicalPayload),
                    cancellationToken).ConfigureAwait(false);
                acceptedCallReference = new AcceptedCallRecoveryReference(
                    acceptedCall.CallId,
                    acceptedCall.WorkItemId,
                    acceptedCall.ToolName,
                    acceptedCall.CapabilityId,
                    acceptedCall.RegistryRevisionDigest,
                    acceptedCall.ArgumentsDigest,
                    acceptedCall.ActionFingerprint,
                    acceptedCall.ExecutionClass,
                    acceptedCall.ReconcilerId,
                    payload);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(canonicalPayload);
            }
        }
        else if (acceptedCall is not null)
        {
            throw new InvalidDataException(
                "Only a tool-call planning decision may carry accepted-call recovery material.");
        }

        return await WriteAsync(
            identity,
            expectedRevision,
            new PlanningDecisionAcceptedTransition(
                correlationKey,
                decisionDigest,
                actionKind,
                callId,
                toolName,
                workGraphRevision,
                materialClaimsDigest,
                workGraph,
                acceptedCallReference),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcceptedCallRecoveryPayload> ReadPendingAcceptedCallAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        var state = await ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The requested turn does not exist.");
        var accepted = state.PendingAcceptedCall
            ?? throw new InvalidDataException("The turn has no unresolved accepted call.");
        accepted.Validate();
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        var json = await _inputStore.OpenAsync(
            identity,
            TurnInputPurposes.AcceptedCall,
            accepted.Payload,
            keys,
            cancellationToken).ConfigureAwait(false);
        AcceptedCallRecoveryPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<AcceptedCallRecoveryPayload>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("The protected accepted call is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The protected accepted call is invalid JSON.", ex);
        }

        payload.Validate();
        if (!string.Equals(payload.CallId, accepted.CallId, StringComparison.Ordinal)
            || !string.Equals(payload.WorkItemId, accepted.WorkItemId, StringComparison.Ordinal)
            || !string.Equals(payload.ToolName, accepted.ToolName, StringComparison.Ordinal)
            || !string.Equals(payload.CapabilityId, accepted.CapabilityId, StringComparison.Ordinal)
            || !FixedTimeDigestEquals(
                payload.RegistryRevisionDigest,
                accepted.RegistryRevisionDigest)
            || !FixedTimeDigestEquals(payload.ArgumentsDigest, accepted.ArgumentsDigest)
            || !FixedTimeDigestEquals(payload.ActionFingerprint, accepted.ActionFingerprint)
            || payload.ExecutionClass != accepted.ExecutionClass
            || !string.Equals(payload.ReconcilerId, accepted.ReconcilerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected accepted call differs from its authoritative state reference.");
        }

        return payload.CloneProtected();
    }

    public Task<TurnTransitionWriteResult> InterruptAcceptedCallAsync(
        TurnIdentity identity,
        long expectedRevision,
        string callId,
        string outcomeCode,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new AcceptedCallInterruptedTransition(correlationKey, callId, outcomeCode),
            cancellationToken);

    public Task<TurnTransitionWriteResult> ChangeControlAsync(
        TurnIdentity identity,
        long expectedRevision,
        TurnControlState control,
        string reasonCode,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new ControlChangedTransition(correlationKey, control, reasonCode),
            cancellationToken);

    public Task<TurnTransitionWriteResult> RevalidateRuntimeBindingsAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        string textDigest,
        TurnRuntimeBindings bindings,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new RuntimeBindingsRevalidatedTransition(
                correlationKey,
                publicationId,
                textDigest,
                bindings),
            cancellationToken);

    public Task<TurnTransitionWriteResult> PrepareActionAsync(
        TurnIdentity identity,
        long expectedRevision,
        PreparedActionIntent intent,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new ActionPreparedTransition(correlationKey, intent),
            cancellationToken);

    public Task<TurnTransitionWriteResult> RecordEvidenceAsync(
        TurnIdentity identity,
        long expectedRevision,
        CommittedEvidenceReference evidence,
        string? actionIdempotencyKey,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new EvidenceReferencedTransition(
                correlationKey,
                evidence,
                actionIdempotencyKey),
            cancellationToken);

    public Task<TurnTransitionWriteResult> RecordAcceptedCallEvidenceAsync(
        TurnIdentity identity,
        long expectedRevision,
        CommittedEvidenceReference evidence,
        string? actionIdempotencyKey,
        string callId,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new EvidenceReferencedTransition(
                correlationKey,
                evidence,
                actionIdempotencyKey,
                callId),
            cancellationToken);

    public Task<TurnTransitionWriteResult> CommitActionAsync(
        TurnIdentity identity,
        long expectedRevision,
        string actionIdempotencyKey,
        string evidenceId,
        long evidenceCursor,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new ActionCommittedTransition(
                correlationKey,
                actionIdempotencyKey,
                evidenceId,
                evidenceCursor),
            cancellationToken);

    public Task<TurnTransitionWriteResult> ResolveUnknownActionAsync(
        TurnIdentity identity,
        long expectedRevision,
        string sourceCommandId,
        string promptPublicationId,
        string promptTextDigest,
        string subjectId,
        long subjectPreparedRevision,
        ActionUserResolution resolution,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new UnknownActionResolvedByUserTransition(
                correlationKey,
                sourceCommandId,
                promptPublicationId,
                promptTextDigest,
                InterimPublicationReason.ActionReconciliationRequired,
                subjectId,
                subjectPreparedRevision,
                resolution),
            cancellationToken);

    public Task<TurnTransitionWriteResult> ResolveUnknownFinalPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string sourceCommandId,
        string promptPublicationId,
        string promptTextDigest,
        string subjectId,
        long subjectPreparedRevision,
        FinalPublicationUserResolution resolution,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new UnknownFinalPublicationResolvedByUserTransition(
                correlationKey,
                sourceCommandId,
                promptPublicationId,
                promptTextDigest,
                InterimPublicationReason.FinalPublicationReconciliationRequired,
                subjectId,
                subjectPreparedRevision,
                resolution),
            cancellationToken);

    public Task<TurnTransitionWriteResult> CancelInDoubtActionAsync(
        TurnIdentity identity,
        long expectedRevision,
        string actionIdempotencyKey,
        long subjectPreparedRevision,
        string outcomeCode,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new InDoubtActionCancelledTransition(
                correlationKey,
                actionIdempotencyKey,
                subjectPreparedRevision,
                outcomeCode),
            cancellationToken);

    public Task<TurnTransitionWriteResult> CancelInDoubtFinalPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        string assistantMessageId,
        string answerDigest,
        long subjectPreparedRevision,
        string outcomeCode,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new InDoubtFinalPublicationCancelledTransition(
                correlationKey,
                publicationId,
                assistantMessageId,
                answerDigest,
                subjectPreparedRevision,
                outcomeCode),
            cancellationToken);

    public async Task<TurnTransitionWriteResult> PrepareInterimPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        InterimPublicationKind kind,
        InterimPublicationReason reason,
        string subjectId,
        string text,
        string textDigest,
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        InterimPublicationPreparedTransition.ValidatePublication(
            publicationId,
            kind,
            reason,
            subjectId,
            textDigest);
        var actualDigest = TurnStateIntegrity.Digest(
            text ?? throw new ArgumentNullException(nameof(text)));
        if (!FixedTimeDigestEquals(actualDigest, textDigest))
        {
            throw new InvalidDataException(
                "The exact interim text does not match its proposed publication digest.");
        }

        var binding = TurnInputPurposes.InterimPublicationBinding(
            publicationId,
            kind,
            reason,
            subjectId,
            textDigest);
        var payload = await StoreProtectedInputAsync(
            identity,
            TurnInputPurposes.InterimPublicationText,
            binding,
            text,
            cancellationToken).ConfigureAwait(false);
        return await WriteAsync(
            identity,
            expectedRevision,
            new InterimPublicationPreparedTransition(
                correlationKey,
                publicationId,
                kind,
                reason,
                subjectId,
                textDigest,
                payload),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadInterimPublicationTextAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        var state = await ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The requested turn does not exist.");
        var interim = state.InterimPublication
            ?? throw new InvalidDataException("The turn has no prepared interim publication.");
        interim.Validate();
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        var text = await _inputStore.OpenAsync(
            identity,
            TurnInputPurposes.InterimPublicationText,
            interim.TextPayload,
            keys,
            cancellationToken).ConfigureAwait(false);
        if (!FixedTimeDigestEquals(TurnStateIntegrity.Digest(text), interim.TextDigest))
        {
            throw new InvalidDataException(
                "Recovered interim text differs from its committed digest.");
        }

        return text;
    }

    public Task<TurnTransitionWriteResult> CommitInterimPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        InterimPublicationKind kind,
        string textDigest,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new InterimPublicationCommittedTransition(
                correlationKey,
                publicationId,
                kind,
                textDigest),
            cancellationToken);

    public Task<TurnTransitionWriteResult> ClearInterimPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        InterimPublicationKind kind,
        string textDigest,
        string outcomeCode,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new InterimPublicationClearedTransition(
                correlationKey,
                publicationId,
                kind,
                textDigest,
                outcomeCode),
            cancellationToken);

    public async Task<TurnTransitionWriteResult> PrepareFinalPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        string assistantMessageId,
        string answerText,
        string answerDigest,
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        FinalPublicationPreparedTransition.ValidatePublication(
            publicationId,
            assistantMessageId,
            answerDigest);
        var actualDigest = TurnStateIntegrity.Digest(
            answerText ?? throw new ArgumentNullException(nameof(answerText)));
        if (!FixedTimeDigestEquals(actualDigest, answerDigest))
        {
            throw new InvalidDataException(
                "The exact final answer does not match its proposed publication digest.");
        }

        var payloadBinding = TurnInputPurposes.FinalPublicationBinding(
            publicationId,
            assistantMessageId,
            answerDigest);
        var answerPayload = await StoreProtectedInputAsync(
            identity,
            TurnInputPurposes.FinalPublicationAnswer,
            payloadBinding,
            answerText,
            cancellationToken).ConfigureAwait(false);
        return await WriteAsync(
            identity,
            expectedRevision,
            new FinalPublicationPreparedTransition(
                correlationKey,
                publicationId,
                assistantMessageId,
                answerDigest,
                answerPayload),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadFinalPublicationAnswerAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        var state = await ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The requested turn does not exist.");
        var publication = state.FinalPublication
            ?? throw new InvalidDataException("The turn has no prepared final publication.");
        publication.Validate();
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        var answer = await _inputStore.OpenAsync(
            identity,
            TurnInputPurposes.FinalPublicationAnswer,
            publication.AnswerPayload,
            keys,
            cancellationToken).ConfigureAwait(false);
        if (!FixedTimeDigestEquals(TurnStateIntegrity.Digest(answer), publication.AnswerDigest))
        {
            throw new InvalidDataException(
                "The recovered final answer does not match its committed publication digest.");
        }

        return answer;
    }

    public async Task<TurnTransitionWriteResult> RetargetFinalPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string previousPublicationId,
        string previousAssistantMessageId,
        string newPublicationId,
        string newAssistantMessageId,
        string answerDigest,
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        FinalPublicationPreparedTransition.ValidatePublication(
            previousPublicationId,
            previousAssistantMessageId,
            answerDigest);
        FinalPublicationPreparedTransition.ValidatePublication(
            newPublicationId,
            newAssistantMessageId,
            answerDigest);
        var state = await ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The requested turn does not exist.");
        if (state.Revision != expectedRevision)
        {
            return new TurnTransitionWriteResult(
                TurnTransitionWriteStatus.RevisionConflict,
                state,
                Entry: null);
        }

        var publication = state.FinalPublication
            ?? throw new InvalidDataException("The turn has no final publication to retarget.");
        if (publication.Status != FinalPublicationStatus.ConfirmedAbsent
            || !string.Equals(
                publication.PublicationId,
                previousPublicationId,
                StringComparison.Ordinal)
            || !string.Equals(
                publication.AssistantMessageId,
                previousAssistantMessageId,
                StringComparison.Ordinal)
            || !FixedTimeDigestEquals(publication.AnswerDigest, answerDigest))
        {
            throw new InvalidDataException(
                "The final publication retarget does not match authoritative confirmed-absent state.");
        }

        var answer = await ReadFinalPublicationAnswerAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (!FixedTimeDigestEquals(TurnStateIntegrity.Digest(answer), answerDigest))
        {
            throw new InvalidDataException(
                "The final publication changed while its retarget payload was being prepared.");
        }
        var binding = TurnInputPurposes.FinalPublicationBinding(
            newPublicationId,
            newAssistantMessageId,
            answerDigest);
        var payload = await StoreProtectedInputAsync(
            identity,
            TurnInputPurposes.FinalPublicationAnswer,
            binding,
            answer,
            cancellationToken).ConfigureAwait(false);
        return await WriteAsync(
            identity,
            expectedRevision,
            new FinalPublicationRetargetedTransition(
                correlationKey,
                previousPublicationId,
                previousAssistantMessageId,
                newPublicationId,
                newAssistantMessageId,
                answerDigest,
                payload),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<TurnTransitionWriteResult> CommitFinalPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        string assistantMessageId,
        string answerDigest,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new FinalPublicationCommittedTransition(
                correlationKey,
                publicationId,
                assistantMessageId,
                answerDigest),
            cancellationToken);

    public Task<TurnTransitionWriteResult> ConfirmFinalPublicationAbsentAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        string assistantMessageId,
        string answerDigest,
        string outcomeCode,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new FinalPublicationConfirmedAbsentTransition(
                correlationKey,
                publicationId,
                assistantMessageId,
                answerDigest,
                outcomeCode),
            cancellationToken);

    public Task<TurnTransitionWriteResult> AbandonFinalPublicationAsync(
        TurnIdentity identity,
        long expectedRevision,
        string publicationId,
        string assistantMessageId,
        string answerDigest,
        string outcomeCode,
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            identity,
            expectedRevision,
            new FinalPublicationAbandonedTransition(
                correlationKey,
                publicationId,
                assistantMessageId,
                answerDigest,
                outcomeCode),
            cancellationToken);

    internal TurnJournalDiagnostics GetDiagnostics(TurnIdentity identity) =>
        GetJournal(identity).Diagnostics;

    private async Task ValidateCommittedReferenceAsync(
        TurnIdentity identity,
        TurnTransition transition,
        CancellationToken cancellationToken)
    {
        switch (transition)
        {
            case TurnStartedTransition started:
                await ValidateProtectedInputAsync(
                    identity,
                    TurnInputPurposes.OriginalRequest,
                    started.OriginalRequest,
                    cancellationToken).ConfigureAwait(false);
                foreach (var entry in started.AcceptedPriorConversation)
                {
                    await ValidateProtectedInputAsync(
                        identity,
                        TurnInputPurposes.AcceptedConversationEntry,
                        entry.Payload,
                        cancellationToken).ConfigureAwait(false);
                }
                break;
            case SteeringAppendedTransition steering:
                await ValidateProtectedInputAsync(
                    identity,
                    TurnInputPurposes.Steering,
                    steering.Steering,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FinalPublicationPreparedTransition publication:
                publication.AnswerPayload.Validate(
                    TurnInputPurposes.FinalPublicationAnswer,
                    TurnInputPurposes.FinalPublicationBinding(
                        publication.PublicationId,
                        publication.AssistantMessageId,
                        publication.AnswerDigest));
                await ValidateProtectedInputAsync(
                    identity,
                    TurnInputPurposes.FinalPublicationAnswer,
                    publication.AnswerPayload,
                    cancellationToken).ConfigureAwait(false);
                break;
            case InterimPublicationPreparedTransition publication:
                publication.TextPayload.Validate(
                    TurnInputPurposes.InterimPublicationText,
                    TurnInputPurposes.InterimPublicationBinding(
                        publication.PublicationId,
                        publication.Kind,
                        publication.Reason,
                        publication.SubjectId,
                        publication.TextDigest));
                await ValidateProtectedInputAsync(
                    identity,
                    TurnInputPurposes.InterimPublicationText,
                    publication.TextPayload,
                    cancellationToken).ConfigureAwait(false);
                break;
            case FinalPublicationRetargetedTransition publication:
                publication.AnswerPayload.Validate(
                    TurnInputPurposes.FinalPublicationAnswer,
                    TurnInputPurposes.FinalPublicationBinding(
                        publication.NewPublicationId,
                        publication.NewAssistantMessageId,
                        publication.AnswerDigest));
                await ValidateProtectedInputAsync(
                    identity,
                    TurnInputPurposes.FinalPublicationAnswer,
                    publication.AnswerPayload,
                    cancellationToken).ConfigureAwait(false);
                break;
            case EvidenceReferencedTransition evidence:
                if (!await _referenceValidator.IsEvidenceCommittedAsync(
                        identity,
                        evidence.Evidence,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "The evidence reference is not committed in the authoritative evidence ledger.");
                }
                break;
            case WorkGraphReferencedTransition workGraph:
                if (!await _referenceValidator.IsWorkGraphCommittedAsync(
                        identity,
                        workGraph.WorkGraph,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "The work-graph reference is not committed in the authoritative work store.");
                }
                break;
            case PlanningDecisionAcceptedTransition decision:
                if (decision.WorkGraph is not null
                    && !await _referenceValidator.IsWorkGraphCommittedAsync(
                        identity,
                        decision.WorkGraph,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "The planning decision's work-graph reference is not committed in the authoritative work store.");
                }
                if (decision.AcceptedCall is not null)
                {
                    await ValidateProtectedInputAsync(
                        identity,
                        TurnInputPurposes.AcceptedCall,
                        decision.AcceptedCall.Payload,
                        cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    private static AcceptedConversationInput[] ValidateAcceptedConversation(
        IEnumerable<AcceptedConversationInput>? acceptedPriorConversation)
    {
        var entries = (acceptedPriorConversation ?? [])
            .OrderBy(entry => entry.Sequence)
            .ToArray();
        if (entries.Length > TurnStateLimits.MaximumAcceptedPriorConversationEntries)
        {
            throw new InvalidDataException("The accepted prior conversation exceeds its entry limit.");
        }

        var sourceMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        long aggregateBytes = 0;
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            entry.Validate();
            if (!sourceMessageIds.Add(entry.SourceMessageId) || !sequences.Add(entry.Sequence))
            {
                throw new InvalidDataException(
                    "The accepted prior conversation contains a duplicate source identity or sequence.");
            }

            aggregateBytes = checked(aggregateBytes + Encoding.UTF8.GetByteCount(entry.Text));
        }

        if (aggregateBytes > TurnStateLimits.MaximumAcceptedPriorConversationUtf8Bytes)
        {
            throw new InvalidDataException(
                "The accepted prior conversation exceeds its protected byte limit.");
        }

        return entries;
    }

    private async Task<ProtectedTurnInputReference> StoreProtectedInputAsync(
        TurnIdentity identity,
        string purpose,
        string correlationKey,
        string plaintext,
        CancellationToken cancellationToken)
    {
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        return await _inputStore.StoreAsync(
            identity,
            purpose,
            correlationKey,
            plaintext,
            keys,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool FixedTimeDigestEquals(string left, string right)
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
                   && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                       leftBytes,
                       rightBytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(leftBytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private async Task ValidateProtectedInputAsync(
        TurnIdentity identity,
        string purpose,
        ProtectedTurnInputReference reference,
        CancellationToken cancellationToken)
    {
        var keys = await GetKeySessionAsync(cancellationToken).ConfigureAwait(false);
        _ = await _inputStore.OpenAsync(
            identity,
            purpose,
            reference,
            keys,
            cancellationToken).ConfigureAwait(false);
    }

    private TurnTransitionJournal GetJournal(TurnIdentity identity)
    {
        var storageKey = identity.StorageKey;
        lock (_journalCacheSync)
        {
            if (_journals.TryGetValue(storageKey, out var cached))
            {
                _journalLru.Remove(cached.Node);
                _journalLru.AddFirst(cached.Node);
                return cached.Journal;
            }

            if (_journals.Count >= MaximumCachedTurnJournals)
            {
                var oldest = _journalLru.Last
                    ?? throw new InvalidOperationException("The turn-journal cache is inconsistent.");
                _journalLru.RemoveLast();
                _journals.Remove(oldest.Value);
            }

            var node = _journalLru.AddFirst(storageKey);
            var journal = new TurnTransitionJournal(
                Path.Combine(_rootDirectory, "turns", storageKey),
                storageKey,
                GetKeySessionAsync,
                _faultInjector);
            _journals.Add(storageKey, (journal, node));
            return journal;
        }
    }

    internal int CaptureCachedTurnJournalCount()
    {
        lock (_journalCacheSync)
        {
            return _journals.Count;
        }
    }

    private async Task<EvidenceKeySession> GetKeySessionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
                current = await _integrityProtector
                    .OpenSessionAsync(cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _keySession, current);
            }

            return current;
        }
        finally
        {
            _keyGate.Release();
        }
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
        lock (_journalCacheSync)
        {
            _journals.Clear();
            _journalLru.Clear();
        }
    }

    private sealed class RejectingReferenceValidator : ITurnCommittedReferenceValidator
    {
        public static RejectingReferenceValidator Instance { get; } = new();

        public ValueTask<bool> IsEvidenceCommittedAsync(
            TurnIdentity identity,
            CommittedEvidenceReference evidence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> IsWorkGraphCommittedAsync(
            TurnIdentity identity,
            CommittedWorkGraphReference workGraph,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}
