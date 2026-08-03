using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Orchestration.State;

internal enum RecordedActionState
{
    Prepared,
    InDoubt,
    Committed,
    Applied,
    Absent,
    Unknown
}

internal sealed record RecordedCorrelation(long Cursor, string TransitionDigest);

internal sealed class TurnReplayIndex
{
    private readonly Dictionary<string, RecordedCorrelation> _correlations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string EvidenceId, long Cursor), string?> _evidence = [];
    private readonly Dictionary<string, RecordedActionState> _actions =
        new(StringComparer.Ordinal);

    private TurnReplayIndex(
        Dictionary<string, RecordedCorrelation> correlations,
        Dictionary<(string EvidenceId, long Cursor), string?> evidence,
        Dictionary<string, RecordedActionState> actions)
    {
        _correlations = correlations;
        _evidence = evidence;
        _actions = actions;
    }

    public TurnReplayIndex()
    {
    }

    public TurnReplayIndex Clone() =>
        new(
            new Dictionary<string, RecordedCorrelation>(_correlations, StringComparer.Ordinal),
            new Dictionary<(string EvidenceId, long Cursor), string?>(_evidence),
            new Dictionary<string, RecordedActionState>(_actions, StringComparer.Ordinal));

    public bool TryGetCorrelation(string key, out RecordedCorrelation? recorded) =>
        _correlations.TryGetValue(key, out recorded);

    public bool TryGetAction(string idempotencyKey, out RecordedActionState state) =>
        _actions.TryGetValue(idempotencyKey, out state);

    public bool HasEvidence(string evidenceId, long cursor, string? actionIdempotencyKey)
    {
        return _evidence.TryGetValue((evidenceId, cursor), out var recordedAction)
               && string.Equals(recordedAction, actionIdempotencyKey, StringComparison.Ordinal);
    }

    public void Record(TurnTransition transition, long cursor, string transitionDigest)
    {
        _correlations.Add(
            transition.CorrelationKey,
            new RecordedCorrelation(cursor, transitionDigest));
        switch (transition)
        {
            case EvidenceReferencedTransition evidence:
                _evidence.Add(
                    (evidence.Evidence.EvidenceId, evidence.Evidence.Cursor),
                    evidence.ActionIdempotencyKey);
                break;
            case ActionPreparedTransition prepared:
                _actions[prepared.Intent.IdempotencyKey] = RecordedActionState.Prepared;
                break;
            case ActionMarkedInDoubtTransition inDoubt:
                _actions[inDoubt.ActionIdempotencyKey] = RecordedActionState.InDoubt;
                break;
            case ActionCommittedTransition committed:
                _actions[committed.ActionIdempotencyKey] = RecordedActionState.Committed;
                break;
            case ActionReconciledTransition reconciled:
                _actions[reconciled.ActionIdempotencyKey] = reconciled.Disposition switch
                {
                    ActionReconciliationDisposition.Applied => RecordedActionState.Applied,
                    ActionReconciliationDisposition.Absent => RecordedActionState.Absent,
                    ActionReconciliationDisposition.Unknown => RecordedActionState.Unknown,
                    _ => throw new InvalidDataException("An action reconciliation disposition is invalid.")
                };
                break;
            case UnknownActionResolvedByUserTransition resolved:
                _actions[resolved.SubjectId] = resolved.Resolution switch
                {
                    ActionUserResolution.ConfirmApplied => RecordedActionState.Applied,
                    ActionUserResolution.ConfirmAbsent => RecordedActionState.Absent,
                    _ => throw new InvalidDataException("An action user resolution is invalid.")
                };
                break;
        }
    }
}

internal static class TurnStateReducer
{
    public static TurnState Reduce(
        TurnIdentity identity,
        TurnState? current,
        TurnTransition transition,
        long nextRevision,
        DateTimeOffset recordedAtUtc,
        TurnReplayIndex index)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(index);
        transition.ValidateShape();
        if (nextRevision <= 0 || recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("A transition revision or timestamp is invalid.");
        }

        if (transition is TurnStartedTransition started)
        {
            if (current is not null || nextRevision != 1)
            {
                throw new InvalidDataException("A turn may be started exactly once at revision one.");
            }

            return new TurnState(
                identity,
                started.OriginalRequest with { },
                started.AcceptedPriorConversation
                    .Select(CloneConversationReference)
                    .ToArray(),
                started.Bindings with { },
                nextRevision,
                nextRevision,
                SteeringCursor: 0,
                EvidenceCursor: 0,
                WorkGraphRevision: 0,
                TurnControlState.Running,
                [],
                PendingAcceptedCall: null,
                InterimPublication: null,
                FinalPublication: null,
                recordedAtUtc);
        }

        if (current is null)
        {
            throw new InvalidDataException("A turn must be started before another transition is committed.");
        }

        current.Validate(identity);
        if (nextRevision != current.Revision + 1)
        {
            throw new InvalidDataException("A turn transition must advance the revision by exactly one.");
        }

        if (current.Control is TurnControlState.Completed or TurnControlState.Cancelled)
        {
            throw new InvalidDataException("A terminal turn cannot accept another authoritative transition.");
        }

        var next = current with
        {
            Revision = nextRevision,
            JournalCursor = nextRevision,
            AcceptedPriorConversation = current.AcceptedPriorConversation
                .Select(CloneConversationReference)
                .ToArray(),
            PendingActions = current.PendingActions.Select(ClonePending).ToArray(),
            PendingAcceptedCall = current.PendingAcceptedCall is null
                ? null
                : CloneAcceptedCall(current.PendingAcceptedCall),
            Bindings = current.Bindings with { },
            InterimPublication = current.InterimPublication is null
                ? null
                : current.InterimPublication with
                {
                    TextPayload = current.InterimPublication.TextPayload with { }
                },
            FinalPublication = current.FinalPublication is null
                ? null
                : current.FinalPublication with
                {
                    AnswerPayload = current.FinalPublication.AnswerPayload with { }
                },
            UpdatedAtUtc = recordedAtUtc
        };

        switch (transition)
        {
            case SteeringAppendedTransition:
                if (IsStructuredReconciliation(current.InterimPublication))
                {
                    throw new InvalidDataException(
                        "Ordinary steering cannot resolve a structured reconciliation decision.");
                }
                next = next with { SteeringCursor = nextRevision };
                break;
            case PlanningDecisionAcceptedTransition decision:
                if (current.PendingAcceptedCall is not null)
                {
                    throw new InvalidDataException(
                        "A new planning decision cannot be accepted while a prior call lacks a terminal.");
                }
                if (current.PendingActions.Length != 0)
                {
                    throw new InvalidDataException(
                        "A new planning decision cannot be accepted while an action remains unresolved.");
                }
                if (current.FinalPublication is not null || current.InterimPublication is not null)
                {
                    throw new InvalidDataException(
                        "A planning decision cannot be accepted while publication is in progress.");
                }
                if (decision.WorkGraph is null
                    && decision.WorkGraphRevision != current.WorkGraphRevision)
                {
                    throw new InvalidDataException(
                        "An accepted planning decision must bind the currently committed work-graph revision.");
                }
                if (decision.WorkGraph is not null)
                {
                    if (decision.WorkGraph.Revision != current.WorkGraphRevision + 1)
                    {
                        throw new InvalidDataException(
                            "An accepted planning decision's work graph must advance by exactly one revision.");
                    }

                    next = next with
                    {
                        WorkGraphRevision = decision.WorkGraph.Revision,
                        WorkGraphReference = decision.WorkGraph with { }
                    };
                }
                if (decision.ActionKind == PlanningAcceptedActionKind.CallTool)
                {
                    next = next with
                    {
                        PendingAcceptedCall = CloneAcceptedCall(
                            decision.AcceptedCall
                            ?? throw new InvalidDataException(
                                "A tool-call decision has no recovery reference."))
                    };
                }
                break;
            case AcceptedCallInterruptedTransition interrupted:
                next = InterruptAcceptedCall(current, next, interrupted);
                break;
            case ControlChangedTransition control:
                ValidateControlTransition(current, control.Control);
                RequireInterimPublicationForControl(current, control.Control);
                if (control.Control == TurnControlState.Completed
                    && current.FinalPublication?.Status != FinalPublicationStatus.Committed)
                {
                    throw new InvalidDataException(
                        "A turn cannot complete before its exact final publication is committed.");
                }
                if (control.Control == TurnControlState.Completed
                    && (current.PendingActions.Length != 0
                        || current.PendingAcceptedCall is not null
                        || current.InterimPublication is not null))
                {
                    throw new InvalidDataException(
                        "A turn cannot complete while an action remains unresolved.");
                }
                if (control.Control == TurnControlState.Cancelled
                    && (current.PendingActions.Length != 0
                        || current.PendingAcceptedCall is not null
                        || current.InterimPublication is not null
                        || current.FinalPublication is { Status: not FinalPublicationStatus.Committed }))
                {
                    throw new InvalidDataException(
                        "A turn cannot finish cancellation while an action or publication remains unresolved.");
                }
                next = next with { Control = control.Control };
                break;
            case RuntimeBindingsRevalidatedTransition revalidated:
                next = RevalidateRuntimeBindings(current, next, revalidated);
                break;
            case EvidenceReferencedTransition evidence:
                if (evidence.Evidence.Cursor != current.EvidenceCursor + 1)
                {
                    throw new InvalidDataException(
                        "An evidence reference must advance the committed evidence cursor by exactly one.");
                }
                if (evidence.ActionIdempotencyKey is not null
                    && !current.TryGetPendingAction(evidence.ActionIdempotencyKey, out _))
                {
                    throw new InvalidDataException(
                        "Action evidence must reference an action that is still pending.");
                }
                if (evidence.CallId is not null)
                {
                    var acceptedCall = RequirePendingAcceptedCall(current, evidence.CallId);
                    if (acceptedCall.ExecutionClass == AcceptedCallExecutionClass.PreparedEffectRequired)
                    {
                        if (evidence.ActionIdempotencyKey is null
                            || !current.PendingActions.Any(item => string.Equals(
                                item.Intent.AcceptedCallId,
                                evidence.CallId,
                                StringComparison.Ordinal)))
                        {
                            throw new InvalidDataException(
                                "Effect-call evidence requires its matching prepared action intent.");
                        }
                    }
                    else if (evidence.ActionIdempotencyKey is not null)
                    {
                        throw new InvalidDataException(
                            "A non-mutating accepted call cannot claim a prepared effect action.");
                    }
                }

                next = next with
                {
                    EvidenceCursor = evidence.Evidence.Cursor,
                    PendingAcceptedCall = evidence.CallId is null
                        ? next.PendingAcceptedCall
                        : null
                };
                break;
            case ProgressAttemptRecordedTransition:
                // Progress history is append-only journal evidence. The compact turn
                // snapshot advances its revision/cursor but does not duplicate history.
                break;
            case WorkGraphReferencedTransition workGraph:
                if (workGraph.WorkGraph.Revision != current.WorkGraphRevision + 1)
                {
                    throw new InvalidDataException(
                        "A work-graph reference must advance its committed revision by exactly one.");
                }
                next = next with
                {
                    WorkGraphRevision = workGraph.WorkGraph.Revision,
                    WorkGraphReference = workGraph.WorkGraph with { }
                };
                break;
            case ActionPreparedTransition prepared:
                next = PrepareAction(current, next, prepared, nextRevision, index);
                break;
            case ActionMarkedInDoubtTransition inDoubt:
                next = MarkActionInDoubt(current, next, inDoubt, nextRevision);
                break;
            case ActionCommittedTransition committed:
                next = CommitAction(current, next, committed, index);
                break;
            case ActionReconciledTransition reconciled:
                next = ReconcileAction(current, next, reconciled, index);
                break;
            case UnknownActionResolvedByUserTransition resolved:
                next = ResolveUnknownActionByUser(current, next, resolved);
                break;
            case UnknownFinalPublicationResolvedByUserTransition resolved:
                next = ResolveUnknownFinalPublicationByUser(current, next, resolved, nextRevision);
                break;
            case InDoubtActionCancelledTransition cancelled:
                next = CancelInDoubtAction(current, next, cancelled);
                break;
            case InDoubtFinalPublicationCancelledTransition cancelled:
                next = CancelInDoubtFinalPublication(current, next, cancelled);
                break;
            case InterimPublicationPreparedTransition publication:
                next = PrepareInterimPublication(current, next, publication, nextRevision);
                break;
            case InterimPublicationCommittedTransition publication:
                next = CommitInterimPublication(current, next, publication, nextRevision);
                break;
            case InterimPublicationClearedTransition publication:
                next = ClearInterimPublication(current, next, publication);
                break;
            case FinalPublicationPreparedTransition publication:
                next = PreparePublication(current, next, publication, nextRevision);
                break;
            case FinalPublicationMarkedInDoubtTransition publication:
                next = MarkPublicationInDoubt(current, next, publication, nextRevision);
                break;
            case FinalPublicationConfirmedAbsentTransition publication:
                next = ConfirmPublicationAbsent(current, next, publication, nextRevision);
                break;
            case FinalPublicationRetargetedTransition publication:
                next = RetargetPublication(current, next, publication, nextRevision);
                break;
            case FinalPublicationAbandonedTransition publication:
                next = AbandonPublication(current, next, publication);
                break;
            case FinalPublicationCommittedTransition publication:
                next = CommitPublication(current, next, publication, nextRevision);
                break;
            default:
                throw new InvalidDataException(
                    $"Transition type '{transition.GetType().Name}' is not an accepted turn transition.");
        }

        next.Validate(identity);
        return next;
    }

    private static TurnState RevalidateRuntimeBindings(
        TurnState current,
        TurnState next,
        RuntimeBindingsRevalidatedTransition revalidated)
    {
        var interim = current.InterimPublication
            ?? throw new InvalidDataException(
                "Runtime bindings cannot be revalidated without an exact admission pause.");
        if (current.Control is not (TurnControlState.SuspendedRuntime or TurnControlState.Running)
            || interim.Status != InterimPublicationStatus.Committed
            || interim.Kind != InterimPublicationKind.SuspendedRuntime
            || !interim.Reason.IsModelConfigurationRecoverablePause()
            || !string.Equals(
                interim.PublicationId,
                revalidated.PublicationId,
                StringComparison.Ordinal)
            || !string.Equals(interim.TextDigest, revalidated.TextDigest, StringComparison.Ordinal)
            || current.PendingActions.Length != 0
            || current.PendingAcceptedCall is not null
            || current.FinalPublication is not null)
        {
            throw new InvalidDataException(
                "Only an exact committed model-configuration-recoverable pause may adopt new model settings.");
        }

        var changed = current.Bindings.FindChangedBindings(revalidated.Bindings);
        if (changed.Count == 0
            || changed.Any(static binding =>
                binding is not "model" and not "generation-settings"))
        {
            throw new InvalidDataException(
                "Model-configuration recovery may adopt only changed model or generation-settings bindings.");
        }

        return next with { Bindings = revalidated.Bindings with { } };
    }

    private static TurnState PrepareAction(
        TurnState current,
        TurnState next,
        ActionPreparedTransition prepared,
        long revision,
        TurnReplayIndex index)
    {
        if (current.Control != TurnControlState.Running)
        {
            throw new InvalidDataException(
                "A side effect can be prepared only while the turn is running.");
        }
        if (current.FinalPublication is not null)
        {
            throw new InvalidDataException(
                "A side effect cannot be prepared after final publication has begun.");
        }

        if (current.TryGetPendingAction(prepared.Intent.IdempotencyKey, out _))
        {
            throw new InvalidDataException("That action identity is already pending reconciliation.");
        }
        if (current.PendingActions.Length != 0)
        {
            throw new InvalidDataException(
                "A turn can have only one unresolved prepared effect at a time.");
        }

        if (index.TryGetAction(prepared.Intent.IdempotencyKey, out var prior)
            && prior != RecordedActionState.Absent)
        {
            throw new InvalidDataException(
                "That action identity is already authoritative and cannot be prepared again.");
        }

        if (current.PendingAcceptedCall is { } acceptedCall)
        {
            if (acceptedCall.ExecutionClass != AcceptedCallExecutionClass.PreparedEffectRequired)
            {
                throw new InvalidDataException(
                    "Only an effect-bearing accepted call may prepare an action intent.");
            }

            if (!string.Equals(
                    prepared.Intent.AcceptedCallId,
                    acceptedCall.CallId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.Intent.WorkItemId,
                    acceptedCall.WorkItemId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.Intent.ToolName,
                    acceptedCall.ToolName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.Intent.CapabilityId,
                    acceptedCall.CapabilityId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.Intent.RegistryRevisionDigest,
                    acceptedCall.RegistryRevisionDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.Intent.CanonicalArgumentsDigest,
                    acceptedCall.ArgumentsDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.Intent.ReconcilerId,
                    acceptedCall.ReconcilerId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A prepared action intent must match the exact accepted call identity.");
            }
        }
        else if (prepared.Intent.AcceptedCallId is not null)
        {
            throw new InvalidDataException(
                "An action intent cannot bind a call that is not pending in authoritative state.");
        }

        var pending = current.PendingActions
            .Select(ClonePending)
            .Append(new PendingActionState(
                prepared.Intent with { },
                ActionIntentState.Prepared,
                revision,
                revision))
            .OrderBy(item => item.Intent.IdempotencyKey, StringComparer.Ordinal)
            .ToArray();
        return next with { PendingActions = pending };
    }

    private static TurnState MarkActionInDoubt(
        TurnState current,
        TurnState next,
        ActionMarkedInDoubtTransition inDoubt,
        long revision)
    {
        var pending = RequirePending(current, inDoubt.ActionIdempotencyKey);
        if (pending.State != ActionIntentState.Prepared)
        {
            throw new InvalidDataException("Only a prepared action may become in-doubt.");
        }

        return next with
        {
            PendingActions = ReplacePending(
                current,
                pending with
                {
                    State = ActionIntentState.InDoubt,
                    LastTransitionRevision = revision
                })
        };
    }

    private static TurnState CommitAction(
        TurnState current,
        TurnState next,
        ActionCommittedTransition committed,
        TurnReplayIndex index)
    {
        _ = RequirePending(current, committed.ActionIdempotencyKey);
        if (!index.HasEvidence(
                committed.EvidenceId,
                committed.EvidenceCursor,
                committed.ActionIdempotencyKey))
        {
            throw new InvalidDataException(
                "An action can commit only after its matching evidence reference is committed.");
        }

        return next with
        {
            PendingActions = RemovePending(current, committed.ActionIdempotencyKey)
        };
    }

    private static TurnState ReconcileAction(
        TurnState current,
        TurnState next,
        ActionReconciledTransition reconciled,
        TurnReplayIndex index)
    {
        var pending = RequirePending(current, reconciled.ActionIdempotencyKey);
        if (pending.State != ActionIntentState.InDoubt)
        {
            throw new InvalidDataException("Only an in-doubt action may be reconciled.");
        }

        switch (reconciled.Disposition)
        {
            case ActionReconciliationDisposition.Applied:
                if (!index.HasEvidence(
                        reconciled.EvidenceId!,
                        reconciled.EvidenceCursor!.Value,
                        reconciled.ActionIdempotencyKey))
                {
                    throw new InvalidDataException(
                        "An applied reconciliation requires matching committed evidence.");
                }
                return CompleteObservedActionReconciliation(
                    current,
                    next,
                    reconciled.ActionIdempotencyKey);
            case ActionReconciliationDisposition.Absent:
                return CompleteObservedActionReconciliation(
                    current,
                    next,
                    reconciled.ActionIdempotencyKey);
            case ActionReconciliationDisposition.Unknown:
                return next with
                {
                    PendingActions = ReplacePending(
                        current,
                        pending with { LastTransitionRevision = next.Revision })
                };
            default:
                throw new InvalidDataException("The action reconciliation disposition is invalid.");
        }
    }

    private static TurnState CompleteObservedActionReconciliation(
        TurnState current,
        TurnState next,
        string actionIdempotencyKey)
    {
        var interim = current.InterimPublication;
        if (interim is not null
            && (interim.Reason != InterimPublicationReason.ActionReconciliationRequired
                || !string.Equals(
                    interim.SubjectId,
                    actionIdempotencyKey,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The observed action reconciliation does not match the authoritative prompt.");
        }

        return next with
        {
            PendingActions = RemovePending(current, actionIdempotencyKey),
            InterimPublication = interim is null ? next.InterimPublication : null,
            Control = interim is not null && current.Control == TurnControlState.AwaitingUser
                ? TurnControlState.Running
                : next.Control
        };
    }

    private static TurnState ResolveUnknownActionByUser(
        TurnState current,
        TurnState next,
        UnknownActionResolvedByUserTransition resolved)
    {
        var interim = RequireStructuredResolutionInterim(
            current,
            resolved.PromptPublicationId,
            resolved.PromptTextDigest,
            resolved.Reason,
            resolved.SubjectId);
        var pending = RequirePending(current, resolved.SubjectId);
        if (current.Control != TurnControlState.AwaitingUser
            || interim.Status != InterimPublicationStatus.Committed
            || pending.State != ActionIntentState.InDoubt
            || pending.PreparedAtRevision != resolved.SubjectPreparedRevision)
        {
            throw new InvalidDataException(
                "The structured action resolution does not match the exact in-doubt action prompt.");
        }

        if (current.PendingAcceptedCall is { } accepted
            && !string.Equals(
                pending.Intent.AcceptedCallId,
                accepted.CallId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The structured action resolution does not match the pending accepted call.");
        }

        return next with
        {
            PendingActions = [],
            PendingAcceptedCall = null,
            InterimPublication = null,
            Control = TurnControlState.Running
        };
    }

    private static TurnState ResolveUnknownFinalPublicationByUser(
        TurnState current,
        TurnState next,
        UnknownFinalPublicationResolvedByUserTransition resolved,
        long revision)
    {
        var interim = RequireStructuredResolutionInterim(
            current,
            resolved.PromptPublicationId,
            resolved.PromptTextDigest,
            resolved.Reason,
            resolved.SubjectId);
        var publication = current.FinalPublication
            ?? throw new InvalidDataException("No final publication awaits user reconciliation.");
        if (current.Control != TurnControlState.AwaitingUser
            || interim.Status != InterimPublicationStatus.Committed
            || publication.Status != FinalPublicationStatus.InDoubt
            || publication.PreparedAtRevision != resolved.SubjectPreparedRevision
            || !string.Equals(
                publication.PublicationId,
                resolved.SubjectId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The structured publication resolution does not match the exact in-doubt answer prompt.");
        }

        return resolved.Resolution switch
        {
            FinalPublicationUserResolution.ConfirmDisplayed => next with
            {
                InterimPublication = null,
                FinalPublication = publication with
                {
                    Status = FinalPublicationStatus.Committed,
                    LastTransitionRevision = revision
                },
                Control = TurnControlState.Completed
            },
            FinalPublicationUserResolution.ConfirmNotDisplayed => next with
            {
                InterimPublication = null,
                FinalPublication = publication with
                {
                    Status = FinalPublicationStatus.ConfirmedAbsent,
                    LastTransitionRevision = revision
                },
                Control = TurnControlState.Running
            },
            _ => throw new InvalidDataException(
                "The structured publication resolution is invalid.")
        };
    }

    private static TurnState CancelInDoubtAction(
        TurnState current,
        TurnState next,
        InDoubtActionCancelledTransition cancelled)
    {
        var pending = RequirePending(current, cancelled.ActionIdempotencyKey);
        if (current.Control != TurnControlState.CancelRequested
            || current.PendingActions.Length != 1
            || pending.State != ActionIntentState.InDoubt
            || pending.PreparedAtRevision != cancelled.SubjectPreparedRevision)
        {
            throw new InvalidDataException(
                "The cancelled action does not match the exact in-doubt action state.");
        }
        if (current.InterimPublication is { } interim
            && (interim.Reason != InterimPublicationReason.ActionReconciliationRequired
                || !string.Equals(
                    interim.SubjectId,
                    pending.Intent.IdempotencyKey,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The cancelled action does not match the authoritative reconciliation prompt.");
        }
        if (current.PendingAcceptedCall is { } accepted
            && !string.Equals(
                pending.Intent.AcceptedCallId,
                accepted.CallId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The cancelled action does not match the pending accepted call.");
        }

        return next with
        {
            PendingActions = [],
            PendingAcceptedCall = null,
            InterimPublication = null,
            Control = TurnControlState.Cancelled
        };
    }

    private static TurnState CancelInDoubtFinalPublication(
        TurnState current,
        TurnState next,
        InDoubtFinalPublicationCancelledTransition cancelled)
    {
        var publication = RequireMatchingPublication(
            current,
            cancelled.PublicationId,
            cancelled.AssistantMessageId,
            cancelled.AnswerDigest);
        if (current.Control != TurnControlState.CancelRequested
            || publication.Status != FinalPublicationStatus.InDoubt
            || publication.PreparedAtRevision != cancelled.SubjectPreparedRevision)
        {
            throw new InvalidDataException(
                "The cancelled publication does not match the exact in-doubt answer state.");
        }
        if (current.InterimPublication is { } interim
            && (interim.Reason
                    != InterimPublicationReason.FinalPublicationReconciliationRequired
                || !string.Equals(
                    interim.SubjectId,
                    publication.PublicationId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The cancelled publication does not match the authoritative reconciliation prompt.");
        }

        return next with
        {
            InterimPublication = null,
            FinalPublication = null,
            Control = TurnControlState.Cancelled
        };
    }

    private static TurnState PreparePublication(
        TurnState current,
        TurnState next,
        FinalPublicationPreparedTransition publication,
        long revision)
    {
        if (current.FinalPublication is not null)
        {
            throw new InvalidDataException("A final publication is already authoritative for this turn.");
        }

        if (current.PendingActions.Length != 0)
        {
            throw new InvalidDataException(
                "A final publication cannot be prepared while an action remains unresolved.");
        }
        if (current.PendingAcceptedCall is not null)
        {
            throw new InvalidDataException(
                "A final publication cannot be prepared while an accepted call lacks a terminal.");
        }
        if (current.InterimPublication is not null)
        {
            throw new InvalidDataException(
                "A final publication cannot be prepared before the interim publication is cleared.");
        }

        return next with
        {
            FinalPublication = new FinalPublicationState(
                publication.PublicationId,
                publication.AssistantMessageId,
                publication.AnswerDigest,
                publication.AnswerPayload with { },
                FinalPublicationStatus.Prepared,
                revision,
                revision)
        };
    }

    private static TurnState MarkPublicationInDoubt(
        TurnState current,
        TurnState next,
        FinalPublicationMarkedInDoubtTransition publication,
        long revision)
    {
        var existing = RequireMatchingPublication(current, publication.PublicationId,
            publication.AssistantMessageId, publication.AnswerDigest);
        if (existing.Status != FinalPublicationStatus.Prepared)
        {
            throw new InvalidDataException("Only a prepared publication may become in-doubt.");
        }

        return next with
        {
            FinalPublication = existing with
            {
                Status = FinalPublicationStatus.InDoubt,
                LastTransitionRevision = revision
            }
        };
    }

    private static TurnState ConfirmPublicationAbsent(
        TurnState current,
        TurnState next,
        FinalPublicationConfirmedAbsentTransition publication,
        long revision)
    {
        var existing = RequireMatchingPublication(
            current,
            publication.PublicationId,
            publication.AssistantMessageId,
            publication.AnswerDigest);
        if (existing.Status != FinalPublicationStatus.InDoubt)
        {
            throw new InvalidDataException(
                "Only an in-doubt publication may be confirmed absent.");
        }
        var interim = RequireMatchingFinalReconciliationInterimOrNull(current, existing);

        return next with
        {
            FinalPublication = existing with
            {
                Status = FinalPublicationStatus.ConfirmedAbsent,
                LastTransitionRevision = revision
            },
            InterimPublication = interim is null ? next.InterimPublication : null,
            Control = interim is not null && current.Control == TurnControlState.AwaitingUser
                ? TurnControlState.Running
                : next.Control
        };
    }

    private static TurnState RetargetPublication(
        TurnState current,
        TurnState next,
        FinalPublicationRetargetedTransition publication,
        long revision)
    {
        var existing = RequireMatchingPublication(
            current,
            publication.PreviousPublicationId,
            publication.PreviousAssistantMessageId,
            publication.AnswerDigest);
        if (current.Control != TurnControlState.Running
            || current.InterimPublication is not null
            || current.PendingActions.Length != 0
            || current.PendingAcceptedCall is not null
            || existing.Status != FinalPublicationStatus.ConfirmedAbsent)
        {
            throw new InvalidDataException(
                "Only a confirmed-absent final publication may be retargeted.");
        }

        return next with
        {
            FinalPublication = new FinalPublicationState(
                publication.NewPublicationId,
                publication.NewAssistantMessageId,
                publication.AnswerDigest,
                publication.AnswerPayload with { },
                FinalPublicationStatus.Prepared,
                revision,
                revision)
        };
    }

    private static TurnState CommitPublication(
        TurnState current,
        TurnState next,
        FinalPublicationCommittedTransition publication,
        long revision)
    {
        var existing = RequireMatchingPublication(current, publication.PublicationId,
            publication.AssistantMessageId, publication.AnswerDigest);
        if (current.PendingActions.Length != 0)
        {
            throw new InvalidDataException(
                "A final publication cannot commit while an action remains unresolved.");
        }
        if (existing.Status == FinalPublicationStatus.Committed)
        {
            throw new InvalidDataException("The final publication receipt is already committed.");
        }
        var interim = RequireMatchingFinalReconciliationInterimOrNull(current, existing);

        return next with
        {
            FinalPublication = existing with
            {
                Status = FinalPublicationStatus.Committed,
                LastTransitionRevision = revision
            },
            InterimPublication = interim is null ? next.InterimPublication : null,
            Control = interim is not null && current.Control == TurnControlState.AwaitingUser
                ? TurnControlState.Running
                : next.Control
        };
    }

    private static TurnState AbandonPublication(
        TurnState current,
        TurnState next,
        FinalPublicationAbandonedTransition publication)
    {
        var existing = RequireMatchingPublication(current, publication.PublicationId,
            publication.AssistantMessageId, publication.AnswerDigest);
        if (existing.Status == FinalPublicationStatus.Committed)
        {
            throw new InvalidDataException(
                "A committed final publication cannot be abandoned.");
        }
        var interim = RequireMatchingFinalReconciliationInterimOrNull(current, existing);

        return next with
        {
            FinalPublication = null,
            InterimPublication = interim is null ? next.InterimPublication : null,
            Control = interim is not null && current.Control == TurnControlState.AwaitingUser
                ? TurnControlState.Running
                : next.Control
        };
    }

    private static InterimPublicationState? RequireMatchingFinalReconciliationInterimOrNull(
        TurnState state,
        FinalPublicationState publication)
    {
        var interim = state.InterimPublication;
        if (interim is not null
            && (interim.Reason
                    != InterimPublicationReason.FinalPublicationReconciliationRequired
                || !string.Equals(
                    interim.SubjectId,
                    publication.PublicationId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The final-publication transition does not match the authoritative reconciliation prompt.");
        }

        return interim;
    }

    private static TurnState InterruptAcceptedCall(
        TurnState current,
        TurnState next,
        AcceptedCallInterruptedTransition interrupted)
    {
        var acceptedCall = RequirePendingAcceptedCall(current, interrupted.CallId);
        if (acceptedCall.ExecutionClass == AcceptedCallExecutionClass.PreparedEffectRequired
            && current.PendingActions.Any(item => string.Equals(
                item.Intent.AcceptedCallId,
                acceptedCall.CallId,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "An effect-bearing accepted call with a prepared action must be reconciled before terminalization.");
        }

        return next with { PendingAcceptedCall = null };
    }

    private static TurnState PrepareInterimPublication(
        TurnState current,
        TurnState next,
        InterimPublicationPreparedTransition publication,
        long revision)
    {
        if (current.Control is not TurnControlState.Running and not TurnControlState.Paused)
        {
            throw new InvalidDataException(
                "An interim publication can be prepared only from an active or paused turn.");
        }
        if (current.InterimPublication is not null)
        {
            throw new InvalidDataException("An interim publication is already authoritative for this turn.");
        }
        if (current.FinalPublication is not null
            && (current.FinalPublication.Status != FinalPublicationStatus.InDoubt
                || publication.Kind != InterimPublicationKind.AwaitingUser
                || publication.Reason
                    != InterimPublicationReason.FinalPublicationReconciliationRequired))
        {
            throw new InvalidDataException(
                "An interim publication can accompany only an in-doubt final publication requiring user reconciliation.");
        }

        return next with
        {
            InterimPublication = new InterimPublicationState(
                publication.PublicationId,
                publication.Kind,
                publication.Reason,
                publication.SubjectId,
                publication.TextDigest,
                publication.TextPayload with { },
                InterimPublicationStatus.Prepared,
                revision,
                revision)
        };
    }

    private static TurnState CommitInterimPublication(
        TurnState current,
        TurnState next,
        InterimPublicationCommittedTransition publication,
        long revision)
    {
        var existing = RequireMatchingInterimPublication(
            current,
            publication.PublicationId,
            publication.Kind,
            publication.TextDigest);
        if (existing.Status == InterimPublicationStatus.Committed)
        {
            throw new InvalidDataException("The interim publication is already committed.");
        }

        return next with
        {
            InterimPublication = existing with
            {
                Status = InterimPublicationStatus.Committed,
                LastTransitionRevision = revision
            }
        };
    }

    private static TurnState ClearInterimPublication(
        TurnState current,
        TurnState next,
        InterimPublicationClearedTransition publication)
    {
        var existing = RequireMatchingInterimPublication(
            current,
            publication.PublicationId,
            publication.Kind,
            publication.TextDigest);
        if (IsStructuredReconciliation(existing)
            && current.Control != TurnControlState.CancelRequested)
        {
            throw new InvalidDataException(
                "A reconciliation prompt can be cleared only by its typed resolution or cancellation.");
        }
        if (existing.Status != InterimPublicationStatus.Committed
            && current.Control != TurnControlState.CancelRequested)
        {
            throw new InvalidDataException(
                "An interim publication cannot be cleared before its display receipt commits.");
        }
        if (current.Control is TurnControlState.AwaitingUser
            or TurnControlState.AwaitingEvent
            or TurnControlState.SuspendedRuntime)
        {
            throw new InvalidDataException(
                "Resume or cancellation must leave the waiting control state before clearing its interim publication.");
        }

        return next with { InterimPublication = null };
    }

    private static PendingActionState RequirePending(TurnState state, string idempotencyKey)
    {
        if (!state.TryGetPendingAction(idempotencyKey, out var pending))
        {
            throw new InvalidDataException("The action has no prepared intent in authoritative state.");
        }

        return pending!;
    }

    private static AcceptedCallRecoveryReference RequirePendingAcceptedCall(
        TurnState state,
        string callId)
    {
        var accepted = state.PendingAcceptedCall
            ?? throw new InvalidDataException("No accepted call is awaiting a terminal.");
        if (!string.Equals(accepted.CallId, callId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The accepted-call terminal does not match the pending call identity.");
        }

        return accepted;
    }

    private static InterimPublicationState RequireStructuredResolutionInterim(
        TurnState state,
        string publicationId,
        string textDigest,
        InterimPublicationReason reason,
        string subjectId)
    {
        var interim = state.InterimPublication
            ?? throw new InvalidDataException("No structured reconciliation prompt is authoritative.");
        if (!IsStructuredReconciliation(interim)
            || !string.Equals(interim.PublicationId, publicationId, StringComparison.Ordinal)
            || !string.Equals(interim.TextDigest, textDigest, StringComparison.Ordinal)
            || interim.Reason != reason
            || !string.Equals(interim.SubjectId, subjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The structured resolution does not match the authoritative reconciliation prompt.");
        }

        return interim;
    }

    private static bool IsStructuredReconciliation(InterimPublicationState? interim) =>
        interim?.Reason is InterimPublicationReason.ActionReconciliationRequired
            or InterimPublicationReason.FinalPublicationReconciliationRequired;

    private static InterimPublicationState RequireMatchingInterimPublication(
        TurnState state,
        string publicationId,
        InterimPublicationKind kind,
        string textDigest)
    {
        var interim = state.InterimPublication
            ?? throw new InvalidDataException("No interim publication is prepared for this turn.");
        if (!string.Equals(interim.PublicationId, publicationId, StringComparison.Ordinal)
            || interim.Kind != kind
            || !string.Equals(interim.TextDigest, textDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "An interim publication identity cannot be rebound to different visible content.");
        }

        return interim;
    }

    private static FinalPublicationState RequireMatchingPublication(
        TurnState state,
        string publicationId,
        string assistantMessageId,
        string answerDigest)
    {
        var publication = state.FinalPublication
            ?? throw new InvalidDataException("No final publication was prepared for this turn.");
        if (!string.Equals(publication.PublicationId, publicationId, StringComparison.Ordinal)
            || !string.Equals(publication.AssistantMessageId, assistantMessageId, StringComparison.Ordinal)
            || !string.Equals(publication.AnswerDigest, answerDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A final publication identity cannot be rebound to different visible content.");
        }

        return publication;
    }

    private static PendingActionState[] ReplacePending(
        TurnState state,
        PendingActionState replacement) =>
        state.PendingActions
            .Select(item => string.Equals(
                    item.Intent.IdempotencyKey,
                    replacement.Intent.IdempotencyKey,
                    StringComparison.Ordinal)
                ? replacement
                : ClonePending(item))
            .OrderBy(item => item.Intent.IdempotencyKey, StringComparer.Ordinal)
            .ToArray();

    private static PendingActionState[] RemovePending(TurnState state, string idempotencyKey) =>
        state.PendingActions
            .Where(item => !string.Equals(
                item.Intent.IdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal))
            .Select(ClonePending)
            .ToArray();

    private static PendingActionState ClonePending(PendingActionState value) =>
        value with { Intent = value.Intent with { } };

    private static AcceptedConversationEntryReference CloneConversationReference(
        AcceptedConversationEntryReference value) =>
        value with { Payload = value.Payload with { } };

    private static AcceptedCallRecoveryReference CloneAcceptedCall(
        AcceptedCallRecoveryReference value) =>
        value with { Payload = value.Payload with { } };

    private static void RequireInterimPublicationForControl(
        TurnState current,
        TurnControlState target)
    {
        var requiredKind = target switch
        {
            TurnControlState.AwaitingUser => InterimPublicationKind.AwaitingUser,
            TurnControlState.AwaitingEvent => InterimPublicationKind.AwaitingEvent,
            TurnControlState.SuspendedRuntime => InterimPublicationKind.SuspendedRuntime,
            _ => (InterimPublicationKind?)null
        };
        if (requiredKind is not null
            && (current.InterimPublication?.Kind != requiredKind.Value
                || current.InterimPublication.Status != InterimPublicationStatus.Committed))
        {
            throw new InvalidDataException(
                "A waiting control state requires its exact committed interim publication receipt.");
        }
    }

    private static void ValidateControlTransition(TurnState current, TurnControlState target)
    {
        if (current.Control == target)
        {
            throw new InvalidDataException("A control transition must change the durable control state.");
        }

        if (target == TurnControlState.Running
            && IsStructuredReconciliation(current.InterimPublication))
        {
            throw new InvalidDataException(
                "A reconciliation prompt requires a typed resolution before the turn can resume.");
        }

        var allowed = target switch
        {
            TurnControlState.Running => current.Control is
                TurnControlState.Paused or
                TurnControlState.AwaitingUser or
                TurnControlState.AwaitingEvent or
                TurnControlState.SuspendedRuntime,
            TurnControlState.Paused => current.Control is
                TurnControlState.Running or
                TurnControlState.AwaitingUser or
                TurnControlState.AwaitingEvent,
            TurnControlState.AwaitingUser => current.Control is
                TurnControlState.Running or
                TurnControlState.Paused or
                TurnControlState.AwaitingEvent or
                TurnControlState.SuspendedRuntime,
            TurnControlState.AwaitingEvent => current.Control is
                TurnControlState.Running or
                TurnControlState.Paused or
                TurnControlState.AwaitingUser,
            TurnControlState.CancelRequested => current.Control is not
                TurnControlState.Completed and not
                TurnControlState.Cancelled and not
                TurnControlState.CancelRequested,
            TurnControlState.Cancelled => current.Control == TurnControlState.CancelRequested,
            TurnControlState.SuspendedRuntime => current.Control is
                TurnControlState.Running or
                TurnControlState.Paused or
                TurnControlState.AwaitingUser or
                TurnControlState.AwaitingEvent,
            TurnControlState.Completed => current.Control is
                TurnControlState.Running or
                TurnControlState.Paused or
                TurnControlState.AwaitingUser or
                TurnControlState.AwaitingEvent or
                TurnControlState.SuspendedRuntime,
            _ => false
        };
        if (!allowed)
        {
            throw new InvalidDataException(
                $"Control transition from {current.Control} to {target} is not valid.");
        }
    }
}
