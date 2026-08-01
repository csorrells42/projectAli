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
    public void Scope_disposal_clears_turn_and_nested_turns_are_rejected()
    {
        var lease = new CoordinatorTurnLease();
        var turn = CreateTurn();
        var scope = lease.Enter(turn);

        Assert.Same(turn, lease.Current);
        Assert.Throws<InvalidOperationException>(() => lease.Enter(CreateTurn()));

        scope.Dispose();
        Assert.Null(lease.Current);
        scope.Dispose();
        Assert.Null(lease.Current);
    }

    private static CoordinatorTurnContext CreateTurn() =>
        new(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            "test request",
            _ => { });
}
