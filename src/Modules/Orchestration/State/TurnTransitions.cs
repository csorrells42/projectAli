using System.Text.Json.Serialization;

namespace Ali.Modules.Orchestration.State;

public enum ActionReconciliationDisposition
{
    Applied,
    Absent,
    Unknown
}

/// <summary>
/// Closed vocabulary for the planner's accepted next action. User/model prose and tool
/// arguments are deliberately excluded from the authoritative transition.
/// </summary>
public enum PlanningAcceptedActionKind
{
    CallTool = 0,
    ExpandTools = 1,
    AnswerDirectly = 2,
    RequestUserInput = 3,
    AwaitExternalEvent = 4,
    BeginCompletion = 5,
    InspectEvidencePage = 6,
    InspectWorkPage = 7
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$transition")]
[JsonDerivedType(typeof(TurnStartedTransition), "turn-started")]
[JsonDerivedType(typeof(SteeringAppendedTransition), "steering-appended")]
[JsonDerivedType(typeof(PlanningDecisionAcceptedTransition), "planning-decision-accepted")]
[JsonDerivedType(typeof(AcceptedCallInterruptedTransition), "accepted-call-interrupted")]
[JsonDerivedType(typeof(ControlChangedTransition), "control-changed")]
[JsonDerivedType(typeof(RuntimeBindingsRevalidatedTransition), "runtime-bindings-revalidated")]
[JsonDerivedType(typeof(EvidenceReferencedTransition), "evidence-referenced")]
[JsonDerivedType(typeof(ProgressAttemptRecordedTransition), "progress-attempt-recorded")]
[JsonDerivedType(typeof(WorkGraphReferencedTransition), "work-graph-referenced")]
[JsonDerivedType(typeof(CompletionCriticReviewPreparedTransition), "completion-critic-review-prepared")]
[JsonDerivedType(typeof(CompletionCriticVerdictCommittedTransition), "completion-critic-verdict-committed")]
[JsonDerivedType(typeof(ActionPreparedTransition), "action-prepared")]
[JsonDerivedType(typeof(ActionCommittedTransition), "action-committed")]
[JsonDerivedType(typeof(ActionMarkedInDoubtTransition), "action-in-doubt")]
[JsonDerivedType(typeof(ActionReconciledTransition), "action-reconciled")]
[JsonDerivedType(typeof(UnknownActionResolvedByUserTransition), "unknown-action-resolved-by-user")]
[JsonDerivedType(typeof(UnknownFinalPublicationResolvedByUserTransition), "unknown-publication-resolved-by-user")]
[JsonDerivedType(typeof(InDoubtActionCancelledTransition), "in-doubt-action-cancelled")]
[JsonDerivedType(typeof(InDoubtFinalPublicationCancelledTransition), "in-doubt-publication-cancelled")]
[JsonDerivedType(typeof(InterimPublicationPreparedTransition), "interim-publication-prepared")]
[JsonDerivedType(typeof(InterimPublicationDisplayMarkedInDoubtTransition), "interim-publication-display-in-doubt")]
[JsonDerivedType(typeof(InterimPublicationCommittedTransition), "interim-publication-committed")]
[JsonDerivedType(typeof(InterimPublicationClearedTransition), "interim-publication-cleared")]
[JsonDerivedType(typeof(FinalPublicationPreparedTransition), "publication-prepared")]
[JsonDerivedType(typeof(FinalPublicationMarkedInDoubtTransition), "publication-in-doubt")]
[JsonDerivedType(typeof(FinalPublicationConfirmedAbsentTransition), "publication-confirmed-absent")]
[JsonDerivedType(typeof(FinalPublicationRetargetedTransition), "publication-retargeted")]
[JsonDerivedType(typeof(FinalPublicationAbandonedTransition), "publication-abandoned")]
[JsonDerivedType(typeof(FinalPublicationCommittedTransition), "publication-committed")]
public abstract record TurnTransition(string CorrelationKey)
{
    internal virtual void ValidateShape()
    {
        TurnStateIntegrity.RequireBoundedValue(CorrelationKey, 256, nameof(CorrelationKey));
    }
}

public sealed record TurnStartedTransition(
    string CorrelationKey,
    ProtectedTurnInputReference OriginalRequest,
    TurnRuntimeBindings Bindings,
    AcceptedConversationEntryReference[] AcceptedPriorConversation) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        ArgumentNullException.ThrowIfNull(OriginalRequest);
        OriginalRequest.Validate(TurnInputPurposes.OriginalRequest, CorrelationKey);
        ArgumentNullException.ThrowIfNull(Bindings);
        Bindings.Validate();
        ArgumentNullException.ThrowIfNull(AcceptedPriorConversation);
        if (AcceptedPriorConversation.Length > TurnStateLimits.MaximumAcceptedPriorConversationEntries)
        {
            throw new InvalidDataException("The accepted prior conversation exceeds its entry limit.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        long aggregateBytes = 0;
        foreach (var entry in AcceptedPriorConversation)
        {
            ArgumentNullException.ThrowIfNull(entry);
            entry.Validate();
            if (!sourceIds.Add(entry.SourceMessageId) || !sequences.Add(entry.Sequence))
            {
                throw new InvalidDataException(
                    "The accepted prior conversation contains a duplicate source identity or sequence.");
            }

            aggregateBytes = checked(aggregateBytes + entry.Payload.Utf8Length);
        }

        if (aggregateBytes > TurnStateLimits.MaximumAcceptedPriorConversationUtf8Bytes)
        {
            throw new InvalidDataException(
                "The accepted prior conversation exceeds its protected byte limit.");
        }
    }
}

public sealed record SteeringAppendedTransition(
    string CorrelationKey,
    ProtectedTurnInputReference Steering) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        ArgumentNullException.ThrowIfNull(Steering);
        Steering.Validate(TurnInputPurposes.Steering, CorrelationKey);
    }
}

public sealed record PlanningDecisionAcceptedTransition(
    string CorrelationKey,
    string DecisionDigest,
    PlanningAcceptedActionKind ActionKind,
    string? CallId,
    string? ToolName,
    long WorkGraphRevision,
    string MaterialClaimsDigest,
    CommittedWorkGraphReference? WorkGraph = null,
    AcceptedCallRecoveryReference? AcceptedCall = null) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireDigest(DecisionDigest, nameof(DecisionDigest));
        TurnStateIntegrity.RequireDigest(MaterialClaimsDigest, nameof(MaterialClaimsDigest));
        if (!Enum.IsDefined(ActionKind))
        {
            throw new ArgumentOutOfRangeException(nameof(ActionKind));
        }

        TurnStateIntegrity.RequireOptionalIdentifier(CallId, 256, nameof(CallId));
        TurnStateIntegrity.RequireOptionalIdentifier(ToolName, 256, nameof(ToolName));
        if (WorkGraphRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkGraphRevision));
        }

        if (WorkGraph is not null)
        {
            WorkGraph.Validate();
            if (WorkGraph.Revision != WorkGraphRevision)
            {
                throw new InvalidDataException(
                    "An accepted decision's exact work-graph reference must match its revision.");
            }
        }

        if (ActionKind == PlanningAcceptedActionKind.CallTool)
        {
            ArgumentNullException.ThrowIfNull(AcceptedCall);
            AcceptedCall.Validate();
            if (!string.Equals(CallId, AcceptedCall.CallId, StringComparison.Ordinal)
                || !string.Equals(ToolName, AcceptedCall.ToolName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The accepted call recovery reference must match the decision call identity.");
            }
        }
        else if (AcceptedCall is not null)
        {
            throw new InvalidDataException(
                "Only a tool-call planning decision may retain accepted-call recovery material.");
        }
    }
}

public sealed record AcceptedCallInterruptedTransition(
    string CorrelationKey,
    string CallId,
    string OutcomeCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(CallId, 256, nameof(CallId));
        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
    }
}

public sealed record ControlChangedTransition(
    string CorrelationKey,
    TurnControlState Control,
    string ReasonCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        if (!Enum.IsDefined(Control))
        {
            throw new ArgumentOutOfRangeException(nameof(Control));
        }

        TurnStateIntegrity.RequireBoundedValue(ReasonCode, 128, nameof(ReasonCode));
    }
}

/// <summary>
/// Explicitly adopts only model/generation bindings while resuming an exact committed
/// model-configuration-recoverable pause. The reducer mechanically rejects every other binding
/// change.
/// </summary>
public sealed record RuntimeBindingsRevalidatedTransition(
    string CorrelationKey,
    string PublicationId,
    string TextDigest,
    TurnRuntimeBindings Bindings) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(PublicationId, 256, nameof(PublicationId));
        TurnStateIntegrity.RequireDigest(TextDigest, nameof(TextDigest));
        ArgumentNullException.ThrowIfNull(Bindings);
        Bindings.Validate();
    }
}

public sealed record EvidenceReferencedTransition(
    string CorrelationKey,
    CommittedEvidenceReference Evidence,
    string? ActionIdempotencyKey,
    string? CallId = null) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        ArgumentNullException.ThrowIfNull(Evidence);
        Evidence.Validate();
        if (ActionIdempotencyKey is not null)
        {
            TurnStateIntegrity.RequireBoundedValue(
                ActionIdempotencyKey,
                256,
                nameof(ActionIdempotencyKey));
        }

        TurnStateIntegrity.RequireOptionalIdentifier(CallId, 256, nameof(CallId));
    }
}

public sealed record ProgressAttemptRecordedTransition(
    string CorrelationKey,
    string ActionFingerprint,
    string EffectFingerprint,
    string? NoEffectFingerprint,
    string BeforeMaterialFingerprint,
    string AfterMaterialFingerprint,
    bool MateriallyAdvanced) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireDigest(ActionFingerprint, nameof(ActionFingerprint));
        TurnStateIntegrity.RequireDigest(EffectFingerprint, nameof(EffectFingerprint));
        if (NoEffectFingerprint is not null)
        {
            TurnStateIntegrity.RequireDigest(NoEffectFingerprint, nameof(NoEffectFingerprint));
        }
        TurnStateIntegrity.RequireDigest(
            BeforeMaterialFingerprint,
            nameof(BeforeMaterialFingerprint));
        TurnStateIntegrity.RequireDigest(
            AfterMaterialFingerprint,
            nameof(AfterMaterialFingerprint));
    }
}

public sealed record CompletionCriticReviewPreparedTransition(
    string CorrelationKey,
    string ReviewIdentityDigest,
    long SourceStateRevision,
    string AnswerId,
    string AnswerDigest,
    string[] RenderedPageHashes,
    string RuntimeDigest,
    string ModelDigest,
    string GenerationSettingsDigest,
    string ReasoningEffort) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireDigest(ReviewIdentityDigest, nameof(ReviewIdentityDigest));
        if (SourceStateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceStateRevision));
        }

        TurnStateIntegrity.RequireBoundedValue(AnswerId, 256, nameof(AnswerId));
        TurnStateIntegrity.RequireDigest(AnswerDigest, nameof(AnswerDigest));
        ArgumentNullException.ThrowIfNull(RenderedPageHashes);
        if (RenderedPageHashes.Length == 0 || RenderedPageHashes.Length > 256)
        {
            throw new InvalidDataException(
                "A completion-critic preparation requires one to 256 rendered page hashes.");
        }

        foreach (var hash in RenderedPageHashes)
        {
            TurnStateIntegrity.RequireDigest(hash, nameof(RenderedPageHashes));
        }

        TurnStateIntegrity.RequireDigest(RuntimeDigest, nameof(RuntimeDigest));
        TurnStateIntegrity.RequireDigest(ModelDigest, nameof(ModelDigest));
        TurnStateIntegrity.RequireDigest(
            GenerationSettingsDigest,
            nameof(GenerationSettingsDigest));
        TurnStateIntegrity.RequireBoundedValue(ReasoningEffort, 128, nameof(ReasoningEffort));
    }
}

public sealed record CompletionCriticVerdictCommittedTransition(
    string CorrelationKey,
    string ReviewIdentityDigest,
    bool Complete,
    string Basis,
    string[] MaterialUnmetOutcomes) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireDigest(ReviewIdentityDigest, nameof(ReviewIdentityDigest));
        TurnStateIntegrity.RequireBoundedValue(Basis, 4_000, nameof(Basis));
        ArgumentNullException.ThrowIfNull(MaterialUnmetOutcomes);
        if (MaterialUnmetOutcomes.Length > 64
            || MaterialUnmetOutcomes.Any(static value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 2_000)
            || MaterialUnmetOutcomes.Distinct(StringComparer.Ordinal).Count()
                != MaterialUnmetOutcomes.Length
            || MaterialUnmetOutcomes.Sum(static value => (long)value.Length) > 16_000
            || Complete != (MaterialUnmetOutcomes.Length == 0))
        {
            throw new InvalidDataException(
                "The completion-critic verdict shape is invalid.");
        }
    }
}

public sealed record WorkGraphReferencedTransition(
    string CorrelationKey,
    CommittedWorkGraphReference WorkGraph) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        ArgumentNullException.ThrowIfNull(WorkGraph);
        WorkGraph.Validate();
    }
}

public sealed record ActionPreparedTransition(
    string CorrelationKey,
    PreparedActionIntent Intent) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        ArgumentNullException.ThrowIfNull(Intent);
        Intent.Validate();
    }
}

public sealed record ActionCommittedTransition(
    string CorrelationKey,
    string ActionIdempotencyKey,
    string EvidenceId,
    long EvidenceCursor) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(
            ActionIdempotencyKey,
            256,
            nameof(ActionIdempotencyKey));
        TurnStateIntegrity.RequireBoundedValue(EvidenceId, 256, nameof(EvidenceId));
        if (EvidenceCursor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EvidenceCursor));
        }
    }
}

public sealed record ActionMarkedInDoubtTransition(
    string CorrelationKey,
    string ActionIdempotencyKey) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(
            ActionIdempotencyKey,
            256,
            nameof(ActionIdempotencyKey));
    }
}

public sealed record ActionReconciledTransition(
    string CorrelationKey,
    string ActionIdempotencyKey,
    ActionReconciliationDisposition Disposition,
    string? EvidenceId,
    long? EvidenceCursor,
    string OutcomeCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(
            ActionIdempotencyKey,
            256,
            nameof(ActionIdempotencyKey));
        if (!Enum.IsDefined(Disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(Disposition));
        }

        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
        if (Disposition == ActionReconciliationDisposition.Applied)
        {
            TurnStateIntegrity.RequireBoundedValue(EvidenceId!, 256, nameof(EvidenceId));
            if (EvidenceCursor is null or <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(EvidenceCursor));
            }
        }
        else if (EvidenceId is not null || EvidenceCursor is not null)
        {
            throw new ArgumentException(
                "Only an applied reconciliation may reference committed evidence.");
        }
    }
}

public sealed record UnknownActionResolvedByUserTransition(
    string CorrelationKey,
    string SourceCommandId,
    string PromptPublicationId,
    string PromptTextDigest,
    InterimPublicationReason Reason,
    string SubjectId,
    long SubjectPreparedRevision,
    ActionUserResolution Resolution) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(SourceCommandId, 256, nameof(SourceCommandId));
        TurnStateIntegrity.RequireBoundedValue(
            PromptPublicationId,
            256,
            nameof(PromptPublicationId));
        TurnStateIntegrity.RequireDigest(PromptTextDigest, nameof(PromptTextDigest));
        if (Reason != InterimPublicationReason.ActionReconciliationRequired)
        {
            throw new ArgumentOutOfRangeException(nameof(Reason));
        }

        TurnStateIntegrity.RequireBoundedValue(SubjectId, 256, nameof(SubjectId));
        if (SubjectPreparedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SubjectPreparedRevision));
        }
        if (!Enum.IsDefined(Resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(Resolution));
        }
    }
}

public sealed record UnknownFinalPublicationResolvedByUserTransition(
    string CorrelationKey,
    string SourceCommandId,
    string PromptPublicationId,
    string PromptTextDigest,
    InterimPublicationReason Reason,
    string SubjectId,
    long SubjectPreparedRevision,
    FinalPublicationUserResolution Resolution) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(SourceCommandId, 256, nameof(SourceCommandId));
        TurnStateIntegrity.RequireBoundedValue(
            PromptPublicationId,
            256,
            nameof(PromptPublicationId));
        TurnStateIntegrity.RequireDigest(PromptTextDigest, nameof(PromptTextDigest));
        if (Reason != InterimPublicationReason.FinalPublicationReconciliationRequired)
        {
            throw new ArgumentOutOfRangeException(nameof(Reason));
        }

        TurnStateIntegrity.RequireBoundedValue(SubjectId, 256, nameof(SubjectId));
        if (SubjectPreparedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SubjectPreparedRevision));
        }
        if (!Enum.IsDefined(Resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(Resolution));
        }
    }
}

public sealed record InDoubtActionCancelledTransition(
    string CorrelationKey,
    string ActionIdempotencyKey,
    long SubjectPreparedRevision,
    string OutcomeCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        TurnStateIntegrity.RequireBoundedValue(
            ActionIdempotencyKey,
            256,
            nameof(ActionIdempotencyKey));
        if (SubjectPreparedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SubjectPreparedRevision));
        }
        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
    }
}

public sealed record InDoubtFinalPublicationCancelledTransition(
    string CorrelationKey,
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest,
    long SubjectPreparedRevision,
    string OutcomeCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        FinalPublicationPreparedTransition.ValidatePublication(
            PublicationId,
            AssistantMessageId,
            AnswerDigest);
        if (SubjectPreparedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SubjectPreparedRevision));
        }
        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
    }
}

public sealed record InterimPublicationPreparedTransition(
    string CorrelationKey,
    string PublicationId,
    InterimPublicationKind Kind,
    InterimPublicationReason Reason,
    string SubjectId,
    string TextDigest,
    ProtectedTurnInputReference TextPayload) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        ValidatePublication(PublicationId, Kind, Reason, SubjectId, TextDigest);
        ArgumentNullException.ThrowIfNull(TextPayload);
        TextPayload.Validate(
            TurnInputPurposes.InterimPublicationText,
            TurnInputPurposes.InterimPublicationBinding(
                PublicationId,
                Kind,
                Reason,
                SubjectId,
                TextDigest));
        if (!string.Equals(TextPayload.ContentDigest, TextDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected interim text does not match the prepared text digest.");
        }
    }

    internal static void ValidatePublication(
        string publicationId,
        InterimPublicationKind kind,
        string textDigest)
    {
        TurnStateIntegrity.RequireBoundedValue(publicationId, 256, nameof(publicationId));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TurnStateIntegrity.RequireDigest(textDigest, nameof(textDigest));
    }

    internal static void ValidatePublication(
        string publicationId,
        InterimPublicationKind kind,
        InterimPublicationReason reason,
        string subjectId,
        string textDigest)
    {
        ValidatePublication(publicationId, kind, textDigest);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        InterimPublicationState.ValidateReasonKind(reason, kind);
        TurnStateIntegrity.RequireBoundedValue(subjectId, 256, nameof(subjectId));
    }
}

public sealed record InterimPublicationCommittedTransition(
    string CorrelationKey,
    string PublicationId,
    InterimPublicationKind Kind,
    string TextDigest) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        InterimPublicationPreparedTransition.ValidatePublication(PublicationId, Kind, TextDigest);
    }
}

public sealed record InterimPublicationDisplayMarkedInDoubtTransition(
    string CorrelationKey,
    string PublicationId,
    InterimPublicationKind Kind,
    string TextDigest) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        InterimPublicationPreparedTransition.ValidatePublication(PublicationId, Kind, TextDigest);
    }
}

public sealed record InterimPublicationClearedTransition(
    string CorrelationKey,
    string PublicationId,
    InterimPublicationKind Kind,
    string TextDigest,
    string OutcomeCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        InterimPublicationPreparedTransition.ValidatePublication(PublicationId, Kind, TextDigest);
        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
    }
}

public sealed record FinalPublicationPreparedTransition(
    string CorrelationKey,
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest,
    ProtectedTurnInputReference AnswerPayload) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        ValidatePublication(PublicationId, AssistantMessageId, AnswerDigest);
        ArgumentNullException.ThrowIfNull(AnswerPayload);
        AnswerPayload.Validate(
            TurnInputPurposes.FinalPublicationAnswer,
            TurnInputPurposes.FinalPublicationBinding(
                PublicationId,
                AssistantMessageId,
                AnswerDigest));
        if (!string.Equals(AnswerPayload.ContentDigest, AnswerDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected final-answer payload does not match the prepared answer digest.");
        }
    }

    internal static void ValidatePublication(
        string publicationId,
        string assistantMessageId,
        string answerDigest)
    {
        TurnStateIntegrity.RequireBoundedValue(publicationId, 256, nameof(publicationId));
        TurnStateIntegrity.RequireBoundedValue(assistantMessageId, 256, nameof(assistantMessageId));
        TurnStateIntegrity.RequireDigest(answerDigest, nameof(answerDigest));
    }
}

public sealed record FinalPublicationMarkedInDoubtTransition(
    string CorrelationKey,
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        FinalPublicationPreparedTransition.ValidatePublication(
            PublicationId,
            AssistantMessageId,
            AnswerDigest);
    }
}

public sealed record FinalPublicationConfirmedAbsentTransition(
    string CorrelationKey,
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest,
    string OutcomeCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        FinalPublicationPreparedTransition.ValidatePublication(
            PublicationId,
            AssistantMessageId,
            AnswerDigest);
        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
    }
}

public sealed record FinalPublicationRetargetedTransition(
    string CorrelationKey,
    string PreviousPublicationId,
    string PreviousAssistantMessageId,
    string NewPublicationId,
    string NewAssistantMessageId,
    string AnswerDigest,
    ProtectedTurnInputReference AnswerPayload) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        FinalPublicationPreparedTransition.ValidatePublication(
            PreviousPublicationId,
            PreviousAssistantMessageId,
            AnswerDigest);
        FinalPublicationPreparedTransition.ValidatePublication(
            NewPublicationId,
            NewAssistantMessageId,
            AnswerDigest);
        ArgumentNullException.ThrowIfNull(AnswerPayload);
        AnswerPayload.Validate(
            TurnInputPurposes.FinalPublicationAnswer,
            TurnInputPurposes.FinalPublicationBinding(
                NewPublicationId,
                NewAssistantMessageId,
                AnswerDigest));
        if (!string.Equals(AnswerPayload.ContentDigest, AnswerDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The retargeted final-answer payload does not match its committed digest.");
        }
    }
}

public sealed record FinalPublicationCommittedTransition(
    string CorrelationKey,
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        FinalPublicationPreparedTransition.ValidatePublication(
            PublicationId,
            AssistantMessageId,
            AnswerDigest);
    }
}

public sealed record FinalPublicationAbandonedTransition(
    string CorrelationKey,
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest,
    string OutcomeCode) : TurnTransition(CorrelationKey)
{
    internal override void ValidateShape()
    {
        base.ValidateShape();
        FinalPublicationPreparedTransition.ValidatePublication(
            PublicationId,
            AssistantMessageId,
            AnswerDigest);
        TurnStateIntegrity.RequireBoundedValue(OutcomeCode, 128, nameof(OutcomeCode));
    }
}
