using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class ExternalCodingAgentOwnershipTests
{
    [Fact]
    public async Task ExternalHandoff_PersistsOriginalJobAndBlocksAliMutationUntilCriticApproval()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "Build a complete Gothic checkers game.",
            _ => { });
        string? receivedPath = null;
        string? receivedObjective = null;
        var policy = new AliToolPermissionPolicy(() => turn);
        var external = policy.Apply(
            AIFunctionFactory.Create(
                (string targetPath, string objective) =>
                {
                    receivedPath = targetPath;
                    receivedObjective = objective;
                    return "external pass returned";
                },
                AliCapabilityCatalog.CodingAgentExecuteName,
                "Run the selected external coding agent."),
            requiresApproval: false,
            AliToolTurnRole.ExternalCodingAgent);

        await external.InvokeAsync(
            new AIFunctionArguments
            {
                ["targetPath"] = "Desktop/GothicCheckers",
                ["objective"] = "Build the complete game."
            },
            TestContext.Current.CancellationToken);

        Assert.True(turn.ExternalCodingAgentOwnsTurn);
        Assert.Equal("Desktop/GothicCheckers", receivedPath);
        Assert.Equal("Build the complete game.", receivedObjective);

        await external.InvokeAsync(
            new AIFunctionArguments
            {
                ["targetPath"] = "Desktop/Other",
                ["objective"] = "Just compile it."
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("Desktop/GothicCheckers", receivedPath);
        Assert.Contains("Build the complete game.", receivedObjective, StringComparison.Ordinal);
        Assert.Contains("Just compile it.", receivedObjective, StringComparison.Ordinal);

        var write = policy.Apply(
            AIFunctionFactory.Create(
                (string content) => content,
                AliCapabilityCatalog.FileWriteName,
                "Write a project file."),
            requiresApproval: false,
            AliToolTurnRole.ImplementationMutation);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => write.InvokeAsync(
                new AIFunctionArguments { ["content"] = "Ali must not write this." },
                TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("external coding agent owns", blocked.Message, StringComparison.OrdinalIgnoreCase);

        var nextTurn = new CoordinatorTurnContext(
            "conversation",
            "next-user",
            "next-assistant",
            "Start another request.",
            _ => { });
        Assert.False(nextTurn.ExternalCodingAgentOwnsTurn);
        var nextTurnWrite = new AliToolPermissionPolicy(() => nextTurn).Apply(
            AIFunctionFactory.Create(
                (string content) => content,
                AliCapabilityCatalog.FileWriteName,
                "Write a project file."),
            requiresApproval: false,
            AliToolTurnRole.ImplementationMutation);
        var result = await nextTurnWrite.InvokeAsync(
            new AIFunctionArguments { ["content"] = "A future turn may write." },
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task NativeFileProvider_BecomesReadOnlyImmediatelyAfterHandoff()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliExternalOwnershipTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inner = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", root)],
                Path.Combine(root, "trash"));
            var ownsTurn = false;
            var guarded = new ExternalOwnershipFileStore(inner, () => ownsTurn);

            await guarded.WriteAsync(
                "Workspace/before.txt",
                "allowed",
                TestContext.Current.CancellationToken);
            ownsTurn = true;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => guarded.WriteAsync(
                    "Workspace/blocked.txt",
                    "blocked",
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                "allowed",
                await guarded.ReadAsync(
                    "Workspace/before.txt",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HandoffAndNativeFileGuardShareOwnershipAcrossSuppressedWorkerContexts()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliExternalOwnershipTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var lease = new CoordinatorTurnLease();
            var turn = new CoordinatorTurnContext(
                "conversation",
                "user",
                "assistant",
                "Build the game.",
                _ => { });
            using var scope = lease.Enter(turn);
            var policy = new AliToolPermissionPolicy(() => lease.Current);
            var handoff = policy.Apply(
                AIFunctionFactory.Create(
                    (string targetPath, string objective) => "accepted",
                    AliCapabilityCatalog.CodingAgentExecuteName,
                    "Run the selected external coding agent."),
                requiresApproval: false,
                AliToolTurnRole.ExternalCodingAgent);
            var handoffCompletion = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    try
                    {
                        handoff.InvokeAsync(
                                new AIFunctionArguments
                                {
                                    ["targetPath"] = "Desktop/GothicCheckers",
                                    ["objective"] = "Build the complete game."
                                },
                                TestContext.Current.CancellationToken)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                        handoffCompletion.TrySetResult(null);
                    }
                    catch (Exception exception)
                    {
                        handoffCompletion.TrySetResult(exception);
                    }
                },
                null);

            Assert.Null(
                await handoffCompletion.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));
            Assert.True(turn.ExternalCodingAgentOwnsTurn);

            var inner = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", root)],
                Path.Combine(root, "trash"));
            var guarded = new ExternalOwnershipFileStore(
                inner,
                () => lease.Current?.ExternalCodingAgentOwnsTurn == true);
            var writeCompletion = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    try
                    {
                        guarded.WriteAsync(
                                "Workspace/blocked.txt",
                                "blocked",
                                TestContext.Current.CancellationToken)
                            .GetAwaiter()
                            .GetResult();
                        writeCompletion.TrySetResult(null);
                    }
                    catch (Exception exception)
                    {
                        writeCompletion.TrySetResult(exception);
                    }
                },
                null);

            var blocked = await writeCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            var invalidOperation = Assert.IsType<InvalidOperationException>(blocked);
            Assert.Contains(
                "external coding agent owns",
                invalidOperation.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "blocked.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
