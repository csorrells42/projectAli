using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration;

/// <summary>
/// Owns the durable V2 stores used by the single Agent Framework loop. A visible turn gets one
/// transition observer; the observer must durably accept a decision before the planner can emit
/// a real task-tool call.
/// </summary>
internal sealed partial class AliPlanningStateCoordinator : IDisposable
{
    private const string UnresolvedLocalUserId = "ali-local-unresolved-user";
    private readonly EvidenceLedger _evidenceLedger;
    private readonly DurableWorkGraphStore _workGraphStore;
    private readonly DurablePlanningReferenceValidator _referenceValidator;
    private readonly TurnTransitionWriter _transitionWriter;
    private readonly TurnRecoveryService _recoveryService;
    private readonly DurablePausedTurnCatalog _pausedTurnCatalog;
    private readonly EffectNormalizationRegistry _effectNormalizations;
    private readonly TargetStateRegistry _targetStates;
    private readonly AliDurableEffectAdapterRegistry _durableEffects;
    private int _disposed;

    internal AliPlanningStateCoordinator(
        string rootDirectory,
        string assistantProfileBinding,
        ITurnPublicationReconciler? publicationReconciler = null,
        EffectNormalizationRegistry? effectNormalizations = null,
        TargetStateRegistry? targetStates = null,
        AliDurableEffectAdapterRegistry? durableEffects = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        var root = Path.GetFullPath(rootDirectory);
        _evidenceLedger = new EvidenceLedger(root, assistantProfileBinding);
        _workGraphStore = new DurableWorkGraphStore(root, assistantProfileBinding);
        _referenceValidator = new DurablePlanningReferenceValidator(
            _evidenceLedger,
            _workGraphStore);
        _transitionWriter = new TurnTransitionWriter(
            root,
            assistantProfileBinding,
            _referenceValidator);
        _pausedTurnCatalog = new DurablePausedTurnCatalog(root, assistantProfileBinding);
        _effectNormalizations = effectNormalizations ?? EffectNormalizationRegistry.Empty;
        _targetStates = targetStates ?? TargetStateRegistry.Empty;
        _durableEffects = durableEffects ?? AliDurableEffectAdapterRegistry.Empty;
        _recoveryService = new TurnRecoveryService(
            _transitionWriter,
            _durableEffects.Reconcilers,
            publicationReconciler);
    }

    internal async Task<TurnRecoveryResult> RecoverTurnAsync(
        TurnIdentity identity,
        TurnRuntimeBindings currentBindings,
        bool explicitlyRequested,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _evidenceLedger.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return await _recoveryService.RecoverAsync(
            identity,
            currentBindings,
            explicitlyRequested,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AliDurablePlanningTurn> BeginTurnAsync(
        CoordinatorTurnContext turn,
        TurnRuntimeBindings bindings,
        IEnumerable<AcceptedConversationInput>? acceptedPriorConversation,
        CanonicalCapabilityRegistry? capabilityRegistry,
        Func<TurnRuntimeBindings>? liveBindingsAccessor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(bindings);
        var identity = turn.ObservationIdentity ?? new TurnIdentity(
            UnresolvedLocalUserId,
            turn.ConversationId,
            turn.AssistantMessageId);
        var acceptedConversation = (acceptedPriorConversation ?? []).ToArray();
        await _evidenceLedger.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _transitionWriter
            .ReadAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "This orchestration turn already has durable state. Resume requires the explicit recovery path.");
        }

        // Admission is indexed before the first state write. A crash can therefore leave at
        // worst a discoverable empty admission (which lookup safely removes), never an
        // undiscoverable running/prepared turn.
        await _pausedTurnCatalog.RecordAsync(identity, cancellationToken).ConfigureAwait(false);

        TurnTransitionWriteResult started;
        try
        {
            started = await _transitionWriter.StartAsync(
                identity,
                turn.OriginalUserText,
                bindings,
                Correlation(identity, "turn-start", TurnStateIntegrity.Digest(turn.OriginalUserText), 0),
                acceptedConversation,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Start can fail after its journal boundary has become durable (for example, a
            // process interruption/fault injected after commit). Remove the admission only when
            // a non-cancellable exact read proves there is no state to recover. On uncertainty,
            // retain it so recovery remains discoverable.
            try
            {
                if (await _transitionWriter.ReadAsync(identity, CancellationToken.None)
                        .ConfigureAwait(false) is null)
                {
                    await _pausedTurnCatalog.RemoveExactAsync(identity, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Preserve the original start failure and fail closed by retaining admission.
            }

            throw;
        }
        if (started.Status != TurnTransitionWriteStatus.Committed || started.State is null)
        {
            if (started.State is null)
            {
                await _pausedTurnCatalog.RemoveExactAsync(identity, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                "Ali could not establish the authoritative durable state for this turn.");
        }

        return new AliDurablePlanningTurn(
            turn,
            identity,
            bindings,
            started.State,
            WorkGraphSnapshot.Empty,
            acceptedConversation.Select(entry => new AcceptedConversationContextEntry(
                entry.Sequence,
                entry.Text,
                entry.Role,
                isSteering: false)),
            capabilityRegistry,
            _evidenceLedger,
            _workGraphStore,
            _referenceValidator,
            _transitionWriter,
            _effectNormalizations,
            _targetStates,
            _durableEffects,
            liveBindingsAccessor ?? (() => bindings));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transitionWriter.Dispose();
        _workGraphStore.Dispose();
    }

    internal static string Correlation(
        TurnIdentity identity,
        string kind,
        string subject,
        long revision) =>
        TurnStateIntegrity.Digest(
            identity.StorageKey + "\0" + kind + "\0" + subject + "\0" + revision);
}

internal sealed record WorkGraphConsumerDiagnostics(
    long ProjectionCalls,
    long ProjectionIndexIdsVisited,
    long ProgressVectorCalls,
    long DependencyDigestCalls,
    long ActiveSelectionCalls,
    bool CurrentAnalysisCacheHit,
    WorkGraphAnalysisDiagnostics CurrentAnalysis,
    WorkGraphApplyDiagnostics? LastApply,
    WorkGraphStoreDiagnostics Store);

internal sealed partial class AliDurablePlanningTurn :
    IAliPlanningTransitionObserver,
    IAliPlanningEvidenceAuthority,
    ICoordinatorActionExecutionAuthority,
    IAsyncDisposable
{
    private const int MaximumProjectedWorkItems = 64;
    private const int MaximumStateProjectionCharacters = 32_000;
    private const int MaximumWorkProjectionCharacters = 24_000;
    private const int MaximumProjectedNodeReferences = 16;
    internal const int MaximumRetainedEvidenceProjections = 64;
    private const int MaximumProjectedUserResolutions = 16;
    private readonly CoordinatorTurnContext _turn;
    private readonly TurnIdentity _identity;
    private readonly TurnRuntimeBindings _bindings;
    private readonly IReadOnlyList<AcceptedConversationContextEntry> _priorConversation;
    private readonly IReadOnlyDictionary<string, CapabilityDescriptor> _descriptors;
    private readonly EvidenceLedger _evidenceLedger;
    private readonly DurableWorkGraphStore _workGraphStore;
    private readonly DurablePlanningReferenceValidator _referenceValidator;
    private readonly TurnTransitionWriter _writer;
    private readonly EffectNormalizationRegistry _effectNormalizations;
    private readonly TargetStateRegistry _targetStates;
    private readonly AliDurableEffectAdapterRegistry _durableEffects;
    private readonly Func<TurnRuntimeBindings> _liveBindingsAccessor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PreparedCall> _preparedCalls = new(StringComparer.Ordinal);
    private readonly List<AcceptedEvidenceProjection> _evidence = [];
    private readonly List<TurnUserResolutionProjection> _userResolutions = [];
    private readonly HashSet<string> _knownEvidenceIds = new(StringComparer.Ordinal);
    private ProgressHistory _progressHistory = ProgressHistory.Empty;
    private TurnState _state;
    private WorkGraphSnapshot _workGraph;
    private WorkGraphApplyDiagnostics? _lastWorkGraphApplyDiagnostics;
    private long _workGraphProjectionCalls;
    private long _workGraphProjectionIndexIdsVisited;
    private long _workGraphProgressVectorCalls;
    private long _workGraphDependencyDigestCalls;
    private long _workGraphActiveSelectionCalls;
    private ProgressReason? _lastProgressReason;
    private FinalPublicationState? _preparedPublication;
    private int _disposed;

    internal AliDurablePlanningTurn(
        CoordinatorTurnContext turn,
        TurnIdentity identity,
        TurnRuntimeBindings bindings,
        TurnState state,
        WorkGraphSnapshot workGraph,
        IEnumerable<AcceptedConversationContextEntry>? priorConversation,
        CanonicalCapabilityRegistry? capabilityRegistry,
        EvidenceLedger evidenceLedger,
        DurableWorkGraphStore workGraphStore,
        DurablePlanningReferenceValidator referenceValidator,
        TurnTransitionWriter writer,
        EffectNormalizationRegistry effectNormalizations,
        TargetStateRegistry targetStates,
        AliDurableEffectAdapterRegistry durableEffects,
        Func<TurnRuntimeBindings> liveBindingsAccessor)
    {
        _turn = turn;
        _identity = identity;
        _bindings = bindings;
        _state = state;
        _workGraph = workGraph;
        _priorConversation = (priorConversation ?? []).ToArray();
        _descriptors = (capabilityRegistry?.Descriptors ?? [])
            .GroupBy(item => item.ToolName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _evidenceLedger = evidenceLedger;
        _workGraphStore = workGraphStore;
        _referenceValidator = referenceValidator;
        _writer = writer;
        _effectNormalizations = effectNormalizations
            ?? throw new ArgumentNullException(nameof(effectNormalizations));
        _targetStates = targetStates
            ?? throw new ArgumentNullException(nameof(targetStates));
        _durableEffects = durableEffects
            ?? throw new ArgumentNullException(nameof(durableEffects));
        _liveBindingsAccessor = liveBindingsAccessor
            ?? throw new ArgumentNullException(nameof(liveBindingsAccessor));
    }

    internal AliPlanningTurnInput Input => CreatePlanningInput();

    TurnIdentity ICoordinatorActionExecutionAuthority.DurableIdentity => _identity;

    internal WorkGraphConsumerDiagnostics CaptureWorkGraphConsumerDiagnostics()
    {
        var analysis = RequireWorkGraphAnalysis(_workGraph, out var cacheHit);
        return new WorkGraphConsumerDiagnostics(
            ProjectionCalls: Interlocked.Read(ref _workGraphProjectionCalls),
            ProjectionIndexIdsVisited: Interlocked.Read(
                ref _workGraphProjectionIndexIdsVisited),
            ProgressVectorCalls: Interlocked.Read(ref _workGraphProgressVectorCalls),
            DependencyDigestCalls: Interlocked.Read(ref _workGraphDependencyDigestCalls),
            ActiveSelectionCalls: Interlocked.Read(ref _workGraphActiveSelectionCalls),
            CurrentAnalysisCacheHit: cacheHit,
            CurrentAnalysis: analysis.Diagnostics,
            LastApply: Volatile.Read(ref _lastWorkGraphApplyDiagnostics),
            Store: _workGraphStore.Diagnostics);
    }

    async Task<IReadOnlyDictionary<string, AcceptedEvidenceProjection>>
        IAliPlanningEvidenceAuthority.ResolveEvidenceAsync(
            IReadOnlyCollection<string> evidenceIds,
            CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveEvidenceCoreAsync(evidenceIds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AliPlanningPassAuthorization> OnPlanningPassStartingAsync(
        AliPlanningPassStartingEvent starting,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateEventIdentity(
                starting.ConversationId,
                starting.AssistantMessageId,
                starting.ExpectedStateRevision);
            IReadOnlyList<string> changedBindings;
            try
            {
                var current = starting.DispatchBindings
                    ?? throw new InvalidOperationException(
                        "The planning pass did not supply its exact bound dispatch snapshot.");
                changedBindings = _bindings.FindChangedBindings(current);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       and not OutOfMemoryException)
            {
                changedBindings = Array.AsReadOnly(
                    new[] { "dispatch-bindings-unavailable-" + ex.GetType().Name });
            }

            if (changedBindings.Count == 0)
            {
                return new AliPlanningPassAuthorization(
                    CanPlan: true,
                    _state.Revision,
                    BuildStateProjection(),
                    Array.Empty<string>());
            }

            return new AliPlanningPassAuthorization(
                CanPlan: false,
                _state.Revision,
                BuildStateProjection(),
                changedBindings);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<AliCompletionDispatchAuthorization> AuthorizeCompletionDispatchAsync(
        long expectedStateRevision,
        TurnRuntimeBindings currentBindings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(currentBindings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refused = new List<string>();
            if (_state.Revision != expectedStateRevision)
            {
                refused.Add("durable-state-revision");
            }

            if (_state.Control != TurnControlState.Running)
            {
                refused.Add("durable-turn-control");
            }

            if (_state.PendingActions.Length != 0)
            {
                refused.Add("pending-action");
            }

            if (_state.PendingAcceptedCall is not null)
            {
                refused.Add("pending-accepted-call");
            }

            if (_state.InterimPublication is not null)
            {
                refused.Add("interim-publication");
            }

            if (_state.FinalPublication is not null)
            {
                refused.Add("final-publication");
            }

            try
            {
                refused.AddRange(_bindings.FindChangedBindings(currentBindings));
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       and not OutOfMemoryException)
            {
                refused.Add("dispatch-bindings-unavailable-" + ex.GetType().Name);
            }

            var exactRefusals = refused
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new AliCompletionDispatchAuthorization(
                CanCompose: exactRefusals.Length == 0,
                StateRevision: _state.Revision,
                ChangedBindings: Array.AsReadOnly(exactRefusals));
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CommitFinalPublicationAsync(
        string renderedAnswer,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderedAnswer);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.PendingActions.Length != 0
                || _state.PendingAcceptedCall is not null
                || _state.InterimPublication is not null)
            {
                throw new InvalidOperationException(
                    "A final answer cannot be committed while an action, accepted call, or interim publication remains unresolved.");
            }

            var publication = _preparedPublication
                ?? throw new InvalidOperationException(
                    "The rendered answer has no prepared durable publication receipt.");
            var digest = TurnStateIntegrity.Digest(renderedAnswer);
            if (!FixedTimeDigestEquals(digest, publication.AnswerDigest))
            {
                throw new InvalidDataException(
                    "The rendered answer differs from the exact prepared publication.");
            }

            var committed = await _writer.CommitFinalPublicationAsync(
                _identity,
                _state.Revision,
                publication.PublicationId,
                publication.AssistantMessageId,
                publication.AnswerDigest,
                AliPlanningStateCoordinator.Correlation(
                    _identity,
                    "publication-commit",
                    publication.PublicationId + ":" + publication.AnswerDigest,
                    publication.PreparedAtRevision),
                cancellationToken).ConfigureAwait(false);
            _state = RequireCommitted(committed, "final publication");
            _preparedPublication = _state.FinalPublication;

            if (_state.Control == TurnControlState.Running)
            {
                var completed = await _writer.ChangeControlAsync(
                    _identity,
                    _state.Revision,
                    TurnControlState.Completed,
                    "answer-published",
                    AliPlanningStateCoordinator.Correlation(
                        _identity,
                        "turn-completed",
                        publication.PublicationId,
                        _state.Revision),
                    cancellationToken).ConfigureAwait(false);
                _state = RequireCommitted(completed, "turn completion");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AliPlanningTransitionReceipt> OnDecisionAcceptedAsync(
        AliPlanningDecisionAcceptedEvent accepted,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateEventIdentity(
                accepted.ConversationId,
                accepted.AssistantMessageId,
                accepted.ExpectedStateRevision);

            var committedWorkGraph = accepted.Decision.WorkUpdate is null
                ? null
                : await CommitWorkUpdateAsync(
                    accepted.Decision.WorkUpdate,
                    cancellationToken).ConfigureAwait(false);
            var candidateWorkGraph = committedWorkGraph?.Snapshot ?? _workGraph;
            var acceptedWorkGraphRevision = committedWorkGraph?.Snapshot.Revision
                ?? _workGraph.Revision;

            var decisionBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(accepted.Decision);
            var claimsBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
                accepted.Decision.MaterialClaims);
            string decisionDigest;
            string claimsDigest;
            try
            {
                decisionDigest = TurnStateIntegrity.Digest(decisionBytes);
                claimsDigest = TurnStateIntegrity.Digest(claimsBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decisionBytes);
                CryptographicOperations.ZeroMemory(claimsBytes);
            }

            PreparedCallPreparation? callPreparation = null;
            if (accepted.Decision.NextAction is CallToolAction call)
            {
                callPreparation = PrepareCall(
                    accepted.CallId
                    ?? throw new InvalidDataException("An accepted tool call has no call ID."),
                    call,
                    decisionDigest,
                    candidateWorkGraph,
                    cancellationToken);
            }

            var recorded = await _writer.RecordPlanningDecisionAcceptedAsync(
                _identity,
                _state.Revision,
                decisionDigest,
                ToAcceptedActionKind(accepted.Decision.NextAction),
                accepted.CallId,
                accepted.ToolName,
                acceptedWorkGraphRevision,
                claimsDigest,
                AliPlanningStateCoordinator.Correlation(
                    _identity,
                    "planning-decision",
                    decisionDigest,
                    _state.Revision),
                acceptedCall: callPreparation?.Recovery,
                workGraph: committedWorkGraph?.Reference,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (committedWorkGraph is not null
                && recorded.Status == TurnTransitionWriteStatus.RevisionConflict)
            {
                // The immutable graph candidate was created before the journal CAS. If another
                // transition won that CAS, retain only the exact graph chain selected by the
                // authoritative state and discard this now-unreachable candidate.
                await _workGraphStore.PruneUnreachableAsync(
                    _identity,
                    recorded.State?.WorkGraphReference,
                    cancellationToken).ConfigureAwait(false);
            }

            _state = RequireCommitted(recorded, "planning decision");
            if (committedWorkGraph is not null)
            {
                _workGraph = committedWorkGraph.Snapshot;
                if (_state.WorkGraphReference != committedWorkGraph.Reference)
                {
                    throw new InvalidDataException(
                        "The accepted work graph differs from the exact durable state reference.");
                }

                if (committedWorkGraph.Reference.Revision
                    % DurableWorkGraphStore.MaximumDeltaChainLength == 0)
                {
                    // The state journal is authoritative. Once it selects a complete checkpoint,
                    // older deltas and losing immutable candidates can be reclaimed without
                    // adding directory work to every planning pass.
                    await _workGraphStore.PruneUnreachableAsync(
                        _identity,
                        _state.WorkGraphReference,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var requiresFreshPass = false;
            if (callPreparation is not null)
            {
                if (!callPreparation.Assessment.CanExecute)
                {
                    _lastProgressReason = callPreparation.Assessment.Reason;
                    var interrupted = await _writer.InterruptAcceptedCallAsync(
                        _identity,
                        _state.Revision,
                        callPreparation.Call.CallId,
                        "progress-" + callPreparation.Assessment.Reason,
                        AliPlanningStateCoordinator.Correlation(
                            _identity,
                            "accepted-call-progress-interrupted",
                            callPreparation.Call.CallId + ":" + callPreparation.Call.ActionIdentity.Fingerprint,
                            revision: 0),
                        cancellationToken).ConfigureAwait(false);
                    _state = RequireCommitted(interrupted, "blocked accepted call interruption");
                    requiresFreshPass = true;
                }
                else
                {
                    _preparedCalls.Add(callPreparation.Call.CallId, callPreparation.Call);
                }
            }

            return new AliPlanningTransitionReceipt(
                _state.Revision,
                BuildStateProjection(),
                _workGraph.Revision,
                requiresFreshPass,
                _workGraph);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AliPlanningEvidenceReceipt> OnToolResultObservedAsync(
        AliPlanningToolResultObservedEvent observed,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateEventIdentity(
                observed.ConversationId,
                observed.AssistantMessageId,
                observed.ExpectedStateRevision);
            if (!_preparedCalls.TryGetValue(observed.CallId, out var prepared)
                || !string.Equals(prepared.ToolName, observed.ToolName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A tool terminal cannot be committed without its matching prepared action.");
            }

            var observedArguments = CanonicalEvidenceJson.SerializeToUtf8Bytes(observed.Arguments);
            try
            {
                var observedDigest = TurnStateIntegrity.Digest(observedArguments);
                if (!FixedTimeDigestEquals(observedDigest, prepared.ArgumentsDigest))
                {
                    throw new InvalidDataException(
                        "The tool terminal arguments differ from the prepared action.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(observedArguments);
            }

            var outcome = CreateOutcome(observed);
            var resultKind = observed.DomainOutcome == PlanningToolDomainOutcome.Succeeded
                ? EffectResultKind.Applied
                : EffectResultKind.NoEffect;
            var normalizedEffect = _effectNormalizations.NormalizeOutcome(
                prepared.EffectNormalization,
                observed.Result,
                outcome,
                resultKind);
            var descriptor = prepared.Descriptor;
            var permission = prepared.Permission is null
                ? ResolvePermission(observed.CallId, observed.ToolName, descriptor)
                : prepared.Permission with { };
            var evidenceDraft = new EvidenceDraft
            {
                EvidenceId = observed.ProposedEvidenceId,
                WorkItemId = prepared.WorkItemId,
                CallId = observed.CallId,
                ToolName = observed.ToolName,
                CapabilityGroup = descriptor?.GroupId ?? "runtime-tool",
                ProviderId = descriptor?.ProviderId ?? "agent-framework",
                RegistryRevision = _bindings.CapabilityRegistryDigest,
                EffectKind = EvidenceEffectKind(descriptor?.Effect.Kind),
                Arguments = observed.Arguments.Clone(),
                Result = observed.Result.Clone(),
                NormalizedTarget = prepared.EffectNormalization.NormalizedTarget,
                NormalizedEffectResult = normalizedEffect.NormalizedDomainOutcome,
                Outcome = outcome,
                StableOutcomeCode = normalizedEffect.NormalizedCode,
                StartedAtUtc = observed.StartedAtUtc.ToUniversalTime(),
                CompletedAtUtc = observed.CompletedAtUtc.ToUniversalTime(),
                Artifacts = [],
                Permission = permission,
                ProtectedPermissionReceipt = JsonSerializer.SerializeToElement(new
                {
                    permissionRevisionDigest = _bindings.PermissionDigest,
                    permission.Decision,
                    permission.Scope,
                    prepared.ExecutionPermissionReceiptDigest
                }),
                Source = new EvidenceSourceMetadata(
                    "tool",
                    descriptor?.ProviderId ?? "agent-framework",
                    "unknown",
                    observed.CompletedAtUtc.ToUniversalTime(),
                    _state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ProtectedProvenance = JsonSerializer.SerializeToElement(new
                {
                    prepared.DecisionDigest,
                    idempotencyKey = prepared.Intent?.IdempotencyKey,
                    prepared.ExecutionRegistryIdentityDigest,
                    prepared.ExecutionPermissionReceiptDigest,
                    observer = "orchestration-v2"
                })
            };
            var evidence = await _evidenceLedger.AppendAsync(
                _identity,
                evidenceDraft,
                cancellationToken).ConfigureAwait(false);
            var evidenceReference = new CommittedEvidenceReference(
                evidence.Evidence.EvidenceId,
                evidence.Cursor,
                evidence.Checksum);
            var referenced = await _writer.RecordAcceptedCallEvidenceAsync(
                _identity,
                _state.Revision,
                evidenceReference,
                prepared.Intent?.IdempotencyKey,
                prepared.CallId,
                AliPlanningStateCoordinator.Correlation(
                    _identity,
                    "evidence-reference",
                    evidence.Evidence.EvidenceId + ":" + evidence.Checksum,
                    _state.Revision),
                cancellationToken).ConfigureAwait(false);
            _state = RequireCommitted(referenced, "evidence reference");
            if (prepared.Intent is { } intent)
            {
                if (IsAuthoritativeActionCompletion(observed, prepared))
                {
                    var committed = await _writer.CommitActionAsync(
                        _identity,
                        _state.Revision,
                        intent.IdempotencyKey,
                        evidence.Evidence.EvidenceId,
                        evidence.Cursor,
                        AliPlanningStateCoordinator.Correlation(
                            _identity,
                            "action-commit",
                            intent.IdempotencyKey + ":" + evidence.Evidence.EvidenceId,
                            _state.Revision),
                        cancellationToken).ConfigureAwait(false);
                    _state = RequireCommitted(committed, "prepared action");
                }
                else
                {
                    var inDoubt = await _writer.WriteAsync(
                        _identity,
                        _state.Revision,
                        new ActionMarkedInDoubtTransition(
                            AliPlanningStateCoordinator.Correlation(
                                _identity,
                                "action-in-doubt",
                                intent.IdempotencyKey + ":" + evidence.Evidence.EvidenceId,
                                _state.Revision),
                            intent.IdempotencyKey),
                        cancellationToken).ConfigureAwait(false);
                    _state = RequireCommitted(inDoubt, "in-doubt action");
                }
            }

            var acceptedProjection = new AcceptedEvidenceProjection(
                evidence.Evidence.EvidenceId,
                observed.CallId,
                observed.ToolName,
                observed.InvocationStatus,
                observed.DomainOutcome,
                observed.BoundedRedactedProjection,
                prepared.WorkItemId);
            _evidence.Add(acceptedProjection);
            _knownEvidenceIds.Add(acceptedProjection.EvidenceId);
            if (_evidence.Count > MaximumRetainedEvidenceProjections)
            {
                var removed = _evidence
                    .Take(_evidence.Count - MaximumRetainedEvidenceProjections)
                    .Select(item => item.EvidenceId)
                    .ToArray();
                _evidence.RemoveRange(
                    0,
                    _evidence.Count - MaximumRetainedEvidenceProjections);
                foreach (var evidenceId in removed)
                {
                    _knownEvidenceIds.Remove(evidenceId);
                }
            }
            var effectOutcome = normalizedEffect.Identity;
            var afterTargetState = _targetStates.Capture(prepared.TargetState);
            var after = CreateProgressVector(afterTargetState);
            var progress = ProgressDetector.Assess(
                _progressHistory,
                prepared.ActionIdentity,
                prepared.EffectNormalization.EffectIdentity,
                effectOutcome,
                prepared.Before,
                after);
            var progressRecorded = await _writer.WriteAsync(
                _identity,
                _state.Revision,
                new ProgressAttemptRecordedTransition(
                    AliPlanningStateCoordinator.Correlation(
                        _identity,
                        "progress-attempt",
                        evidence.Evidence.EvidenceId + ":" + prepared.ActionIdentity.Fingerprint,
                        _state.Revision),
                    prepared.ActionIdentity.Fingerprint,
                    prepared.EffectNormalization.EffectIdentity.Fingerprint,
                    effectOutcome.NoEffectFingerprint,
                    prepared.Before.MaterialFingerprint,
                    after.MaterialFingerprint,
                    progress.ChangedMaterialState),
                cancellationToken).ConfigureAwait(false);
            _state = RequireCommitted(progressRecorded, "progress attempt");
            _progressHistory = progress.History;
            _lastProgressReason = progress.Reason;
            _preparedCalls.Remove(observed.CallId);

            return new AliPlanningEvidenceReceipt(
                _state.Revision,
                evidence.Evidence.EvidenceId,
                BuildStateProjection(),
                _workGraph.Revision,
                _workGraph,
                prepared.WorkItemId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AliPlanningTransitionReceipt> OnPlanningSuspendedAsync(
        AliPlanningSuspendedEvent suspended,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateEventIdentity(
                suspended.ConversationId,
                suspended.AssistantMessageId,
                suspended.ExpectedStateRevision);
            return new AliPlanningTransitionReceipt(
                _state.Revision,
                BuildStateProjection(),
                _workGraph.Revision,
                RequiresFreshPlanningPass: false,
                _workGraph);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AliPlanningTransitionReceipt> OnInterimResponsePreparedAsync(
        AliPlanningInterimPreparedEvent prepared,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateEventIdentity(
                prepared.ConversationId,
                prepared.AssistantMessageId,
                prepared.ExpectedStateRevision);
            if (_state.PendingActions.Length != 0
                || _state.PendingAcceptedCall is not null
                || _state.FinalPublication is not null)
            {
                throw new InvalidOperationException(
                    "A planner interim response cannot be prepared while an action, accepted call, or final publication remains unresolved.");
            }
            var actualDigest = TurnStateIntegrity.Digest(prepared.Text);
            if (!FixedTimeDigestEquals(actualDigest, prepared.TextDigest))
            {
                throw new InvalidDataException(
                    "The interim publication digest does not match its exact text.");
            }

            var stateKind = ToInterimPublicationKind(prepared.Kind);
            var stateReason = ToInterimPublicationReason(prepared.Kind);
            var written = await _writer.PrepareInterimPublicationAsync(
                _identity,
                _state.Revision,
                prepared.PublicationId,
                stateKind,
                stateReason,
                prepared.PublicationId,
                prepared.Text,
                prepared.TextDigest,
                AliPlanningStateCoordinator.Correlation(
                    _identity,
                    "interim-publication-prepare",
                    prepared.PublicationId + ":" + prepared.TextDigest,
                    revision: 0),
                cancellationToken).ConfigureAwait(false);
            _state = RequireCommitted(written, "interim publication preparation");
            return new AliPlanningTransitionReceipt(
                _state.Revision,
                BuildStateProjection(),
                _workGraph.Revision,
                RequiresFreshPlanningPass: false,
                _workGraph);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CommitInterimPublicationAsync(
        AliPreparedInterimResponse prepared,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(prepared);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (prepared.DurableIdentity != _identity)
            {
                throw new InvalidDataException(
                    "An interim response cannot cross its durable turn identity.");
            }

            var kind = ToInterimPublicationKind(prepared.Kind);
            var reason = ToInterimPublicationReason(prepared.Kind);
            var interim = _state.InterimPublication
                ?? throw new InvalidDataException(
                    "The exact interim response is no longer prepared.");
            if (!string.Equals(interim.PublicationId, prepared.PublicationId, StringComparison.Ordinal)
                || interim.Kind != kind
                || interim.Reason != reason
                || !string.Equals(
                    interim.SubjectId,
                    prepared.PublicationId,
                    StringComparison.Ordinal)
                || interim.Status != InterimPublicationStatus.Prepared
                || !FixedTimeDigestEquals(interim.TextDigest, prepared.AnswerDigest))
            {
                throw new InvalidDataException(
                    "The displayed interim response differs from authoritative durable state.");
            }

            var committed = await _writer.CommitInterimPublicationAsync(
                _identity,
                _state.Revision,
                prepared.PublicationId,
                kind,
                prepared.AnswerDigest,
                AliPlanningStateCoordinator.Correlation(
                    _identity,
                    "interim-publication-commit",
                    prepared.PublicationId + ":" + prepared.AnswerDigest,
                    revision: 0),
                cancellationToken).ConfigureAwait(false);
            _state = RequireCommitted(committed, "interim publication commitment");

            var control = ToInterimControl(prepared.Kind);
            var changed = await _writer.ChangeControlAsync(
                _identity,
                _state.Revision,
                control,
                InterimReasonCode(reason),
                AliPlanningStateCoordinator.Correlation(
                    _identity,
                    "interim-publication-control",
                    prepared.PublicationId + ":" + reason + ":" + control,
                    revision: 0),
                cancellationToken).ConfigureAwait(false);
            _state = RequireCommitted(changed, "interim publication control");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AliPlanningPublicationReceipt> OnFinalAnswerPreparedAsync(
        AliPlanningPublicationPreparedEvent prepared,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateEventIdentity(
                prepared.ConversationId,
                prepared.AssistantMessageId,
                prepared.ExpectedStateRevision);
            if (_state.PendingActions.Length != 0
                || _state.PendingAcceptedCall is not null
                || _state.InterimPublication is not null)
            {
                throw new InvalidOperationException(
                    "A final answer cannot be prepared while an action, accepted call, or interim publication remains unresolved.");
            }
            var digest = TurnStateIntegrity.Digest(prepared.AnswerText);
            if (!FixedTimeDigestEquals(digest, prepared.AnswerDigest))
            {
                throw new InvalidDataException(
                    "The final publication digest does not match the exact answer text.");
            }

            var publicationAssistantMessageId = prepared.PublicationAssistantMessageId
                ?? prepared.AssistantMessageId;

            var written = await _writer.PrepareFinalPublicationAsync(
                _identity,
                _state.Revision,
                prepared.PublicationId,
                publicationAssistantMessageId,
                prepared.AnswerText,
                prepared.AnswerDigest,
                AliPlanningStateCoordinator.Correlation(
                    _identity,
                    "publication-prepare",
                    prepared.PublicationId + ":" + prepared.AnswerDigest,
                    _state.Revision),
                cancellationToken).ConfigureAwait(false);
            _state = RequireCommitted(written, "final publication preparation");
            _preparedPublication = _state.FinalPublication
                ?? throw new InvalidDataException("The prepared publication was not retained in turn state.");
            return new AliPlanningPublicationReceipt(
                _state.Revision,
                prepared.PublicationId,
                prepared.AnswerDigest,
                BuildStateProjection(),
                _workGraph.Revision,
                _workGraph);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<CommittedWorkGraphSnapshot?> CommitWorkUpdateAsync(
        OrchestrationWorkUpdate update,
        CancellationToken cancellationToken)
    {
        var mapped = new WorkGraphDelta(
            update.BaseRevision,
            update.Items.Select(item => new WorkNode(
                    item.WorkItemId,
                    item.Outcome,
                    item.ParentId,
                    ToWorkStatus(item.Status),
                    ImmutableArray.CreateRange(item.DependencyIds),
                    ImmutableArray.CreateRange(item.EvidenceIds),
                    item.SupersededById))
                .ToImmutableArray());
        var requiredEvidenceIds = mapped.Upserts
            .Where(static item => !item.EvidenceIds.IsDefaultOrEmpty)
            .SelectMany(static item => item.EvidenceIds)
            .Where(static evidenceId => !string.IsNullOrWhiteSpace(evidenceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var evidence = await ResolveEvidenceCoreAsync(
            requiredEvidenceIds,
            cancellationToken).ConfigureAwait(false);
        foreach (var item in mapped.Upserts)
        {
            var hasSucceededEvidence = false;
            foreach (var evidenceId in item.EvidenceIds)
            {
                if (!evidence.TryGetValue(evidenceId, out var projection)
                    || !string.Equals(
                        projection.WorkItemId,
                        item.Id,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Work item '{item.Id}' requires only accepted evidence bound to that exact work-item ID using ordinal comparison; every cited evidence ID must resolve.");
                }

                hasSucceededEvidence |=
                    projection.DomainOutcome == PlanningToolDomainOutcome.Succeeded;
            }

            if (item.Status is WorkNodeStatus.Satisfied
                    or WorkNodeStatus.Impossible
                    or WorkNodeStatus.Superseded
                && !hasSucceededEvidence)
            {
                throw new InvalidDataException(
                    $"Terminal work item '{item.Id}' requires at least one explicit succeeded evidence record bound to that exact ordinal work-item ID.");
            }
        }

        var applied = WorkGraphApplier.Apply(
            _workGraph,
            mapped,
            evidence.Keys.ToHashSet(StringComparer.Ordinal));
        if (!applied.Accepted)
        {
            throw new InvalidDataException(
                "The planner work delta failed authoritative graph validation: "
                + string.Join(" ", applied.Errors));
        }

        Volatile.Write(ref _lastWorkGraphApplyDiagnostics, applied.Diagnostics);

        if (!applied.Changed)
        {
            return null;
        }

        var committed = await _workGraphStore.CommitValidatedAsync(
            _identity,
            _state.WorkGraphReference,
            applied,
            cancellationToken).ConfigureAwait(false);
        if (committed.Current is null)
        {
            throw new InvalidOperationException(
                "The durable work graph changed concurrently; fresh-state planning is required.");
        }

        return committed.Current;
    }

    private async Task<IReadOnlyDictionary<string, AcceptedEvidenceProjection>>
        ResolveEvidenceCoreAsync(
            IReadOnlyCollection<string> evidenceIds,
            CancellationToken cancellationToken)
    {
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidenceId in evidenceIds)
        {
            if (string.IsNullOrWhiteSpace(evidenceId)
                || evidenceId.Length > 256
                || !string.Equals(evidenceId, evidenceId.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            requested.Add(evidenceId);
        }

        if (requested.Count == 0)
        {
            return new Dictionary<string, AcceptedEvidenceProjection>(StringComparer.Ordinal);
        }

        var evidence = _evidence
            .Where(item => requested.Contains(item.EvidenceId))
            .ToDictionary(
                item => item.EvidenceId,
                item => item,
                StringComparer.Ordinal);
        requested.ExceptWith(evidence.Keys);
        if (requested.Count == 0)
        {
            return evidence;
        }

        var references = await _writer.ResolveEvidenceReferencesAsync(
            _identity,
            requested,
            cancellationToken).ConfigureAwait(false);
        foreach (var chunk in references.Values.Chunk(
                     EvidenceLedger.MaximumCommittedEvidenceLookupBatchSize))
        {
            var committed = await _evidenceLedger.ReadCommittedBatchAsync(
                _identity,
                chunk,
                cancellationToken).ConfigureAwait(false);
            foreach (var reference in chunk)
            {
                if (!committed.TryGetValue(reference.EvidenceId, out var exact))
                {
                    throw new InvalidDataException(
                        "The accepted evidence authority lost an exact committed record.");
                }

                if (exact.Cursor.Cursor != reference.Cursor
                    || !string.Equals(
                        exact.Cursor.Checksum,
                        reference.RecordDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The accepted evidence authority crossed its exact committed reference.");
                }

                evidence.Add(reference.EvidenceId, ToPlanningEvidenceProjection(exact));
            }
        }

        return evidence;
    }

    private static AcceptedEvidenceProjection ToPlanningEvidenceProjection(
        CommittedProtectedEvidence exact)
    {
        var invocationStatus = exact.Cursor.Evidence.InvocationStatus switch
        {
            InvocationStatus.Returned or InvocationStatus.Denied =>
                PlanningToolInvocationStatus.Returned,
            InvocationStatus.Threw => PlanningToolInvocationStatus.Threw,
            InvocationStatus.Cancelled => PlanningToolInvocationStatus.Cancelled,
            _ => throw new InvalidDataException(
                "Accepted evidence has an invalid invocation status.")
        };
        return new AcceptedEvidenceProjection(
            exact.Cursor.Evidence.EvidenceId,
            exact.ProtectedContent.Identity.CallId,
            exact.ProtectedContent.Identity.ToolName,
            invocationStatus,
            ToPlanningDomainOutcome(exact.Cursor.Evidence),
            AliPlanningProjectionSafety.ProjectResult(exact.ProtectedContent.Result),
            exact.ProtectedContent.Identity.WorkItemId);
    }

    private static PlanningToolDomainOutcome ToPlanningDomainOutcome(EvidenceRecord evidence) =>
        evidence.InvocationStatus == InvocationStatus.Denied
            ? PlanningToolDomainOutcome.Denied
            : evidence.DomainOutcome switch
            {
                DomainOutcome.Succeeded => PlanningToolDomainOutcome.Succeeded,
                DomainOutcome.Failed => PlanningToolDomainOutcome.Failed,
                DomainOutcome.Unreported or DomainOutcome.PartiallySucceeded =>
                    PlanningToolDomainOutcome.Unreported,
                _ => throw new InvalidDataException(
                    "Accepted evidence has an invalid domain outcome.")
            };

    private PreparedCallPreparation PrepareCall(
        string callId,
        CallToolAction call,
        string decisionDigest,
        WorkGraphSnapshot workGraph,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_preparedCalls.ContainsKey(callId))
        {
            throw new InvalidDataException("The planner reused an accepted call ID.");
        }

        _descriptors.TryGetValue(call.ToolName, out var descriptor);
        var arguments = JsonSerializer.SerializeToElement(call.Arguments).Clone();
        var effectNormalization = _effectNormalizations.Prepare(call.ToolName, arguments);
        var targetState = _targetStates.Prepare(call.ToolName, arguments);
        var beforeTargetState = _targetStates.Capture(targetState);
        var argumentsBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(arguments);
        string argumentsDigest;
        ActionIdentity actionIdentity;
        var workItemId = SelectWorkItemId(workGraph);
        try
        {
            argumentsDigest = TurnStateIntegrity.Digest(argumentsBytes);
            actionIdentity = ActionIdentity.Create(
                _bindings.CapabilityRegistryDigest,
                workItemId,
                call.ToolName,
                argumentsBytes,
                targetVersions: beforeTargetState.TargetVersions,
                _bindings.PermissionDigest,
                DependencyState(workGraph));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(argumentsBytes);
        }

        var before = CreateProgressVector(beforeTargetState, workGraph);
        var targetVersionDigest = DigestCanonicalValue(beforeTargetState.TargetVersions);
        var assessment = ProgressDetector.AssessPlannedAction(
            _progressHistory,
            actionIdentity,
            before);
        AliDurableEffectPreview? durableEffect = null;
        if (descriptor is not null
            && RequiresDurableEffectAdapter(descriptor)
            && _durableEffects.TryGet(call.ToolName, out var durableAdapter)
            && durableAdapter is not null)
        {
            durableEffect = durableAdapter.Preview(new AliDurableEffectPreviewRequest(
                _identity,
                _turn,
                callId,
                call.ToolName,
                argumentsDigest,
                targetVersionDigest));
            if (!string.Equals(
                    durableEffect.ReconcilerId,
                    durableAdapter.ReconcilerId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A durable effect preview returned a different reconciler identity.");
            }
        }
        var prepared = new PreparedCall(
                callId,
                call.ToolName,
                workItemId,
                arguments,
                argumentsDigest,
                decisionDigest,
                descriptor,
                null,
                actionIdentity,
                effectNormalization,
                targetState,
                before,
                targetVersionDigest,
                durableEffect,
                _state.Revision,
                null,
                false);
        var executionClass = descriptor is null
            ? AcceptedCallExecutionClass.Unavailable
            : durableEffect?.RequiresPreparedIntent == true
                ? AcceptedCallExecutionClass.PreparedEffectRequired
                : RequiresDurableEffectAdapter(descriptor) && durableEffect is null
                    ? AcceptedCallExecutionClass.Unavailable
                    : AcceptedCallExecutionClass.NonMutating;
        var recovery = new AcceptedCallRecoveryPayload(
            callId,
            workItemId,
            call.ToolName,
            descriptor?.Id,
            descriptor?.SchemaFingerprint,
            _bindings.CapabilityRegistryDigest,
            arguments,
            argumentsDigest,
            actionIdentity.Fingerprint,
            effectNormalization.EffectIdentity.Fingerprint,
            executionClass,
            durableEffect?.ReconcilerId ?? descriptor?.Effect.ReconcilerId,
            descriptor?.Permission.RequiresApproval ?? true);
        return new PreparedCallPreparation(prepared, recovery, assessment);
    }

    public async ValueTask<CapabilityInvocationAuthorization> PrepareExecutionAsync(
        CapabilityInvocationLease lease,
        string callId,
        AIFunctionArguments arguments,
        bool requiresApproval,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(arguments);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_preparedCalls.TryGetValue(callId, out var prepared)
                || !string.Equals(prepared.ToolName, lease.ToolName, StringComparison.Ordinal))
            {
                return BlockExecution(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "planning-call",
                    "The terminal invocation does not match an accepted planner call.");
            }

            if (prepared.ExecutionAuthorized)
            {
                return BlockExecution(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "action-idempotency",
                    "This accepted call already consumed its one execution authorization.");
            }

            var descriptor = prepared.Descriptor;
            if (descriptor is null
                || !string.Equals(descriptor.Id, lease.CapabilityId, StringComparison.Ordinal)
                || !string.Equals(descriptor.SchemaFingerprint, lease.SchemaFingerprint, StringComparison.Ordinal))
            {
                return BlockExecution(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "capability-identity",
                    "The live capability identity differs from the accepted planner call.");
            }

            var invocationArguments = JsonSerializer.SerializeToElement(arguments).Clone();
            var argumentBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(invocationArguments);
            string argumentsDigest;
            try
            {
                argumentsDigest = TurnStateIntegrity.Digest(argumentBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(argumentBytes);
            }

            if (!FixedTimeDigestEquals(argumentsDigest, prepared.ArgumentsDigest))
            {
                return BlockExecution(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "canonical-arguments",
                    "The terminal invocation arguments differ from the accepted planner call.");
            }

            TurnRuntimeBindings currentBindings;
            try
            {
                currentBindings = _liveBindingsAccessor()
                    ?? throw new InvalidOperationException("The live runtime binding accessor returned no snapshot.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return BlockExecution(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "runtime-bindings",
                    $"The live runtime bindings could not be captured ({ex.GetType().Name}).");
            }

            var changedBindings = _bindings.FindChangedBindings(currentBindings);
            if (changedBindings.Count > 0)
            {
                return BlockExecution(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "runtime-bindings",
                    "The turn runtime bindings changed before execution: "
                    + string.Join(", ", changedBindings));
            }

            CoordinatorPermissionDecisionReceipt permissionReceipt;
            if (requiresApproval)
            {
                if (!_turn.TryGetPermissionReceipt(callId, out var exactPermission)
                    || exactPermission is null
                    || !IsApprovedPermission(exactPermission.Permission))
                {
                    return BlockExecution(
                        CapabilityAvailabilityReasonCode.PermissionUnavailable,
                        descriptor.Permission.PolicyId,
                        "The exact accepted call has no affirmative approval receipt.");
                }

                permissionReceipt = exactPermission;
            }
            else
            {
                permissionReceipt = new CoordinatorPermissionDecisionReceipt(
                    new EvidencePermissionMetadata("not-required", "none"),
                    "capability-policy",
                    ApprovalRequestId: null);
            }

            var permissionReceiptDigest = DigestCanonicalValue(new
            {
                callId,
                permissionReceipt.Permission.Decision,
                permissionReceipt.Permission.Scope,
                permissionReceipt.Source,
                permissionReceipt.ApprovalRequestId,
                lease.ActiveUserId,
                lease.PermissionRevision,
                argumentsDigest,
                requiresApproval
            });
            var registryIdentityDigest = DigestCanonicalValue(new
            {
                lease.RegistryRevision,
                lease.PlanningPublicationRevision,
                lease.PlanningResolutionRevision,
                lease.CapabilityId,
                lease.SchemaFactoryId,
                lease.SchemaFingerprint,
                lease.RuntimeRevision,
                lease.ProviderRevision,
                lease.McpRevision,
                lease.ReconcilerRevision
            });
            var assessment = ProgressDetector.AssessPlannedAction(
                _progressHistory,
                prepared.ActionIdentity,
                prepared.Before);
            if (!assessment.CanExecute)
            {
                _lastProgressReason = assessment.Reason;
                return BlockExecution(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "progress-identity",
                    "The exact action would repeat without evidence-backed progress.");
            }

            if (RequiresDurableEffectAdapter(descriptor))
            {
                if (prepared.DurableEffect is not { } durableEffect
                    || !_durableEffects.TryGet(descriptor.ToolName, out var durableAdapter)
                    || durableAdapter is null
                    || !string.Equals(
                        durableEffect.ReconcilerId,
                        durableAdapter.ReconcilerId,
                        StringComparison.Ordinal))
                {
                    return BlockExecution(
                        CapabilityAvailabilityReasonCode.ReconcilerUnavailable,
                        descriptor.Effect.ReconcilerId ?? "effect-adapter",
                        "No live exact-name durable effect adapter owns this operation.");
                }

                if (durableEffect.RequiresPreparedIntent)
                {
                    if (string.IsNullOrWhiteSpace(durableEffect.OperationId))
                    {
                        return BlockExecution(
                            CapabilityAvailabilityReasonCode.ReconcilerUnavailable,
                            durableEffect.ReconcilerId,
                            "The durable effect adapter did not issue an operation identity.");
                    }

                    var currentTargetState = _targetStates.Capture(prepared.TargetState);
                    var currentTargetDigest = DigestCanonicalValue(
                        currentTargetState.TargetVersions);
                    if (!FixedTimeDigestEquals(
                            currentTargetDigest,
                            prepared.TargetVersionDigest))
                    {
                        return BlockExecution(
                            CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                            "target-version",
                            "The exact target version changed after the action was planned.");
                    }

                    var intent = new PreparedActionIntent(
                        durableEffect.OperationId,
                        prepared.WorkItemId,
                        prepared.ToolName,
                        descriptor.Id,
                        prepared.ArgumentsDigest,
                        prepared.TargetVersionDigest,
                        permissionReceiptDigest,
                        _bindings.CapabilityRegistryDigest,
                        durableEffect.ReconcilerId,
                        requiresApproval,
                        prepared.CallId);
                    var preparedAction = await _writer.PrepareActionAsync(
                        _identity,
                        _state.Revision,
                        intent,
                        AliPlanningStateCoordinator.Correlation(
                            _identity,
                            "action-prepare",
                            intent.IdempotencyKey,
                            _state.Revision),
                        cancellationToken).ConfigureAwait(false);
                    _state = RequireCommitted(preparedAction, "prepared action intent");
                    _turn.RegisterDurableOperationId(
                        prepared.CallId,
                        durableEffect.OperationId);
                    prepared = prepared with { Intent = intent };
                }
            }

            _preparedCalls[callId] = prepared with
            {
                Permission = permissionReceipt.Permission with { },
                ExecutionRegistryIdentityDigest = registryIdentityDigest,
                ExecutionPermissionReceiptDigest = permissionReceiptDigest,
                ExecutionAuthorized = true
            };
            return CapabilityInvocationAuthorization.Allow();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CapabilityInvocationAuthorization BlockExecution(
        CapabilityAvailabilityReasonCode code,
        string dependencyId,
        string message) =>
        CapabilityInvocationAuthorization.Block(
            new CapabilityAvailabilityReason(code, dependencyId, message));

    private static bool IsApprovedPermission(EvidencePermissionMetadata permission) =>
        permission.Decision is "approved-once" or "approved-standing" or "approved-policy";

    private static bool RequiresDurableEffectAdapter(CapabilityDescriptor descriptor) =>
        descriptor.Effect.IsMutation
        || descriptor.Effect.WritesLocalData
        || descriptor.Effect.StartsProcesses
        || descriptor.Effect.ChangesSystemState
        || (descriptor.Effect.UsesNetwork && !descriptor.Effect.SupportsIdempotency);

    private static string DigestCanonicalValue<T>(T value)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(value);
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task ChangeControlAsync(
        TurnControlState control,
        string reasonCode,
        string subject,
        CancellationToken cancellationToken)
    {
        var changed = await _writer.ChangeControlAsync(
            _identity,
            _state.Revision,
            control,
            reasonCode,
            AliPlanningStateCoordinator.Correlation(
                _identity,
                "control-" + control,
                subject,
                _state.Revision),
            cancellationToken).ConfigureAwait(false);
        _state = RequireCommitted(changed, "turn control");
    }

    private AliPlanningTurnInput CreatePlanningInput() =>
        new(
            _state.Revision,
            BuildStateProjection(),
            _priorConversation,
            _evidence,
            _workGraph.Nodes.Keys,
            approvedExternalTicketIds: [],
            _workGraph.Revision,
            _workGraph);

    private string BuildStateProjection()
    {
        Interlocked.Increment(ref _workGraphProjectionCalls);
        var analysis = RequireWorkGraphAnalysis(_workGraph, out _);
        var projectedCandidateIds = analysis
            .EnumerateEligibleIds(MaximumProjectedWorkItems + 1)
            .ToArray();
        Interlocked.Add(
            ref _workGraphProjectionIndexIdsVisited,
            projectedCandidateIds.Length);
        var candidates = projectedCandidateIds
            .Take(MaximumProjectedWorkItems)
            .Select(id => analysis.CanonicalNodes[id])
            .ToArray();
        var eligibleCount = analysis.EligibleCount;
        var projectedNodes = new JsonArray();
        var evidenceItems = new JsonArray();
        var userResolutionItems = new JsonArray();
        var workGraph = new JsonObject
        {
            ["revision"] = _workGraph.Revision,
            ["total"] = analysis.CanonicalNodes.Count,
            ["eligible"] = eligibleCount,
            ["items"] = projectedNodes,
            ["projectedCount"] = 0,
            ["omitted"] = eligibleCount,
            ["nextOmittedWorkItemId"] = projectedCandidateIds.FirstOrDefault(),
            ["satisfied"] = analysis.SatisfiedIds.Count,
            ["impossible"] = analysis.ImpossibleIds.Count,
            ["superseded"] = analysis.SupersededIds.Count
        };
        var evidenceIndex = new JsonObject
        {
            ["durableCursor"] = _state.EvidenceCursor,
            ["retainedTotal"] = _evidence.Count,
            ["order"] = "newest-first",
            ["items"] = evidenceItems,
            ["projectedCount"] = 0,
            ["retainedOmitted"] = _evidence.Count,
            ["olderThanRetained"] = Math.Max(0L, _state.EvidenceCursor - _evidence.Count),
            ["nextOmittedRetainedEvidenceId"] = _evidence.LastOrDefault()?.EvidenceId
        };
        var newestUserResolutions = _userResolutions
            .AsEnumerable()
            .Reverse()
            .ToArray();
        var userResolutionCandidates = newestUserResolutions
            .Take(MaximumProjectedUserResolutions)
            .ToArray();
        var userResolutionIndex = new JsonObject
        {
            ["retainedTotal"] = _userResolutions.Count,
            ["order"] = "newest-first",
            ["items"] = userResolutionItems,
            ["projectedCount"] = 0,
            ["retainedOmitted"] = _userResolutions.Count,
            ["nextOmittedStateRevision"] = newestUserResolutions.FirstOrDefault()?.StateRevision
        };
        var root = new JsonObject
        {
            ["stateRevision"] = _state.Revision,
            ["control"] = _state.Control.ToString(),
            ["workGraph"] = workGraph,
            ["evidenceIndex"] = evidenceIndex,
            ["acceptedUserResolutions"] = userResolutionIndex,
            ["pendingActionCount"] = _state.PendingActions.Length,
            ["lastProgressReason"] = _lastProgressReason?.ToString()
        };

        var projectedUserResolutionCount = 0;
        foreach (var resolution in userResolutionCandidates)
        {
            userResolutionItems.Add(new JsonObject
            {
                ["stateRevision"] = resolution.StateRevision,
                ["sourceCommandId"] = resolution.SourceCommandId,
                ["promptPublicationId"] = resolution.PromptPublicationId,
                ["promptTextDigest"] = resolution.PromptTextDigest,
                ["kind"] = resolution.Kind.ToString(),
                ["reason"] = resolution.Reason.ToString(),
                ["subjectId"] = resolution.SubjectId,
                ["subjectPreparedRevision"] = resolution.SubjectPreparedRevision,
                ["outcome"] = resolution.Outcome.ToString()
            });
            userResolutionIndex["projectedCount"] = projectedUserResolutionCount + 1;
            userResolutionIndex["retainedOmitted"] =
                _userResolutions.Count - projectedUserResolutionCount - 1;
            userResolutionIndex["nextOmittedStateRevision"] =
                projectedUserResolutionCount + 1 < newestUserResolutions.Length
                    ? newestUserResolutions[projectedUserResolutionCount + 1].StateRevision
                    : null;
            if (root.ToJsonString().Length > MaximumStateProjectionCharacters)
            {
                userResolutionItems.RemoveAt(userResolutionItems.Count - 1);
                userResolutionIndex["projectedCount"] = projectedUserResolutionCount;
                userResolutionIndex["retainedOmitted"] =
                    _userResolutions.Count - projectedUserResolutionCount;
                userResolutionIndex["nextOmittedStateRevision"] = resolution.StateRevision;
                break;
            }

            projectedUserResolutionCount++;
        }

        var projectedNodeCount = 0;
        foreach (var item in candidates)
        {
            var dependencies = item.DependsOn
                .Take(MaximumProjectedNodeReferences)
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToArray();
            var evidence = item.EvidenceIds
                .TakeLast(MaximumProjectedNodeReferences)
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToArray();
            projectedNodes.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["objective"] = Bound(item.Objective, 1_000),
                ["status"] = item.Status.ToString(),
                ["parentId"] = item.ParentId,
                ["dependencies"] = new JsonArray(dependencies),
                ["dependenciesOmitted"] = Math.Max(
                    0,
                    item.DependsOn.Length - MaximumProjectedNodeReferences),
                ["evidence"] = new JsonArray(evidence),
                ["evidenceOmitted"] = Math.Max(
                    0,
                    item.EvidenceIds.Length - MaximumProjectedNodeReferences)
            });
            workGraph["projectedCount"] = projectedNodeCount + 1;
            workGraph["omitted"] = eligibleCount - projectedNodeCount - 1;
            workGraph["nextOmittedWorkItemId"] =
                projectedNodeCount + 1 < projectedCandidateIds.Length
                ? projectedCandidateIds[projectedNodeCount + 1]
                : null;
            if (root.ToJsonString().Length > MaximumWorkProjectionCharacters)
            {
                projectedNodes.RemoveAt(projectedNodes.Count - 1);
                workGraph["projectedCount"] = projectedNodeCount;
                workGraph["omitted"] = eligibleCount - projectedNodeCount;
                workGraph["nextOmittedWorkItemId"] = item.Id;
                break;
            }

            projectedNodeCount++;
        }

        var newestEvidence = _evidence.AsEnumerable().Reverse().ToArray();
        var projectedEvidenceCount = 0;
        foreach (var accepted in newestEvidence)
        {
            evidenceItems.Add(new JsonObject
            {
                ["evidenceId"] = accepted.EvidenceId,
                ["callId"] = accepted.CallId,
                ["toolName"] = accepted.ToolName,
                ["invocationStatus"] = accepted.InvocationStatus.ToString(),
                ["domainOutcome"] = accepted.DomainOutcome.ToString()
            });
            evidenceIndex["projectedCount"] = projectedEvidenceCount + 1;
            evidenceIndex["retainedOmitted"] = newestEvidence.Length - projectedEvidenceCount - 1;
            evidenceIndex["nextOmittedRetainedEvidenceId"] =
                projectedEvidenceCount + 1 < newestEvidence.Length
                    ? newestEvidence[projectedEvidenceCount + 1].EvidenceId
                    : null;
            if (root.ToJsonString().Length > MaximumStateProjectionCharacters)
            {
                evidenceItems.RemoveAt(evidenceItems.Count - 1);
                evidenceIndex["projectedCount"] = projectedEvidenceCount;
                evidenceIndex["retainedOmitted"] = newestEvidence.Length - projectedEvidenceCount;
                evidenceIndex["nextOmittedRetainedEvidenceId"] = accepted.EvidenceId;
                break;
            }

            projectedEvidenceCount++;
        }

        var projection = root.ToJsonString();
        if (projection.Length > MaximumStateProjectionCharacters)
        {
            throw new InvalidDataException(
                "The bounded authoritative state projection exceeded its hard limit.");
        }

        return projection;
    }

    private ProgressVector CreateProgressVector(
        TargetStateSnapshot? targetState = null,
        WorkGraphSnapshot? authoritativeWorkGraph = null)
    {
        Interlocked.Increment(ref _workGraphProgressVectorCalls);
        var graph = authoritativeWorkGraph ?? _workGraph;
        var analysis = RequireWorkGraphAnalysis(graph, out _);
        return ProgressVector.CreateFromWorkGraphAnalysis(
            _state.EvidenceCursor,
            analysis,
            artifactVersions: targetState?.ArtifactVersions,
            diagnosticStates: targetState?.DiagnosticStates,
            testStates: targetState?.TestStates,
            permissionStates: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["binding"] = _bindings.PermissionDigest
            });
    }

    private string DependencyState(WorkGraphSnapshot? authoritativeWorkGraph = null)
    {
        Interlocked.Increment(ref _workGraphDependencyDigestCalls);
        return RequireWorkGraphAnalysis(authoritativeWorkGraph ?? _workGraph, out _)
            .ActionDependencyStateDigest;
    }

    private EvidencePermissionMetadata ResolvePermission(
        string callId,
        string toolName,
        CapabilityDescriptor? descriptor)
    {
        if (_turn.TryGetShadowPermission(callId, out var exact) && exact is not null)
        {
            return exact with { };
        }

        if (descriptor?.Permission.RequiresApproval != true)
        {
            return new EvidencePermissionMetadata("not-required", "none");
        }

        if (_turn.TryGetShadowStandingPermission(toolName, out var standing)
            && standing is not null)
        {
            return standing with { };
        }

        return new EvidencePermissionMetadata("unknown", "unknown");
    }

    private string SelectWorkItemId(WorkGraphSnapshot workGraph)
    {
        ArgumentNullException.ThrowIfNull(workGraph);
        Interlocked.Increment(ref _workGraphActiveSelectionCalls);
        var analysis = RequireWorkGraphAnalysis(workGraph, out _);
        if (analysis.TryGetSoleActiveId(out var activeId) && activeId is not null)
        {
            return activeId;
        }

        throw new InvalidDataException(
            "A tool call must be bound to exactly one authoritative Active work item.");
    }

    private static WorkGraphSnapshotAnalysis RequireWorkGraphAnalysis(
        WorkGraphSnapshot graph,
        out bool cacheHit)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var analysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(graph, out cacheHit);
        if (!analysis.IsValid)
        {
            throw new InvalidDataException(
                "The authoritative work graph failed cached material-index validation: "
                + string.Join(" ", analysis.Errors));
        }

        return analysis;
    }

    private void ValidateEventIdentity(
        string conversationId,
        string assistantMessageId,
        long expectedRevision)
    {
        if (!string.Equals(conversationId, _identity.ConversationId, StringComparison.Ordinal)
            || !string.Equals(assistantMessageId, _identity.AssistantMessageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A planning event crossed its visible-turn identity boundary.");
        }

        if (expectedRevision != _state.Revision)
        {
            throw new InvalidDataException(
                $"A planning event expected revision {expectedRevision}, but authoritative revision is {_state.Revision}.");
        }
    }

    private static TurnState RequireCommitted(
        TurnTransitionWriteResult result,
        string operation)
    {
        if (result.Status == TurnTransitionWriteStatus.RevisionConflict || result.State is null)
        {
            throw new InvalidOperationException(
                $"The durable {operation} lost its expected-revision boundary.");
        }

        return result.State;
    }

    private static PlanningAcceptedActionKind ToAcceptedActionKind(
        OrchestrationNextAction action) => action switch
        {
            CallToolAction => PlanningAcceptedActionKind.CallTool,
            ExpandToolsAction => PlanningAcceptedActionKind.ExpandTools,
            AnswerDirectlyAction => PlanningAcceptedActionKind.AnswerDirectly,
            RequestUserInputAction => PlanningAcceptedActionKind.RequestUserInput,
            AwaitExternalEventAction => PlanningAcceptedActionKind.AwaitExternalEvent,
            BeginCompletionAction => PlanningAcceptedActionKind.BeginCompletion,
            _ => throw new InvalidDataException("The accepted planner action kind is unsupported.")
        };

    private static InterimPublicationKind ToInterimPublicationKind(
        AliPlanningInterimKind kind) => kind switch
        {
            AliPlanningInterimKind.AwaitingUser => InterimPublicationKind.AwaitingUser,
            AliPlanningInterimKind.AwaitingExternalEvent => InterimPublicationKind.AwaitingEvent,
            AliPlanningInterimKind.ProtocolSuspended
                or AliPlanningInterimKind.RuntimeSuspended
                or AliPlanningInterimKind.ModelInputNotAdmitted
                or AliPlanningInterimKind.CompletionInputNotAdmitted
                or AliPlanningInterimKind.CompletionDispatchBindingsChanged
                or AliPlanningInterimKind.CompletionOutputIncomplete =>
                InterimPublicationKind.SuspendedRuntime,
            _ => throw new InvalidDataException("The planning interim kind is unsupported.")
        };

    private static InterimPublicationReason ToInterimPublicationReason(
        AliPlanningInterimKind kind) => kind switch
        {
            AliPlanningInterimKind.AwaitingUser =>
                InterimPublicationReason.PlannerAwaitingUser,
            AliPlanningInterimKind.AwaitingExternalEvent =>
                InterimPublicationReason.PlannerAwaitingExternalEvent,
            AliPlanningInterimKind.ProtocolSuspended =>
                InterimPublicationReason.PlannerProtocolSuspended,
            AliPlanningInterimKind.RuntimeSuspended =>
                InterimPublicationReason.RuntimeBindingsChanged,
            AliPlanningInterimKind.ModelInputNotAdmitted =>
                InterimPublicationReason.ModelInputNotAdmitted,
            AliPlanningInterimKind.CompletionInputNotAdmitted =>
                InterimPublicationReason.CompletionInputNotAdmitted,
            AliPlanningInterimKind.CompletionDispatchBindingsChanged =>
                InterimPublicationReason.CompletionDispatchBindingsChanged,
            AliPlanningInterimKind.CompletionOutputIncomplete =>
                InterimPublicationReason.CompletionOutputIncomplete,
            _ => throw new InvalidDataException("The planning interim kind is unsupported.")
        };

    private static TurnControlState ToInterimControl(AliPlanningInterimKind kind) => kind switch
    {
        AliPlanningInterimKind.AwaitingUser => TurnControlState.AwaitingUser,
        AliPlanningInterimKind.AwaitingExternalEvent => TurnControlState.AwaitingEvent,
        AliPlanningInterimKind.ProtocolSuspended
            or AliPlanningInterimKind.RuntimeSuspended
            or AliPlanningInterimKind.ModelInputNotAdmitted
            or AliPlanningInterimKind.CompletionInputNotAdmitted
            or AliPlanningInterimKind.CompletionDispatchBindingsChanged
            or AliPlanningInterimKind.CompletionOutputIncomplete =>
            TurnControlState.SuspendedRuntime,
        _ => throw new InvalidDataException("The planning interim kind is unsupported.")
    };

    internal static string InterimReasonCode(InterimPublicationReason reason) => reason switch
    {
        InterimPublicationReason.PlannerAwaitingUser => "planner-requested-user-input",
        InterimPublicationReason.PlannerAwaitingExternalEvent => "planner-awaiting-external-event",
        InterimPublicationReason.PlannerProtocolSuspended => "planner-protocol-suspended",
        InterimPublicationReason.RuntimeBindingsChanged => "runtime-bindings-changed",
        InterimPublicationReason.ModelInputNotAdmitted => "model-input-not-admitted",
        InterimPublicationReason.CompletionInputNotAdmitted =>
            "completion-input-not-admitted",
        InterimPublicationReason.CompletionDispatchBindingsChanged =>
            "completion-dispatch-bindings-changed",
        InterimPublicationReason.CompletionOutputIncomplete =>
            "completion-output-incomplete",
        InterimPublicationReason.ActionReconciliationRequired => "action-reconciliation-required",
        InterimPublicationReason.FinalPublicationReconciliationRequired =>
            "final-publication-reconciliation-required",
        _ => throw new InvalidDataException("The interim publication reason is unsupported.")
    };

    private static WorkNodeStatus ToWorkStatus(OrchestrationWorkStatus status) => status switch
    {
        OrchestrationWorkStatus.Pending => WorkNodeStatus.Pending,
        OrchestrationWorkStatus.Active => WorkNodeStatus.Active,
        OrchestrationWorkStatus.Satisfied => WorkNodeStatus.Satisfied,
        OrchestrationWorkStatus.Impossible => WorkNodeStatus.Impossible,
        OrchestrationWorkStatus.Superseded => WorkNodeStatus.Superseded,
        _ => throw new InvalidDataException("The planner work status is unsupported.")
    };

    private static ToolInvocationOutcome CreateOutcome(
        AliPlanningToolResultObservedEvent observed)
    {
        var resultBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(observed.Result);
        try
        {
            return observed.InvocationStatus switch
            {
                PlanningToolInvocationStatus.Returned
                    when observed.DomainOutcome == PlanningToolDomainOutcome.Denied =>
                    ToolInvocationOutcome.Denied("permission-denied"),
                PlanningToolInvocationStatus.Returned => ToolInvocationOutcome.Returned(
                    resultBytes,
                    observed.DomainOutcome switch
                    {
                        PlanningToolDomainOutcome.Succeeded => true,
                        PlanningToolDomainOutcome.Failed => false,
                        _ => null
                    }),
                PlanningToolInvocationStatus.Cancelled => ToolInvocationOutcome.Cancelled(),
                PlanningToolInvocationStatus.Threw => ToolInvocationOutcome.ThrewType("tool-invocation-threw"),
                _ => throw new InvalidDataException("The planner invocation status is unsupported.")
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(resultBytes);
        }
    }

    private static string StableOutcomeCode(AliPlanningToolResultObservedEvent observed) =>
        observed.InvocationStatus switch
        {
            PlanningToolInvocationStatus.Cancelled => "cancelled",
            PlanningToolInvocationStatus.Threw => "threw",
            _ => observed.DomainOutcome switch
            {
                PlanningToolDomainOutcome.Succeeded => "returned-succeeded",
                PlanningToolDomainOutcome.Failed => "returned-failed",
                PlanningToolDomainOutcome.Denied => "denied",
                _ => "returned-unreported"
            }
        };

    private bool IsAuthoritativeActionCompletion(
        AliPlanningToolResultObservedEvent observed,
        PreparedCall prepared)
    {
        if (observed.InvocationStatus != PlanningToolInvocationStatus.Returned)
        {
            return false;
        }
        if (observed.DomainOutcome == PlanningToolDomainOutcome.Succeeded)
        {
            return true;
        }
        return observed.DomainOutcome == PlanningToolDomainOutcome.Failed
            && prepared.Intent is { } intent
            && _durableEffects.TryGet(prepared.ToolName, out var durableAdapter)
            && durableAdapter is not null
            && durableAdapter.ConfirmsAuthoritativeNoEffect(intent, observed.Result);
    }

    private static string EvidenceEffectKind(CapabilityEffectKind? effect) => effect switch
    {
        null or CapabilityEffectKind.None => "none",
        CapabilityEffectKind.Read or CapabilityEffectKind.ExternalRead => "read",
        CapabilityEffectKind.LocalMutation => "update",
        CapabilityEffectKind.SourceMutation => "update",
        CapabilityEffectKind.ProcessControl => "execute",
        CapabilityEffectKind.ExternalMutation => "external",
        CapabilityEffectKind.Destructive => "delete",
        _ => "mixed"
    };

    private static string Bound(string value, int maximumCharacters)
    {
        var normalized = value ?? string.Empty;
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + " [bounded]";
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
                && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private sealed record PreparedCall(
        string CallId,
        string ToolName,
        string WorkItemId,
        JsonElement Arguments,
        string ArgumentsDigest,
        string DecisionDigest,
        CapabilityDescriptor? Descriptor,
        PreparedActionIntent? Intent,
        ActionIdentity ActionIdentity,
        PreparedEffectNormalization EffectNormalization,
        PreparedTargetState TargetState,
        ProgressVector Before,
        string TargetVersionDigest,
        AliDurableEffectPreview? DurableEffect,
        long DecisionStateRevision,
        EvidencePermissionMetadata? Permission,
        bool ExecutionAuthorized,
        string? ExecutionRegistryIdentityDigest = null,
        string? ExecutionPermissionReceiptDigest = null);

    private sealed record PreparedCallPreparation(
        PreparedCall Call,
        AcceptedCallRecoveryPayload Recovery,
        PlannedActionAssessment Assessment);
}

internal sealed class DurablePlanningReferenceValidator : ITurnCommittedReferenceValidator
{
    private readonly EvidenceLedger _evidenceLedger;
    private readonly DurableWorkGraphStore _workGraphStore;

    internal DurablePlanningReferenceValidator(
        EvidenceLedger evidenceLedger,
        DurableWorkGraphStore workGraphStore)
    {
        _evidenceLedger = evidenceLedger;
        _workGraphStore = workGraphStore;
    }

    public ValueTask<bool> IsEvidenceCommittedAsync(
        TurnIdentity identity,
        CommittedEvidenceReference evidence,
        CancellationToken cancellationToken) =>
        _evidenceLedger.IsCommittedAsync(identity, evidence, cancellationToken);

    public ValueTask<bool> IsWorkGraphCommittedAsync(
        TurnIdentity identity,
        CommittedWorkGraphReference workGraph,
        CancellationToken cancellationToken) =>
        _workGraphStore.IsCommittedAsync(identity, workGraph, cancellationToken);

}
