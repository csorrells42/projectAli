using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration;

internal sealed record AliStructuredRecoveryResolution(
    TurnState State,
    string SourceCommandId,
    AgentRecoveryDecisionChoice Choice);

internal sealed partial class AliPlanningStateCoordinator
{
    internal async Task<AliStructuredRecoveryResolution> ResolveStructuredRecoveryAsync(
        CoordinatorTurnContext visibleTurn,
        AgentRecoveryDecision decision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(visibleTurn);
        ArgumentNullException.ThrowIfNull(decision);
        decision.Validate();
        RequireVisibleRecoveryIdentity(visibleTurn, decision.Prompt.DurableIdentity);

        var prompt = decision.Prompt;
        var sourceCommandId = Correlation(
            prompt.DurableIdentity,
            "structured-recovery-command",
            prompt.PromptPublicationId,
            prompt.SubjectPreparedRevision);
        var correlationKey = Correlation(
            prompt.DurableIdentity,
            "structured-recovery-resolution",
            prompt.PromptPublicationId,
            prompt.ExpectedStateRevision);

        TurnTransitionWriteResult write = decision.Choice switch
        {
            AgentRecoveryDecisionChoice.ConfirmApplied =>
                await _transitionWriter.ResolveUnknownActionAsync(
                    prompt.DurableIdentity,
                    prompt.ExpectedStateRevision,
                    sourceCommandId,
                    prompt.PromptPublicationId,
                    prompt.PromptTextDigest,
                    prompt.SubjectId,
                    prompt.SubjectPreparedRevision,
                    ActionUserResolution.ConfirmApplied,
                    correlationKey,
                    cancellationToken).ConfigureAwait(false),
            AgentRecoveryDecisionChoice.ConfirmAbsent =>
                await _transitionWriter.ResolveUnknownActionAsync(
                    prompt.DurableIdentity,
                    prompt.ExpectedStateRevision,
                    sourceCommandId,
                    prompt.PromptPublicationId,
                    prompt.PromptTextDigest,
                    prompt.SubjectId,
                    prompt.SubjectPreparedRevision,
                    ActionUserResolution.ConfirmAbsent,
                    correlationKey,
                    cancellationToken).ConfigureAwait(false),
            AgentRecoveryDecisionChoice.ConfirmDisplayed =>
                await _transitionWriter.ResolveUnknownFinalPublicationAsync(
                    prompt.DurableIdentity,
                    prompt.ExpectedStateRevision,
                    sourceCommandId,
                    prompt.PromptPublicationId,
                    prompt.PromptTextDigest,
                    prompt.SubjectId,
                    prompt.SubjectPreparedRevision,
                    FinalPublicationUserResolution.ConfirmDisplayed,
                    correlationKey,
                    cancellationToken).ConfigureAwait(false),
            AgentRecoveryDecisionChoice.ConfirmNotDisplayed =>
                await _transitionWriter.ResolveUnknownFinalPublicationAsync(
                    prompt.DurableIdentity,
                    prompt.ExpectedStateRevision,
                    sourceCommandId,
                    prompt.PromptPublicationId,
                    prompt.PromptTextDigest,
                    prompt.SubjectId,
                    prompt.SubjectPreparedRevision,
                    FinalPublicationUserResolution.ConfirmNotDisplayed,
                    correlationKey,
                    cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };

        if (write.Status is TurnTransitionWriteStatus.RevisionConflict
            || write.State is null)
        {
            throw new InvalidOperationException(
                "The structured recovery prompt changed before the decision committed.");
        }

        RequireResolvedState(write.State, decision.Choice);
        if (decision.Choice == AgentRecoveryDecisionChoice.ConfirmDisplayed)
        {
            await _pausedTurnCatalog.RemoveExactAsync(
                prompt.DurableIdentity,
                cancellationToken).ConfigureAwait(false);
        }

        return new AliStructuredRecoveryResolution(
            write.State,
            sourceCommandId,
            decision.Choice);
    }

    internal async Task CancelStructuredRecoveryAsync(
        CoordinatorTurnContext visibleTurn,
        AgentRecoveryPrompt prompt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(visibleTurn);
        ArgumentNullException.ThrowIfNull(prompt);
        prompt.Validate();
        RequireVisibleRecoveryIdentity(visibleTurn, prompt.DurableIdentity);

        var state = await _transitionWriter.ReadAsync(
                prompt.DurableIdentity,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The recovery turn no longer exists.");
        if (state.Control == TurnControlState.Cancelled)
        {
            await _pausedTurnCatalog.RemoveExactAsync(
                prompt.DurableIdentity,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (state.Control != TurnControlState.CancelRequested)
        {
            RequireExactCommittedPrompt(state, prompt);
            var requested = await _transitionWriter.ChangeControlAsync(
                prompt.DurableIdentity,
                prompt.ExpectedStateRevision,
                TurnControlState.CancelRequested,
                "structured-recovery-cancelled-by-user",
                Correlation(
                    prompt.DurableIdentity,
                    "structured-recovery-cancel-requested",
                    prompt.PromptPublicationId,
                    prompt.ExpectedStateRevision),
                cancellationToken).ConfigureAwait(false);
            if (requested.Status == TurnTransitionWriteStatus.RevisionConflict
                || requested.State?.Control != TurnControlState.CancelRequested)
            {
                throw new InvalidOperationException(
                    "The structured recovery prompt changed before cancellation committed.");
            }

            state = requested.State;
        }

        var recovered = await _recoveryService.RecoverAsync(
            prompt.DurableIdentity,
            state.Bindings,
            explicitlyRequested: true,
            cancellationToken).ConfigureAwait(false);
        if (recovered.State?.Control != TurnControlState.Cancelled)
        {
            throw new InvalidOperationException(
                "The recovered turn could not finish cancellation without guessing an outcome.");
        }

        await _pausedTurnCatalog.RemoveExactAsync(
            prompt.DurableIdentity,
            cancellationToken).ConfigureAwait(false);
    }

    private static void RequireVisibleRecoveryIdentity(
        CoordinatorTurnContext visibleTurn,
        TurnIdentity durableIdentity)
    {
        if (!string.Equals(
                visibleTurn.ConversationId,
                durableIdentity.ConversationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A recovery decision cannot cross its durable conversation boundary.");
        }

        var visibleUserId = visibleTurn.ObservationIdentity?.UserId ?? UnresolvedLocalUserId;
        if (!string.Equals(visibleUserId, durableIdentity.UserId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A recovery decision cannot cross its captured active-user boundary.");
        }
    }

    private static void RequireExactCommittedPrompt(
        TurnState state,
        AgentRecoveryPrompt prompt)
    {
        if (state.Revision != prompt.ExpectedStateRevision
            || state.Control != TurnControlState.AwaitingUser
            || state.InterimPublication is not { Status: InterimPublicationStatus.Committed } interim
            || !string.Equals(
                interim.PublicationId,
                prompt.PromptPublicationId,
                StringComparison.Ordinal)
            || !string.Equals(interim.TextDigest, prompt.PromptTextDigest, StringComparison.Ordinal)
            || !string.Equals(interim.SubjectId, prompt.SubjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovery command does not match the exact committed prompt.");
        }

        switch (prompt.Kind)
        {
            case AgentRecoveryPromptKind.ActionReconciliation:
            {
                var action = state.PendingActions.SingleOrDefault(item => string.Equals(
                    item.Intent.IdempotencyKey,
                    prompt.SubjectId,
                    StringComparison.Ordinal));
                if (interim.Reason != InterimPublicationReason.ActionReconciliationRequired
                    || action is null
                    || action.State != ActionIntentState.InDoubt
                    || action.PreparedAtRevision != prompt.SubjectPreparedRevision)
                {
                    throw new InvalidDataException(
                        "The recovery command does not match the exact in-doubt action.");
                }

                break;
            }
            case AgentRecoveryPromptKind.FinalPublicationReconciliation:
                if (interim.Reason
                        != InterimPublicationReason.FinalPublicationReconciliationRequired
                    || state.FinalPublication is not { Status: FinalPublicationStatus.InDoubt } publication
                    || !string.Equals(
                        publication.PublicationId,
                        prompt.SubjectId,
                        StringComparison.Ordinal)
                    || publication.PreparedAtRevision != prompt.SubjectPreparedRevision)
                {
                    throw new InvalidDataException(
                        "The recovery command does not match the exact in-doubt final publication.");
                }

                break;
            default:
                throw new InvalidDataException("The recovery prompt kind is invalid.");
        }
    }

    private static void RequireResolvedState(
        TurnState state,
        AgentRecoveryDecisionChoice choice)
    {
        var valid = choice switch
        {
            AgentRecoveryDecisionChoice.ConfirmApplied
                or AgentRecoveryDecisionChoice.ConfirmAbsent =>
                state.Control == TurnControlState.Running
                && state.PendingActions.Length == 0
                && state.PendingAcceptedCall is null
                && state.InterimPublication is null
                && state.FinalPublication is null,
            AgentRecoveryDecisionChoice.ConfirmDisplayed =>
                state.Control == TurnControlState.Completed
                && state.PendingActions.Length == 0
                && state.PendingAcceptedCall is null
                && state.InterimPublication is null
                && state.FinalPublication?.Status == FinalPublicationStatus.Committed,
            AgentRecoveryDecisionChoice.ConfirmNotDisplayed =>
                state.Control == TurnControlState.Running
                && state.PendingActions.Length == 0
                && state.PendingAcceptedCall is null
                && state.InterimPublication is null
                && state.FinalPublication?.Status == FinalPublicationStatus.ConfirmedAbsent,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException(
                "The structured recovery decision did not produce its required durable state.");
        }
    }
}
