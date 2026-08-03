using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AliFrameworkToolOutcomeSidecarTests
{
    [Fact]
    public void Record_IsIdempotent_AndConsumeIsExactAndOnceOnly()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var key = Key("turn-a", "call-a", AliCapabilityCatalog.FileReadName);

        sidecar.Record(key, AliFrameworkToolOutcomeSignal.Found);
        sidecar.Record(key, AliFrameworkToolOutcomeSignal.Found);

        Assert.Equal(1, sidecar.Count);
        Assert.True(sidecar.TryConsume(key, out var signal));
        Assert.Equal(AliFrameworkToolOutcomeSignal.Found, signal);
        Assert.False(sidecar.TryConsume(key, out _));
        Assert.Equal(0, sidecar.Count);
    }

    [Fact]
    public void ContradictoryDuplicate_CannotPromote()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var key = Key("turn-a", "call-a", AliCapabilityCatalog.FileReplaceName);

        sidecar.Record(key, AliFrameworkToolOutcomeSignal.Found);
        sidecar.Record(key, AliFrameworkToolOutcomeSignal.Completed);

        Assert.True(sidecar.TryConsume(key, out var signal));
        Assert.Equal(AliFrameworkToolOutcomeSignal.Conflicted, signal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureDominatesRegardlessOfArrivalOrder(bool failureFirst)
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var key = Key("turn-a", "call-a", AliCapabilityCatalog.FileWriteName);
        var first = failureFirst
            ? AliFrameworkToolOutcomeSignal.Failed
            : AliFrameworkToolOutcomeSignal.Completed;
        var second = failureFirst
            ? AliFrameworkToolOutcomeSignal.Completed
            : AliFrameworkToolOutcomeSignal.Failed;

        sidecar.Record(key, first);
        sidecar.Record(key, second);

        Assert.True(sidecar.TryConsume(key, out var signal));
        Assert.Equal(AliFrameworkToolOutcomeSignal.Failed, signal);
    }

    [Fact]
    public void CapacityEviction_BecomesMissingInsteadOfRebinding()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar(capacity: 2);
        var first = Key("turn-a", "call-1", AliCapabilityCatalog.FileReadName);
        var second = Key("turn-a", "call-2", AliCapabilityCatalog.FileReadName);
        var third = Key("turn-a", "call-3", AliCapabilityCatalog.FileReadName);

        sidecar.Record(first, AliFrameworkToolOutcomeSignal.Found);
        sidecar.Record(second, AliFrameworkToolOutcomeSignal.Found);
        sidecar.Record(third, AliFrameworkToolOutcomeSignal.Found);

        Assert.False(sidecar.TryConsume(first, out _));
        Assert.True(sidecar.TryConsume(second, out _));
        Assert.True(sidecar.TryConsume(third, out _));
    }

    [Fact]
    public void IdentityCallAndToolMismatches_DoNotConsumeOrAliasTheOriginal()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var original = Key("turn-a", "call-a", AliCapabilityCatalog.FileReadName);
        sidecar.Record(original, AliFrameworkToolOutcomeSignal.Found);

        Assert.False(sidecar.TryConsume(
            Key("turn-b", "call-a", AliCapabilityCatalog.FileReadName),
            out _));
        Assert.False(sidecar.TryConsume(
            Key("turn-a", "call-b", AliCapabilityCatalog.FileReadName),
            out _));
        Assert.False(sidecar.TryConsume(
            Key("turn-a", "call-a", AliCapabilityCatalog.WorkMemoryReadName),
            out _));
        Assert.True(sidecar.TryConsume(original, out var signal));
        Assert.Equal(AliFrameworkToolOutcomeSignal.Found, signal);
    }

    [Fact]
    public void ConcurrentTurns_RemainIsolated()
    {
        const int count = 256;
        var sidecar = new AliFrameworkToolOutcomeSidecar(capacity: count);
        var keys = Enumerable.Range(0, count)
            .Select(index => Key(
                $"turn-{index % 8}",
                $"call-{index}",
                index % 2 == 0
                    ? AliCapabilityCatalog.FileListName
                    : AliCapabilityCatalog.WorkMemoryListName))
            .ToArray();

        Parallel.ForEach(keys, key =>
            sidecar.Record(key, AliFrameworkToolOutcomeSignal.NoMatches));

        Assert.Equal(count, sidecar.Count);
        Parallel.ForEach(keys, key =>
        {
            Assert.True(sidecar.TryConsume(key, out var signal));
            Assert.Equal(AliFrameworkToolOutcomeSignal.NoMatches, signal);
        });
        Assert.Equal(0, sidecar.Count);
    }

    [Fact]
    public void DiscardTurn_RemovesOnlyTheExactDurableIdentity()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var firstTurn = Identity("turn-a");
        var secondTurn = Identity("turn-b");
        var first = new AliFrameworkToolOutcomeKey(
            firstTurn,
            "call-a",
            AliCapabilityCatalog.FileReadName);
        var second = new AliFrameworkToolOutcomeKey(
            secondTurn,
            "call-b",
            AliCapabilityCatalog.FileReadName);
        sidecar.Record(first, AliFrameworkToolOutcomeSignal.Found);
        sidecar.Record(second, AliFrameworkToolOutcomeSignal.Found);

        Assert.Equal(1, sidecar.DiscardTurn(firstTurn));
        Assert.False(sidecar.TryConsume(first, out _));
        Assert.True(sidecar.TryConsume(second, out _));
    }

    [Fact]
    public void ExactInvocationScope_UsesDurableAuthorityWhenActiveUserIdentityIsUnavailable()
    {
        var durableIdentity = Identity("fallback-turn");
        var authority = new TestAuthority(durableIdentity);
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-fallback", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(authority);
        var sidecar = new AliFrameworkToolOutcomeSidecar();

        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-fallback",
            AliCapabilityCatalog.FileReadName,
            out var invocation));
        using (invocation)
        {
            Assert.True(sidecar.TryRecordActive(
                [AliCapabilityCatalog.FileReadName],
                AliFrameworkToolOutcomeSignal.Found));
        }
        Assert.True(sidecar.TryConsume(
            new AliFrameworkToolOutcomeKey(
                durableIdentity,
                "call-fallback",
                AliCapabilityCatalog.FileReadName),
            out var signal));
        Assert.Equal(AliFrameworkToolOutcomeSignal.Found, signal);
    }

    [Fact]
    public void ExactInvocationScope_UsesPreservedDurableIdentityAcrossAResumedVisibleTurn()
    {
        var visibleResumeIdentity = Identity("new-visible-resume");
        var preservedDurableIdentity = Identity("preserved-durable");
        var turn = CreateTurn(visibleResumeIdentity);
        turn.RegisterToolPlan(Plan("call-a", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(new TestAuthority(preservedDurableIdentity));
        var sidecar = new AliFrameworkToolOutcomeSidecar();

        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-a",
            AliCapabilityCatalog.FileReadName,
            out var invocation));
        using (invocation)
        {
            Assert.True(sidecar.TryRecordActive(
                [AliCapabilityCatalog.FileReadName],
                AliFrameworkToolOutcomeSignal.Found));
        }
        Assert.False(sidecar.TryConsume(
            new AliFrameworkToolOutcomeKey(
                visibleResumeIdentity,
                "call-a",
                AliCapabilityCatalog.FileReadName),
            out _));
        Assert.True(sidecar.TryConsume(
            new AliFrameworkToolOutcomeKey(
                preservedDurableIdentity,
                "call-a",
                AliCapabilityCatalog.FileReadName),
            out var signal));
        Assert.Equal(AliFrameworkToolOutcomeSignal.Found, signal);
    }

    [Fact]
    public void DiscardCoordinatorTurn_CleansFallbackIdentityEntriesAfterAuthorityIsCleared()
    {
        var durableIdentity = Identity("fallback-turn");
        var authority = new TestAuthority(durableIdentity);
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-fallback", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(authority);
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-fallback",
            AliCapabilityCatalog.FileReadName,
            out var invocation));
        using (invocation)
        {
            Assert.True(sidecar.TryRecordActive(
                [AliCapabilityCatalog.FileReadName],
                AliFrameworkToolOutcomeSignal.Found));
        }
        turn.ClearActionExecutionAuthority(authority);

        Assert.Equal(1, sidecar.DiscardCoordinatorTurn(turn));
        Assert.Equal(0, sidecar.Count);
    }

    [Fact]
    public void RetainedRegisteredToolPlan_CannotSeedAnOutOfScopeStoreSignal()
    {
        var identity = Identity("retained-plan");
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-a", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(new TestAuthority(identity));
        var sidecar = new AliFrameworkToolOutcomeSidecar();

        Assert.False(sidecar.TryRecordActive(
            [AliCapabilityCatalog.FileReadName],
            AliFrameworkToolOutcomeSignal.Found));
        Assert.Equal(0, sidecar.Count);
    }

    [Fact]
    public void InvocationScope_RejectsCallAndToolMismatches()
    {
        var identity = Identity("mismatch");
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-a", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(new TestAuthority(identity));
        var sidecar = new AliFrameworkToolOutcomeSidecar();

        Assert.False(sidecar.TryEnterInvocation(
            turn,
            "call-b",
            AliCapabilityCatalog.FileReadName,
            out _));
        Assert.False(sidecar.TryEnterInvocation(
            turn,
            "call-a",
            AliCapabilityCatalog.WorkMemoryReadName,
            out _));
        Assert.Equal(0, sidecar.Count);
    }

    [Fact]
    public void NestedInvocationScopes_RestoreOnlyTheExactOuterCall()
    {
        var identity = Identity("nested");
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-outer", AliCapabilityCatalog.FileReadName));
        turn.RegisterToolPlan(Plan("call-inner", AliCapabilityCatalog.WorkMemoryReadName));
        turn.RegisterActionExecutionAuthority(new TestAuthority(identity));
        var sidecar = new AliFrameworkToolOutcomeSidecar();

        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-outer",
            AliCapabilityCatalog.FileReadName,
            out var outer));
        using (outer)
        {
            Assert.True(turn.TryGetActiveToolCallId(
                AliCapabilityCatalog.FileReadName,
                out var outerCallId));
            Assert.Equal("call-outer", outerCallId);

            Assert.True(sidecar.TryEnterInvocation(
                turn,
                "call-inner",
                AliCapabilityCatalog.WorkMemoryReadName,
                out var inner));
            using (inner)
            {
                Assert.True(turn.TryGetActiveToolCallId(
                    AliCapabilityCatalog.WorkMemoryReadName,
                    out var innerCallId));
                Assert.Equal("call-inner", innerCallId);
                Assert.False(turn.TryGetActiveToolCallId(
                    AliCapabilityCatalog.FileReadName,
                    out _));
            }

            Assert.True(turn.TryGetActiveToolCallId(
                AliCapabilityCatalog.FileReadName,
                out outerCallId));
            Assert.Equal("call-outer", outerCallId);
        }

        Assert.False(turn.TryGetActiveToolCallId(
            AliCapabilityCatalog.FileReadName,
            out _));
    }

    [Fact]
    public async Task InterleavedInvocationScopes_KeepSameToolCallIdsIsolated()
    {
        var identity = Identity("interleaved");
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-a", AliCapabilityCatalog.FileReadName));
        turn.RegisterToolPlan(Plan("call-b", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(new TestAuthority(identity));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        using var barrier = new Barrier(participantCount: 2);

        async Task<string?> ObserveAsync(string callId)
        {
            await Task.Yield();
            Assert.True(sidecar.TryEnterInvocation(
                turn,
                callId,
                AliCapabilityCatalog.FileReadName,
                out var invocation));
            using (invocation)
            {
                barrier.SignalAndWait(TestContext.Current.CancellationToken);
                Assert.True(turn.TryGetActiveToolCallId(
                    AliCapabilityCatalog.FileReadName,
                    out var observed));
                Assert.True(sidecar.TryRecordActive(
                    [AliCapabilityCatalog.FileReadName],
                    AliFrameworkToolOutcomeSignal.Found));
                return observed;
            }
        }

        var observed = await Task.WhenAll(
            Task.Run(() => ObserveAsync("call-a")),
            Task.Run(() => ObserveAsync("call-b")));

        Assert.Equal(["call-a", "call-b"], observed.Order(StringComparer.Ordinal));
        Assert.True(sidecar.TryConsume(
            new AliFrameworkToolOutcomeKey(
                identity,
                "call-a",
                AliCapabilityCatalog.FileReadName),
            out _));
        Assert.True(sidecar.TryConsume(
            new AliFrameworkToolOutcomeKey(
                identity,
                "call-b",
                AliCapabilityCatalog.FileReadName),
            out _));
    }

    [Fact]
    public void DisposedInvocationScope_CannotAcceptLateSignals()
    {
        var identity = Identity("disposed");
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-a", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(new TestAuthority(identity));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-a",
            AliCapabilityCatalog.FileReadName,
            out var invocation));

        invocation!.Dispose();

        Assert.False(sidecar.TryRecordActive(
            [AliCapabilityCatalog.FileReadName],
            AliFrameworkToolOutcomeSignal.Found));
        Assert.Equal(0, sidecar.Count);
    }

    [Fact]
    public async Task DisposedInvocationScope_DeactivatesCapturedBackgroundContext()
    {
        var identity = Identity("background-late");
        var turn = CreateTurn(observationIdentity: null);
        turn.RegisterToolPlan(Plan("call-a", AliCapabilityCatalog.FileReadName));
        turn.RegisterActionExecutionAuthority(new TestAuthority(identity));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-a",
            AliCapabilityCatalog.FileReadName,
            out var invocation));
        Task<(bool FoundCall, bool Recorded)> lateObservation;
        using (invocation)
        {
            lateObservation = Task.Run(async () =>
            {
                await release.Task;
                var foundCall = turn.TryGetActiveToolCallId(
                    AliCapabilityCatalog.FileReadName,
                    out _);
                var recorded = sidecar.TryRecordActive(
                    [AliCapabilityCatalog.FileReadName],
                    AliFrameworkToolOutcomeSignal.Found);
                return (foundCall, recorded);
            });
        }

        release.SetResult();
        var late = await lateObservation;

        Assert.False(late.FoundCall);
        Assert.False(late.Recorded);
        Assert.Equal(0, sidecar.Count);
    }

    [Fact]
    public void InvalidEnumValue_IsRejectedBeforeStateChanges()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();

        Assert.Throws<ArgumentOutOfRangeException>(() => sidecar.Record(
            Key("turn-a", "call-a", AliCapabilityCatalog.FileReadName),
            (AliFrameworkToolOutcomeSignal)999));
        Assert.Equal(0, sidecar.Count);
    }

    private static CoordinatorTurnContext CreateTurn(TurnIdentity? observationIdentity) =>
        new(
            "conversation",
            "user-message",
            "assistant-message",
            "request",
            _ => { },
            capturedUserSelection: null,
            observationIdentity);

    private static CoordinatorToolPlan Plan(string callId, string toolName) =>
        new(callId, toolName, "assessment", "plan", "next", "selection", "result", "{}");

    private static AliFrameworkToolOutcomeKey Key(
        string turn,
        string callId,
        string toolName) =>
        new(Identity(turn), callId, toolName);

    private static TurnIdentity Identity(string suffix) =>
        new($"user-{suffix}", $"conversation-{suffix}", $"assistant-{suffix}");

    private sealed class TestAuthority(TurnIdentity durableIdentity) :
        ICoordinatorActionExecutionAuthority
    {
        public TurnIdentity DurableIdentity { get; } = durableIdentity;

        public ValueTask<CapabilityInvocationAuthorization> PrepareExecutionAsync(
            CapabilityInvocationLease lease,
            string callId,
            AIFunctionArguments arguments,
            bool requiresApproval,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
