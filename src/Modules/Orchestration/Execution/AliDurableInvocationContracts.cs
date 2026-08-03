using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration.Execution;

internal enum AliDurableInvocationState
{
    Prepared,
    Started,
    Completed,
    Failed,
    InDoubt
}

internal readonly record struct AliExactExecutionAdapterIdentity
{
    internal AliExactExecutionAdapterIdentity(
        string toolName,
        string capabilityId,
        string reconcilerId)
    {
        AliDurableInvocationValidation.RequireSafeIdentifier(
            toolName,
            256,
            nameof(toolName));
        AliDurableInvocationValidation.RequireSafeIdentifier(
            capabilityId,
            256,
            nameof(capabilityId));
        AliDurableInvocationValidation.RequireSafeIdentifier(
            reconcilerId,
            256,
            nameof(reconcilerId));
        ToolName = toolName;
        CapabilityId = capabilityId;
        ReconcilerId = reconcilerId;
    }

    internal string ToolName { get; }

    internal string CapabilityId { get; }

    internal string ReconcilerId { get; }

    internal bool Matches(
        string toolName,
        string capabilityId,
        string reconcilerId) =>
        string.Equals(ToolName, toolName, StringComparison.Ordinal)
        && string.Equals(CapabilityId, capabilityId, StringComparison.Ordinal)
        && string.Equals(ReconcilerId, reconcilerId, StringComparison.Ordinal);
}

internal sealed record AliDurableInvocationPlan(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string TurnStorageKey,
    string CallId,
    string WorkItemId,
    string ToolName,
    string CapabilityId,
    string ReconcilerId,
    string CanonicalArgumentsDigest,
    string ActionIdentityFingerprint,
    string TargetVersionDigest,
    string PermissionReceiptDigest,
    string RegistryRevisionDigest,
    string ExecutionRegistryIdentityDigest,
    string RootBinding,
    string DomainPreparationIdentity,
    string DomainPreparationDigest,
    AliDurableInvocationStartAnchor? StartAnchor)
{
    internal AliExactExecutionAdapterIdentity ExactIdentity =>
        new(ToolName, CapabilityId, ReconcilerId);

    internal static AliDurableInvocationPlan Create(
        AliExecutionPreparationRequest request,
        string rootBinding,
        string domainPreparationIdentity,
        string domainPreparationDigest,
        DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return new AliDurableInvocationPlan(
            Guid.NewGuid().ToString("N"),
            createdAtUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow,
            request.TurnIdentity.StorageKey,
            request.CallId,
            request.WorkItemId,
            request.ToolName,
            request.CapabilityId,
            request.ReconcilerId,
            request.CanonicalArgumentsDigest,
            request.ActionIdentityFingerprint,
            request.TargetVersionDigest,
            request.PermissionReceiptDigest,
            request.RegistryRevisionDigest,
            request.ExecutionRegistryIdentityDigest,
            rootBinding,
            domainPreparationIdentity,
            domainPreparationDigest,
            StartAnchor: null);
    }

    internal void Validate()
    {
        AliDurableInvocationValidation.RequireId(Id, nameof(Id));
        if (CreatedAtUtc == default || CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "A durable invocation creation time must be a non-default UTC value.");
        }

        TurnStateIntegrity.RequireDigest(TurnStorageKey, nameof(TurnStorageKey));
        TurnStateIntegrity.RequireBoundedValue(CallId, 256, nameof(CallId));
        TurnStateIntegrity.RequireBoundedValue(WorkItemId, 256, nameof(WorkItemId));
        _ = ExactIdentity;
        TurnStateIntegrity.RequireDigest(
            CanonicalArgumentsDigest,
            nameof(CanonicalArgumentsDigest));
        TurnStateIntegrity.RequireDigest(
            ActionIdentityFingerprint,
            nameof(ActionIdentityFingerprint));
        TurnStateIntegrity.RequireDigest(TargetVersionDigest, nameof(TargetVersionDigest));
        TurnStateIntegrity.RequireDigest(
            PermissionReceiptDigest,
            nameof(PermissionReceiptDigest));
        TurnStateIntegrity.RequireDigest(RegistryRevisionDigest, nameof(RegistryRevisionDigest));
        TurnStateIntegrity.RequireDigest(
            ExecutionRegistryIdentityDigest,
            nameof(ExecutionRegistryIdentityDigest));
        TurnStateIntegrity.RequireBoundedValue(RootBinding, 2048, nameof(RootBinding));
        AliDurableInvocationValidation.RequireSafeIdentifier(
            DomainPreparationIdentity,
            256,
            nameof(DomainPreparationIdentity));
        TurnStateIntegrity.RequireDigest(
            DomainPreparationDigest,
            nameof(DomainPreparationDigest));
        StartAnchor?.Validate();
    }

    internal void RequireExactGrant(AliExecutionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!string.Equals(Id, grant.PreparationIdentity, StringComparison.Ordinal)
            || !ExactIdentity.Matches(
                grant.ToolName,
                grant.CapabilityId,
                grant.ReconcilerId)
            || !string.Equals(CallId, grant.CallId, StringComparison.Ordinal)
            || !string.Equals(
                CanonicalArgumentsDigest,
                grant.CanonicalArgumentsDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                TargetVersionDigest,
                grant.TargetVersionDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                PermissionReceiptDigest,
                grant.PermissionReceiptDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                ExecutionRegistryIdentityDigest,
                grant.RegistryIdentityDigest,
                StringComparison.Ordinal)
            || !string.Equals(RootBinding, grant.RootBinding, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The one-use execution grant does not match the exact durable invocation plan.");
        }
    }
}

internal sealed record AliDurableInvocationStartAnchor(
    DateTimeOffset StartedAtUtc,
    string AuthorizationDigest)
{
    internal void Validate()
    {
        if (StartedAtUtc == default || StartedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "A durable invocation start anchor requires a non-default UTC time.");
        }
        TurnStateIntegrity.RequireDigest(AuthorizationDigest, nameof(AuthorizationDigest));
    }
}

internal sealed record AliDurableInvocationReceipt(
    string PlanId,
    int Revision,
    AliDurableInvocationState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    string AuthorizationDigest,
    string? StableOutcomeCode,
    string? ResultDigest,
    string? FailureCode)
{
    internal void Validate()
    {
        AliDurableInvocationValidation.RequireId(PlanId, nameof(PlanId));
        if (Revision <= 0)
        {
            throw new InvalidDataException(
                "A durable invocation receipt revision must be positive.");
        }
        if (!Enum.IsDefined(State) || State == AliDurableInvocationState.Prepared)
        {
            throw new InvalidDataException(
                "A durable invocation receipt must contain a post-prepare state.");
        }
        if (StartedAtUtc == default || StartedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "A durable invocation start time must be a non-default UTC value.");
        }

        TurnStateIntegrity.RequireDigest(AuthorizationDigest, nameof(AuthorizationDigest));
        if (State == AliDurableInvocationState.Started)
        {
            if (Revision != 1)
            {
                throw new InvalidDataException(
                    "A started durable invocation must be at receipt revision one.");
            }
            if (TerminalAtUtc is not null
                || StableOutcomeCode is not null
                || ResultDigest is not null
                || FailureCode is not null)
            {
                throw new InvalidDataException(
                    "A started durable invocation cannot contain terminal fields.");
            }
            return;
        }

        if (TerminalAtUtc is null
            || TerminalAtUtc.Value == default
            || TerminalAtUtc.Value.Offset != TimeSpan.Zero
            || TerminalAtUtc < StartedAtUtc)
        {
            throw new InvalidDataException(
                "A terminal durable invocation requires a valid UTC terminal time.");
        }
        if (Revision != 2)
        {
            throw new InvalidDataException(
                "A terminal durable invocation must be at receipt revision two.");
        }

        switch (State)
        {
            case AliDurableInvocationState.Completed:
                AliDurableInvocationValidation.RequireSafeIdentifier(
                    StableOutcomeCode,
                    128,
                    nameof(StableOutcomeCode));
                TurnStateIntegrity.RequireDigest(
                    ResultDigest ?? string.Empty,
                    nameof(ResultDigest));
                if (FailureCode is not null)
                {
                    throw new InvalidDataException(
                        "A completed durable invocation cannot contain a failure code.");
                }
                break;
            case AliDurableInvocationState.Failed:
            case AliDurableInvocationState.InDoubt:
                AliDurableInvocationValidation.RequireSafeIdentifier(
                    FailureCode,
                    128,
                    nameof(FailureCode));
                if (StableOutcomeCode is not null || ResultDigest is not null)
                {
                    throw new InvalidDataException(
                        "A non-successful durable invocation cannot contain success fields.");
                }
                break;
            default:
                throw new InvalidDataException(
                    "The durable invocation receipt contains an invalid state.");
        }
    }
}

internal sealed record AliDurableInvocationSnapshot(
    AliDurableInvocationPlan Plan,
    AliDurableInvocationState State,
    AliDurableInvocationReceipt? Receipt);

internal enum AliDurableInvocationRecoveryDisposition
{
    Absent,
    Applied,
    Failed,
    Unknown
}

internal sealed record AliDurableInvocationRecoveryResult(
    AliDurableInvocationRecoveryDisposition Disposition,
    string OutcomeCode)
{
    internal static AliDurableInvocationRecoveryResult Absent(string outcomeCode) =>
        Create(AliDurableInvocationRecoveryDisposition.Absent, outcomeCode);

    internal static AliDurableInvocationRecoveryResult Applied(string outcomeCode) =>
        Create(AliDurableInvocationRecoveryDisposition.Applied, outcomeCode);

    internal static AliDurableInvocationRecoveryResult Failed(string outcomeCode) =>
        Create(AliDurableInvocationRecoveryDisposition.Failed, outcomeCode);

    internal static AliDurableInvocationRecoveryResult Unknown(string outcomeCode) =>
        Create(AliDurableInvocationRecoveryDisposition.Unknown, outcomeCode);

    internal void Validate()
    {
        if (!Enum.IsDefined(Disposition))
        {
            throw new InvalidDataException(
                "The durable invocation recovery disposition is invalid.");
        }
        AliDurableInvocationValidation.RequireSafeIdentifier(
            OutcomeCode,
            128,
            nameof(OutcomeCode));
    }

    private static AliDurableInvocationRecoveryResult Create(
        AliDurableInvocationRecoveryDisposition disposition,
        string outcomeCode)
    {
        var result = new AliDurableInvocationRecoveryResult(disposition, outcomeCode);
        result.Validate();
        return result;
    }
}

internal interface IAliStartedInvocationDomainReconciler
{
    AliExactExecutionAdapterIdentity ExactIdentity { get; }

    ValueTask<AliDurableInvocationRecoveryResult> ReconcileStartedAsync(
        AliDurableInvocationPlan plan,
        AliDurableInvocationReceipt startedReceipt,
        CancellationToken cancellationToken);
}

internal static class AliDurableInvocationValidation
{
    internal static void RequireSafeIdentifier(
        string? value,
        int maximumLength,
        string parameterName)
    {
        TurnStateIntegrity.RequireBoundedValue(
            value ?? string.Empty,
            maximumLength,
            parameterName);
        TurnStateIntegrity.RequireOptionalIdentifier(
            value,
            maximumLength,
            parameterName);
    }

    internal static void RequireId(string value, string parameterName)
    {
        if (value is null
            || value.Length != 32
            || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{parameterName} must be a lowercase 128-bit hexadecimal identifier.");
        }
    }
}
