using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class ExternalCodingAgentOwnershipTests
{
    [Fact]
    public async Task ExternalExecutor_IsAnOptionalToolAndDoesNotBlockAliMutation()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "Build a complete Gothic checkers game.",
            _ => { });
        var policy = new AliToolPermissionPolicy(() => turn);
        var externalInvocations = 0;
        var external = policy.Apply(
            AIFunctionFactory.Create(
                (string targetPath, string objective) =>
                {
                    externalInvocations++;
                    return $"{targetPath}:{objective}";
                },
                AliCapabilityCatalog.CodingAgentExecuteName,
                "Run the selected external coding collaborator."),
            requiresApproval: false);

        await external.InvokeAsync(
            new AIFunctionArguments
            {
                ["targetPath"] = "Desktop/GothicCheckers",
                ["objective"] = "Build the complete game."
            },
            TestContext.Current.CancellationToken);

        var nativeMutation = policy.Apply(
            AIFunctionFactory.Create(
                (string content) => content,
                AliCapabilityCatalog.FileWriteName,
                "Write a project file."),
            requiresApproval: false);
        var result = await nativeMutation.InvokeAsync(
            new AIFunctionArguments { ["content"] = "Ali remains the actor." },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, externalInvocations);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task NativeFileStore_RemainsWritableAfterExternalCollaboration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliExternalCollaborationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", root)],
                Path.Combine(root, "trash"));

            await store.WriteAsync(
                "Workspace/after.txt",
                "allowed",
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "allowed",
                await store.ReadAsync(
                    "Workspace/after.txt",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Instructions_NeverTransferImplementationOwnership()
    {
        var instructions = AliToolCatalog.BuildInstructions(
            "Ali",
            new AgentOrchestrationSettings
            {
                ProgrammingAgentMode = ProgrammingAgentModes.Aider,
                AlwaysUseProgrammingAgent = true
            });

        Assert.Contains("optional", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never owns the turn", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transfers implementation ownership", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("implementation-changing tools are unavailable", instructions, StringComparison.OrdinalIgnoreCase);
    }
}
