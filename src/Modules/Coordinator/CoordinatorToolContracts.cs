using System.Text.Json.Serialization;

namespace Ali.Modules.Coordinator;

public sealed record CoordinatorMemoryResult(
    string Status,
    IReadOnlyList<CoordinatorMemoryItem> Memories,
    IReadOnlyList<string> Warnings);

public sealed record CoordinatorMemoryItem(
    string MemoryId,
    string Text,
    string Category,
    DateTimeOffset UpdatedAt);

public sealed record CoordinatorMemoryWriteResult(
    bool Saved,
    string Message,
    string? MemoryId = null);

public sealed record CoordinatorActiveUserResult(
    bool Selected,
    string Status,
    string? StableId,
    string? DisplayName,
    string? Address,
    string? Email,
    string? PhoneNumber);

public sealed record CoordinatorSourceResult(
    string Status,
    IReadOnlyList<CoordinatorSourceItem> Sources,
    IReadOnlyList<string> Warnings,
    bool CanRetry = false);

public sealed record CoordinatorSourceItem(
    string Name,
    string Topic,
    string Url,
    [property: JsonIgnore]
    DateTimeOffset RetrievedAt,
    string Excerpt);

public sealed record CoordinatorResearchResult(
    bool Succeeded,
    string Status,
    string Provider,
    string Tool,
    string Evidence);

public sealed record CoordinatorReminderResult(
    bool Saved,
    string Message,
    string? ReminderId = null,
    DateTimeOffset? DueAt = null);

public sealed record CoordinatorIdentityResult(
    string AssistantName,
    string ProfileId,
    string Description);

public sealed record CoordinatorCapability(
    string Name,
    string Description,
    string Source = "Ali native");

public sealed record CoordinatorCapabilityResult(
    string Status,
    IReadOnlyList<CoordinatorCapability> Tools);

public enum AgentToolExecutionOutcome
{
    Completed,
    Failed,
    Cancelled
}

public sealed record AgentToolExecutionReceipt(
    string ToolName,
    AgentToolExecutionOutcome Outcome,
    string Summary,
    DateTimeOffset RecordedAt);

internal sealed class CoordinatorTurnContext(
    string conversationId,
    string userMessageId,
    string assistantMessageId,
    string originalUserText,
    Action<AssistantStreamChunk> publish)
{
    private readonly long _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly Dictionary<string, CoordinatorToolPlan> _toolPlans = new(StringComparer.Ordinal);
    private readonly object _toolPlanSync = new();

    public string ConversationId { get; } = conversationId;

    public string UserMessageId { get; } = userMessageId;

    public string AssistantMessageId { get; } = assistantMessageId;

    public string OriginalUserText { get; } = originalUserText;

    public bool UsedEvidenceTool { get; set; }

    public bool PermissionDenied { get; private set; }

    public bool ExternalCodingAgentOwnsTurn { get; private set; }

    public bool RequiresExternalCodingAgent { get; private set; }

    public bool DirectFinalAllowed { get; private set; }

    public string? CodingDispositionBasis { get; private set; }

    public string? ExternalCodingAgentProjectPath { get; private set; }

    public string? ExternalCodingAgentObjective { get; private set; }

    public int WebSearchAttempts { get; set; }

    public int GoogleSearchAttempts { get; set; }

    public HashSet<string> FailedGoogleQueryKeys { get; } = new(StringComparer.Ordinal);

    public bool UsedCurrentWebSearch { get; set; }

    public bool UsedNavigationTool { get; set; }

    public List<CoordinatorSourceItem> WebSources { get; } = [];

    public CoordinatorToolPlan? CurrentToolPlan { get; private set; }

    public void RegisterToolPlan(CoordinatorToolPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_toolPlanSync)
        {
            _toolPlans[plan.CallId] = plan;
            CurrentToolPlan = plan;
        }
    }

    public bool TryGetToolPlan(string callId, out CoordinatorToolPlan? plan)
    {
        lock (_toolPlanSync)
        {
            return _toolPlans.TryGetValue(callId, out plan);
        }
    }

    public void RecordPermissionDecision(AgentToolApprovalChoice choice)
    {
        if (choice == AgentToolApprovalChoice.Deny)
        {
            PermissionDenied = true;
            // A denial is an authoritative tool outcome. Force the same bounded
            // final-answer audit used for retrieved evidence so the model cannot
            // acknowledge the rejection and then falsely claim the action ran.
            UsedEvidenceTool = true;
        }
    }

    public ExternalCodingAgentJob ClaimExternalCodingAgentOwnership(
        string projectPath,
        string objective)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);

        var isContinuation = ExternalCodingAgentOwnsTurn;
        ExternalCodingAgentOwnsTurn = true;
        ExternalCodingAgentProjectPath ??= projectPath;
        ExternalCodingAgentObjective ??= objective;

        var effectiveObjective = isContinuation
            ? string.Join(
                Environment.NewLine,
                ExternalCodingAgentObjective,
                string.Empty,
                "Continuation evidence or unmet behavior from the current turn:",
                objective)
            : ExternalCodingAgentObjective;
        return new ExternalCodingAgentJob(
            ExternalCodingAgentProjectPath,
            effectiveObjective);
    }

    public void SetCodingDisposition(
        bool requiresExternalCodingAgent,
        bool directFinalAllowed,
        string? basis)
    {
        RequiresExternalCodingAgent = requiresExternalCodingAgent;
        DirectFinalAllowed = directFinalAllowed;
        CodingDispositionBasis = string.IsNullOrWhiteSpace(basis)
            ? null
            : basis.Trim();
    }

    public void Report(
        AgentActivityKind kind,
        string title,
        string? detail = null,
        double? elapsedMilliseconds = null,
        AgentToolApprovalPrompt? approvalPrompt = null,
        string? activityKey = null,
        AgentToolExecutionReceipt? executionReceipt = null) =>
        publish(new AssistantStreamChunk(
            ConversationId,
            UserMessageId,
            AssistantMessageId,
            title,
            Ali.Modules.Evidence.EvidenceStatus.Unknown,
            IsActivity: true,
            ActivityKind: kind,
            ActivityDetail: detail,
            ElapsedMilliseconds: elapsedMilliseconds ??
                System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
            ApprovalPrompt: approvalPrompt,
            ActivityKey: activityKey,
            ExecutionReceipt: executionReceipt));
}

internal sealed record ExternalCodingAgentJob(
    string ProjectPath,
    string Objective);

internal sealed record CodingTurnDisposition(
    bool IsCodingWork,
    bool CanAnswerDirectlyWithoutCritic,
    string Basis);

internal sealed record CoordinatorToolPlan(
    string CallId,
    string ToolName,
    string Assessment,
    string ActionPlan,
    string NextStep,
    string SelectionHeadline,
    string ResultHeadline,
    string TechnicalArguments);
