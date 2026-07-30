using Ali.Modules.Coding.Agents;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class ExternalCodingAgentIntegrationTests
{
    [Fact]
    public async Task Hybrid_RunsOpenHandsThenAiderAgainstOneApprovedProject()
    {
        var calls = new List<string>();
        var openHands = new FakeProvider("OpenHands", calls);
        var aider = new FakeProvider("Aider", calls);
        await WithAgentsAsync(
            ProgrammingAgentModes.Hybrid,
            aider,
            openHands,
            async agents =>
            {
                var result = await agents.ExecuteAsync(
                    "Workspace/sample/sample.csproj",
                    "Add a verified feature.",
                    TestContext.Current.CancellationToken);

                Assert.True(result.Success);
                Assert.Equal(["OpenHands", "Aider"], calls);
                Assert.Equal(2, result.Passes.Count);
                Assert.Contains("direct build, test, diff, or runtime evidence", result.Summary, StringComparison.Ordinal);
            });
    }

    [Theory]
    [InlineData(ProgrammingAgentModes.Aider, "Aider")]
    [InlineData(ProgrammingAgentModes.OpenHands, "OpenHands")]
    public async Task SingleMode_RunsOnlySelectedProvider(string mode, string expected)
    {
        var calls = new List<string>();
        await WithAgentsAsync(
            mode,
            new FakeProvider("Aider", calls),
            new FakeProvider("OpenHands", calls),
            async agents =>
            {
                var result = await agents.ExecuteAsync(
                    "Workspace/sample/sample.csproj",
                    "Implement the objective.",
                    TestContext.Current.CancellationToken);

                Assert.True(result.Success);
                Assert.Equal([expected], calls);
            });
    }

    [Fact]
    public async Task Hybrid_DoesNotPolishFailedOpenHandsPassOrClaimCompletion()
    {
        var calls = new List<string>();
        await WithAgentsAsync(
            ProgrammingAgentModes.Hybrid,
            new FakeProvider("Aider", calls),
            new FakeProvider("OpenHands", calls, succeeds: false),
            async agents =>
            {
                var result = await agents.ExecuteAsync(
                    "Workspace/sample/sample.csproj",
                    "Implement the objective.",
                    TestContext.Current.CancellationToken);

                Assert.False(result.Success);
                Assert.Equal(["OpenHands"], calls);
                Assert.Contains("could not complete", result.Summary, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public void ExecuteTool_UsesSharedPermissionAndMcpCatalogPath()
    {
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingAgentExecuteName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.CodingAgentStatusName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.CodingAgentStatusName,
            AgentPermissionProfile.LockedDown));
        var inventory = AliCapabilityCatalog.ListAvailableTools(new AgentOrchestrationSettings());
        Assert.Contains(inventory.Tools, tool => tool.Name == AliCapabilityCatalog.CodingAgentExecuteName);
        Assert.Contains(inventory.Tools, tool => tool.Name == AliCapabilityCatalog.CodingAgentStatusName);
    }

    private static async Task WithAgentsAsync(
        string mode,
        IExternalCodingAgentProvider aider,
        IExternalCodingAgentProvider openHands,
        Func<AliExternalCodingAgents, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliExternalCodingAgentTests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace", "sample");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            TestContext.Current.CancellationToken);
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))],
                Path.Combine(root, "trash"));
            var access = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(root, activeUsers: null),
                permissions);
            var agents = new AliExternalCodingAgents(
                access,
                () => new AgentOrchestrationSettings { ProgrammingAgentMode = mode },
                RuntimeSettingsStore.GetDefaultOptions,
                root,
                aider: aider,
                openHands: openHands);
            await test(agents);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class FakeProvider(
        string name,
        List<string> calls,
        bool succeeds = true) : IExternalCodingAgentProvider
    {
        public string Name => name;

        public Task<ExternalCodingAgentProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalCodingAgentProviderStatus(Name, true, "test", "ready", Name));

        public Task<ExternalCodingAgentPassResult> ExecuteAsync(
            string projectDirectory,
            string objective,
            CancellationToken cancellationToken)
        {
            calls.Add(Name);
            Assert.True(Directory.Exists(projectDirectory));
            Assert.Contains("objective", objective, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new ExternalCodingAgentPassResult(
                Name,
                succeeds,
                succeeds ? 0 : 1,
                1,
                succeeds ? "completed" : "failed",
                "test output"));
        }
    }
}
