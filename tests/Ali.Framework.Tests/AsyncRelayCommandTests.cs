using Ali.UI.ViewModels;

namespace Ali.Framework.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ReentrantExecution_CanCancelAStillRunningSendOperation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        var command = new AsyncRelayCommand(
            async () =>
            {
                if (Interlocked.Increment(ref invocation) == 1)
                {
                    started.SetResult();
                    await release.Task;
                    return;
                }

                stopped.SetResult();
            },
            allowExecutionWhileRunning: true);

        command.Execute(null);
        await started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await stopped.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        release.SetResult();
    }
}
