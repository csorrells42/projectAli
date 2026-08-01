using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Observation;
using Ali.Modules.Runtime;
using Ali.UI.ViewModels;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class RuntimeRequestSafetyTests
{
    [Fact]
    public async Task ActivityObserver_PreservesToolContractAndExactReturnedObject()
    {
        var turn = CreateObservedTurn("call-exact", "exact_tool");
        var expected = new object();
        var observer = new RecordingShadowObserver();
        var inner = AIFunctionFactory.Create(
            (string value) => expected,
            "exact_tool",
            "Return the exact marker.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn, observer);

        var result = await wrapped.InvokeAsync(
            new AIFunctionArguments { ["value"] = "unchanged" },
            TestContext.Current.CancellationToken);

        // AIFunctionFactory may normalize the delegate's return value, but the wrapper must
        // give the caller the exact same normalized object reference that it observed.
        Assert.Same(result, observer.Result);
        Assert.Equal("call-exact", observer.CallId);
        Assert.Equal("returned", observer.Terminal);
        Assert.Equal(inner.Name, wrapped.Name);
        Assert.Equal(inner.Description, wrapped.Description);
        Assert.Equal(inner.JsonSchema.GetRawText(), wrapped.JsonSchema.GetRawText());
        Assert.Equal(
            inner.ReturnJsonSchema?.GetRawText(),
            wrapped.ReturnJsonSchema?.GetRawText());
        Assert.True(turn.WasShadowObserved("call-exact"));
    }

    [Fact]
    public async Task ActivityObserverFailure_PreservesOriginalExceptionInstanceAndStack()
    {
        var turn = CreateObservedTurn("call-throw", "throwing_tool");
        var expected = new InvalidOperationException("original failure");
        var observer = new RecordingShadowObserver(throwFromCallback: true);
        var inner = AIFunctionFactory.Create(
            (Func<object>)(() => throw expected),
            "throwing_tool",
            "Throw the original exception.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn, observer);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await wrapped.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Same(expected, observer.Exception);
        Assert.Equal("threw", observer.Terminal);
        Assert.False(turn.WasShadowObserved("call-throw"));
        Assert.Contains(nameof(ActivityObserverFailure_PreservesOriginalExceptionInstanceAndStack),
            actual.StackTrace,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedOperationCanceledException_IsObservedAsThrownWithFailedReceipt()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = CreateObservedTurn("call-oce", "unexpected_oce", activity.Add);
        var expected = new OperationCanceledException("tool-level cancellation");
        var observer = new RecordingShadowObserver();
        var inner = AIFunctionFactory.Create(
            (Func<object>)(() => throw expected),
            "unexpected_oce",
            "Throw without cancelling the caller token.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn, observer);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await wrapped.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal("threw", observer.Terminal);
        Assert.Same(expected, observer.Exception);
        Assert.DoesNotContain(activity, item =>
            item.ExecutionReceipt?.Outcome == AgentToolExecutionOutcome.Cancelled);
        Assert.Contains(activity, item =>
            item.ExecutionReceipt?.Outcome == AgentToolExecutionOutcome.Failed);
        Assert.DoesNotContain(activity, item =>
            (item.ActivityDetail?.Contains(expected.Message, StringComparison.Ordinal) ?? false)
            || (item.ExecutionReceipt?.Summary.Contains(expected.Message, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task ActivityFormattingAndPublisherFailures_DoNotReplaceSuccessfulResult()
    {
        var turn = CreateObservedTurn(
            "call-cyclic",
            "cyclic_tool",
            _ => throw new InvalidOperationException("activity publisher failed"));
        var cyclic = new CyclicResult();
        cyclic.Self = cyclic;
        Assert.Equal(
            "CyclicResult returned.",
            ActivityReportingAIFunction.DescribeResult(cyclic));
        var observer = new RecordingShadowObserver();
        var inner = AIFunctionFactory.Create(
            () => "completed",
            "cyclic_tool",
            "Return a successful result.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn, observer);

        var result = await wrapped.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Same(result, observer.Result);
        Assert.Equal("returned", observer.Terminal);
    }

    [Fact]
    public async Task ActivityPublisherFailure_DoesNotReplaceOriginalToolException()
    {
        var turn = CreateObservedTurn(
            "call-publisher-throw",
            "publisher_throw_tool",
            _ => throw new ApplicationException("activity publisher failed"));
        var expected = new InvalidOperationException("original tool failure");
        var observer = new RecordingShadowObserver();
        var inner = AIFunctionFactory.Create(
            (Func<object>)(() => throw expected),
            "publisher_throw_tool",
            "Throw the original exception.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn, observer);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await wrapped.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal("threw", observer.Terminal);
    }

    [Fact]
    public async Task HostileTurnAccessor_DoesNotPreventTheRealToolFromRunning()
    {
        var invocationCount = 0;
        var inner = AIFunctionFactory.Create(
            () => ++invocationCount,
            "accessor_failure_tool",
            "Run even when the optional activity context is unavailable.");
        var wrapped = new ActivityReportingAIFunction(
            inner,
            () => throw new InvalidOperationException("ambient turn lookup failed"),
            new RecordingShadowObserver());

        await wrapped.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task ToolFailureActivity_DoesNotExposeRawExceptionMessage()
    {
        const string secretCanary = @"C:\private\customer\secret.txt";
        var activity = new List<AssistantStreamChunk>();
        var turn = CreateObservedTurn("call-redacted", "redacted_tool", activity.Add);
        var expected = new IOException($"Could not write {secretCanary}");
        var inner = AIFunctionFactory.Create(
            (Func<object>)(() => throw expected),
            "redacted_tool",
            "Fail without exposing private exception text.");
        var wrapped = new ActivityReportingAIFunction(
            inner,
            () => turn,
            new RecordingShadowObserver());

        var actual = await Assert.ThrowsAsync<IOException>(async () =>
            await wrapped.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Contains(activity, item =>
            item.ExecutionReceipt?.Outcome == AgentToolExecutionOutcome.Failed);
        Assert.DoesNotContain(activity, item =>
            (item.ActivityDetail?.Contains(secretCanary, StringComparison.Ordinal) ?? false)
            || (item.ExecutionReceipt?.Summary.Contains(secretCanary, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task RequestedCancellation_PreservesTheExactExceptionAndCancelledReceipt()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = CreateObservedTurn("call-cancel", "cancelled_tool", activity.Add);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException("cancelled", cancellation.Token);
        var observer = new RecordingShadowObserver();
        var inner = AIFunctionFactory.Create(
            (Func<object>)(() => throw expected),
            "cancelled_tool",
            "Throw with the requested caller cancellation.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn, observer);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await wrapped.InvokeAsync(new AIFunctionArguments(), cancellation.Token));

        Assert.Same(expected, actual);
        Assert.Equal("cancelled", observer.Terminal);
        Assert.Same(expected, observer.Exception);
        Assert.Contains(activity, item =>
            item.ExecutionReceipt?.Outcome == AgentToolExecutionOutcome.Cancelled);
    }

    [Fact]
    public async Task ActivityHeadline_DoesNotLeakRawToolArgumentsOrResults()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "Build a chess game.",
            activity.Add);
        turn.RegisterToolPlan(new CoordinatorToolPlan(
            "call-write",
            "file_access_write",
            "the chess board is incomplete",
            "write the board and legal move logic",
            "read the final source and verify the requested behavior",
            "Ali sees: the chess board is incomplete -> chose file access write -> plan: write the board and legal move logic",
            "File access write returned -> Ali next: read the final source and verify the requested behavior",
            "{\"content\":\"\\u003Chtml\\u003E...\"}"));
        var inner = AIFunctionFactory.Create(
            (string content) => new { success = true, content },
            "file_access_write",
            "Write a file.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn);

        await wrapped.InvokeAsync(
            new AIFunctionArguments { ["content"] = "<html>chess</html>" },
            TestContext.Current.CancellationToken);

        Assert.Contains(activity, item =>
            item.IsActivity
            && item.Text.Contains("chose file access write", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(activity, item =>
            (item.ActivityDetail?.Contains("Arguments:", StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.ActivityDetail?.Contains("\\u003C", StringComparison.Ordinal) ?? false)
            || (item.ActivityDetail?.Contains("<html>", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    [Fact]
    public void ActivityView_FlattensMultilineTextAndHidesStructuredPayloads()
    {
        var item = new AgentActivityItemViewModel(new AssistantStreamChunk(
            "conversation",
            "user",
            "assistant",
            "Ali chose a tool\nfor the next step",
            Ali.Modules.Evidence.EvidenceStatus.Unknown,
            IsActivity: true,
            ActivityKind: AgentActivityKind.ToolCall,
            ActivityDetail: "{\"content\":\"\\u003Chtml\\u003E\"}"));

        Assert.Equal("Ali chose a tool for the next step", item.Title);
        Assert.Equal("Technical payload omitted from the human activity view.", item.Detail);
    }

    private static CoordinatorTurnContext CreateObservedTurn(
        string callId,
        string toolName,
        Action<AssistantStreamChunk>? publish = null)
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "Run the test tool.",
            publish ?? (_ => { }),
            observationIdentity: new TurnIdentity("user", "conversation", "assistant"));
        turn.RegisterToolPlan(new CoordinatorToolPlan(
            callId,
            toolName,
            "test assessment",
            "test summary",
            "test next step",
            "test selection",
            "test result",
            "{}"));
        return turn;
    }

    private sealed class RecordingShadowObserver(bool throwFromCallback = false) : IShadowToolObserver
    {
        public string? CallId { get; private set; }

        public string? Terminal { get; private set; }

        public object? Result { get; private set; }

        public Exception? Exception { get; private set; }

        public ShadowObservationHealthSnapshot Health => throw new NotSupportedException();

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
            Record(callId, "returned", result, null);
            return true;
        }

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
            Record(callId, "denied", null, null);
            return true;
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
            Record(callId, "threw", null, exception);
            return true;
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
            Record(callId, "cancelled", null, exception);
            return true;
        }

        private void Record(
            string callId,
            string terminal,
            object? result,
            Exception? exception)
        {
            CallId = callId;
            Terminal = terminal;
            Result = result;
            Exception = exception;
            if (throwFromCallback)
            {
                throw new ApplicationException("shadow observer failed");
            }
        }
    }

    private sealed class CyclicResult
    {
        public CyclicResult? Self { get; set; }
    }
}
