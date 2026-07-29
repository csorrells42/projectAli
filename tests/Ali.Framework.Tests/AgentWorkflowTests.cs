using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class AgentWorkflowTests
{
    [Fact]
    public void Catalog_RegistersOfficialSequentialAndGroupChatWorkflows()
    {
        var workflows = AliCapabilityCatalog.Tools
            .Where(item => item.Source == "Microsoft Agent Framework workflow")
            .ToArray();

        Assert.Equal(2, workflows.Length);
        Assert.Contains(workflows, item => item.Name == AliCapabilityCatalog.RunResearchArtifactWorkflowName);
        Assert.Contains(workflows, item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);
        Assert.Equal(4, AliAgentWorkflowFactory.ProgrammingMaximumTurns);
    }

    [Fact]
    public async Task SequentialWorkflow_RunsResearchThenArtifactSynthesis()
    {
        var client = new CountingChatClient();
        var tools = CreateWorkflowTools(client);
        var sequential = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunResearchArtifactWorkflowName);

        var result = await sequential.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Research a topic and draft a one-page brief." },
            TestContext.Current.CancellationToken);

        Assert.Contains("workflow response", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task ProgrammingGroupChat_IsSynchronousAndBoundedToFourTurns()
    {
        var client = new CountingChatClient();
        var tools = CreateWorkflowTools(client);
        var groupChat = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);

        var result = await groupChat.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Review a substantial C# implementation plan." },
            TestContext.Current.CancellationToken);

        Assert.Contains("workflow response", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AliAgentWorkflowFactory.ProgrammingMaximumTurns, client.CallCount);
    }

    private static IReadOnlyList<AITool> CreateWorkflowTools(CountingChatClient client)
    {
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        return new AliAgentWorkflowFactory(client, runtime, () => null).CreateTools(team);
    }

    private sealed class CountingChatClient : IChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            return Task.FromResult(new ChatResponse(
                new MeaiChatMessage(MeaiChatRole.Assistant, $"workflow response {call}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(MeaiChatRole.Assistant, $"workflow response {call}");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
