using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Coordinator;

public enum AgentActivityKind
{
    Status,
    Planning,
    ToolCall,
    ToolResult,
    Approval,
    Warning,
    Error,
    Complete
}

public enum AgentToolApprovalChoice
{
    Deny,
    AllowOnce,
    AlwaysAllowArguments,
    AlwaysAllowTool
}

public sealed record AgentToolApprovalPrompt(
    string RequestId,
    string ToolName,
    string Arguments,
    string Description);

public sealed record AgentToolApprovalDecision(
    string RequestId,
    AgentToolApprovalChoice Choice);

public enum AgentRecoveryPromptKind
{
    ActionReconciliation,
    FinalPublicationReconciliation
}

public enum AgentRecoveryDecisionChoice
{
    ConfirmApplied,
    ConfirmAbsent,
    ConfirmDisplayed,
    ConfirmNotDisplayed
}

/// <summary>
/// Exact hidden identity for one durable recovery question. Human-facing controls must never
/// display these fields; they are carried back unchanged so the state boundary can reject a
/// stale, cross-turn, or rebound decision.
/// </summary>
public sealed record AgentRecoveryPrompt(
    TurnIdentity DurableIdentity,
    long ExpectedStateRevision,
    string PromptPublicationId,
    string PromptTextDigest,
    string SubjectId,
    long SubjectPreparedRevision,
    AgentRecoveryPromptKind Kind)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(DurableIdentity);
        if (ExpectedStateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedStateRevision));
        }

        RequireBoundedValue(PromptPublicationId, nameof(PromptPublicationId));
        RequireSha256Digest(PromptTextDigest, nameof(PromptTextDigest));
        RequireBoundedValue(SubjectId, nameof(SubjectId));
        if (SubjectPreparedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SubjectPreparedRevision));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }
    }

    private static void RequireBoundedValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException(
                "A recovery identity component must contain 1 to 256 characters.",
                parameterName);
        }
    }

    private static void RequireSha256Digest(string value, string parameterName)
    {
        if (value is null
            || value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A recovery prompt digest must be an exact SHA-256 hexadecimal digest.",
                parameterName);
        }
    }
}

public sealed record AgentRecoveryDecision(
    AgentRecoveryPrompt Prompt,
    AgentRecoveryDecisionChoice Choice)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Prompt);
        Prompt.Validate();
        var compatible = Prompt.Kind switch
        {
            AgentRecoveryPromptKind.ActionReconciliation =>
                Choice is AgentRecoveryDecisionChoice.ConfirmApplied
                    or AgentRecoveryDecisionChoice.ConfirmAbsent,
            AgentRecoveryPromptKind.FinalPublicationReconciliation =>
                Choice is AgentRecoveryDecisionChoice.ConfirmDisplayed
                    or AgentRecoveryDecisionChoice.ConfirmNotDisplayed,
            _ => false
        };
        if (!compatible)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Choice),
                "The recovery choice does not match the exact recovery prompt kind.");
        }
    }
}
