using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Permissions;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class CoordinatorTurnObservationContextTests
{
    [Fact]
    public void Context_PreservesCapturedUserAndObservationIdentityWithoutChangingLegacyConstruction()
    {
        var legacy = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "request",
            static _ => { });
        Assert.Null(legacy.CapturedUserSelection);
        Assert.Null(legacy.ObservationIdentity);

        var selected = new ActiveUser("user-a", "Alice", false, "explicit-selection");
        var snapshot = ActiveUserSelectionSnapshot.Resolved(selected);
        var identity = new TurnIdentity("user-a", "conversation", "assistant-message");
        var captured = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "request",
            static _ => { },
            snapshot,
            identity);

        Assert.Same(snapshot, captured.CapturedUserSelection);
        Assert.Same(identity, captured.ObservationIdentity);
        var capturedSelection = Assert.IsType<ActiveUserSelectionSnapshot>(captured.CapturedUserSelection);
        Assert.NotSame(selected, capturedSelection.SelectedUser);
    }

    [Fact]
    public void InterimResponse_IsExplicitlyMarkedAsAPausedDurableTurn()
    {
        AssistantStreamChunk? published = null;
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "request",
            chunk => published = chunk);

        turn.PublishInterimResponse("Waiting for your input.", "awaiting-user");

        var response = Assert.IsType<AssistantStreamChunk>(published);
        Assert.True(response.IsInterimPause);
        Assert.False(response.IsActivity);
        Assert.Equal("Waiting for your input.", response.Text);
    }

    [Fact]
    public void ShadowObservationTracking_IsExactOrdinalAndIdempotent()
    {
        var turn = CreateTurn();

        Assert.True(turn.MarkShadowObserved("call-A"));
        Assert.False(turn.MarkShadowObserved("call-A"));
        Assert.True(turn.WasShadowObserved("call-A"));
        Assert.False(turn.WasShadowObserved("call-a"));
        Assert.False(turn.MarkShadowObserved(" "));
        Assert.False(turn.WasShadowObserved(""));
    }

    [Fact]
    public void ActiveToolCorrelation_RequiresAnExactInvocationScope()
    {
        var turn = CreateTurn();
        var registeredPlan = CreateToolPlan("call-Exact", "file_access_read");
        turn.RegisterToolPlan(registeredPlan);
        turn.RegisterActionExecutionAuthority(new TestAuthority());

        Assert.False(turn.TryGetActiveToolCallId("file_access_read", out var callId));
        Assert.Null(callId);
        Assert.False(turn.TryGetActiveToolPlan("file_access_read", out var inactivePlan));
        Assert.Null(inactivePlan);
        Assert.True(turn.TryEnterActiveToolInvocation(
            "call-Exact",
            "file_access_read",
            out var invocation));
        using (invocation)
        {
            Assert.True(turn.TryGetActiveToolCallId("file_access_read", out callId));
            Assert.Equal("call-Exact", callId);
            Assert.True(turn.TryGetActiveToolPlan("file_access_read", out var activePlan));
            Assert.Same(registeredPlan, activePlan);
            Assert.False(turn.TryGetActiveToolCallId("FILE_ACCESS_READ", out callId));
            Assert.Null(callId);
            Assert.False(turn.TryGetActiveToolPlan("FILE_ACCESS_READ", out activePlan));
            Assert.Null(activePlan);
        }

        Assert.False(turn.TryGetActiveToolCallId("file_access_read", out callId));
    }

    [Fact]
    public void ToolPlanLifecycle_RetiresLongTurnCompletionsWithoutEvictingAnInFlightPlan()
    {
        var turn = CreateTurn();
        var inFlightPlan = CreateToolPlan("call-in-flight", "file_access_read");
        turn.RegisterToolPlan(inFlightPlan);
        turn.RegisterActionExecutionAuthority(new TestAuthority());
        Assert.True(turn.TryEnterActiveToolInvocation(
            inFlightPlan.CallId,
            inFlightPlan.ToolName,
            out var invocation));

        using (invocation)
        {
            // A terminal notification racing the invocation lease schedules exact cleanup,
            // but the plan remains available until the in-flight consumer releases it.
            Assert.True(turn.RequestToolPlanRetirement(inFlightPlan.CallId));
            Assert.True(turn.TryGetToolPlan(inFlightPlan.CallId, out var rememberedInFlight));
            Assert.Same(inFlightPlan, rememberedInFlight);
            Assert.False(turn.TryEnterActiveToolInvocation(
                inFlightPlan.CallId,
                inFlightPlan.ToolName,
                out var lateInvocation));
            Assert.Null(lateInvocation);

            for (var index = 0;
                 index <= CoordinatorTurnContext.MaximumRememberedShadowTerminals;
                 index++)
            {
                var completed = CreateToolPlan($"call-completed-{index}", "completed-tool");
                turn.RegisterToolPlan(completed);
                Assert.True(turn.RequestToolPlanRetirement(completed.CallId));
            }

            Assert.Equal(1, turn.RememberedToolPlanCount);
            Assert.True(turn.TryGetActiveToolPlan(inFlightPlan.ToolName, out var activePlan));
            Assert.Same(inFlightPlan, activePlan);
        }

        Assert.Equal(0, turn.RememberedToolPlanCount);
        Assert.False(turn.TryGetActiveToolPlan(inFlightPlan.ToolName, out _));
        Assert.False(turn.TryGetToolPlan(inFlightPlan.CallId, out _));
        Assert.False(turn.RequestToolPlanRetirement("call-not-registered"));
    }

    [Fact]
    public async Task ShadowObservationTracking_IsThreadSafe()
    {
        var turn = CreateTurn();
        var calls = Enumerable.Range(0, 128).Select(index => $"call-{index}").ToArray();

        await Task.WhenAll(calls.Select(callId => Task.Run(() =>
            Assert.True(turn.MarkShadowObserved(callId)))));

        Assert.All(calls, callId => Assert.True(turn.WasShadowObserved(callId)));
    }

    [Fact]
    public void ShadowObservationTracking_EvictsTheOldestTerminalAtItsBound()
    {
        var turn = CreateTurn();
        for (var index = 0; index <= CoordinatorTurnContext.MaximumRememberedShadowTerminals; index++)
        {
            Assert.True(turn.MarkShadowObserved($"call-{index}"));
        }

        Assert.False(turn.WasShadowObserved("call-0"));
        Assert.True(turn.WasShadowObserved("call-1"));
        Assert.True(turn.WasShadowObserved(
            $"call-{CoordinatorTurnContext.MaximumRememberedShadowTerminals}"));
    }

    [Fact]
    public void ShadowPermissionTracking_IsImmutableExactAndDeterministicallyBounded()
    {
        var turn = CreateTurn();
        var supplied = new EvidencePermissionMetadata("approved-once", "once");
        turn.RecordShadowPermission("call-exact", supplied);

        Assert.True(turn.TryGetShadowPermission("call-exact", out var captured));
        Assert.NotNull(captured);
        Assert.NotSame(supplied, captured);
        Assert.Equal("approved-once", captured.Decision);
        Assert.Equal("once", captured.Scope);
        Assert.False(turn.TryGetShadowPermission("CALL-EXACT", out _));

        for (var index = 0; index <= CoordinatorTurnContext.MaximumRememberedShadowPermissions; index++)
        {
            turn.RecordShadowPermission(
                $"bounded-{index}",
                new EvidencePermissionMetadata("unknown", "unknown"));
        }

        Assert.False(turn.TryGetShadowPermission("call-exact", out _));
        Assert.False(turn.TryGetShadowPermission("bounded-0", out _));
        Assert.True(turn.TryGetShadowPermission("bounded-1", out _));
        Assert.True(turn.TryGetShadowPermission(
            $"bounded-{CoordinatorTurnContext.MaximumRememberedShadowPermissions}",
            out _));
    }

    [Fact]
    public void PendingTracker_CapturesApprovalWrappedCallMetadataWithoutPayload()
    {
        const string secretCanary = "pending-must-not-retain-raw-arguments";
        var observedAt = DateTimeOffset.UtcNow;
        var functionCall = new FunctionCallContent(
            "call-approved",
            "file_access_write",
            new Dictionary<string, object?> { ["content"] = secretCanary });
        var approval = new ToolApprovalRequestContent("approval-request", functionCall);
        var tracker = new PendingShadowCallTracker(capacity: 2);

        Assert.True(tracker.TryTrack(approval.ToolCall, observedAt));
        Assert.True(tracker.TryTake("call-approved", out var pending));

        Assert.NotNull(pending);
        Assert.Equal("file_access_write", pending.ToolName);
        Assert.Equal(observedAt, pending.StartedAtUtc);
        Assert.DoesNotContain(
            secretCanary,
            System.Text.Json.JsonSerializer.Serialize(pending),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(PendingShadowCall).GetProperties(),
            property => property.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Result", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Exception", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void StandingAndSavedApprovals_ReturnOneCallResponsesWithoutSessionCaching()
    {
        var request = new ToolApprovalRequestContent(
            "approval-one-call",
            new FunctionCallContent("call-one-call", "protected-tool"));

        var response = AliAgentHarnessRunner.CreateOneCallApprovalResponse(
            request,
            "Approved for this call only.");

        Assert.IsType<ToolApprovalResponseContent>(response);
        Assert.IsNotType<AlwaysApproveToolApprovalResponseContent>(response);
    }

    [Fact]
    public void PendingStandingPermission_CapturesExactArgumentsAndClearsAtTurnBoundary()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["path"] = "first.txt",
            ["count"] = 1
        };
        var call = new FunctionCallContent(
            "call-standing-snapshot",
            "protected-tool",
            arguments);
        var tracker = new PendingStandingPermissionTracker();

        Assert.True(tracker.TryQueue(
            new ActiveUser("user-a", "Alice", false, "test"),
            AgentToolApprovalChoice.AlwaysAllowArguments,
            call,
            out var reason), reason);
        arguments["path"] = "mutated-after-approval.txt";
        var completion = tracker.Complete(
            new FunctionResultContent(call.CallId, new { success = true }));
        var pending = Assert.IsType<PendingStandingPermission>(completion.Permission);

        Assert.Equal(PendingStandingPermissionCompletionStatus.ReadyToSave, completion.Status);
        Assert.Equal("first.txt", Assert.IsType<System.Text.Json.JsonElement>(pending.Arguments["path"]).GetString());
        Assert.Equal(0, tracker.Count);

        Assert.True(tracker.TryQueue(
            new ActiveUser("user-a", "Alice", false, "test"),
            AgentToolApprovalChoice.AlwaysAllowTool,
            new FunctionCallContent("call-clear", "protected-tool"),
            out reason), reason);
        tracker.Clear();
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void PendingTracker_EvictsTheOldestIncompleteCallAtItsBound()
    {
        var tracker = new PendingShadowCallTracker(capacity: 2);
        var observedAt = DateTimeOffset.UtcNow;
        Assert.True(tracker.TryTrack(new FunctionCallContent("call-1", "tool-1"), observedAt));
        Assert.True(tracker.TryTrack(new FunctionCallContent("call-2", "tool-2"), observedAt));
        Assert.True(tracker.TryTrack(new FunctionCallContent("call-3", "tool-3"), observedAt));

        Assert.Equal(2, tracker.Count);
        Assert.False(tracker.TryTake("call-1", out _));
        Assert.True(tracker.TryTake("call-2", out _));
        Assert.True(tracker.TryTake("call-3", out _));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void ShadowCorrelation_RejectsOversizedCallAndToolMetadata()
    {
        var turn = CreateTurn();
        var overlongCallId = new string('c', CoordinatorTurnContext.MaximumShadowCallIdCharacters + 1);
        var overlongToolName = new string('t', CoordinatorTurnContext.MaximumShadowToolNameCharacters + 1);
        var permission = new EvidencePermissionMetadata("unknown", "unknown");
        var now = DateTimeOffset.UtcNow;

        Assert.False(turn.MarkShadowObserved(overlongCallId));
        turn.RecordShadowPermission(overlongCallId, permission);
        Assert.False(turn.TryGetShadowPermission(overlongCallId, out _));
        Assert.False(turn.RecordPendingExplicitShadowTerminal(
            new PendingExplicitShadowTerminal(
                "call",
                overlongToolName,
                ExplicitShadowTerminalKind.Denied,
                "user-denied",
                now,
                now,
                permission)));

        var tracker = new PendingShadowCallTracker(capacity: 2);
        Assert.False(tracker.TryTrack(
            new FunctionCallContent(overlongCallId, "tool"),
            now));
        Assert.False(tracker.TryTrack(
            new FunctionCallContent("call", overlongToolName),
            now));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void PendingExplicitTerminals_EvictOldestAndClearOnlyTheRequestedEntry()
    {
        var turn = CreateTurn();
        var now = DateTimeOffset.UtcNow;
        var permission = new EvidencePermissionMetadata("denied", "none");
        for (var index = 0;
             index <= CoordinatorTurnContext.MaximumRememberedPendingExplicitShadowTerminals;
             index++)
        {
            Assert.True(turn.RecordPendingExplicitShadowTerminal(
                new PendingExplicitShadowTerminal(
                    $"pending-{index}",
                    "protected-tool",
                    ExplicitShadowTerminalKind.Denied,
                    "user-denied",
                    now,
                    now,
                    permission)));
        }

        Assert.False(turn.TryGetPendingExplicitShadowTerminal("pending-0", out _));
        Assert.True(turn.TryGetPendingExplicitShadowTerminal("pending-1", out _));
        var newest = $"pending-{CoordinatorTurnContext.MaximumRememberedPendingExplicitShadowTerminals}";
        Assert.True(turn.TryGetPendingExplicitShadowTerminal(newest, out _));
        Assert.True(turn.ClearPendingExplicitShadowTerminal("pending-1"));
        Assert.False(turn.TryGetPendingExplicitShadowTerminal("pending-1", out _));
        Assert.True(turn.TryGetPendingExplicitShadowTerminal(newest, out _));
    }

    [Theory]
    [InlineData(AgentToolApprovalChoice.Deny, "denied", "none")]
    [InlineData(AgentToolApprovalChoice.AllowOnce, "approved-once", "once")]
    [InlineData(AgentToolApprovalChoice.AlwaysAllowArguments, "approved-standing", "exact-arguments")]
    [InlineData(AgentToolApprovalChoice.AlwaysAllowTool, "approved-standing", "tool")]
    public void InteractivePermissionCorrelation_UsesTheExactCallIdAndChoice(
        AgentToolApprovalChoice choice,
        string expectedDecision,
        string expectedScope)
    {
        var turn = CreateTurn();
        var functionCall = new FunctionCallContent("call-permission", "protected-tool");

        AliAgentHarnessRunner.RecordInteractiveShadowPermission(turn, functionCall, choice);

        Assert.True(turn.TryGetShadowPermission("call-permission", out var permission));
        Assert.NotNull(permission);
        Assert.Equal(expectedDecision, permission.Decision);
        Assert.Equal(expectedScope, permission.Scope);
        if (choice is AgentToolApprovalChoice.AlwaysAllowArguments
            or AgentToolApprovalChoice.AlwaysAllowTool)
        {
            Assert.True(turn.TryGetShadowStandingPermission(
                "protected-tool",
                out var standing));
            Assert.Equal(expectedDecision, standing?.Decision);
            Assert.Equal(expectedScope, standing?.Scope);
        }
        else
        {
            Assert.False(turn.TryGetShadowStandingPermission(
                "protected-tool",
                out _));
        }
    }

    [Theory]
    [InlineData(AgentToolPermissionScope.ExactArguments, "exact-arguments")]
    [InlineData(AgentToolPermissionScope.Tool, "tool")]
    public void StandingPermissionCorrelation_UsesTheExactSavedScope(
        AgentToolPermissionScope scope,
        string expectedScope)
    {
        var turn = CreateTurn();
        var functionCall = new FunctionCallContent("call-standing", "protected-tool");

        AliAgentHarnessRunner.RecordStandingShadowPermission(turn, functionCall, scope);

        Assert.True(turn.TryGetShadowPermission("call-standing", out var permission));
        Assert.NotNull(permission);
        Assert.Equal("approved-standing", permission.Decision);
        Assert.Equal(expectedScope, permission.Scope);
        Assert.True(turn.TryGetShadowStandingPermission(
            "protected-tool",
            out var standing));
        Assert.Equal("approved-standing", standing?.Decision);
        Assert.Equal(expectedScope, standing?.Scope);
    }

    [Fact]
    public void LegacySessionDefaultSnapshot_CapturesAValidIdentity()
    {
        var selection = ActiveUserSelectionSnapshot.Resolved(
            new ActiveUser(" alice ", "Alice", false, "explicit-selection"));
        var session = new FixedSelectionSession(selection);

        var (captured, identity) = AliToolCoordinator.CaptureTurnAdmissionIdentity(
            session,
            "conversation",
            "assistant-message");

        var capturedSelection = Assert.IsType<ActiveUserSelectionSnapshot>(captured);
        Assert.True(capturedSelection.IsResolved);
        Assert.Equal("alice", capturedSelection.SelectedUser?.StableId);
        Assert.Equal(
            new TurnIdentity("alice", "conversation", "assistant-message"),
            identity);
    }

    [Fact]
    public void ResolvedSelection_RejectsAnInvalidStableUserId()
    {
        Assert.Throws<ArgumentException>(() => ActiveUserSelectionSnapshot.Resolved(
            new ActiveUser(" ", "Alice", false, "explicit-selection")));
    }

    private static CoordinatorTurnContext CreateTurn() => new(
        "conversation",
        "user-message",
        "assistant-message",
        "request",
        static _ => { });

    private static CoordinatorToolPlan CreateToolPlan(string callId, string toolName) => new(
        callId,
        toolName,
        "assessment",
        "plan",
        "next",
        "selected",
        "returned",
        "{}");

    private sealed class FixedSelectionSession(ActiveUserSelectionSnapshot selection)
        : IActiveUserSession
    {
        public ActiveUser Current => selection.SelectedUser!;

        public IReadOnlyList<ActiveUser> AvailableUsers => selection.SelectedUser is { } user
            ? [user]
            : [];

        public bool RequiresSelection => selection.RequiresSelection;

        public event EventHandler<ActiveUser>? Changed
        {
            add { }
            remove { }
        }

        public ActiveUser Select(string stableId) => Current;

        public void Refresh()
        {
        }
    }

    private sealed class TestAuthority : ICoordinatorActionExecutionAuthority
    {
        public TurnIdentity DurableIdentity { get; } =
            new("user", "conversation", "assistant-message");

        public ValueTask<CapabilityInvocationAuthorization> PrepareExecutionAsync(
            CapabilityInvocationLease lease,
            string callId,
            AIFunctionArguments arguments,
            bool requiresApproval,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
