using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Observation;
using Ali.Modules.Permissions;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class ShadowPermissionHookTests
{
    [Fact]
    public async Task PriorPermissionDenial_IsRecordedAsDeniedAndNeverInvokesTheTool()
    {
        var turn = CreateTurn("call-denied", "protected_tool");
        turn.RecordPermissionDecision(AgentToolApprovalChoice.Deny);
        var observer = new RecordingObserver();
        var invocationCount = 0;
        var inner = AIFunctionFactory.Create(
            () =>
            {
                invocationCount++;
                return new { success = true };
            },
            "protected_tool",
            "Protected test operation.");
        var guarded = new AliToolPermissionPolicy(
            () => turn,
            shadowObserver: observer).Apply(inner, requiresApproval: true);

        var result = await guarded.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, invocationCount);
        Assert.Contains("denied", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("call-denied", observer.CallId);
        Assert.Equal("prior-permission-denial", observer.FailureCode);
        Assert.Equal("policy-blocked", observer.Permission?.Decision);
        Assert.Equal("none", observer.Permission?.Scope);
        Assert.True(turn.WasShadowObserved("call-denied"));
    }

    [Fact]
    public async Task HostileObserver_CannotBypassThePriorDenialGuard()
    {
        var turn = CreateTurn("call-hostile", "protected_tool");
        turn.RecordPermissionDecision(AgentToolApprovalChoice.Deny);
        var invocationCount = 0;
        var inner = AIFunctionFactory.Create(
            () => ++invocationCount,
            "protected_tool",
            "Protected test operation.");
        var guarded = new AliToolPermissionPolicy(
            () => turn,
            shadowObserver: new RecordingObserver(throwAfterRecord: true))
            .Apply(inner, requiresApproval: true);

        var result = await guarded.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, invocationCount);
        Assert.Contains("denied", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.False(turn.WasShadowObserved("call-hostile"));
        Assert.True(turn.TryGetPendingExplicitShadowTerminal(
            "call-hostile",
            out var pending));
        Assert.Equal(ExplicitShadowTerminalKind.Denied, pending?.Kind);

        var retryObserver = new RecordingObserver();
        AliAgentHarnessRunner.TryObserveFrameworkResult(
            retryObserver,
            turn,
            new PendingShadowCallTracker(capacity: 2),
            new FunctionResultContent("call-hostile", result));

        Assert.True(turn.WasShadowObserved("call-hostile"));
        Assert.Equal("denied", retryObserver.LastTerminal);
        Assert.False(turn.TryGetPendingExplicitShadowTerminal("call-hostile", out _));
    }

    [Fact]
    public async Task FormerExternalOwnershipPolicy_DoesNotBlockAliMutation()
    {
        var turn = CreateTurn("call-owner", "implementation_write");
        var observer = new RecordingObserver();
        var invocationCount = 0;
        var inner = AIFunctionFactory.Create(
            () => ++invocationCount,
            "implementation_write",
            "Mutate implementation source.");
        var guarded = new AliToolPermissionPolicy(
            () => turn,
            shadowObserver: observer).Apply(
                inner,
                requiresApproval: false);

        var result = await guarded.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, invocationCount);
        Assert.NotNull(result);
        Assert.Equal("returned", observer.LastTerminal);
        Assert.True(turn.WasShadowObserved("call-owner"));
    }

    [Fact]
    public void ExplicitApprovalDenial_IsRetryableUntilTheObserverAcceptsIt()
    {
        var turn = CreateTurn("call-explicit-denial", "protected_tool");
        var functionCall = new FunctionCallContent(
            "call-explicit-denial",
            "protected_tool");
        var rejected = new RecordingObserver(accept: false);
        var now = DateTimeOffset.UtcNow;

        AliAgentHarnessRunner.TryObserveApprovalDenied(
            rejected,
            turn,
            functionCall,
            "protected_tool",
            now,
            now);

        Assert.False(turn.WasShadowObserved("call-explicit-denial"));
        Assert.Equal(1, rejected.InvocationCount);
        Assert.True(turn.TryGetPendingExplicitShadowTerminal(
            "call-explicit-denial",
            out var pending));
        Assert.Equal(ExplicitShadowTerminalKind.Denied, pending?.Kind);

        var accepted = new RecordingObserver();
        AliAgentHarnessRunner.TryObserveFrameworkResult(
            accepted,
            turn,
            new PendingShadowCallTracker(capacity: 2),
            new FunctionResultContent(
                "call-explicit-denial",
                new { success = false, denied = true }));

        Assert.True(turn.WasShadowObserved("call-explicit-denial"));
        Assert.Equal(1, accepted.InvocationCount);
        Assert.Equal("denied", accepted.LastTerminal);
        Assert.False(turn.TryGetPendingExplicitShadowTerminal(
            "call-explicit-denial",
            out _));
    }

    [Fact]
    public void ApprovalCancellation_IsNotMarkedObservedWhenTheObserverRejectsIt()
    {
        var turn = CreateTurn("call-explicit-cancel", "protected_tool");
        var functionCall = new FunctionCallContent(
            "call-explicit-cancel",
            "protected_tool");
        var observer = new RecordingObserver(accept: false);
        var now = DateTimeOffset.UtcNow;

        AliAgentHarnessRunner.TryObserveApprovalCancelled(
            observer,
            turn,
            functionCall,
            "protected_tool",
            new OperationCanceledException("approval cancelled"),
            now,
            now);

        Assert.False(turn.WasShadowObserved("call-explicit-cancel"));
        Assert.Equal(1, observer.InvocationCount);
        Assert.True(turn.TryGetPendingExplicitShadowTerminal(
            "call-explicit-cancel",
            out var pending));
        Assert.Equal(ExplicitShadowTerminalKind.Cancelled, pending?.Kind);
    }

    [Fact]
    public void WrapperObservedFrameworkResult_RemovesItsCompletedFallbackEntry()
    {
        var turn = CreateTurn("call-wrapper", "protected_tool");
        var tracker = new PendingShadowCallTracker(capacity: 2);
        Assert.True(tracker.TryTrack(
            new FunctionCallContent("call-wrapper", "protected_tool"),
            DateTimeOffset.UtcNow));
        Assert.True(turn.MarkShadowObserved("call-wrapper"));
        var observer = new RecordingObserver();

        AliAgentHarnessRunner.TryObserveFrameworkResult(
            observer,
            turn,
            tracker,
            new FunctionResultContent("call-wrapper", new { success = true }));

        Assert.Equal(0, observer.InvocationCount);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void RejectedFrameworkResult_KeepsCorrelationForTypedRetry()
    {
        var turn = CreateTurn("call-retry", "provider_tool");
        var tracker = new PendingShadowCallTracker(capacity: 2);
        Assert.True(tracker.TryTrack(
            new FunctionCallContent("call-retry", "provider_tool"),
            DateTimeOffset.UtcNow));

        AliAgentHarnessRunner.TryObserveFrameworkResult(
            new RecordingObserver(accept: false),
            turn,
            tracker,
            new FunctionResultContent("call-retry", new { success = true }));

        Assert.False(turn.WasShadowObserved("call-retry"));
        Assert.Equal(1, tracker.Count);

        var retry = new RecordingObserver();
        AliAgentHarnessRunner.TryObserveFrameworkResult(
            retry,
            turn,
            tracker,
            new FunctionResultContent("call-retry", new { success = true }));

        Assert.Equal("returned", retry.LastTerminal);
        Assert.True(turn.WasShadowObserved("call-retry"));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void FrameworkOperationCanceledExceptionWithoutExplicitCancellation_IsThrownEvidence()
    {
        var turn = CreateTurn("call-framework-oce", "provider_tool");
        var tracker = new PendingShadowCallTracker(capacity: 2);
        Assert.True(tracker.TryTrack(
            new FunctionCallContent("call-framework-oce", "provider_tool"),
            DateTimeOffset.UtcNow));
        var observer = new RecordingObserver();
        var result = new FunctionResultContent("call-framework-oce", null)
        {
            Exception = new OperationCanceledException("provider stopped unexpectedly")
        };

        AliAgentHarnessRunner.TryObserveFrameworkResult(
            observer,
            turn,
            tracker,
            result);

        Assert.Equal("threw", observer.LastTerminal);
        Assert.True(turn.WasShadowObserved("call-framework-oce"));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void AlreadyObservedApprovalCancellation_DoesNotLeaveAStalePendingTerminal()
    {
        var turn = CreateTurn("call-already-cancelled", "protected_tool");
        Assert.True(turn.MarkShadowObserved("call-already-cancelled"));
        var observer = new RecordingObserver();
        var now = DateTimeOffset.UtcNow;

        AliAgentHarnessRunner.TryObserveApprovalCancelled(
            observer,
            turn,
            new FunctionCallContent("call-already-cancelled", "protected_tool"),
            "protected_tool",
            new OperationCanceledException("approval cancelled"),
            now,
            now);

        Assert.Equal(0, observer.InvocationCount);
        Assert.False(turn.TryGetPendingExplicitShadowTerminal(
            "call-already-cancelled",
            out _));
    }

    private static CoordinatorTurnContext CreateTurn(string callId, string toolName)
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "request",
            static _ => { },
            observationIdentity: new TurnIdentity("user", "conversation", "assistant-message"));
        turn.RegisterToolPlan(new CoordinatorToolPlan(
            callId,
            toolName,
            "assessment",
            "plan",
            "next",
            "selected",
            "returned",
            "{}"));
        return turn;
    }

    private sealed class RecordingObserver(
        bool throwAfterRecord = false,
        bool accept = true) : IShadowToolObserver
    {
        public int InvocationCount { get; private set; }
        public string? LastTerminal { get; private set; }
        public string? CallId { get; private set; }
        public string? FailureCode { get; private set; }
        public EvidencePermissionMetadata? Permission { get; private set; }
        public ShadowObservationHealthSnapshot Health => throw new NotSupportedException();

        public bool TryObserveDenied(
            TurnIdentity? identity,
            string callId,
            string toolName,
            object? arguments,
            string? failureCode,
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc,
            EvidencePermissionMetadata permission)
        {
            InvocationCount++;
            LastTerminal = "denied";
            CallId = callId;
            FailureCode = failureCode;
            Permission = permission;
            if (throwAfterRecord)
            {
                throw new IOException("observer unavailable");
            }

            return accept;
        }

        public bool TryObserveReturned(
            TurnIdentity? identity,
            string callId,
            string toolName,
            object? arguments,
            object? result,
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc,
            EvidencePermissionMetadata permission)
        {
            InvocationCount++;
            LastTerminal = "returned";
            return accept;
        }

        public bool TryObserveThrew(
            TurnIdentity? identity,
            string callId,
            string toolName,
            object? arguments,
            Exception exception,
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc,
            EvidencePermissionMetadata permission)
        {
            InvocationCount++;
            LastTerminal = "threw";
            return accept;
        }

        public bool TryObserveCancelled(
            TurnIdentity? identity,
            string callId,
            string toolName,
            object? arguments,
            OperationCanceledException exception,
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc,
            EvidencePermissionMetadata permission)
        {
            InvocationCount++;
            LastTerminal = "cancelled";
            return accept;
        }
    }
}
