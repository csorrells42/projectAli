using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class SpecialistAgentTests
{
    [Fact]
    public void Catalog_RegistersExactlyThreePrivateSpecialists()
    {
        var specialists = AliCapabilityCatalog.Tools
            .Where(tool => tool.Source == "Microsoft Agent Framework agent as tool")
            .ToArray();

        Assert.Equal(3, specialists.Length);
        Assert.Contains(specialists, item => item.Name == AliCapabilityCatalog.ConsultSoftwareEngineerName);
        Assert.Contains(specialists, item => item.Name == AliCapabilityCatalog.ConsultResearcherName);
        Assert.Contains(specialists, item => item.Name == AliCapabilityCatalog.ConsultOfficeSpecialistName);
        Assert.All(specialists, item => Assert.Contains("only user-facing personality", item.Description));
    }

    [Fact]
    public void SpecialistAssignments_KeepEveryToolOnTheOuterCapabilityBoundary()
    {
        var tools = AliCapabilityCatalog.Tools
            .Select(item => (AITool)AIFunctionFactory.Create(
                () => item.Name,
                item.Name,
                item.Description))
            .ToArray();

        var assignments = AliSpecialistAgentFactory.DescribeToolAssignments(tools);

        Assert.Equal(3, assignments.Count);
        Assert.All(assignments.Values, Assert.Empty);
    }

    [Fact]
    public void Instructions_KeepAliInControlOfActionsAndFinalReply()
    {
        var instructions = AliToolCatalog.BuildInstructions("Charlie");

        Assert.Contains("Specialists are synchronous advisers", instructions);
        Assert.Contains("execute any needed approval-requiring tools yourself", instructions);
        Assert.Contains("give the final answer in your own voice", instructions);
        Assert.Contains("pass the user's complete objective", instructions);
        Assert.Contains("cannot substitute for your direct mutation, build, test, run", instructions);
        Assert.Contains("a direct tool provides a concrete blocker", instructions);
        Assert.Contains("Build success with a nonzero warning count is not a warning-free build", instructions);
        Assert.Contains("Never claim tests or unit-test coverage unless a test tool succeeded", instructions);
        Assert.Contains("the task is incomplete until an appropriate write/edit tool succeeds", instructions);
    }

    [Fact]
    public void Factory_CreatesThreeFrameworkAgentFunctionsWithStableNames()
    {
        var nativeTools = AliCapabilityCatalog.Tools
            .Where(item => item.Source != "Microsoft Agent Framework agent as tool")
            .Select(item => (AITool)AIFunctionFactory.Create(
                () => item.Name,
                item.Name,
                item.Description))
            .ToArray();
        var factory = new AliSpecialistAgentFactory(
            new NoOpChatClient(),
            new DevelopmentLocalModelRuntime(),
            () => null);

        var tools = factory.CreateTools(nativeTools).OfType<AIFunctionDeclaration>().ToArray();

        Assert.Equal(3, tools.Length);
        Assert.Equal(
            [
                AliCapabilityCatalog.ConsultOfficeSpecialistName,
                AliCapabilityCatalog.ConsultResearcherName,
                AliCapabilityCatalog.ConsultSoftwareEngineerName
            ],
            tools.Select(item => item.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task AgentAsTool_InvokesPrivateAgentWithoutNestedTools()
    {
        var client = new NoOpChatClient();
        var factory = new AliSpecialistAgentFactory(
            client,
            new DevelopmentLocalModelRuntime(),
            () => null);
        var tool = Assert.Single(
            factory.CreateTools([]).OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.ConsultOfficeSpecialistName);

        var result = await tool.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Draft a concise project brief." },
            TestContext.Current.CancellationToken);

        Assert.Contains("specialist result", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.ObservedToolNames);
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public IReadOnlyList<string> ObservedToolNames { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CaptureTools(options);
            return Task.FromResult(
                new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, "specialist result")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CaptureTools(options);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(MeaiChatRole.Assistant, "specialist result");
        }

        private void CaptureTools(ChatOptions? options)
        {
            ObservedToolNames = (options?.Tools ?? [])
                .OfType<AIFunctionDeclaration>()
                .Select(tool => tool.Name)
                .ToArray();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
