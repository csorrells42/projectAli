using System.Text.Json.Serialization;
using Ali.Modules.Capabilities;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.UserMemory;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

public sealed record CoordinatorMemoryResult(
    string Status,
    IReadOnlyList<CoordinatorMemoryItem> Memories,
    IReadOnlyList<string> Warnings)
{
    public CoordinatorParticipantRosterResult? ParticipantRoster { get; init; }
}

public sealed record CoordinatorMemoryItem(
    string MemoryId,
    string Text,
    string Category,
    DateTimeOffset UpdatedAt)
{
    public string? SpeakerParticipantReference { get; init; }

    public IReadOnlyList<string> SubjectParticipantReferences { get; init; } = [];

    public IReadOnlyList<string> WitnessParticipantReferences { get; init; } = [];

    public string? SharedEventReference { get; init; }

    public ParticipantMemoryClaimKind ClaimKind { get; init; }

    public ParticipantMemoryEvidenceKind EvidenceKind { get; init; }

    public ParticipantMemoryVisibility Visibility { get; init; }

    public IReadOnlyList<string> AudienceParticipantReferences { get; init; } = [];

    public ParticipantMemorySensitivity Sensitivity { get; init; }

    public string? ReportedByParticipantReference { get; init; }
}

public sealed record CoordinatorParticipantRosterResult(
    string Revision,
    string? SelectedParticipantReference,
    IReadOnlyList<CoordinatorParticipantRosterItem> Participants);

public sealed record CoordinatorParticipantRosterItem(
    string ReferenceId,
    string DisplayLabel,
    ParticipantReferenceKind Kind,
    ParticipantPresenceState Presence,
    string ObservationSource,
    double? ObservationConfidence);

public sealed record CoordinatorMemoryWriteResult(
    bool Saved,
    string Message,
    string? MemoryId = null,
    string? RequestId = null,
    bool DurableCompletionConfirmed = false);

public sealed record CoordinatorMemoryReconciliationResult(
    bool Succeeded,
    string Message,
    string MutationRequestId,
    string? MutationOperation,
    string? MutationStatus,
    string? MemoryId = null);

public sealed record CoordinatorParticipantMemoryConsentResult(
    bool Recorded,
    bool Ready,
    string Message,
    string ProposalFingerprint,
    IReadOnlyList<string> PendingParticipantReferences);

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

internal sealed record CoordinatorPermissionDecisionReceipt(
    EvidencePermissionMetadata Permission,
    string Source,
    string? ApprovalRequestId);

internal interface ICoordinatorActionExecutionAuthority
{
    TurnIdentity DurableIdentity { get; }

    ValueTask<CapabilityInvocationAuthorization> PrepareExecutionAsync(
        CapabilityInvocationLease lease,
        string callId,
        AIFunctionArguments arguments,
        bool requiresApproval,
        CancellationToken cancellationToken);
}

internal sealed class CoordinatorTurnContext(
    string conversationId,
    string userMessageId,
    string assistantMessageId,
    string originalUserText,
    Action<AssistantStreamChunk> publish,
    ActiveUserSelectionSnapshot? capturedUserSelection = null,
    TurnIdentity? observationIdentity = null,
    ParticipantRosterSnapshot? participantRoster = null)
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
    private readonly Dictionary<string, int> _activeToolInvocationCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toolPlanRetirementRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _capabilityIssueReports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shadowObservedCallIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _shadowObservedOldestFirst = new();
    private readonly Dictionary<string, EvidencePermissionMetadata> _shadowPermissions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoordinatorPermissionDecisionReceipt> _permissionReceipts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _durableOperationIds =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _shadowPermissionOldestFirst = new();
    private readonly Dictionary<string, EvidencePermissionMetadata> _shadowStandingPermissions =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _shadowStandingPermissionOldestFirst = new();
    private readonly Dictionary<string, PendingExplicitShadowTerminalEntry> _pendingExplicitShadowTerminals =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _pendingExplicitShadowOldestFirst = new();
    private readonly AsyncLocal<ActiveToolInvocationFrame?> _activeToolInvocation = new();
    private readonly object _toolPlanSync = new();
    private ICoordinatorActionExecutionAuthority? _actionExecutionAuthority;

    public string ConversationId { get; } = conversationId;

    public string UserMessageId { get; } = userMessageId;

    public string AssistantMessageId { get; } = assistantMessageId;

    public string OriginalUserText { get; } = originalUserText;

    public ActiveUserSelectionSnapshot? CapturedUserSelection { get; } = capturedUserSelection;

    public TurnIdentity? ObservationIdentity { get; } = observationIdentity;

    public ParticipantRosterSnapshot? ParticipantRoster { get; } = participantRoster;

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
        }
    }

    public bool TryGetToolPlan(string callId, out CoordinatorToolPlan? plan)
    {
        lock (_toolPlanSync)
        {
            return _toolPlans.TryGetValue(callId, out plan);
        }
    }

    internal int RememberedToolPlanCount
    {
        get
        {
            lock (_toolPlanSync)
            {
                return _toolPlans.Count;
            }
        }
    }

    // A terminal consumer may finish while the invocation lease is still unwinding.
    // Record that exact retirement and let the last matching lease remove the plan.
    internal bool RequestToolPlanRetirement(string callId)
    {
        if (!IsBoundedShadowCallId(callId))
        {
            return false;
        }

        lock (_toolPlanSync)
        {
            if (!_toolPlans.ContainsKey(callId))
            {
                return false;
            }

            if (_activeToolInvocationCounts.TryGetValue(callId, out var activeCount)
                && activeCount > 0)
            {
                _toolPlanRetirementRequests.Add(callId);
                return true;
            }

            _toolPlanRetirementRequests.Remove(callId);
            _durableOperationIds.Remove(callId);
            return _toolPlans.Remove(callId);
        }
    }

    internal bool TryEnterActiveToolInvocation(
        string callId,
        string toolName,
        out IDisposable? scope)
    {
        scope = null;
        if (!IsBoundedShadowCallId(callId)
            || !IsBoundedShadowToolName(toolName))
        {
            return false;
        }

        CoordinatorToolPlan plan;
        lock (_toolPlanSync)
        {
            if (_actionExecutionAuthority is null
                || !_toolPlans.TryGetValue(callId, out var candidate)
                || _toolPlanRetirementRequests.Contains(callId)
                || !string.Equals(candidate.ToolName, toolName, StringComparison.Ordinal))
            {
                return false;
            }

            plan = candidate;
            _activeToolInvocationCounts[callId] =
                _activeToolInvocationCounts.GetValueOrDefault(callId) + 1;
        }

        var frame = new ActiveToolInvocationFrame(
            plan,
            _activeToolInvocation.Value);
        _activeToolInvocation.Value = frame;
        scope = new ActiveToolInvocationLease(this, frame);
        return true;
    }

    // The fast core-assistant path never registers a CoordinatorToolPlan or an
    // action-execution authority (that machinery is intentionally skipped for
    // speed). A narrow set of read-only tools still needs TryGetActiveToolCallId
    // to resolve so they can look up an auto-approved permission receipt. This
    // entry point makes only that possible, with no plan lookup and no execution
    // authority requirement, so it cannot be mistaken for a durable action grant.
    internal bool TryEnterLightweightToolInvocation(
        string callId,
        string toolName,
        out IDisposable? scope)
    {
        scope = null;
        if (!IsBoundedShadowCallId(callId)
            || !IsBoundedShadowToolName(toolName))
        {
            return false;
        }

        var plan = new CoordinatorToolPlan(
            callId,
            toolName,
            Assessment: string.Empty,
            ActionPlan: string.Empty,
            NextStep: string.Empty,
            SelectionHeadline: string.Empty,
            ResultHeadline: string.Empty,
            TechnicalArguments: string.Empty);
        var frame = new ActiveToolInvocationFrame(
            plan,
            _activeToolInvocation.Value);
        _activeToolInvocation.Value = frame;
        scope = new ActiveToolInvocationLease(this, frame);
        return true;
    }

    internal bool TryGetActiveToolCallId(string toolName, out string? callId)
    {
        var frame = _activeToolInvocation.Value;
        if (frame?.IsActive == true
            && string.Equals(frame.ToolName, toolName, StringComparison.Ordinal))
        {
            callId = frame.CallId;
            return true;
        }

        callId = null;
        return false;
    }

    internal bool TryGetActiveToolPlan(string toolName, out CoordinatorToolPlan? plan)
    {
        var frame = _activeToolInvocation.Value;
        if (frame?.IsActive == true
            && string.Equals(frame.ToolName, toolName, StringComparison.Ordinal))
        {
            plan = frame.Plan;
            return true;
        }

        plan = null;
        return false;
    }

    internal void RegisterDurableOperationId(string callId, string operationId)
    {
        if (!IsBoundedShadowCallId(callId)
            || !IsBoundedValue(operationId, MaximumShadowCallIdCharacters))
        {
            throw new ArgumentException("The durable operation identity is invalid.");
        }

        lock (_toolPlanSync)
        {
            if (_durableOperationIds.TryGetValue(callId, out var existing)
                && !string.Equals(existing, operationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "An exact tool call cannot change its durable operation identity.");
            }

            _durableOperationIds[callId] = operationId;
        }
    }

    internal bool TryGetActiveDurableOperationId(
        string toolName,
        out string? operationId)
    {
        var frame = _activeToolInvocation.Value;
        if (frame?.IsActive == true
            && string.Equals(frame.ToolName, toolName, StringComparison.Ordinal))
        {
            lock (_toolPlanSync)
            {
                return _durableOperationIds.TryGetValue(frame.CallId, out operationId);
            }
        }

        operationId = null;
        return false;
    }

    private void ExitActiveToolInvocation(ActiveToolInvocationFrame frame)
    {
        frame.Deactivate();
        lock (_toolPlanSync)
        {
            if (_activeToolInvocationCounts.TryGetValue(frame.CallId, out var activeCount))
            {
                if (activeCount <= 1)
                {
                    _activeToolInvocationCounts.Remove(frame.CallId);
                    if (_toolPlanRetirementRequests.Remove(frame.CallId))
                    {
                        _toolPlans.Remove(frame.CallId);
                        _durableOperationIds.Remove(frame.CallId);
                    }
                }
                else
                {
                    _activeToolInvocationCounts[frame.CallId] = activeCount - 1;
                }
            }
        }

        if (ReferenceEquals(_activeToolInvocation.Value, frame))
        {
            _activeToolInvocation.Value = frame.Previous;
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
        EvidencePermissionMetadata permission,
        string source = "runtime",
        string? approvalRequestId = null)
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
                _permissionReceipts[callId] = new CoordinatorPermissionDecisionReceipt(
                    permission with { },
                    source,
                    approvalRequestId);
                return;
            }

            if (_shadowPermissions.Count >= MaximumRememberedShadowPermissions)
            {
                var oldest = _shadowPermissionOldestFirst.Dequeue();
                _shadowPermissions.Remove(oldest);
                _permissionReceipts.Remove(oldest);
            }

            _shadowPermissions.Add(callId, permission with { });
            _permissionReceipts.Add(
                callId,
                new CoordinatorPermissionDecisionReceipt(
                    permission with { },
                    source,
                    approvalRequestId));
            _shadowPermissionOldestFirst.Enqueue(callId);
        }
    }

    public bool TryGetPermissionReceipt(
        string callId,
        out CoordinatorPermissionDecisionReceipt? receipt)
    {
        if (!IsBoundedShadowCallId(callId))
        {
            receipt = null;
            return false;
        }

        lock (_toolPlanSync)
        {
            if (_permissionReceipts.TryGetValue(callId, out var stored))
            {
                receipt = stored with { Permission = stored.Permission with { } };
                return true;
            }

            receipt = null;
            return false;
        }
    }

    public void RegisterActionExecutionAuthority(ICoordinatorActionExecutionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_toolPlanSync)
        {
            if (_actionExecutionAuthority is not null
                && !ReferenceEquals(_actionExecutionAuthority, authority))
            {
                throw new InvalidOperationException(
                    "This visible turn already has a durable action execution authority.");
            }

            _actionExecutionAuthority = authority;
        }
    }

    public void ClearActionExecutionAuthority(ICoordinatorActionExecutionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (_toolPlanSync)
        {
            if (ReferenceEquals(_actionExecutionAuthority, authority))
            {
                _actionExecutionAuthority = null;
            }
        }
    }

    public bool TryGetActionExecutionAuthority(
        out ICoordinatorActionExecutionAuthority? authority)
    {
        lock (_toolPlanSync)
        {
            authority = _actionExecutionAuthority;
            return authority is not null;
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

    private sealed class ActiveToolInvocationFrame(
        CoordinatorToolPlan plan,
        ActiveToolInvocationFrame? previous)
    {
        private int _active = 1;

        public CoordinatorToolPlan Plan { get; } = plan;

        public string CallId => Plan.CallId;

        public string ToolName => Plan.ToolName;

        public ActiveToolInvocationFrame? Previous { get; } = previous;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Deactivate() => Interlocked.Exchange(ref _active, 0);
    }

    private sealed class ActiveToolInvocationLease(
        CoordinatorTurnContext owner,
        ActiveToolInvocationFrame frame) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ExitActiveToolInvocation(frame);
            }
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
        AgentToolExecutionReceipt? executionReceipt = null,
        AgentRecoveryPrompt? recoveryPrompt = null) =>
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
            ExecutionReceipt: executionReceipt,
            RecoveryPrompt: recoveryPrompt));

    internal bool HasPublishedResponseText { get; private set; }

    internal void PublishResponseText(string responseText)
    {
        if (string.IsNullOrEmpty(responseText))
        {
            return;
        }

        HasPublishedResponseText = true;
        publish(new AssistantStreamChunk(
            ConversationId,
            UserMessageId,
            AssistantMessageId,
            responseText,
            Ali.Modules.Evidence.EvidenceStatus.Unknown));
    }

    internal void PublishInterimResponse(
        string responseText,
        string? finishReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseText);
        publish(new AssistantStreamChunk(
            ConversationId,
            UserMessageId,
            AssistantMessageId,
            responseText,
            Ali.Modules.Evidence.EvidenceStatus.Unknown,
            finishReason)
        {
            IsInterimPause = true
        });
    }
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
