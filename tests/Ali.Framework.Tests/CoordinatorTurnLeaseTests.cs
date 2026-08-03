using System.Threading;
using Ali.Modules.Coordinator;

namespace Ali.Framework.Tests;

public sealed class CoordinatorTurnLeaseTests
{
    [Fact]
    public async Task Active_turn_is_visible_when_execution_context_flow_is_suppressed()
    {
        var lease = new CoordinatorTurnLease();
        var turn = CreateTurn();
        using var scope = lease.Enter(turn);
        var completion = new TaskCompletionSource<CoordinatorTurnContext?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ThreadPool.UnsafeQueueUserWorkItem(
            _ => completion.TrySetResult(lease.Current),
            null);

        Assert.Same(
            turn,
            await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Nested_turns_are_flow_isolated_and_scope_disposal_restores_the_prior_turn()
    {
        var lease = new CoordinatorTurnLease();
        var turn = CreateTurn();
        var scope = lease.Enter(turn);

        Assert.Same(turn, lease.Current);
        var nestedTurn = CreateTurn();
        var nested = lease.Enter(nestedTurn);
        Assert.Same(nestedTurn, lease.Current);

        nested.Dispose();
        Assert.Same(turn, lease.Current);

        scope.Dispose();
        Assert.Null(lease.Current);
        scope.Dispose();
        Assert.Null(lease.Current);
    }

    [Fact]
    public async Task Suppressed_flow_fails_closed_when_multiple_turns_are_active()
    {
        var lease = new CoordinatorTurnLease();
        using var first = lease.Enter(CreateTurn());
        using var second = lease.Enter(CreateTurn());
        var completion = new TaskCompletionSource<CoordinatorTurnContext?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ThreadPool.UnsafeQueueUserWorkItem(
            _ => completion.TrySetResult(lease.Current),
            null);

        Assert.Null(await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
    }

    private static CoordinatorTurnContext CreateTurn() =>
        new(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            "test request",
            _ => { });
}
