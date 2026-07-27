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
