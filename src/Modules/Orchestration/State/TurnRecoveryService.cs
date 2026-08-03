using System.Collections.Concurrent;
using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Orchestration.State;

public interface ITurnActionReconciler
{
    string ReconcilerId { get; }

    /// <summary>
    /// Observes the target system only. A reconciler must never repeat the prepared effect.
    /// </summary>
    ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken);
}

public sealed record ActionReconciliationResult(
    ActionReconciliationDisposition Disposition,
    string OutcomeCode,
    CommittedEvidenceReference? AppliedEvidence)
{
    public static ActionReconciliationResult Applied(
        string outcomeCode,
        CommittedEvidenceReference evidence) =>
        new(ActionReconciliationDisposition.Applied, outcomeCode, evidence);

    public static ActionReconciliationResult Absent(string outcomeCode) =>
        new(ActionReconciliationDisposition.Absent, outcomeCode, AppliedEvidence: null);

    public static ActionReconciliationResult Unknown(string outcomeCode) =>
        new(ActionReconciliationDisposition.Unknown, outcomeCode, AppliedEvidence: null);

    internal void Validate()
    {
        if (!Enum.IsDefined(Disposition))
        {
            throw new InvalidDataException("The reconciler returned an invalid disposition.");
        }

        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
        if (Disposition == ActionReconciliationDisposition.Applied)
        {
            ArgumentNullException.ThrowIfNull(AppliedEvidence);
            AppliedEvidence.Validate();
        }
        else if (AppliedEvidence is not null)
        {
            throw new InvalidDataException(
                "Only an applied reconciliation may carry an evidence reference.");
        }
    }
}

public enum PublicationReconciliationDisposition
{
    Applied,
    Absent,
    Unknown
}

public interface ITurnPublicationReconciler
{
    /// <summary>
    /// Checks the conversation target for the exact assistant-message ID and answer digest.
    /// It does not publish or modify the answer.
    /// </summary>
    ValueTask<PublicationReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        FinalPublicationState publication,
        CancellationToken cancellationToken);
}

public sealed record PublicationReconciliationResult(
    PublicationReconciliationDisposition Disposition,
    string OutcomeCode)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(Disposition))
        {
            throw new InvalidDataException("The publication reconciler returned an invalid disposition.");
        }

        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
    }
}

public enum TurnRecoveryStatus
{
    NotFound,
    ExplicitRequestRequired,
    RevalidationRequired,
    Ready,
    AwaitingUserDecision,
    StructuredResolutionRequired,
    ConcurrentTransition
}

public sealed record RecoveredAction(
    string IdempotencyKey,
    ActionReconciliationDisposition Disposition,
    string OutcomeCode,
    bool SafeToRetry,
    bool RequiresFreshApproval);

public sealed record RecoveredPublication(
    string PublicationId,
    PublicationReconciliationDisposition Disposition,
    string OutcomeCode,
    bool SafeToPublishIdempotently);

public sealed record TurnRecoveryResult(
    TurnRecoveryStatus Status,
    TurnState? State,
    IReadOnlyList<string> ChangedBindings,
    IReadOnlyList<RecoveredAction> Actions,
    RecoveredPublication? Publication,
    string? OriginalRequest = null);

/// <summary>
/// Explicit recovery gate. It revalidates exact bindings and reconciles observation-only;
/// it has no API capable of executing a prepared action or publishing an answer.
/// </summary>
public sealed class TurnRecoveryService
{
    private readonly TurnTransitionWriter _writer;
    private readonly IReadOnlyDictionary<string, ITurnActionReconciler> _reconcilers;
    private readonly ITurnPublicationReconciler? _publicationReconciler;
    private readonly ConcurrentDictionary<string, RecoveryGate> _turnGates =
        new(StringComparer.Ordinal);

    public TurnRecoveryService(
        TurnTransitionWriter writer,
        IEnumerable<ITurnActionReconciler> reconcilers,
        ITurnPublicationReconciler? publicationReconciler = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentNullException.ThrowIfNull(reconcilers);
        var byId = new Dictionary<string, ITurnActionReconciler>(StringComparer.Ordinal);
        foreach (var reconciler in reconcilers)
        {
            ArgumentNullException.ThrowIfNull(reconciler);
            TurnStateIntegrity.RequireBoundedValue(
                reconciler.ReconcilerId,
                256,
                nameof(reconciler.ReconcilerId));
            if (!byId.TryAdd(reconciler.ReconcilerId, reconciler))
            {
                throw new ArgumentException(
                    $"Duplicate action reconciler '{reconciler.ReconcilerId}'.",
                    nameof(reconcilers));
            }
        }

        _reconcilers = byId;
        _publicationReconciler = publicationReconciler;
    }

    public async Task<TurnRecoveryResult> RecoverAsync(
        TurnIdentity identity,
        TurnRuntimeBindings currentBindings,
        bool explicitlyRequested,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(currentBindings);
        currentBindings.Validate();
        var storageKey = identity.StorageKey;
        var gate = RetainGate(storageKey);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var state = await _writer.ReadAsync(identity, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return Result(TurnRecoveryStatus.NotFound, null);
            }

            if (!explicitlyRequested)
            {
                return Result(TurnRecoveryStatus.ExplicitRequestRequired, state);
            }

            var changedBindings = state.Bindings.FindChangedBindings(currentBindings);
            if (changedBindings.Count > 0)
            {
                return new TurnRecoveryResult(
                    TurnRecoveryStatus.RevalidationRequired,
                    state,
                    changedBindings,
                    [],
                    Publication: null);
            }

            // Only the explicit, exact-binding recovery path may materialize protected user input.
            // The plaintext exists in memory for the resumed planner and is never journaled.
            var originalRequest = await _writer
                .ReadOriginalRequestAsync(identity, cancellationToken)
                .ConfigureAwait(false);

            var recoveredActions = new List<RecoveredAction>();
            foreach (var pendingSnapshot in state.PendingActions
                         .OrderBy(item => item.PreparedAtRevision)
                         .ThenBy(item => item.Intent.IdempotencyKey, StringComparer.Ordinal)
                         .ToArray())
            {
                var pending = state.PendingActions.FirstOrDefault(item =>
                    string.Equals(
                        item.Intent.IdempotencyKey,
                        pendingSnapshot.Intent.IdempotencyKey,
                        StringComparison.Ordinal));
                if (pending is null)
                {
                    continue;
                }

                if (pending.State == ActionIntentState.Prepared)
                {
                    var marked = await _writer.WriteAsync(
                        identity,
                        state.Revision,
                        new ActionMarkedInDoubtTransition(
                            RecoveryCorrelation(
                                identity,
                                "action-in-doubt",
                                pending.Intent.IdempotencyKey,
                                pending.PreparedAtRevision),
                            pending.Intent.IdempotencyKey),
                        cancellationToken).ConfigureAwait(false);
                    if (marked.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(marked.State, recoveredActions, originalRequest);
                    }

                    state = marked.State!;
                    pending = state.PendingActions.Single(item => string.Equals(
                        item.Intent.IdempotencyKey,
                        pending.Intent.IdempotencyKey,
                        StringComparison.Ordinal));
                }

                var reconciliation = await ReconcileActionSafelyAsync(
                    identity,
                    pending.Intent,
                    cancellationToken).ConfigureAwait(false);
                reconciliation.Validate();
                if (reconciliation.Disposition == ActionReconciliationDisposition.Applied)
                {
                    var evidence = reconciliation.AppliedEvidence!;
                    var evidenceCorrelation = RecoveryCorrelation(
                        identity,
                        "reconciled-evidence",
                        pending.Intent.IdempotencyKey + ":" + evidence.RecordDigest,
                        pending.PreparedAtRevision);
                    TurnTransitionWriteResult evidenceWrite;
                    if (state.PendingAcceptedCall is { } acceptedCall)
                    {
                        if (!string.Equals(
                                pending.Intent.AcceptedCallId,
                                acceptedCall.CallId,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "The reconciled action does not match the pending accepted call.");
                        }

                        evidenceWrite = await _writer.RecordAcceptedCallEvidenceAsync(
                            identity,
                            state.Revision,
                            evidence,
                            pending.Intent.IdempotencyKey,
                            acceptedCall.CallId,
                            evidenceCorrelation,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        evidenceWrite = await _writer.RecordEvidenceAsync(
                            identity,
                            state.Revision,
                            evidence,
                            pending.Intent.IdempotencyKey,
                            evidenceCorrelation,
                            cancellationToken).ConfigureAwait(false);
                    }
                    if (evidenceWrite.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(evidenceWrite.State, recoveredActions, originalRequest);
                    }

                    state = evidenceWrite.State!;
                }

                var reconciledWrite = await _writer.WriteAsync(
                    identity,
                    state.Revision,
                    new ActionReconciledTransition(
                        RecoveryCorrelation(
                            identity,
                            "action-reconciled-" + reconciliation.Disposition,
                            pending.Intent.IdempotencyKey + ":" + reconciliation.OutcomeCode,
                            pending.PreparedAtRevision),
                        pending.Intent.IdempotencyKey,
                        reconciliation.Disposition,
                        reconciliation.AppliedEvidence?.EvidenceId,
                        reconciliation.AppliedEvidence?.Cursor,
                        reconciliation.OutcomeCode),
                    cancellationToken).ConfigureAwait(false);
                if (reconciledWrite.Status == TurnTransitionWriteStatus.RevisionConflict)
                {
                    return Concurrent(reconciledWrite.State, recoveredActions, originalRequest);
                }

                state = reconciledWrite.State!;
                if (reconciliation.Disposition == ActionReconciliationDisposition.Unknown
                    && state.Control == TurnControlState.CancelRequested)
                {
                    recoveredActions.Add(new RecoveredAction(
                        pending.Intent.IdempotencyKey,
                        reconciliation.Disposition,
                        reconciliation.OutcomeCode,
                        SafeToRetry: false,
                        RequiresFreshApproval: false));
                    var cancelled = await _writer.CancelInDoubtActionAsync(
                        identity,
                        state.Revision,
                        pending.Intent.IdempotencyKey,
                        pending.PreparedAtRevision,
                        "turn-cancelled-with-action-state-unknown",
                        RecoveryCorrelation(
                            identity,
                            "in-doubt-action-cancelled",
                            pending.Intent.IdempotencyKey,
                            pending.PreparedAtRevision),
                        cancellationToken).ConfigureAwait(false);
                    if (cancelled.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(cancelled.State, recoveredActions, originalRequest);
                    }

                    return new TurnRecoveryResult(
                        TurnRecoveryStatus.Ready,
                        cancelled.State,
                        [],
                        recoveredActions.AsReadOnly(),
                        Publication: null,
                        OriginalRequest: originalRequest);
                }
                if (reconciliation.Disposition == ActionReconciliationDisposition.Absent
                    && pending.Intent.AcceptedCallId is { } absentCallId
                    && state.PendingAcceptedCall is not null)
                {
                    var interrupted = await _writer.InterruptAcceptedCallAsync(
                        identity,
                        state.Revision,
                        absentCallId,
                        "reconciliation-proved-effect-absent",
                        RecoveryCorrelation(
                            identity,
                            "accepted-call-absent",
                            absentCallId,
                            pending.PreparedAtRevision),
                        cancellationToken).ConfigureAwait(false);
                    if (interrupted.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(interrupted.State, recoveredActions, originalRequest);
                    }

                    state = interrupted.State!;
                }
                recoveredActions.Add(new RecoveredAction(
                    pending.Intent.IdempotencyKey,
                    reconciliation.Disposition,
                    reconciliation.OutcomeCode,
                    SafeToRetry: reconciliation.Disposition == ActionReconciliationDisposition.Absent
                                 && !pending.Intent.RequiresApproval,
                    RequiresFreshApproval: reconciliation.Disposition == ActionReconciliationDisposition.Absent
                                            && pending.Intent.RequiresApproval));
            }

            if (state.PendingAcceptedCall is { } acceptedWithoutTerminal
                && state.PendingActions.Length == 0)
            {
                _ = await _writer.ReadPendingAcceptedCallAsync(identity, cancellationToken)
                    .ConfigureAwait(false);
                var interrupted = await _writer.InterruptAcceptedCallAsync(
                    identity,
                    state.Revision,
                    acceptedWithoutTerminal.CallId,
                    acceptedWithoutTerminal.ExecutionClass == AcceptedCallExecutionClass.PreparedEffectRequired
                        ? "effect-not-prepared-before-interruption"
                        : "non-effect-call-interrupted-before-terminal",
                    RecoveryCorrelation(
                        identity,
                        "accepted-call-interrupted",
                        acceptedWithoutTerminal.CallId,
                        state.Revision),
                    cancellationToken).ConfigureAwait(false);
                if (interrupted.Status == TurnTransitionWriteStatus.RevisionConflict)
                {
                    return Concurrent(interrupted.State, recoveredActions, originalRequest);
                }

                state = interrupted.State!;
            }

            RecoveredPublication? recoveredPublication = null;
            if (state.FinalPublication is
                { Status: FinalPublicationStatus.ConfirmedAbsent } confirmedAbsent)
            {
                if (state.Control == TurnControlState.CancelRequested)
                {
                    var abandoned = await _writer.AbandonFinalPublicationAsync(
                        identity,
                        state.Revision,
                        confirmedAbsent.PublicationId,
                        confirmedAbsent.AssistantMessageId,
                        confirmedAbsent.AnswerDigest,
                        "user-confirmed-publication-absent-before-cancellation",
                        RecoveryCorrelation(
                            identity,
                            "confirmed-absent-publication-abandoned",
                            confirmedAbsent.PublicationId + ":" + confirmedAbsent.AnswerDigest,
                            confirmedAbsent.PreparedAtRevision),
                        cancellationToken).ConfigureAwait(false);
                    if (abandoned.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(abandoned.State, recoveredActions, originalRequest);
                    }

                    state = abandoned.State!;
                }
                else
                {
                    recoveredPublication = new RecoveredPublication(
                        confirmedAbsent.PublicationId,
                        PublicationReconciliationDisposition.Absent,
                        "user-confirmed-publication-absent",
                        SafeToPublishIdempotently: true);
                }
            }
            else if (state.FinalPublication is { Status: not FinalPublicationStatus.Committed } publication)
            {
                if (publication.Status == FinalPublicationStatus.Prepared)
                {
                    var marked = await _writer.WriteAsync(
                        identity,
                        state.Revision,
                        new FinalPublicationMarkedInDoubtTransition(
                            RecoveryCorrelation(
                                identity,
                                "publication-in-doubt",
                                publication.PublicationId,
                                publication.PreparedAtRevision),
                            publication.PublicationId,
                            publication.AssistantMessageId,
                            publication.AnswerDigest),
                        cancellationToken).ConfigureAwait(false);
                    if (marked.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(marked.State, recoveredActions, originalRequest);
                    }

                    state = marked.State!;
                    publication = state.FinalPublication!;
                }

                var publicationResult = await ReconcilePublicationSafelyAsync(
                    identity,
                    publication,
                    cancellationToken).ConfigureAwait(false);
                publicationResult.Validate();
                if (publicationResult.Disposition == PublicationReconciliationDisposition.Unknown
                    && state.Control == TurnControlState.CancelRequested)
                {
                    var cancelled = await _writer.CancelInDoubtFinalPublicationAsync(
                        identity,
                        state.Revision,
                        publication.PublicationId,
                        publication.AssistantMessageId,
                        publication.AnswerDigest,
                        publication.PreparedAtRevision,
                        "turn-cancelled-with-publication-state-unknown",
                        RecoveryCorrelation(
                            identity,
                            "in-doubt-publication-cancelled",
                            publication.PublicationId,
                            publication.PreparedAtRevision),
                        cancellationToken).ConfigureAwait(false);
                    if (cancelled.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(cancelled.State, recoveredActions, originalRequest);
                    }

                    return new TurnRecoveryResult(
                        TurnRecoveryStatus.Ready,
                        cancelled.State,
                        [],
                        recoveredActions.AsReadOnly(),
                        new RecoveredPublication(
                            publication.PublicationId,
                            publicationResult.Disposition,
                            publicationResult.OutcomeCode,
                            SafeToPublishIdempotently: false),
                        originalRequest);
                }
                if (publicationResult.Disposition == PublicationReconciliationDisposition.Applied)
                {
                    var committed = await _writer.CommitFinalPublicationAsync(
                        identity,
                        state.Revision,
                        publication.PublicationId,
                        publication.AssistantMessageId,
                        publication.AnswerDigest,
                        RecoveryCorrelation(
                            identity,
                            "publication-committed",
                            publication.PublicationId + ":" + publication.AnswerDigest,
                            publication.PreparedAtRevision),
                        cancellationToken).ConfigureAwait(false);
                    if (committed.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(committed.State, recoveredActions, originalRequest);
                    }
                    state = committed.State!;
                }
                else if (publicationResult.Disposition == PublicationReconciliationDisposition.Absent
                         && state.Control == TurnControlState.CancelRequested)
                {
                    var abandoned = await _writer.AbandonFinalPublicationAsync(
                        identity,
                        state.Revision,
                        publication.PublicationId,
                        publication.AssistantMessageId,
                        publication.AnswerDigest,
                        publicationResult.OutcomeCode,
                        RecoveryCorrelation(
                            identity,
                            "publication-abandoned",
                            publication.PublicationId + ":" + publication.AnswerDigest,
                            publication.PreparedAtRevision),
                        cancellationToken).ConfigureAwait(false);
                    if (abandoned.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(abandoned.State, recoveredActions, originalRequest);
                    }
                    state = abandoned.State!;
                }
                else if (publicationResult.Disposition
                         == PublicationReconciliationDisposition.Absent)
                {
                    var confirmedAbsentWrite = await _writer.ConfirmFinalPublicationAbsentAsync(
                        identity,
                        state.Revision,
                        publication.PublicationId,
                        publication.AssistantMessageId,
                        publication.AnswerDigest,
                        publicationResult.OutcomeCode,
                        RecoveryCorrelation(
                            identity,
                            "publication-confirmed-absent",
                            publication.PublicationId + ":" + publication.AnswerDigest,
                            publication.PreparedAtRevision),
                        cancellationToken).ConfigureAwait(false);
                    if (confirmedAbsentWrite.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(
                            confirmedAbsentWrite.State,
                            recoveredActions,
                            originalRequest);
                    }

                    state = confirmedAbsentWrite.State!;
                }
                recoveredPublication = new RecoveredPublication(
                    publication.PublicationId,
                    publicationResult.Disposition,
                    publicationResult.OutcomeCode,
                    SafeToPublishIdempotently:
                    publicationResult.Disposition == PublicationReconciliationDisposition.Absent);
            }

            var awaitsUser = recoveredActions.Any(action =>
                    action.Disposition == ActionReconciliationDisposition.Unknown)
                || recoveredPublication?.Disposition == PublicationReconciliationDisposition.Unknown;
            var requiredReconciliationReason = recoveredPublication?.Disposition
                    == PublicationReconciliationDisposition.Unknown
                ? InterimPublicationReason.FinalPublicationReconciliationRequired
                : InterimPublicationReason.ActionReconciliationRequired;
            if (awaitsUser && state.Control != TurnControlState.CancelRequested)
            {
                if (state.InterimPublication is { } priorInterim
                    && (priorInterim.Kind != InterimPublicationKind.AwaitingUser
                        || priorInterim.Reason != requiredReconciliationReason))
                {
                    if (state.Control != TurnControlState.Running)
                    {
                        var running = await _writer.ChangeControlAsync(
                            identity,
                            state.Revision,
                            TurnControlState.Running,
                            "recovery-superseding-interim",
                            RecoveryCorrelation(
                                identity,
                                "recovery-interim-running",
                                priorInterim.PublicationId,
                                state.Revision),
                            cancellationToken).ConfigureAwait(false);
                        if (running.Status == TurnTransitionWriteStatus.RevisionConflict)
                        {
                            return Concurrent(running.State, recoveredActions, originalRequest);
                        }

                        state = running.State!;
                    }

                    var cleared = await _writer.ClearInterimPublicationAsync(
                        identity,
                        state.Revision,
                        priorInterim.PublicationId,
                        priorInterim.Kind,
                        priorInterim.TextDigest,
                        "superseded-by-reconciliation-question",
                        RecoveryCorrelation(
                            identity,
                            "recovery-interim-clear",
                            priorInterim.PublicationId,
                            state.Revision),
                        cancellationToken).ConfigureAwait(false);
                    if (cleared.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(cleared.State, recoveredActions, originalRequest);
                    }

                    state = cleared.State!;
                }

                if (state.InterimPublication is null)
                {
                    var subject = recoveredActions.FirstOrDefault(action =>
                                      action.Disposition == ActionReconciliationDisposition.Unknown)
                                      ?.IdempotencyKey
                                  ?? recoveredPublication?.PublicationId
                                  ?? throw new InvalidDataException(
                                      "Recovery requires user input without an exact recovery subject.");
                    var prompt = recoveredPublication?.Disposition
                                     == PublicationReconciliationDisposition.Unknown
                        ? "Ali could not prove whether the prepared answer was already displayed. Use the recovery buttons to confirm whether you saw it so Ali can finish without duplicating it."
                        : "Ali could not prove whether the interrupted action was applied. Check the target, then use the recovery buttons to confirm whether the action happened.";
                    var promptDigest = TurnStateIntegrity.Digest(prompt);
                    var interimId = "recovery_" + TurnStateIntegrity.Digest(
                        identity.StorageKey + "\0" + subject + "\0" + promptDigest)[..32];
                    var preparedInterim = await _writer.PrepareInterimPublicationAsync(
                        identity,
                        state.Revision,
                        interimId,
                        InterimPublicationKind.AwaitingUser,
                        requiredReconciliationReason,
                        subject,
                        prompt,
                        promptDigest,
                        RecoveryCorrelation(
                            identity,
                            "recovery-interim-prepare",
                            interimId,
                            state.Revision),
                        cancellationToken).ConfigureAwait(false);
                    if (preparedInterim.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(preparedInterim.State, recoveredActions, originalRequest);
                    }

                    state = preparedInterim.State!;
                }

                if (state.InterimPublication?.Status == InterimPublicationStatus.Committed
                    && state.Control != TurnControlState.AwaitingUser)
                {
                    var awaiting = await _writer.ChangeControlAsync(
                        identity,
                        state.Revision,
                        TurnControlState.AwaitingUser,
                        "reconciliation-requires-user",
                        RecoveryCorrelation(
                            identity,
                            "recovery-awaiting-user",
                            state.InterimPublication!.PublicationId,
                            state.Revision),
                        cancellationToken).ConfigureAwait(false);
                    if (awaiting.Status == TurnTransitionWriteStatus.RevisionConflict)
                    {
                        return Concurrent(awaiting.State, recoveredActions, originalRequest);
                    }

                    state = awaiting.State!;
                }
            }

            if (state.FinalPublication is
                {
                    Status: FinalPublicationStatus.Committed
                } committedPublication
                && state.PendingActions.Length == 0
                && state.PendingAcceptedCall is null
                && state.InterimPublication is null
                && state.Control != TurnControlState.CancelRequested
                && state.Control != TurnControlState.Completed)
            {
                var completed = await _writer.ChangeControlAsync(
                    identity,
                    state.Revision,
                    TurnControlState.Completed,
                    "recovered-answer-published",
                    RecoveryCorrelation(
                        identity,
                        "recovered-turn-completed",
                        committedPublication.PublicationId,
                        committedPublication.PreparedAtRevision),
                    cancellationToken).ConfigureAwait(false);
                if (completed.Status == TurnTransitionWriteStatus.RevisionConflict)
                {
                    return Concurrent(completed.State, recoveredActions, originalRequest);
                }

                state = completed.State!;
            }

            if (state.Control == TurnControlState.CancelRequested
                && state.InterimPublication is { } cancelledInterim)
            {
                var cleared = await _writer.ClearInterimPublicationAsync(
                    identity,
                    state.Revision,
                    cancelledInterim.PublicationId,
                    cancelledInterim.Kind,
                    cancelledInterim.TextDigest,
                    "turn-cancelled",
                    RecoveryCorrelation(
                        identity,
                        "cancel-interim-clear",
                        cancelledInterim.PublicationId,
                        state.Revision),
                    cancellationToken).ConfigureAwait(false);
                if (cleared.Status == TurnTransitionWriteStatus.RevisionConflict)
                {
                    return Concurrent(cleared.State, recoveredActions, originalRequest);
                }

                state = cleared.State!;
            }

            if (state.Control == TurnControlState.CancelRequested
                && state.PendingActions.Length == 0
                && state.PendingAcceptedCall is null
                && state.InterimPublication is null
                && (state.FinalPublication is null or
                    { Status: FinalPublicationStatus.Committed }))
            {
                var cancelled = await _writer.ChangeControlAsync(
                    identity,
                    state.Revision,
                    TurnControlState.Cancelled,
                    "recovered-cancellation-finished",
                    RecoveryCorrelation(
                        identity,
                        "recovered-turn-cancelled",
                        state.FinalPublication?.PublicationId ?? "no-publication",
                        state.Revision),
                    cancellationToken).ConfigureAwait(false);
                if (cancelled.Status == TurnTransitionWriteStatus.RevisionConflict)
                {
                    return Concurrent(cancelled.State, recoveredActions, originalRequest);
                }

                state = cancelled.State!;
            }

            return new TurnRecoveryResult(
                state.Control is TurnControlState.Cancelled or TurnControlState.Completed
                    ? TurnRecoveryStatus.Ready
                    : awaitsUser
                    ? TurnRecoveryStatus.StructuredResolutionRequired
                    : state.Control == TurnControlState.AwaitingUser
                        ? TurnRecoveryStatus.AwaitingUserDecision
                        : TurnRecoveryStatus.Ready,
                state,
                [],
                recoveredActions.AsReadOnly(),
                recoveredPublication,
                originalRequest);
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }

            ReleaseGate(storageKey, gate);
        }
    }

    internal int CaptureRetainedTurnGateCount() => _turnGates.Count;

    private RecoveryGate RetainGate(string storageKey)
    {
        while (true)
        {
            var gate = _turnGates.GetOrAdd(storageKey, static _ => new RecoveryGate());
            lock (gate.Sync)
            {
                if (gate.Retired)
                {
                    continue;
                }

                checked
                {
                    gate.References++;
                }
                return gate;
            }
        }
    }

    private void ReleaseGate(string storageKey, RecoveryGate gate)
    {
        var retire = false;
        lock (gate.Sync)
        {
            if (gate.References <= 0)
            {
                throw new InvalidOperationException("The turn-recovery gate count is inconsistent.");
            }

            gate.References--;
            if (gate.References == 0)
            {
                gate.Retired = true;
                retire = true;
            }
        }

        if (!retire)
        {
            return;
        }

        _turnGates.TryRemove(
            new KeyValuePair<string, RecoveryGate>(storageKey, gate));
        gate.Semaphore.Dispose();
    }

    private async ValueTask<ActionReconciliationResult> ReconcileActionSafelyAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        if (!_reconcilers.TryGetValue(intent.ReconcilerId, out var reconciler))
        {
            return ActionReconciliationResult.Unknown("reconciler-unavailable");
        }

        try
        {
            return await reconciler.ReconcileAsync(identity, intent, cancellationToken)
                .ConfigureAwait(false)
                ?? ActionReconciliationResult.Unknown("reconciler-returned-null");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var exceptionCode = TurnStateIntegrity.Digest(
                ex.GetType().FullName ?? ex.GetType().Name)[..24];
            return ActionReconciliationResult.Unknown("reconciler-threw-" + exceptionCode);
        }
    }

    private async ValueTask<PublicationReconciliationResult> ReconcilePublicationSafelyAsync(
        TurnIdentity identity,
        FinalPublicationState publication,
        CancellationToken cancellationToken)
    {
        if (_publicationReconciler is null)
        {
            return new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Unknown,
                "publication-reconciler-unavailable");
        }

        try
        {
            return await _publicationReconciler
                .ReconcileAsync(identity, publication, cancellationToken)
                .ConfigureAwait(false)
                ?? new PublicationReconciliationResult(
                    PublicationReconciliationDisposition.Unknown,
                    "publication-reconciler-returned-null");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var exceptionCode = TurnStateIntegrity.Digest(
                ex.GetType().FullName ?? ex.GetType().Name)[..24];
            return new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Unknown,
                "publication-reconciler-threw-" + exceptionCode);
        }
    }

    private static string RecoveryCorrelation(
        TurnIdentity identity,
        string kind,
        string subject,
        long preparedAtRevision) =>
        TurnStateIntegrity.Digest(
            identity.StorageKey + "\0" + kind + "\0" + subject + "\0" + preparedAtRevision);

    private static TurnRecoveryResult Result(TurnRecoveryStatus status, TurnState? state) =>
        new(status, state, [], [], Publication: null);

    private static TurnRecoveryResult Concurrent(
        TurnState? state,
        IReadOnlyList<RecoveredAction> recoveredActions,
        string originalRequest) =>
        new(
            TurnRecoveryStatus.ConcurrentTransition,
            state,
            [],
            recoveredActions,
            Publication: null,
            OriginalRequest: originalRequest);

    private sealed class RecoveryGate
    {
        internal object Sync { get; } = new();

        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int References { get; set; }

        internal bool Retired { get; set; }
    }
}
