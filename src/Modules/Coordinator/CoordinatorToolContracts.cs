using System.Text.Json.Serialization;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.UserMemory;

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
    DateTimeOffset RecordedAt)
{
    public string? DisplayName { get; init; }
}

internal enum ExplicitShadowTerminalKind
{
    Denied,
    Cancelled
}

internal sealed record PendingExplicitShadowTerminal(
    string CallId,
    string ToolName,
    ExplicitShadowTerminalKind Kind,
    string? FailureCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    EvidencePermissionMetadata Permission);

internal sealed class CoordinatorTurnContext(
    string conversationId,
    string userMessageId,
    string assistantMessageId,
    string originalUserText,
    Action<AssistantStreamChunk> publish,
    ActiveUserSelectionSnapshot? capturedUserSelection = null,
    TurnIdentity? observationIdentity = null)
{
    internal const int MaximumRememberedShadowTerminals = 4_096;
    internal const int MaximumRememberedShadowPermissions = 4_096;
    internal const int MaximumRememberedShadowStandingPermissions = 4_096;
    internal const int MaximumRememberedPendingExplicitShadowTerminals = 4_096;
    internal const int MaximumShadowCallIdCharacters = 256;
    internal const int MaximumShadowToolNameCharacters = 256;
    internal const int MaximumShadowFailureCodeCharacters = 128;
    internal const int MaximumShadowPermissionValueCharacters = 64;

    private readonly long _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly Dictionary<string, CoordinatorToolPlan> _toolPlans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _capabilityIssueReports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shadowObservedCallIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _shadowObservedOldestFirst = new();
    private readonly Dictionary<string, EvidencePermissionMetadata> _shadowPermissions =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _shadowPermissionOldestFirst = new();
    private readonly Dictionary<string, EvidencePermissionMetadata> _shadowStandingPermissions =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _shadowStandingPermissionOldestFirst = new();
    private readonly Dictionary<string, PendingExplicitShadowTerminalEntry> _pendingExplicitShadowTerminals =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _pendingExplicitShadowOldestFirst = new();
    private readonly object _toolPlanSync = new();

    public string ConversationId { get; } = conversationId;

    public string UserMessageId { get; } = userMessageId;

    public string AssistantMessageId { get; } = assistantMessageId;

    public string OriginalUserText { get; } = originalUserText;

    public ActiveUserSelectionSnapshot? CapturedUserSelection { get; } = capturedUserSelection;

    public TurnIdentity? ObservationIdentity { get; } = observationIdentity;

    public bool UsedEvidenceTool { get; set; }

    public bool PermissionDenied { get; private set; }

    public bool DirectFinalAllowed { get; private set; }

    public string? CodingDispositionBasis { get; private set; }

    public int WebSearchAttempts { get; set; }

    public int GoogleSearchAttempts { get; set; }

    public HashSet<string> FailedGoogleQueryKeys { get; } = new(StringComparer.Ordinal);

    public bool UsedCurrentWebSearch { get; set; }

    public bool UsedNavigationTool { get; set; }

    public List<CoordinatorSourceItem> WebSources { get; } = [];

    public CoordinatorToolPlan? CurrentToolPlan { get; private set; }

    public bool TryRegisterCapabilityIssueReport(string reportKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportKey);
        lock (_toolPlanSync)
        {
            return _capabilityIssueReports.Add(reportKey);
        }
    }

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

    public bool TryGetCurrentToolCallId(string toolName, out string? callId)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            callId = null;
            return false;
        }

        lock (_toolPlanSync)
        {
            if (CurrentToolPlan is not null
                && string.Equals(CurrentToolPlan.ToolName, toolName, StringComparison.Ordinal))
            {
                callId = CurrentToolPlan.CallId;
                return true;
            }

            callId = null;
            return false;
        }
    }

    public bool MarkShadowObserved(string callId)
    {
        if (!IsBoundedShadowCallId(callId))
        {
            return false;
        }

        lock (_toolPlanSync)
        {
            if (!_shadowObservedCallIds.Add(callId))
            {
                return false;
            }

            if (_shadowObservedCallIds.Count > MaximumRememberedShadowTerminals)
            {
                var oldest = _shadowObservedOldestFirst.Dequeue();
                _shadowObservedCallIds.Remove(oldest);
            }

            _shadowObservedOldestFirst.Enqueue(callId);
            return true;
        }
    }

    public bool WasShadowObserved(string callId)
    {
        if (!IsBoundedShadowCallId(callId))
        {
            return false;
        }

        lock (_toolPlanSync)
        {
            return _shadowObservedCallIds.Contains(callId);
        }
    }

    public void RecordShadowPermission(
        string callId,
        EvidencePermissionMetadata permission)
    {
        if (!IsBoundedShadowCallId(callId)
            || !IsBoundedShadowPermission(permission))
        {
            return;
        }

        lock (_toolPlanSync)
        {
            if (_shadowPermissions.ContainsKey(callId))
            {
                _shadowPermissions[callId] = permission with { };
                return;
            }

            if (_shadowPermissions.Count >= MaximumRememberedShadowPermissions)
            {
                var oldest = _shadowPermissionOldestFirst.Dequeue();
                _shadowPermissions.Remove(oldest);
            }

            _shadowPermissions.Add(callId, permission with { });
            _shadowPermissionOldestFirst.Enqueue(callId);
        }
    }

    public bool TryGetShadowPermission(
        string callId,
        out EvidencePermissionMetadata? permission)
    {
        if (!IsBoundedShadowCallId(callId))
        {
            permission = null;
            return false;
        }

        lock (_toolPlanSync)
        {
            if (_shadowPermissions.TryGetValue(callId, out var stored))
            {
                permission = stored with { };
                return true;
            }

            permission = null;
            return false;
        }
    }

    public void RecordShadowStandingPermission(
        string toolName,
        EvidencePermissionMetadata permission)
    {
        if (!IsBoundedShadowToolName(toolName)
            || !IsBoundedShadowPermission(permission))
        {
            return;
        }

        lock (_toolPlanSync)
        {
            if (_shadowStandingPermissions.ContainsKey(toolName))
            {
                _shadowStandingPermissions[toolName] = permission with { };
                return;
            }

            if (_shadowStandingPermissions.Count
                >= MaximumRememberedShadowStandingPermissions)
            {
                var oldest = _shadowStandingPermissionOldestFirst.Dequeue();
                _shadowStandingPermissions.Remove(oldest);
            }

            _shadowStandingPermissions.Add(toolName, permission with { });
            _shadowStandingPermissionOldestFirst.Enqueue(toolName);
        }
    }

    public bool TryGetShadowStandingPermission(
        string toolName,
        out EvidencePermissionMetadata? permission)
    {
        if (!IsBoundedShadowToolName(toolName))
        {
            permission = null;
            return false;
        }

        lock (_toolPlanSync)
        {
            if (_shadowStandingPermissions.TryGetValue(toolName, out var stored))
            {
                permission = stored with { };
                return true;
            }

            permission = null;
            return false;
        }
    }

    public bool RecordPendingExplicitShadowTerminal(PendingExplicitShadowTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!IsBoundedShadowCallId(terminal.CallId)
            || !IsBoundedShadowToolName(terminal.ToolName)
            || terminal.Kind is not (ExplicitShadowTerminalKind.Denied
                or ExplicitShadowTerminalKind.Cancelled)
            || !IsOptionalBoundedValue(
                terminal.FailureCode,
                MaximumShadowFailureCodeCharacters)
            || !IsBoundedShadowPermission(terminal.Permission)
            || terminal.CompletedAtUtc < terminal.StartedAtUtc)
        {
            return false;
        }

        var snapshot = terminal with { Permission = terminal.Permission with { } };
        lock (_toolPlanSync)
        {
            if (_pendingExplicitShadowTerminals.TryGetValue(terminal.CallId, out var existing))
            {
                _pendingExplicitShadowTerminals[terminal.CallId] = existing with
                {
                    Terminal = snapshot
                };
                return true;
            }

            if (_pendingExplicitShadowTerminals.Count
                >= MaximumRememberedPendingExplicitShadowTerminals)
            {
                var oldest = _pendingExplicitShadowOldestFirst.First
                    ?? throw new InvalidOperationException(
                        "The pending explicit shadow-terminal map is inconsistent.");
                _pendingExplicitShadowOldestFirst.RemoveFirst();
                _pendingExplicitShadowTerminals.Remove(oldest.Value);
            }

            var node = _pendingExplicitShadowOldestFirst.AddLast(terminal.CallId);
            _pendingExplicitShadowTerminals.Add(
                terminal.CallId,
                new PendingExplicitShadowTerminalEntry(snapshot, node));
            return true;
        }
    }

    public bool TryGetPendingExplicitShadowTerminal(
        string callId,
        out PendingExplicitShadowTerminal? terminal)
    {
        if (!IsBoundedShadowCallId(callId))
        {
            terminal = null;
            return false;
        }

        lock (_toolPlanSync)
        {
            if (_pendingExplicitShadowTerminals.TryGetValue(callId, out var entry))
            {
                terminal = entry.Terminal with
                {
                    Permission = entry.Terminal.Permission with { }
                };
                return true;
            }

            terminal = null;
            return false;
        }
    }

    public bool ClearPendingExplicitShadowTerminal(string callId)
    {
        if (!IsBoundedShadowCallId(callId))
        {
            return false;
        }

        lock (_toolPlanSync)
        {
            if (!_pendingExplicitShadowTerminals.Remove(callId, out var entry))
            {
                return false;
            }

            _pendingExplicitShadowOldestFirst.Remove(entry.Node);
            return true;
        }
    }

    internal static bool IsBoundedShadowCallId(string? value) =>
        IsBoundedValue(value, MaximumShadowCallIdCharacters);

    internal static bool IsBoundedShadowToolName(string? value) =>
        IsBoundedValue(value, MaximumShadowToolNameCharacters);

    private static bool IsBoundedShadowPermission(EvidencePermissionMetadata? permission) =>
        permission is not null
        && IsBoundedValue(permission.Decision, MaximumShadowPermissionValueCharacters)
        && IsBoundedValue(permission.Scope, MaximumShadowPermissionValueCharacters);

    private static bool IsBoundedValue(string? value, int maximumCharacters) =>
        value is not null
        && value.Length > 0
        && value.Length <= maximumCharacters
        && !string.IsNullOrWhiteSpace(value);

    private static bool IsOptionalBoundedValue(string? value, int maximumCharacters) =>
        value is null || IsBoundedValue(value, maximumCharacters);

    private sealed record PendingExplicitShadowTerminalEntry(
        PendingExplicitShadowTerminal Terminal,
        LinkedListNode<string> Node);

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

    public void SetCodingDisposition(
        bool directFinalAllowed,
        string? basis)
    {
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
