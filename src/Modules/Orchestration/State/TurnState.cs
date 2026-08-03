using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.State;

public enum TurnControlState
{
    Running,
    Paused,
    AwaitingUser,
    AwaitingEvent,
    CancelRequested,
    Cancelled,
    SuspendedRuntime,
    Completed
}

public enum ActionIntentState
{
    Prepared,
    InDoubt
}

public enum FinalPublicationStatus
{
    Prepared,
    InDoubt,
    ConfirmedAbsent,
    Committed
}

public enum InterimPublicationKind
{
    AwaitingUser,
    AwaitingEvent,
    SuspendedRuntime
}

public enum InterimPublicationReason
{
    PlannerAwaitingUser,
    PlannerAwaitingExternalEvent,
    PlannerProtocolSuspended,
    RuntimeBindingsChanged,
    ActionReconciliationRequired,
    FinalPublicationReconciliationRequired,
    ModelInputNotAdmitted,
    CompletionInputNotAdmitted,
    CompletionDispatchBindingsChanged,
    CompletionOutputIncomplete
}

internal static class InterimPublicationReasonPolicy
{
    internal static bool IsModelConfigurationRecoverablePause(
        this InterimPublicationReason reason) =>
        reason is InterimPublicationReason.ModelInputNotAdmitted
            or InterimPublicationReason.CompletionInputNotAdmitted
            or InterimPublicationReason.CompletionDispatchBindingsChanged
            or InterimPublicationReason.CompletionOutputIncomplete;
}

public enum InterimPublicationStatus
{
    Prepared,
    Committed
}

public enum AcceptedCallExecutionClass
{
    NonMutating,
    PreparedEffectRequired,
    Unavailable
}

public enum AcceptedConversationRole
{
    User,
    Assistant,
    System
}

public enum ActionUserResolution
{
    ConfirmApplied,
    ConfirmAbsent
}

public enum FinalPublicationUserResolution
{
    ConfirmDisplayed,
    ConfirmNotDisplayed
}

public sealed record AcceptedConversationInput(
    string SourceMessageId,
    long Sequence,
    string Text,
    AcceptedConversationRole Role = AcceptedConversationRole.User)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(SourceMessageId, 256, nameof(SourceMessageId));
        if (Sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Sequence));
        }

        if (!Enum.IsDefined(Role))
        {
            throw new ArgumentOutOfRangeException(nameof(Role));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Text);
    }
}

public sealed record AcceptedConversationEntryReference(
    string SourceMessageId,
    long Sequence,
    AcceptedConversationRole Role,
    string ContentDigest,
    ProtectedTurnInputReference Payload)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(SourceMessageId, 256, nameof(SourceMessageId));
        if (Sequence < 0)
        {
            throw new InvalidDataException("An accepted conversation sequence cannot be negative.");
        }

        if (!Enum.IsDefined(Role))
        {
            throw new InvalidDataException("An accepted conversation role is invalid.");
        }

        TurnStateIntegrity.RequireDigest(ContentDigest, nameof(ContentDigest));
        ArgumentNullException.ThrowIfNull(Payload);
        Payload.Validate(
            TurnInputPurposes.AcceptedConversationEntry,
            TurnInputPurposes.AcceptedConversationBinding(
                SourceMessageId,
                Sequence,
                Role,
                ContentDigest));
        if (!string.Equals(Payload.ContentDigest, ContentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "An accepted conversation reference does not match its protected content digest.");
        }
    }
}

/// <summary>
/// Exact accepted-call material stored only in the protected input store. The journal retains
/// a compact authenticated reference so a restart can distinguish a safe read retry from an
/// effect that requires a prepared intent and reconciliation.
/// </summary>
public sealed record AcceptedCallRecoveryPayload(
    string CallId,
    string WorkItemId,
    string ToolName,
    string? CapabilityId,
    string? SchemaFingerprint,
    string RegistryRevisionDigest,
    JsonElement CanonicalArguments,
    string ArgumentsDigest,
    string ActionFingerprint,
    string EffectFingerprint,
    AcceptedCallExecutionClass ExecutionClass,
    string? ReconcilerId,
    bool RequiresApproval)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(CallId, 256, nameof(CallId));
        TurnStateIntegrity.RequireBoundedValue(WorkItemId, 256, nameof(WorkItemId));
        TurnStateIntegrity.RequireBoundedValue(ToolName, 256, nameof(ToolName));
        TurnStateIntegrity.RequireOptionalIdentifier(CapabilityId, 256, nameof(CapabilityId));
        if (SchemaFingerprint is not null)
        {
            TurnStateIntegrity.RequireSha256Hex(SchemaFingerprint, nameof(SchemaFingerprint));
        }

        TurnStateIntegrity.RequireDigest(RegistryRevisionDigest, nameof(RegistryRevisionDigest));
        TurnStateIntegrity.RequireDigest(ArgumentsDigest, nameof(ArgumentsDigest));
        TurnStateIntegrity.RequireDigest(ActionFingerprint, nameof(ActionFingerprint));
        TurnStateIntegrity.RequireDigest(EffectFingerprint, nameof(EffectFingerprint));
        if (!Enum.IsDefined(ExecutionClass))
        {
            throw new ArgumentOutOfRangeException(nameof(ExecutionClass));
        }

        TurnStateIntegrity.RequireOptionalIdentifier(ReconcilerId, 256, nameof(ReconcilerId));
        if (ExecutionClass == AcceptedCallExecutionClass.PreparedEffectRequired
            && (string.IsNullOrWhiteSpace(CapabilityId)
                || SchemaFingerprint is null
                || string.IsNullOrWhiteSpace(ReconcilerId)))
        {
            throw new InvalidDataException(
                "An effect-bearing accepted call must bind its capability, schema, and reconciler.");
        }

        if (CanonicalArguments.ValueKind is JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Accepted call arguments cannot be undefined JSON.");
        }

        var argumentBytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(CanonicalArguments);
        try
        {
            if (!string.Equals(
                    TurnStateIntegrity.Digest(argumentBytes),
                    ArgumentsDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Accepted call arguments do not match their canonical digest.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(argumentBytes);
        }
    }

    internal AcceptedCallRecoveryPayload CloneProtected() =>
        this with { CanonicalArguments = CanonicalArguments.Clone() };
}

public sealed record AcceptedCallRecoveryReference(
    string CallId,
    string WorkItemId,
    string ToolName,
    string? CapabilityId,
    string RegistryRevisionDigest,
    string ArgumentsDigest,
    string ActionFingerprint,
    AcceptedCallExecutionClass ExecutionClass,
    string? ReconcilerId,
    ProtectedTurnInputReference Payload)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(CallId, 256, nameof(CallId));
        TurnStateIntegrity.RequireBoundedValue(WorkItemId, 256, nameof(WorkItemId));
        TurnStateIntegrity.RequireBoundedValue(ToolName, 256, nameof(ToolName));
        TurnStateIntegrity.RequireOptionalIdentifier(CapabilityId, 256, nameof(CapabilityId));
        TurnStateIntegrity.RequireDigest(RegistryRevisionDigest, nameof(RegistryRevisionDigest));
        TurnStateIntegrity.RequireDigest(ArgumentsDigest, nameof(ArgumentsDigest));
        TurnStateIntegrity.RequireDigest(ActionFingerprint, nameof(ActionFingerprint));
        if (!Enum.IsDefined(ExecutionClass))
        {
            throw new ArgumentOutOfRangeException(nameof(ExecutionClass));
        }
        TurnStateIntegrity.RequireOptionalIdentifier(ReconcilerId, 256, nameof(ReconcilerId));
        if (ExecutionClass == AcceptedCallExecutionClass.PreparedEffectRequired
            && (string.IsNullOrWhiteSpace(CapabilityId)
                || string.IsNullOrWhiteSpace(ReconcilerId)))
        {
            throw new InvalidDataException(
                "An effect-bearing accepted call reference must bind its capability and reconciler.");
        }

        ArgumentNullException.ThrowIfNull(Payload);
        Payload.Validate(
            TurnInputPurposes.AcceptedCall,
            TurnInputPurposes.AcceptedCallBinding(
                CallId,
                WorkItemId,
                ToolName,
                CapabilityId,
                RegistryRevisionDigest,
                ArgumentsDigest,
                ActionFingerprint,
                ExecutionClass,
                ReconcilerId));
    }
}

/// <summary>
/// Exact environment identities captured when a turn starts. These are opaque digests;
/// orchestration never interprets their source values or silently accepts a changed value.
/// </summary>
public sealed record TurnRuntimeBindings(
    string AssistantProfileDigest,
    string RuntimeDigest,
    string ModelDigest,
    string GenerationSettingsDigest,
    string CapabilityRegistryDigest,
    string PermissionDigest,
    string McpDigest,
    string AttachmentDigest,
    string ArtifactDigest)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireDigest(AssistantProfileDigest, nameof(AssistantProfileDigest));
        TurnStateIntegrity.RequireDigest(RuntimeDigest, nameof(RuntimeDigest));
        TurnStateIntegrity.RequireDigest(ModelDigest, nameof(ModelDigest));
        TurnStateIntegrity.RequireDigest(GenerationSettingsDigest, nameof(GenerationSettingsDigest));
        TurnStateIntegrity.RequireDigest(CapabilityRegistryDigest, nameof(CapabilityRegistryDigest));
        TurnStateIntegrity.RequireDigest(PermissionDigest, nameof(PermissionDigest));
        TurnStateIntegrity.RequireDigest(McpDigest, nameof(McpDigest));
        TurnStateIntegrity.RequireDigest(AttachmentDigest, nameof(AttachmentDigest));
        TurnStateIntegrity.RequireDigest(ArtifactDigest, nameof(ArtifactDigest));
    }

    public IReadOnlyList<string> FindChangedBindings(TurnRuntimeBindings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Validate();
        current.Validate();
        var changed = new List<string>(9);
        AddIfChanged(changed, "assistant-profile", AssistantProfileDigest, current.AssistantProfileDigest);
        AddIfChanged(changed, "runtime", RuntimeDigest, current.RuntimeDigest);
        AddIfChanged(changed, "model", ModelDigest, current.ModelDigest);
        AddIfChanged(changed, "generation-settings", GenerationSettingsDigest, current.GenerationSettingsDigest);
        AddIfChanged(changed, "capability-registry", CapabilityRegistryDigest, current.CapabilityRegistryDigest);
        AddIfChanged(changed, "permissions", PermissionDigest, current.PermissionDigest);
        AddIfChanged(changed, "mcp", McpDigest, current.McpDigest);
        AddIfChanged(changed, "attachments", AttachmentDigest, current.AttachmentDigest);
        AddIfChanged(changed, "artifacts", ArtifactDigest, current.ArtifactDigest);
        return changed.AsReadOnly();
    }

    private static void AddIfChanged(
        ICollection<string> changed,
        string name,
        string expected,
        string current)
    {
        if (!string.Equals(expected, current, StringComparison.Ordinal))
        {
            changed.Add(name);
        }
    }
}

public sealed record PreparedActionIntent(
    string IdempotencyKey,
    string WorkItemId,
    string ToolName,
    string CapabilityId,
    string CanonicalArgumentsDigest,
    string TargetVersionDigest,
    string PermissionReceiptDigest,
    string RegistryRevisionDigest,
    string ReconcilerId,
    bool RequiresApproval,
    string? AcceptedCallId = null)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(IdempotencyKey, 256, nameof(IdempotencyKey));
        TurnStateIntegrity.RequireBoundedValue(WorkItemId, 256, nameof(WorkItemId));
        TurnStateIntegrity.RequireBoundedValue(ToolName, 256, nameof(ToolName));
        TurnStateIntegrity.RequireBoundedValue(CapabilityId, 256, nameof(CapabilityId));
        TurnStateIntegrity.RequireDigest(CanonicalArgumentsDigest, nameof(CanonicalArgumentsDigest));
        TurnStateIntegrity.RequireDigest(TargetVersionDigest, nameof(TargetVersionDigest));
        TurnStateIntegrity.RequireDigest(PermissionReceiptDigest, nameof(PermissionReceiptDigest));
        TurnStateIntegrity.RequireDigest(RegistryRevisionDigest, nameof(RegistryRevisionDigest));
        TurnStateIntegrity.RequireBoundedValue(ReconcilerId, 256, nameof(ReconcilerId));
        TurnStateIntegrity.RequireOptionalIdentifier(AcceptedCallId, 256, nameof(AcceptedCallId));
    }
}

public sealed record CommittedEvidenceReference(
    string EvidenceId,
    long Cursor,
    string RecordDigest)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(EvidenceId, 256, nameof(EvidenceId));
        if (Cursor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Cursor), "An evidence cursor must be positive.");
        }

        TurnStateIntegrity.RequireDigest(RecordDigest, nameof(RecordDigest));
    }
}

public sealed record CommittedWorkGraphReference(long Revision, string RecordDigest)
{
    internal void Validate()
    {
        if (Revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision), "A work-graph revision must be positive.");
        }

        TurnStateIntegrity.RequireDigest(RecordDigest, nameof(RecordDigest));
    }
}

public sealed record PendingActionState(
    PreparedActionIntent Intent,
    ActionIntentState State,
    long PreparedAtRevision,
    long LastTransitionRevision)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Intent);
        Intent.Validate();
        if (!Enum.IsDefined(State) || PreparedAtRevision <= 0 || LastTransitionRevision < PreparedAtRevision)
        {
            throw new InvalidDataException("A pending action has invalid lifecycle metadata.");
        }
    }
}

public sealed record FinalPublicationState(
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest,
    ProtectedTurnInputReference AnswerPayload,
    FinalPublicationStatus Status,
    long PreparedAtRevision,
    long LastTransitionRevision)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(PublicationId, 256, nameof(PublicationId));
        TurnStateIntegrity.RequireBoundedValue(AssistantMessageId, 256, nameof(AssistantMessageId));
        TurnStateIntegrity.RequireDigest(AnswerDigest, nameof(AnswerDigest));
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
                "The protected final-answer payload does not match the committed answer digest.");
        }

        if (!Enum.IsDefined(Status) || PreparedAtRevision <= 0 || LastTransitionRevision < PreparedAtRevision)
        {
            throw new InvalidDataException("The final-publication lifecycle metadata is invalid.");
        }
    }
}

public sealed record InterimPublicationState(
    string PublicationId,
    InterimPublicationKind Kind,
    InterimPublicationReason Reason,
    string SubjectId,
    string TextDigest,
    ProtectedTurnInputReference TextPayload,
    InterimPublicationStatus Status,
    long PreparedAtRevision,
    long LastTransitionRevision)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireBoundedValue(PublicationId, 256, nameof(PublicationId));
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Reason) || !Enum.IsDefined(Status))
        {
            throw new InvalidDataException("The interim-publication kind, reason, or status is invalid.");
        }
        ValidateReasonKind(Reason, Kind);
        TurnStateIntegrity.RequireBoundedValue(SubjectId, 256, nameof(SubjectId));

        TurnStateIntegrity.RequireDigest(TextDigest, nameof(TextDigest));
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
                "The protected interim text does not match its committed digest.");
        }

        if (PreparedAtRevision <= 0 || LastTransitionRevision < PreparedAtRevision)
        {
            throw new InvalidDataException("The interim-publication lifecycle metadata is invalid.");
        }
    }

    internal static void ValidateReasonKind(
        InterimPublicationReason reason,
        InterimPublicationKind kind)
    {
        var expectedKind = reason switch
        {
            InterimPublicationReason.PlannerAwaitingUser
                or InterimPublicationReason.ActionReconciliationRequired
                or InterimPublicationReason.FinalPublicationReconciliationRequired =>
                InterimPublicationKind.AwaitingUser,
            InterimPublicationReason.PlannerAwaitingExternalEvent =>
                InterimPublicationKind.AwaitingEvent,
            InterimPublicationReason.PlannerProtocolSuspended
                or InterimPublicationReason.RuntimeBindingsChanged
                or InterimPublicationReason.ModelInputNotAdmitted
                or InterimPublicationReason.CompletionInputNotAdmitted
                or InterimPublicationReason.CompletionDispatchBindingsChanged
                or InterimPublicationReason.CompletionOutputIncomplete =>
                InterimPublicationKind.SuspendedRuntime,
            _ => throw new InvalidDataException("The interim-publication reason is invalid.")
        };
        if (kind != expectedKind)
        {
            throw new InvalidDataException(
                "The interim-publication reason does not match its control kind.");
        }
    }
}

/// <summary>
/// Compact authoritative state. Growing evidence, steering, work and action history stays in
/// the journal and is referenced by cursor/revision; only unresolved action state is retained.
/// </summary>
public sealed record TurnState(
    TurnIdentity Identity,
    ProtectedTurnInputReference OriginalRequest,
    AcceptedConversationEntryReference[] AcceptedPriorConversation,
    TurnRuntimeBindings Bindings,
    long Revision,
    long JournalCursor,
    long SteeringCursor,
    long EvidenceCursor,
    long WorkGraphRevision,
    TurnControlState Control,
    PendingActionState[] PendingActions,
    AcceptedCallRecoveryReference? PendingAcceptedCall,
    InterimPublicationState? InterimPublication,
    FinalPublicationState? FinalPublication,
    DateTimeOffset UpdatedAtUtc,
    CommittedWorkGraphReference? WorkGraphReference = null)
{
    public string OriginalRequestDigest => OriginalRequest.ContentDigest;

    public bool TryGetPendingAction(string idempotencyKey, out PendingActionState? pending)
    {
        pending = PendingActions.FirstOrDefault(item =>
            string.Equals(item.Intent.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
        return pending is not null;
    }

    internal void Validate(TurnIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(Identity);
        if (Identity != expectedIdentity)
        {
            throw new InvalidDataException(
                "A turn state cannot cross its user, conversation, or assistant-message boundary.");
        }

        ArgumentNullException.ThrowIfNull(OriginalRequest);
        OriginalRequest.ValidateStoredShape();

        ArgumentNullException.ThrowIfNull(AcceptedPriorConversation);
        if (AcceptedPriorConversation.Length > TurnStateLimits.MaximumAcceptedPriorConversationEntries)
        {
            throw new InvalidDataException("The accepted prior conversation exceeds its entry limit.");
        }

        var sourceMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        long acceptedConversationBytes = 0;
        foreach (var entry in AcceptedPriorConversation)
        {
            ArgumentNullException.ThrowIfNull(entry);
            entry.Validate();
            if (!sourceMessageIds.Add(entry.SourceMessageId) || !sequences.Add(entry.Sequence))
            {
                throw new InvalidDataException(
                    "The accepted prior conversation contains a duplicate source identity or sequence.");
            }

            acceptedConversationBytes = checked(acceptedConversationBytes + entry.Payload.Utf8Length);
        }

        if (acceptedConversationBytes > TurnStateLimits.MaximumAcceptedPriorConversationUtf8Bytes)
        {
            throw new InvalidDataException(
                "The accepted prior conversation exceeds its protected byte limit.");
        }

        ArgumentNullException.ThrowIfNull(Bindings);
        Bindings.Validate();
        if (Revision <= 0 || JournalCursor != Revision || SteeringCursor < 0
            || EvidenceCursor < 0 || WorkGraphRevision < 0 || UpdatedAtUtc.Offset != TimeSpan.Zero
            || !Enum.IsDefined(Control))
        {
            throw new InvalidDataException("The turn-state revision or cursor metadata is invalid.");
        }

        if (WorkGraphRevision == 0 && WorkGraphReference is not null
            || WorkGraphRevision > 0
            && (WorkGraphReference is null || WorkGraphReference.Revision != WorkGraphRevision))
        {
            throw new InvalidDataException(
                "The turn-state work-graph revision must have one exact committed reference.");
        }
        WorkGraphReference?.Validate();

        ArgumentNullException.ThrowIfNull(PendingActions);
        var actionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in PendingActions)
        {
            ArgumentNullException.ThrowIfNull(pending);
            pending.Validate();
            if (!actionKeys.Add(pending.Intent.IdempotencyKey))
            {
                throw new InvalidDataException("A turn state contains a duplicate pending action identity.");
            }
        }

        PendingAcceptedCall?.Validate();
        InterimPublication?.Validate();
        FinalPublication?.Validate();
        if (InterimPublication is { } typedInterim)
        {
            switch (typedInterim.Reason)
            {
                case InterimPublicationReason.PlannerAwaitingUser:
                    if (PendingActions.Length != 0
                        || PendingAcceptedCall is not null
                        || FinalPublication is not null)
                    {
                        throw new InvalidDataException(
                            "A planner user question cannot coexist with unresolved execution or final publication.");
                    }
                    goto case InterimPublicationReason.PlannerAwaitingExternalEvent;
                case InterimPublicationReason.PlannerAwaitingExternalEvent:
                case InterimPublicationReason.PlannerProtocolSuspended:
                case InterimPublicationReason.RuntimeBindingsChanged:
                case InterimPublicationReason.ModelInputNotAdmitted:
                case InterimPublicationReason.CompletionInputNotAdmitted:
                case InterimPublicationReason.CompletionDispatchBindingsChanged:
                case InterimPublicationReason.CompletionOutputIncomplete:
                    if (typedInterim.Reason.IsModelConfigurationRecoverablePause()
                        && (PendingActions.Length != 0
                            || PendingAcceptedCall is not null
                            || FinalPublication is not null))
                    {
                        throw new InvalidDataException(
                            "A model-configuration-recoverable pause cannot coexist with unresolved execution or final publication.");
                    }
                    if (!string.Equals(
                            typedInterim.SubjectId,
                            typedInterim.PublicationId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "A planner or runtime interim must bind its own publication identity as subject.");
                    }
                    break;
                case InterimPublicationReason.ActionReconciliationRequired:
                    if (PendingActions.Length != 1
                        || PendingActions[0].State != ActionIntentState.InDoubt
                        || !string.Equals(
                            typedInterim.SubjectId,
                            PendingActions[0].Intent.IdempotencyKey,
                            StringComparison.Ordinal)
                        || PendingAcceptedCall is { } acceptedCall
                        && !string.Equals(
                            PendingActions[0].Intent.AcceptedCallId,
                            acceptedCall.CallId,
                            StringComparison.Ordinal)
                        || FinalPublication is not null)
                    {
                        throw new InvalidDataException(
                            "An action-reconciliation interim must bind the exact in-doubt action.");
                    }
                    break;
                case InterimPublicationReason.FinalPublicationReconciliationRequired:
                    if (FinalPublication?.Status != FinalPublicationStatus.InDoubt
                        || !string.Equals(
                            typedInterim.SubjectId,
                            FinalPublication.PublicationId,
                            StringComparison.Ordinal)
                        || PendingActions.Length != 0
                        || PendingAcceptedCall is not null)
                    {
                        throw new InvalidDataException(
                            "A final-publication reconciliation interim must bind the exact in-doubt publication.");
                    }
                    break;
                default:
                    throw new InvalidDataException("The interim-publication reason is invalid.");
            }
        }
        if (InterimPublication is not null
            && FinalPublication is not null
            && (FinalPublication.Status != FinalPublicationStatus.InDoubt
                || InterimPublication.Kind != InterimPublicationKind.AwaitingUser
                || InterimPublication.Reason
                    != InterimPublicationReason.FinalPublicationReconciliationRequired))
        {
            throw new InvalidDataException(
                "An interim publication may coexist only with an in-doubt final publication that requires user reconciliation.");
        }

        if (PendingAcceptedCall is not null && FinalPublication is not null)
        {
            throw new InvalidDataException(
                "A final publication cannot coexist with an unresolved accepted call.");
        }

        var requiredInterimKind = Control switch
        {
            TurnControlState.AwaitingUser => InterimPublicationKind.AwaitingUser,
            TurnControlState.AwaitingEvent => InterimPublicationKind.AwaitingEvent,
            TurnControlState.SuspendedRuntime => InterimPublicationKind.SuspendedRuntime,
            _ => (InterimPublicationKind?)null
        };
        if (requiredInterimKind is not null
            && (InterimPublication?.Kind != requiredInterimKind.Value
                || InterimPublication.Status != InterimPublicationStatus.Committed))
        {
            throw new InvalidDataException(
                "A waiting turn must retain its exact committed interim publication receipt.");
        }

        if ((Control is TurnControlState.Completed or TurnControlState.Cancelled)
            && (PendingAcceptedCall is not null || InterimPublication is not null))
        {
            throw new InvalidDataException(
                "A terminal turn cannot retain an accepted call or interim publication.");
        }
    }
}

internal static class TurnStateLimits
{
    internal const int MaximumAcceptedPriorConversationEntries = 512;
    internal const int MaximumAcceptedPriorConversationUtf8Bytes = 16 * 1024 * 1024;
    internal const int MaximumAcceptedSteeringUtf8Bytes = 16 * 1024 * 1024;
}

internal static class TurnStateIntegrity
{
    public const string EmptyDigest =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static string Digest(string value) =>
        Digest(Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value))));

    public static string Digest(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    public static void RequireDigest(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A binding must be a lowercase SHA-256 digest.",
                parameterName);
        }
    }

    public static void RequireSha256Hex(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A binding must be a SHA-256 hexadecimal value.",
                parameterName);
        }
    }

    public static void RequireBoundedValue(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"A non-empty value of at most {maximumLength} characters is required.",
                parameterName);
        }
    }

    public static void RequireOptionalIdentifier(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length == 0 || value.Length > maximumLength || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_' and not '.' and not ':' and not '/'
                    and not '@' and not '+'))
        {
            throw new ArgumentException(
                $"An identifier of at most {maximumLength} safe ASCII characters is required.",
                parameterName);
        }
    }
}
