using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Orchestration;

internal sealed record AliPlanningResumeAttempt(
    AliDurablePlanningTurn? Turn,
    TurnRecoveryResult Recovery,
    string? FailureCode,
    AliRecoveredFinalPublication? RecoveredPublication = null,
    AliRecoveredInterimPublication? RecoveredInterimPublication = null)
{
    internal bool IsReady => Turn is not null
        && FailureCode is null
        && RecoveredPublication is null
        && RecoveredInterimPublication is null;
}

internal sealed record AliRecoveredFinalPublication(
    TurnIdentity DurableIdentity,
    string PublicationId,
    string AssistantMessageId,
    string AnswerText,
    string AnswerDigest);

internal sealed record AliRecoveredInterimPublication(
    TurnIdentity DurableIdentity,
    string PublicationId,
    InterimPublicationKind Kind,
    InterimPublicationReason Reason,
    string SubjectId,
    long SubjectPreparedRevision,
    string Text,
    string TextDigest);

internal sealed partial class AliPlanningStateCoordinator
{
    internal async Task<AliPlanningResumeAttempt> ResumeTurnAsync(
        CoordinatorTurnContext visibleTurn,
        TurnIdentity durableIdentity,
        TurnRuntimeBindings currentBindings,
        string steeringText,
        string steeringSourceMessageId,
        CanonicalCapabilityRegistry? capabilityRegistry,
        Func<TurnRuntimeBindings>? liveBindingsAccessor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(visibleTurn);
        ArgumentNullException.ThrowIfNull(durableIdentity);
        ArgumentNullException.ThrowIfNull(currentBindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(steeringText);
        TurnStateIntegrity.RequireBoundedValue(
            steeringSourceMessageId,
            256,
            nameof(steeringSourceMessageId));
        var visibleIdentity = visibleTurn.ObservationIdentity
            ?? throw new InvalidDataException(
                "An explicit durable resume requires the visible turn's admitted user identity.");
        if (!string.Equals(
                visibleTurn.ConversationId,
                durableIdentity.ConversationId,
                StringComparison.Ordinal)
            || !string.Equals(
                visibleIdentity.ConversationId,
                durableIdentity.ConversationId,
                StringComparison.Ordinal)
            || !string.Equals(
                visibleIdentity.UserId,
                durableIdentity.UserId,
                StringComparison.Ordinal)
            || !string.Equals(
                visibleIdentity.AssistantMessageId,
                visibleTurn.AssistantMessageId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A visible response cannot resume a durable turn across its admitted user or conversation boundary.");
        }

        await _evidenceLedger.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var preRecoveryState = await _transitionWriter
            .ReadAsync(durableIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (preRecoveryState?.InterimPublication is
            {
                Status: InterimPublicationStatus.Committed,
                Kind: InterimPublicationKind.SuspendedRuntime
            } admissionInterim
            && admissionInterim.Reason.IsModelConfigurationRecoverablePause())
        {
            if (preRecoveryState.Control is not (
                    TurnControlState.SuspendedRuntime or TurnControlState.Running)
                || preRecoveryState.PendingActions.Length != 0
                || preRecoveryState.PendingAcceptedCall is not null
                || preRecoveryState.FinalPublication is not null
                || !string.Equals(
                    admissionInterim.SubjectId,
                    admissionInterim.PublicationId,
                    StringComparison.Ordinal))
            {
                return ResumeRevalidationFailure(
                    preRecoveryState,
                    [],
                    "model-input-admission-resume-preconditions-not-met");
            }

            TurnRuntimeBindings freshBindings;
            try
            {
                freshBindings = liveBindingsAccessor?.Invoke() ?? currentBindings;
                if (freshBindings is null)
                {
                    throw new InvalidOperationException(
                        "The live runtime binding accessor returned no snapshot.");
                }

                freshBindings.Validate();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ResumeRevalidationFailure(
                    preRecoveryState,
                    Array.AsReadOnly(new[]
                    {
                        "runtime-bindings-unavailable-" + ex.GetType().Name
                    }),
                    "live-runtime-bindings-unavailable");
            }

            var changedSinceAdmissionRequest = currentBindings.FindChangedBindings(freshBindings);
            if (changedSinceAdmissionRequest.Count != 0)
            {
                return ResumeRevalidationFailure(
                    preRecoveryState,
                    changedSinceAdmissionRequest,
                    "live-runtime-bindings-changed-during-resume");
            }

            currentBindings = freshBindings;
            var changedBindings = preRecoveryState.Bindings.FindChangedBindings(currentBindings);
            if (changedBindings.Any(static binding =>
                    binding is not "model" and not "generation-settings"))
            {
                return ResumeRevalidationFailure(
                    preRecoveryState,
                    changedBindings,
                    "model-input-admission-unrelated-bindings-changed");
            }

            if (changedBindings.Count != 0)
            {
                var revalidated = await _transitionWriter.RevalidateRuntimeBindingsAsync(
                    durableIdentity,
                    preRecoveryState.Revision,
                    admissionInterim.PublicationId,
                    admissionInterim.TextDigest,
                    currentBindings,
                    Correlation(
                        durableIdentity,
                        "model-input-bindings-revalidated",
                        admissionInterim.PublicationId
                        + ":" + currentBindings.ModelDigest
                        + ":" + currentBindings.GenerationSettingsDigest,
                        preRecoveryState.Revision),
                    cancellationToken).ConfigureAwait(false);
                if (revalidated.Status == TurnTransitionWriteStatus.RevisionConflict
                    || revalidated.State is null)
                {
                    return new AliPlanningResumeAttempt(
                        Turn: null,
                        new TurnRecoveryResult(
                            TurnRecoveryStatus.ConcurrentTransition,
                            revalidated.State,
                            [],
                            [],
                            Publication: null),
                        FailureCode: "concurrent-transition");
                }
            }
        }

        var recovery = await _recoveryService.RecoverAsync(
            durableIdentity,
            currentBindings,
            explicitlyRequested: true,
            cancellationToken).ConfigureAwait(false);
        if (recovery.State is null
            || string.IsNullOrWhiteSpace(recovery.OriginalRequest))
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: "recovery-" + recovery.Status.ToString().ToLowerInvariant());
        }

        var state = recovery.State;
        if (state.InterimPublication is { } interim
            && (interim.Status == InterimPublicationStatus.Prepared
                || interim.Status == InterimPublicationStatus.Committed
                && IsStructuredReconciliation(interim.Reason)))
        {
            var text = await _transitionWriter.ReadInterimPublicationTextAsync(
                durableIdentity,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    TurnStateIntegrity.Digest(text),
                    interim.TextDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The recovered interim response differs from its exact durable digest.");
            }

            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: null,
                RecoveredPublication: null,
                RecoveredInterimPublication: new AliRecoveredInterimPublication(
                    durableIdentity,
                    interim.PublicationId,
                    interim.Kind,
                    interim.Reason,
                    interim.SubjectId,
                    ResolveSubjectPreparedRevision(state, interim),
                    text,
                    interim.TextDigest));
        }

        if (recovery.Publication is { SafeToPublishIdempotently: true }
            && state.FinalPublication is { Status: FinalPublicationStatus.ConfirmedAbsent })
        {
            return await CreateRecoveredFinalPublicationAttemptAsync(
                visibleTurn,
                durableIdentity,
                state,
                recovery,
                cancellationToken).ConfigureAwait(false);
        }

        var resumesCommittedUserPause = (recovery.Status is
                TurnRecoveryStatus.Ready or TurnRecoveryStatus.AwaitingUserDecision)
            && state.InterimPublication is
            {
                Status: InterimPublicationStatus.Committed,
                Kind: InterimPublicationKind.AwaitingUser,
                Reason: InterimPublicationReason.PlannerAwaitingUser
            } plannerPause
            && string.Equals(
                plannerPause.SubjectId,
                plannerPause.PublicationId,
                StringComparison.Ordinal);
        var resumesOrdinaryRunningTurn = recovery.Status == TurnRecoveryStatus.Ready
            && state.InterimPublication is null;
        var resumesCommittedModelConfigurationPause = recovery.Status == TurnRecoveryStatus.Ready
            && state.Control is (TurnControlState.SuspendedRuntime or TurnControlState.Running)
            && state.PendingActions.Length == 0
            && state.PendingAcceptedCall is null
            && state.FinalPublication is null
            && state.InterimPublication is
            {
                Status: InterimPublicationStatus.Committed,
                Kind: InterimPublicationKind.SuspendedRuntime
            } inputAdmissionPause
            && inputAdmissionPause.Reason.IsModelConfigurationRecoverablePause()
            && string.Equals(
                inputAdmissionPause.SubjectId,
                inputAdmissionPause.PublicationId,
                StringComparison.Ordinal);
        if (!resumesOrdinaryRunningTurn
            && !resumesCommittedUserPause
            && !resumesCommittedModelConfigurationPause)
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: "recovery-" + recovery.Status.ToString().ToLowerInvariant());
        }

        if (state.Control is TurnControlState.Completed
            or TurnControlState.Cancelled
            or TurnControlState.CancelRequested
            || state.FinalPublication is not null)
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: "turn-terminal-or-publication-pending");
        }

        var resumeProjection = await _transitionWriter.ReadResumeProjectionAsync(
            durableIdentity,
            cancellationToken).ConfigureAwait(false);
        if (resumeProjection.State is null
            || resumeProjection.State.Revision != state.Revision)
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: "concurrent-transition");
        }

        var steeringDigest = TurnStateIntegrity.Digest(steeringText);
        var steeringWrite = await _transitionWriter.AppendSteeringAsync(
            durableIdentity,
            state.Revision,
            steeringText,
            Correlation(
                durableIdentity,
                "resume-steering",
                steeringSourceMessageId + ":" + steeringDigest,
                revision: 0),
            cancellationToken).ConfigureAwait(false);
        if (steeringWrite.Status == TurnTransitionWriteStatus.RevisionConflict
            || steeringWrite.State is null)
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery with { State = steeringWrite.State },
                FailureCode: "concurrent-transition");
        }

        state = steeringWrite.State;
        var committedInterim = state.InterimPublication is
            { Status: InterimPublicationStatus.Committed } exactInterim
            ? exactInterim
            : null;

        if (state.Control != TurnControlState.Running)
        {
            var controlWrite = await _transitionWriter.ChangeControlAsync(
                durableIdentity,
                state.Revision,
                TurnControlState.Running,
                "explicit-resume",
                Correlation(
                    durableIdentity,
                    "resume-control-running",
                    steeringDigest,
                    state.Revision),
                cancellationToken).ConfigureAwait(false);
            if (controlWrite.Status == TurnTransitionWriteStatus.RevisionConflict
                || controlWrite.State is null)
            {
                return new AliPlanningResumeAttempt(
                    Turn: null,
                    recovery with { State = controlWrite.State },
                    FailureCode: "concurrent-transition");
            }

            state = controlWrite.State;
        }

        if (committedInterim is not null)
        {
            var cleared = await _transitionWriter.ClearInterimPublicationAsync(
                durableIdentity,
                state.Revision,
                committedInterim.PublicationId,
                committedInterim.Kind,
                committedInterim.TextDigest,
                committedInterim.Reason.IsModelConfigurationRecoverablePause()
                    ? "explicit-model-configuration-resume"
                    : "resumed-with-user-steering",
                Correlation(
                    durableIdentity,
                    "resume-interim-cleared",
                    committedInterim.PublicationId + ":" + steeringSourceMessageId,
                    revision: 0),
                cancellationToken).ConfigureAwait(false);
            if (cleared.Status == TurnTransitionWriteStatus.RevisionConflict
                || cleared.State is null)
            {
                return new AliPlanningResumeAttempt(
                    Turn: null,
                    recovery with { State = cleared.State },
                    FailureCode: "concurrent-transition");
            }

            state = cleared.State;
        }

        var resumedProjection = await _transitionWriter.ReadResumeProjectionAsync(
            durableIdentity,
            cancellationToken).ConfigureAwait(false);
        if (resumedProjection.State?.Revision != state.Revision)
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery with { State = resumedProjection.State },
                FailureCode: "concurrent-transition");
        }
        return await CreateReadyResumeAttemptAsync(
            visibleTurn,
            durableIdentity,
            currentBindings,
            state,
            recovery,
            resumedProjection,
            capabilityRegistry,
            liveBindingsAccessor,
            cancellationToken).ConfigureAwait(false);
    }

    private static AliPlanningResumeAttempt ResumeRevalidationFailure(
        TurnState state,
        IReadOnlyList<string> changedBindings,
        string failureCode) =>
        new(
            Turn: null,
            new TurnRecoveryResult(
                TurnRecoveryStatus.RevalidationRequired,
                state,
                changedBindings,
                [],
                Publication: null),
            failureCode);

    internal async Task<AliPlanningResumeAttempt> RecoverResolvedFinalPublicationAsync(
        CoordinatorTurnContext visibleTurn,
        TurnIdentity durableIdentity,
        TurnRuntimeBindings currentBindings,
        string sourceCommandId,
        long resolutionStateRevision,
        string subjectId,
        long subjectPreparedRevision,
        FinalPublicationUserResolution resolution,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(visibleTurn);
        ArgumentNullException.ThrowIfNull(durableIdentity);
        ArgumentNullException.ThrowIfNull(currentBindings);
        TurnStateIntegrity.RequireBoundedValue(sourceCommandId, 256, nameof(sourceCommandId));
        TurnStateIntegrity.RequireBoundedValue(subjectId, 256, nameof(subjectId));
        if (resolutionStateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolutionStateRevision));
        }
        if (subjectPreparedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectPreparedRevision));
        }
        if (resolution != FinalPublicationUserResolution.ConfirmNotDisplayed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolution),
                "Only a confirmed-not-displayed publication can enter exact recovery publication.");
        }
        if (!string.Equals(
                visibleTurn.ConversationId,
                durableIdentity.ConversationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A visible response cannot resume a durable turn from another conversation.");
        }

        await _evidenceLedger.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var recovery = await _recoveryService.RecoverAsync(
            durableIdentity,
            currentBindings,
            explicitlyRequested: true,
            cancellationToken).ConfigureAwait(false);
        var state = recovery.State;
        if (recovery.Status != TurnRecoveryStatus.Ready
            || recovery.Publication is not { SafeToPublishIdempotently: true }
            || state is null
            || state.Revision != resolutionStateRevision
            || state.Control != TurnControlState.Running
            || state.PendingActions.Length != 0
            || state.PendingAcceptedCall is not null
            || state.InterimPublication is not null
            || state.FinalPublication is not
                { Status: FinalPublicationStatus.ConfirmedAbsent })
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: "typed-final-resolution-not-current");
        }

        var resumeProjection = await _transitionWriter.ReadResumeProjectionAsync(
            durableIdentity,
            cancellationToken).ConfigureAwait(false);
        var exactResolution = resumeProjection.UserResolutions.LastOrDefault();
        if (resumeProjection.State?.Revision != state.Revision
            || exactResolution is null
            || exactResolution.StateRevision != resolutionStateRevision
            || exactResolution.Kind != TurnUserResolutionKind.FinalPublication
            || exactResolution.Reason
                != InterimPublicationReason.FinalPublicationReconciliationRequired
            || exactResolution.Outcome
                != TurnUserResolutionOutcome.FinalPublicationConfirmedNotDisplayed
            || exactResolution.SubjectPreparedRevision != subjectPreparedRevision
            || !string.Equals(exactResolution.SubjectId, subjectId, StringComparison.Ordinal)
            || !string.Equals(
                exactResolution.SourceCommandId,
                sourceCommandId,
                StringComparison.Ordinal))
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery with { State = resumeProjection.State },
                FailureCode: "typed-final-resolution-not-current");
        }

        return await CreateRecoveredFinalPublicationAttemptAsync(
            visibleTurn,
            durableIdentity,
            state,
            recovery,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AliPlanningResumeAttempt> ResumeResolvedActionAsync(
        CoordinatorTurnContext visibleTurn,
        TurnIdentity durableIdentity,
        TurnRuntimeBindings currentBindings,
        string sourceCommandId,
        long resolutionStateRevision,
        string subjectId,
        long subjectPreparedRevision,
        ActionUserResolution resolution,
        CanonicalCapabilityRegistry? capabilityRegistry,
        Func<TurnRuntimeBindings>? liveBindingsAccessor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(visibleTurn);
        ArgumentNullException.ThrowIfNull(durableIdentity);
        ArgumentNullException.ThrowIfNull(currentBindings);
        TurnStateIntegrity.RequireBoundedValue(sourceCommandId, 256, nameof(sourceCommandId));
        TurnStateIntegrity.RequireBoundedValue(subjectId, 256, nameof(subjectId));
        if (resolutionStateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolutionStateRevision));
        }
        if (subjectPreparedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectPreparedRevision));
        }
        if (!Enum.IsDefined(resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
        if (!string.Equals(
                visibleTurn.ConversationId,
                durableIdentity.ConversationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A visible response cannot resume a durable turn from another conversation.");
        }

        await _evidenceLedger.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var recovery = await _recoveryService.RecoverAsync(
            durableIdentity,
            currentBindings,
            explicitlyRequested: true,
            cancellationToken).ConfigureAwait(false);
        var state = recovery.State;
        if (state is null || string.IsNullOrWhiteSpace(recovery.OriginalRequest))
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: "recovery-" + recovery.Status.ToString().ToLowerInvariant());
        }
        if (recovery.Status != TurnRecoveryStatus.Ready
            || state.Revision != resolutionStateRevision
            || state.Control != TurnControlState.Running
            || state.PendingActions.Length != 0
            || state.PendingAcceptedCall is not null
            || state.InterimPublication is not null
            || state.FinalPublication is not null)
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery,
                FailureCode: "typed-action-resolution-not-current");
        }

        var resumeProjection = await _transitionWriter.ReadResumeProjectionAsync(
            durableIdentity,
            cancellationToken).ConfigureAwait(false);
        var exactResolution = resumeProjection.UserResolutions.LastOrDefault();
        var expectedOutcome = resolution switch
        {
            ActionUserResolution.ConfirmApplied =>
                TurnUserResolutionOutcome.ActionConfirmedApplied,
            ActionUserResolution.ConfirmAbsent =>
                TurnUserResolutionOutcome.ActionConfirmedAbsent,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };
        if (resumeProjection.State?.Revision != state.Revision
            || exactResolution is null
            || exactResolution.StateRevision != resolutionStateRevision
            || exactResolution.Kind != TurnUserResolutionKind.Action
            || exactResolution.Reason
                != InterimPublicationReason.ActionReconciliationRequired
            || exactResolution.Outcome != expectedOutcome
            || exactResolution.SubjectPreparedRevision != subjectPreparedRevision
            || !string.Equals(exactResolution.SubjectId, subjectId, StringComparison.Ordinal)
            || !string.Equals(
                exactResolution.SourceCommandId,
                sourceCommandId,
                StringComparison.Ordinal))
        {
            return new AliPlanningResumeAttempt(
                Turn: null,
                recovery with { State = resumeProjection.State },
                FailureCode: "typed-action-resolution-not-current");
        }

        return await CreateReadyResumeAttemptAsync(
            visibleTurn,
            durableIdentity,
            currentBindings,
            state,
            recovery,
            resumeProjection,
            capabilityRegistry,
            liveBindingsAccessor,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AliPlanningResumeAttempt> CreateRecoveredFinalPublicationAttemptAsync(
        CoordinatorTurnContext visibleTurn,
        TurnIdentity durableIdentity,
        TurnState state,
        TurnRecoveryResult recovery,
        CancellationToken cancellationToken)
    {
        if (recovery.Publication is not { SafeToPublishIdempotently: true }
            || state.FinalPublication is not
                { Status: FinalPublicationStatus.ConfirmedAbsent } absent)
        {
            throw new InvalidDataException(
                "Exact final-publication recovery requires a confirmed-absent receipt.");
        }

        var answer = await _transitionWriter.ReadFinalPublicationAnswerAsync(
            durableIdentity,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                TurnStateIntegrity.Digest(answer),
                absent.AnswerDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovered final answer differs from its exact durable digest.");
        }

        var publicationId = "publication_" + visibleTurn.AssistantMessageId;
        if (!string.Equals(absent.PublicationId, publicationId, StringComparison.Ordinal)
            || !string.Equals(
                absent.AssistantMessageId,
                visibleTurn.AssistantMessageId,
                StringComparison.Ordinal))
        {
            var retargeted = await _transitionWriter.RetargetFinalPublicationAsync(
                durableIdentity,
                state.Revision,
                absent.PublicationId,
                absent.AssistantMessageId,
                publicationId,
                visibleTurn.AssistantMessageId,
                absent.AnswerDigest,
                Correlation(
                    durableIdentity,
                    "publication-retarget",
                    absent.PublicationId + ":" + absent.AssistantMessageId + ":"
                    + publicationId + ":" + visibleTurn.AssistantMessageId + ":"
                    + absent.AnswerDigest,
                    revision: 0),
                cancellationToken).ConfigureAwait(false);
            if (retargeted.Status == TurnTransitionWriteStatus.RevisionConflict
                || retargeted.State is null)
            {
                return new AliPlanningResumeAttempt(
                    Turn: null,
                    recovery with { State = retargeted.State },
                    FailureCode: "concurrent-transition");
            }

            state = retargeted.State;
        }

        return new AliPlanningResumeAttempt(
            Turn: null,
            recovery with { State = state },
            FailureCode: null,
            new AliRecoveredFinalPublication(
                durableIdentity,
                publicationId,
                visibleTurn.AssistantMessageId,
                answer,
                absent.AnswerDigest));
    }

    private static bool IsStructuredReconciliation(InterimPublicationReason reason) =>
        reason is InterimPublicationReason.ActionReconciliationRequired
            or InterimPublicationReason.FinalPublicationReconciliationRequired;

    private static long ResolveSubjectPreparedRevision(
        TurnState state,
        InterimPublicationState interim)
    {
        if (interim.Reason == InterimPublicationReason.ActionReconciliationRequired)
        {
            if (state.PendingActions.Length != 1
                || state.PendingActions[0].State != ActionIntentState.InDoubt
                || !string.Equals(
                    state.PendingActions[0].Intent.IdempotencyKey,
                    interim.SubjectId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The structured recovery prompt lost its exact prepared subject.");
            }

            return state.PendingActions[0].PreparedAtRevision;
        }

        if (interim.Reason == InterimPublicationReason.FinalPublicationReconciliationRequired)
        {
            if (state.FinalPublication is not
                    { Status: FinalPublicationStatus.InDoubt } publication
                || !string.Equals(
                    publication.PublicationId,
                    interim.SubjectId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The structured recovery prompt lost its exact prepared subject.");
            }

            return publication.PreparedAtRevision;
        }

        return interim.PreparedAtRevision;
    }

    private async Task<AliPlanningResumeAttempt> CreateReadyResumeAttemptAsync(
        CoordinatorTurnContext visibleTurn,
        TurnIdentity durableIdentity,
        TurnRuntimeBindings currentBindings,
        TurnState state,
        TurnRecoveryResult recovery,
        TurnResumeProjection resumeProjection,
        CanonicalCapabilityRegistry? capabilityRegistry,
        Func<TurnRuntimeBindings>? liveBindingsAccessor,
        CancellationToken cancellationToken)
    {
        var workGraph = await ReadSelectedWorkGraphAsync(
            durableIdentity,
            state,
            cancellationToken).ConfigureAwait(false);
        var evidence = await RehydrateEvidenceAsync(
            durableIdentity,
            resumeProjection.EvidenceReferences,
            cancellationToken).ConfigureAwait(false);
        var progress = RestoreProgressHistory(resumeProjection.ProgressAttempts);
        var conversation = await RestoreConversationAsync(
            durableIdentity,
            resumeProjection.SteeringTransitions,
            cancellationToken).ConfigureAwait(false);
        var resumed = new AliDurablePlanningTurn(
            visibleTurn,
            durableIdentity,
            state.Bindings,
            state,
            workGraph,
            conversation,
            capabilityRegistry,
            _evidenceLedger,
            _workGraphStore,
            _referenceValidator,
            _transitionWriter,
            _effectNormalizations,
            _targetStates,
            _durableEffects,
            liveBindingsAccessor ?? (() => currentBindings));
        resumed.HydrateRecoveredState(
            recovery.OriginalRequest!,
            evidence.Projections,
            evidence.KnownEvidenceIds,
            resumeProjection.UserResolutions,
            progress);
        return new AliPlanningResumeAttempt(
            resumed,
            recovery with { Status = TurnRecoveryStatus.Ready, State = state },
            FailureCode: null);
    }

    internal async Task CommitRecoveredFinalPublicationAsync(
        AliRecoveredFinalPublication recovered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        var state = await _transitionWriter.ReadAsync(
                recovered.DurableIdentity,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The recovered publication turn no longer exists.");
        var publication = state.FinalPublication
            ?? throw new InvalidDataException("The recovered publication is no longer prepared.");
        if (state.PendingActions.Length != 0
            || state.PendingAcceptedCall is not null
            || state.InterimPublication is not null
            || !string.Equals(publication.PublicationId, recovered.PublicationId, StringComparison.Ordinal)
            || !string.Equals(publication.AssistantMessageId, recovered.AssistantMessageId, StringComparison.Ordinal)
            || !string.Equals(publication.AnswerDigest, recovered.AnswerDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The acknowledged recovered publication differs from current durable state.");
        }

        var committed = await _transitionWriter.CommitFinalPublicationAsync(
            recovered.DurableIdentity,
            state.Revision,
            recovered.PublicationId,
            recovered.AssistantMessageId,
            recovered.AnswerDigest,
            Correlation(
                recovered.DurableIdentity,
                "recovered-publication-commit",
                recovered.PublicationId + ":" + recovered.AnswerDigest,
                state.Revision),
            cancellationToken).ConfigureAwait(false);
        if (committed.Status != TurnTransitionWriteStatus.Committed || committed.State is null)
        {
            throw new InvalidOperationException(
                "The recovered final publication changed before its acknowledgment committed.");
        }

        var completed = await _transitionWriter.ChangeControlAsync(
            recovered.DurableIdentity,
            committed.State.Revision,
            TurnControlState.Completed,
            "recovered-answer-published",
            Correlation(
                recovered.DurableIdentity,
                "recovered-publication-completed",
                recovered.PublicationId,
                committed.State.Revision),
            cancellationToken).ConfigureAwait(false);
        if (completed.Status != TurnTransitionWriteStatus.Committed
            || completed.State?.Control != TurnControlState.Completed)
        {
            throw new InvalidOperationException(
                "The recovered publication committed but its turn did not complete durably.");
        }

        await _pausedTurnCatalog.RemoveExactAsync(
            recovered.DurableIdentity,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TurnState> CommitRecoveredInterimPublicationAsync(
        AliRecoveredInterimPublication recovered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        var state = await _transitionWriter.ReadAsync(
                recovered.DurableIdentity,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The recovered interim turn no longer exists.");
        var interim = state.InterimPublication
            ?? throw new InvalidDataException("The recovered interim response is no longer prepared.");
        if (!string.Equals(interim.PublicationId, recovered.PublicationId, StringComparison.Ordinal)
            || interim.Kind != recovered.Kind
            || interim.Reason != recovered.Reason
            || !string.Equals(interim.SubjectId, recovered.SubjectId, StringComparison.Ordinal)
            || ResolveSubjectPreparedRevision(state, interim)
                != recovered.SubjectPreparedRevision
            || !string.Equals(interim.TextDigest, recovered.TextDigest, StringComparison.Ordinal)
            || !string.Equals(
                TurnStateIntegrity.Digest(recovered.Text),
                recovered.TextDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The displayed recovered interim response differs from durable state.");
        }

        TurnState committedState;
        if (interim.Status == InterimPublicationStatus.Prepared)
        {
            var committed = await _transitionWriter.CommitInterimPublicationAsync(
                recovered.DurableIdentity,
                state.Revision,
                recovered.PublicationId,
                recovered.Kind,
                recovered.TextDigest,
                Correlation(
                    recovered.DurableIdentity,
                    "recovered-interim-publication-commit",
                    recovered.PublicationId + ":" + recovered.TextDigest,
                    revision: 0),
                cancellationToken).ConfigureAwait(false);
            if (committed.Status == TurnTransitionWriteStatus.RevisionConflict
                || committed.State is null)
            {
                throw new InvalidOperationException(
                    "The recovered interim response changed before its display committed.");
            }

            committedState = committed.State;
        }
        else if (interim.Status == InterimPublicationStatus.Committed)
        {
            committedState = state;
        }
        else
        {
            throw new InvalidDataException("The recovered interim publication status is invalid.");
        }

        var control = recovered.Kind switch
        {
            InterimPublicationKind.AwaitingUser => TurnControlState.AwaitingUser,
            InterimPublicationKind.AwaitingEvent => TurnControlState.AwaitingEvent,
            InterimPublicationKind.SuspendedRuntime => TurnControlState.SuspendedRuntime,
            _ => throw new InvalidDataException("The recovered interim publication kind is invalid.")
        };
        if (committedState.Control == control)
        {
            return committedState;
        }

        var changed = await _transitionWriter.ChangeControlAsync(
            recovered.DurableIdentity,
            committedState.Revision,
            control,
            AliDurablePlanningTurn.InterimReasonCode(recovered.Reason),
            Correlation(
                recovered.DurableIdentity,
                "recovered-interim-publication-control",
                recovered.PublicationId + ":" + recovered.Reason + ":" + control,
                revision: 0),
            cancellationToken).ConfigureAwait(false);

        if (changed.Status == TurnTransitionWriteStatus.RevisionConflict
            || changed.State?.Control != control)
        {
            throw new InvalidOperationException(
                "The recovered interim response committed but its waiting state did not.");
        }

        return changed.State!;
    }

    private async Task<WorkGraphSnapshot> ReadSelectedWorkGraphAsync(
        TurnIdentity identity,
        TurnState state,
        CancellationToken cancellationToken)
    {
        if (state.WorkGraphReference is null)
        {
            if (state.WorkGraphRevision != 0)
            {
                throw new InvalidDataException(
                    "A recovered work graph has no exact authoritative reference.");
            }

            await _workGraphStore.PruneUnreachableAsync(
                identity,
                selectedReference: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return WorkGraphSnapshot.Empty;
        }

        var selected = await _workGraphStore.ReadAsync(
            identity,
            state.WorkGraphReference,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The exact work graph selected by recovered state is unavailable.");
        if (selected.Reference != state.WorkGraphReference
            || selected.Snapshot.Revision != state.WorkGraphRevision)
        {
            throw new InvalidDataException(
                "The recovered work graph crossed its exact state reference.");
        }

        await _workGraphStore.PruneUnreachableAsync(
            identity,
            state.WorkGraphReference,
            cancellationToken).ConfigureAwait(false);
        return selected.Snapshot;
    }

    private async Task<RecoveredEvidenceProjection> RehydrateEvidenceAsync(
        TurnIdentity identity,
        IReadOnlyList<CommittedEvidenceReference> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return RecoveredEvidenceProjection.Empty;
        }

        var projections = new List<AcceptedEvidenceProjection>(references.Count);
        var knownEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in references.Chunk(
                     EvidenceLedger.MaximumCommittedEvidenceLookupBatchSize))
        {
            var committed = await _evidenceLedger.ReadCommittedBatchAsync(
                identity,
                chunk,
                cancellationToken).ConfigureAwait(false);
            foreach (var reference in chunk)
            {
                if (!committed.TryGetValue(reference.EvidenceId, out var exact)
                    || exact.Cursor.Cursor != reference.Cursor
                    || !string.Equals(
                        exact.Cursor.Checksum,
                        reference.RecordDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A recovered evidence reference does not select an exact committed record.");
                }

                var cursor = exact.Cursor;
                var protectedContent = exact.ProtectedContent;
                var invocationStatus = cursor.Evidence.InvocationStatus switch
                {
                    InvocationStatus.Returned or InvocationStatus.Denied =>
                        PlanningToolInvocationStatus.Returned,
                    InvocationStatus.Threw => PlanningToolInvocationStatus.Threw,
                    InvocationStatus.Cancelled => PlanningToolInvocationStatus.Cancelled,
                    _ => throw new InvalidDataException(
                        "Recovered evidence has an invalid invocation status.")
                };
                var domainOutcome = cursor.Evidence.InvocationStatus == InvocationStatus.Denied
                    ? PlanningToolDomainOutcome.Denied
                    : cursor.Evidence.DomainOutcome switch
                    {
                        DomainOutcome.Succeeded => PlanningToolDomainOutcome.Succeeded,
                        DomainOutcome.Failed => PlanningToolDomainOutcome.Failed,
                        DomainOutcome.Unreported or DomainOutcome.PartiallySucceeded =>
                            PlanningToolDomainOutcome.Unreported,
                        _ => throw new InvalidDataException(
                            "Recovered evidence has an invalid domain outcome.")
                    };
                projections.Add(new AcceptedEvidenceProjection(
                    reference.EvidenceId,
                    protectedContent.Identity.CallId,
                    protectedContent.Identity.ToolName,
                    invocationStatus,
                    domainOutcome,
                    AliPlanningProjectionSafety.ProjectResult(protectedContent.Result),
                    protectedContent.Identity.WorkItemId));
                knownEvidenceIds.Add(reference.EvidenceId);
            }
        }

        if (projections.Count > AliDurablePlanningTurn.MaximumRecoveredEvidenceProjections)
        {
            projections.RemoveRange(
                0,
                projections.Count - AliDurablePlanningTurn.MaximumRecoveredEvidenceProjections);
        }

        return new RecoveredEvidenceProjection(
            projections,
            knownEvidenceIds);
    }

    private async Task<List<AcceptedConversationContextEntry>> RestoreConversationAsync(
        TurnIdentity identity,
        IReadOnlyList<SteeringAppendedTransition> steeringTransitions,
        CancellationToken cancellationToken)
    {
        ValidateSteeringProjectionBound(steeringTransitions);
        var acceptedConversation = await _transitionWriter.ReadAcceptedPriorConversationAsync(
            identity,
            cancellationToken).ConfigureAwait(false);
        var conversation = acceptedConversation
            .Select(item => new AcceptedConversationContextEntry(
                item.Sequence,
                item.Text,
                item.Role,
                isSteering: false))
            .ToList();
        var nextSequence = acceptedConversation.Count == 0
            ? 0
            : acceptedConversation.Max(item => item.Sequence) + 1;
        foreach (var transition in steeringTransitions)
        {
            var steering = await _transitionWriter.ReadSteeringAsync(
                identity,
                transition,
                cancellationToken).ConfigureAwait(false);
            conversation.Add(new AcceptedConversationContextEntry(
                nextSequence++,
                steering,
                AcceptedConversationRole.User,
                isSteering: true));
        }

        return conversation;
    }

    internal static void ValidateSteeringProjectionBound(
        IReadOnlyList<SteeringAppendedTransition> steeringTransitions)
    {
        ArgumentNullException.ThrowIfNull(steeringTransitions);
        if (steeringTransitions.Count
            > TurnTransitionJournal.MaximumResumeSteeringTransitions)
        {
            throw new InvalidDataException(
                "The accepted steering history exceeds its fixed entry limit.");
        }

        long aggregateBytes = 0;
        foreach (var transition in steeringTransitions)
        {
            ArgumentNullException.ThrowIfNull(transition);
            transition.ValidateShape();
            aggregateBytes = checked(aggregateBytes + transition.Steering.Utf8Length);
            if (aggregateBytes > TurnStateLimits.MaximumAcceptedSteeringUtf8Bytes)
            {
                throw new InvalidDataException(
                    "The accepted steering history exceeds its protected byte limit.");
            }
        }
    }

    private static ProgressHistory RestoreProgressHistory(
        IReadOnlyList<ProgressAttemptRecordedTransition> progressAttempts)
    {
        var progress = ProgressHistory.Empty;
        foreach (var transition in progressAttempts)
        {
            progress = progress.Restore(new ProgressAttemptFingerprint(
                transition.ActionFingerprint,
                transition.EffectFingerprint,
                transition.NoEffectFingerprint,
                transition.BeforeMaterialFingerprint,
                transition.AfterMaterialFingerprint,
                transition.MateriallyAdvanced));
        }

        return progress;
    }

    private sealed record RecoveredEvidenceProjection(
        IReadOnlyList<AcceptedEvidenceProjection> Projections,
        IReadOnlySet<string> KnownEvidenceIds)
    {
        internal static RecoveredEvidenceProjection Empty { get; } = new(
            [],
            new HashSet<string>(StringComparer.Ordinal));
    }
}

internal sealed partial class AliDurablePlanningTurn
{
    internal const int MaximumRecoveredEvidenceProjections = MaximumRetainedEvidenceProjections;

    private string? _recoveredOriginalRequest;

    internal TurnIdentity DurableIdentity => _identity;

    internal string ImmutableOriginalRequest =>
        _recoveredOriginalRequest ?? _turn.OriginalUserText;

    internal int RetainedProgressFingerprintCount => _progressHistory.Fingerprints.Length;

    internal void HydrateRecoveredState(
        string originalRequest,
        IEnumerable<AcceptedEvidenceProjection> evidence,
        IEnumerable<string> knownEvidenceIds,
        IEnumerable<TurnUserResolutionProjection> userResolutions,
        ProgressHistory progressHistory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalRequest);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(knownEvidenceIds);
        ArgumentNullException.ThrowIfNull(userResolutions);
        ArgumentNullException.ThrowIfNull(progressHistory);
        if (_evidence.Count != 0 || _knownEvidenceIds.Count != 0
            || _userResolutions.Count != 0
            || _progressHistory.Fingerprints.Length != 0)
        {
            throw new InvalidOperationException(
                "Recovered planning state can be hydrated only once into a fresh durable turn.");
        }

        var recoveredUserResolutions = userResolutions
            .Take(TurnTransitionJournal.MaximumResumeUserResolutions + 1)
            .Select(item => item with { })
            .ToArray();
        if (recoveredUserResolutions.Length
            > TurnTransitionJournal.MaximumResumeUserResolutions)
        {
            throw new InvalidDataException(
                "The recovered user-resolution projection exceeded its fixed bound.");
        }

        _recoveredOriginalRequest = originalRequest;
        _evidence.AddRange(evidence);
        foreach (var evidenceId in knownEvidenceIds)
        {
            _knownEvidenceIds.Add(evidenceId);
        }
        _userResolutions.AddRange(recoveredUserResolutions);
        _progressHistory = progressHistory;
    }
}
