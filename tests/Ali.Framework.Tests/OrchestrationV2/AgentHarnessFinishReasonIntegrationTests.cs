using System.Runtime.CompilerServices;
using System.Text.Json;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Coding;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Mcp;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Ali.Modules.Storage;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AgentHarnessFinishReasonIntegrationTests
{
    [Fact]
    public async Task NativeProtocolToolCalls_FullHarnessPublishesExactAnswerWithStopFinishReason()
    {
        using var fixture = new HarnessFixture();
        var profile = PlanningTestModelProfile.GptOss65K();
        var runtime = new NativeProtocolRuntime(profile, "Native harness answer.");
        var assistant = AssistantProfile.Create("Ali harness finish reason test");
        var mcpClients = new McpClientManager(fixture.Root);
        await using var codingModule = new AliCodingModule(fixture.FileAccess);
        var source = new EmptySourceRetriever();
        var catalog = new AliToolCatalog(
            source,
            source,
            new McpWebResearchClient(() => new WebSourceBackendSettings()),
            new FileMemoryStore(fixture.Root),
            new FileReminderStore(fixture.Root),
            assistant,
            mcpClients,
            fixture.Permissions,
            fixture.FileAccess,
            codingModule,
            () => fixture.CurrentTurn,
            orchestrationSettings: () => new AgentOrchestrationSettings());
        using var runner = new AliAgentHarnessRunner(
            runtime,
            runtime,
            assistant,
            catalog,
            mcpClients,
            fixture.Permissions,
            fixture.FileAccess,
            new AliAgentWorkMemory(fixture.Root),
            activeUsers: null,
            turnAccessor: () => fixture.CurrentTurn,
            checkpointPath: Path.Combine(fixture.Root, "WorkflowCheckpoints"),
            orchestrationSettings: () => new AgentOrchestrationSettings());
        var identity = new TurnIdentity("user", "conversation", "assistant-message");
        var turn = new CoordinatorTurnContext(
            identity.ConversationId,
            "user-message",
            identity.AssistantMessageId,
            "hello",
            publish: _ => { },
            observationIdentity: identity);
        fixture.CurrentTurn = turn;
        FinalAnswerPublication? published = null;

        var result = await runner.RunAsync(
            turn,
            "hello",
            Array.Empty<Ali.Modules.Runtime.ChatMessage>(),
            Array.Empty<ChatAttachment>(),
            (publication, _) =>
            {
                published = publication;
                return ValueTask.FromResult(
                    FinalAnswerPublicationAcknowledgment.Accepted(publication));
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.WroteAnswer);
        Assert.False(result.Paused);
        Assert.Null(result.ResumeIdentity);
        Assert.Equal(ChatFinishReason.Stop.ToString(), result.FinishReason);
        Assert.NotNull(published);
        Assert.Equal("Native harness answer.", published.AnswerText);
        Assert.Equal(ChatFinishReason.Stop.ToString(), published.FinishReason);
        Assert.Equal(1, runtime.RequestCount);
    }

    private sealed class HarnessFixture : IDisposable
    {
        public HarnessFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "ProjectAli.AgentHarnessFinishReasonTests",
                Guid.NewGuid().ToString("N"));
            var workspace = Directory.CreateDirectory(Path.Combine(Root, "Workspace")).FullName;
            Permissions = new AgentToolPermissionStore(Root);
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", workspace)],
                Path.Combine(Root, "RecoverableTrash"));
            FileAccess = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(Root, activeUsers: null),
                Permissions);
        }

        internal string Root { get; }

        internal AgentToolPermissionStore Permissions { get; }

        internal AliWorkstationFileAccess FileAccess { get; }

        internal CoordinatorTurnContext? CurrentTurn { get; set; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class EmptySourceRetriever : ISourceRetriever
    {
        public Task<SourceRetrievalResult> RetrieveAsync(
            string userText,
            CancellationToken cancellationToken) =>
            Task.FromResult(SourceRetrievalResult.Empty);
    }

    private sealed class NativeProtocolRuntime(
        ModelProfile profile,
        string answer) : ILocalModelRuntime, IChatClient, IBoundModelDispatchSource
    {
        private int _requestCount;

        public ModelProfile ActiveProfile { get; } = profile;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        BoundModelDispatchSnapshot IBoundModelDispatchSource.CaptureBoundModelDispatch() =>
            new(
                this,
                ActiveProfile,
                new BoundRuntimeBindingMaterial(
                    "test-runtime",
                    "native-protocol-test-client",
                    ActiveProfile.RuntimeKind,
                    ActiveProfile.RuntimeLocation,
                    ActiveProfile.RuntimeEndpoint),
                new BoundModelBindingMaterial(
                    ActiveProfile.ProfileId,
                    ActiveProfile.PackageId,
                    ActiveProfile.Family,
                    ActiveProfile.Size,
                    ActiveProfile.Quantization,
                    ActiveProfile.SupportsVision,
                    ActiveProfile.SupportsToolCalls),
                new BoundGenerationSettingsBindingMaterial(
                    ActiveProfile.ContextTokens,
                    ActiveProfile.OutputTokenLimit,
                    ActiveProfile.Temperature,
                    TopP: 0.9,
                    StreamingEnabled: ActiveProfile.StreamingEnabled,
                    ThinkingControl: "test",
                    ThinkingEnabled: false,
                    ReasoningEffort: "low"));

        public async IAsyncEnumerable<ModelToken> StreamChatAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelToken(
                answer,
                Ali.Modules.Evidence.EvidenceStatus.Unverified,
                ChatFinishReason.Stop.ToString());
        }

        public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeHealthCheck(
                true,
                "ready",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            var decisionJson = PlanningContractTests.DecisionJson(
                $"{{\"kind\":\"answerDirectly\",\"answer\":{JsonSerializer.Serialize(answer)}}}");
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(decisionJson)
                ?? throw new InvalidDataException("The native protocol decision did not deserialize.");
            var message = new AIChatMessage(AIChatRole.Assistant, string.Empty);
            message.Contents.Add(new FunctionCallContent(
                "protocol-call",
                OrchestrationProtocolCapability.ToolName,
                arguments));
            return Task.FromResult(new ChatResponse(message)
            {
                FinishReason = ChatFinishReason.ToolCalls
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
