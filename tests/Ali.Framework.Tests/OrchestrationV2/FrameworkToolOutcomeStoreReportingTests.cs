using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.WorkstationFiles;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class FrameworkToolOutcomeStoreReportingTests
{
    [Theory]
    [InlineData(AliCapabilityCatalog.FileReplaceName)]
    [InlineData(AliCapabilityCatalog.FileReplaceLinesName)]
    public async Task WorkstationSuccessfulEdit_IsCompletedByWriteWithoutReadConflict(string toolName)
    {
        using var fixture = new Fixture();
        var inner = new StubFileStore { ReadResult = "old content" };
        var store = new AuditedAgentFileStore(
            inner,
            new AgentFileActionAuditStore(fixture.Root, activeUsers: null));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity(toolName);
        var turn = Turn(identity, "call-edit", toolName);
        store.ConfigureOutcomeReporting(sidecar);

        Assert.True(sidecar.TryEnterInvocation(turn, "call-edit", toolName, out var invocation));
        using (invocation)
        {
            Assert.Equal(
                "old content",
                await store.ReadAsync(
                    "Workspace/file.txt",
                    TestContext.Current.CancellationToken));
            await store.WriteAsync(
                "Workspace/file.txt",
                "new content",
                TestContext.Current.CancellationToken);
        }

        var outcome = new AliProductionToolOutcomeRegistry(sidecar).Classify(
            Request(identity, "call-edit", toolName, "provider success text"));
        Assert.Equal(PlanningToolDomainOutcome.Succeeded, outcome);
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.WorkMemoryReplaceName)]
    [InlineData(AliCapabilityCatalog.WorkMemoryReplaceLinesName)]
    public async Task WorkMemorySuccessfulEdit_IsCompletedByWriteWithoutReadConflict(string toolName)
    {
        using var fixture = new Fixture();
        var memory = new AliAgentWorkMemory(fixture.Root);
        using var scope = memory.EnterScope("conversation", activeUser: null);
        await memory.Store.WriteAsync(
            "note.md",
            "old content",
            TestContext.Current.CancellationToken);
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity(toolName);
        var turn = Turn(identity, "call-edit", toolName);
        memory.ConfigureOutcomeReporting(sidecar);

        Assert.True(sidecar.TryEnterInvocation(turn, "call-edit", toolName, out var invocation));
        using (invocation)
        {
            Assert.Equal(
                "old content",
                await memory.Store.ReadAsync(
                    "note.md",
                    TestContext.Current.CancellationToken));
            await memory.Store.WriteAsync(
                "note.md",
                "new content",
                TestContext.Current.CancellationToken);
        }

        var outcome = new AliProductionToolOutcomeRegistry(sidecar).Classify(
            Request(identity, "call-edit", toolName, "provider success text"));
        Assert.Equal(PlanningToolDomainOutcome.Succeeded, outcome);
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.FileReplaceName)]
    [InlineData(AliCapabilityCatalog.FileReplaceLinesName)]
    public async Task WorkstationEditNotFound_RecordsExactFailure(string toolName)
    {
        using var fixture = new Fixture();
        var store = new AuditedAgentFileStore(
            new StubFileStore { ReadResult = null },
            new AgentFileActionAuditStore(fixture.Root, activeUsers: null));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity(toolName);
        var turn = Turn(identity, "call-missing", toolName);
        store.ConfigureOutcomeReporting(sidecar);

        Assert.True(sidecar.TryEnterInvocation(turn, "call-missing", toolName, out var invocation));
        using (invocation)
        {
            Assert.Null(await store.ReadAsync(
                "Workspace/missing.txt",
                TestContext.Current.CancellationToken));
        }

        var outcome = new AliProductionToolOutcomeRegistry(sidecar).Classify(
            Request(identity, "call-missing", toolName, "ordinary not-found text"));
        Assert.Equal(PlanningToolDomainOutcome.Failed, outcome);
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.WorkMemoryReplaceName)]
    [InlineData(AliCapabilityCatalog.WorkMemoryReplaceLinesName)]
    public async Task WorkMemoryEditNotFound_RecordsExactFailure(string toolName)
    {
        using var fixture = new Fixture();
        var memory = new AliAgentWorkMemory(fixture.Root);
        using var scope = memory.EnterScope("conversation", activeUser: null);
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity(toolName);
        var turn = Turn(identity, "call-missing", toolName);
        memory.ConfigureOutcomeReporting(sidecar);

        Assert.True(sidecar.TryEnterInvocation(turn, "call-missing", toolName, out var invocation));
        using (invocation)
        {
            Assert.Null(await memory.Store.ReadAsync(
                "missing.txt",
                TestContext.Current.CancellationToken));
        }

        var outcome = new AliProductionToolOutcomeRegistry(sidecar).Classify(
            Request(identity, "call-missing", toolName, "ordinary not-found text"));
        Assert.Equal(PlanningToolDomainOutcome.Failed, outcome);
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.FileListName, false)]
    [InlineData(AliCapabilityCatalog.FileSearchName, true)]
    public async Task EmptyWorkstationListOrSearch_IsAValidNoMatchesCompletion(
        string toolName,
        bool search)
    {
        using var fixture = new Fixture();
        var store = new AuditedAgentFileStore(
            new StubFileStore(),
            new AgentFileActionAuditStore(fixture.Root, activeUsers: null));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity(toolName);
        var turn = Turn(identity, "call-empty", toolName);
        store.ConfigureOutcomeReporting(sidecar);

        Assert.True(sidecar.TryEnterInvocation(turn, "call-empty", toolName, out var invocation));
        using (invocation)
        {
            if (search)
            {
                Assert.Empty(await store.SearchAsync(
                    "Workspace",
                    "needle",
                    null,
                    true,
                    TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.Empty(await store.ListChildrenAsync(
                    "Workspace",
                    TestContext.Current.CancellationToken));
            }
        }

        var outcome = new AliProductionToolOutcomeRegistry(sidecar).Classify(
            Request(identity, "call-empty", toolName, Array.Empty<object>()));
        Assert.Equal(PlanningToolDomainOutcome.Succeeded, outcome);
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.WorkMemoryListName, false)]
    [InlineData(AliCapabilityCatalog.WorkMemorySearchName, true)]
    public async Task EmptyWorkMemoryListOrSearch_IsAValidNoMatchesCompletion(
        string toolName,
        bool search)
    {
        using var fixture = new Fixture();
        var memory = new AliAgentWorkMemory(fixture.Root);
        using var scope = memory.EnterScope("conversation", activeUser: null);
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity(toolName);
        var turn = Turn(identity, "call-empty", toolName);
        memory.ConfigureOutcomeReporting(sidecar);

        Assert.True(sidecar.TryEnterInvocation(turn, "call-empty", toolName, out var invocation));
        using (invocation)
        {
            if (search)
            {
                Assert.Empty(await memory.Store.SearchAsync(
                    string.Empty,
                    "needle",
                    null,
                    true,
                    TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.Empty(await memory.Store.ListChildrenAsync(
                    string.Empty,
                    TestContext.Current.CancellationToken));
            }
        }

        var outcome = new AliProductionToolOutcomeRegistry(sidecar).Classify(
            Request(identity, "call-empty", toolName, Array.Empty<object>()));
        Assert.Equal(PlanningToolDomainOutcome.Succeeded, outcome);
    }

    [Fact]
    public async Task RetainedFilePlan_DoesNotLetIncidentalStoreCallsSignal()
    {
        using var fixture = new Fixture();
        var store = new AuditedAgentFileStore(
            new StubFileStore { ReadResult = "content" },
            new AgentFileActionAuditStore(fixture.Root, activeUsers: null));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity("file-out-of-scope");
        var turn = Turn(identity, "call-file", AliCapabilityCatalog.FileReadName);
        store.ConfigureOutcomeReporting(sidecar);

        Assert.Equal(
            "content",
            await store.ReadAsync(
                "Workspace/file.txt",
                TestContext.Current.CancellationToken));
        Assert.Equal(
            PlanningToolDomainOutcome.Unreported,
            new AliProductionToolOutcomeRegistry(sidecar).Classify(Request(
                identity,
                "call-file",
                AliCapabilityCatalog.FileReadName,
                "content")));

        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-file",
            AliCapabilityCatalog.FileReadName,
            out var invocation));
        using (invocation)
        {
            Assert.Equal(
                "content",
                await store.ReadAsync(
                    "Workspace/file.txt",
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            PlanningToolDomainOutcome.Succeeded,
            new AliProductionToolOutcomeRegistry(sidecar).Classify(Request(
                identity,
                "call-file",
                AliCapabilityCatalog.FileReadName,
                "content")));
    }

    [Fact]
    public async Task RetainedWorkMemoryPlan_DoesNotLetIncidentalStoreCallsSignal()
    {
        using var fixture = new Fixture();
        var memory = new AliAgentWorkMemory(fixture.Root);
        using var memoryScope = memory.EnterScope("conversation", activeUser: null);
        await memory.Store.WriteAsync(
            "note.md",
            "content",
            TestContext.Current.CancellationToken);
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity("memory-out-of-scope");
        var turn = Turn(identity, "call-memory", AliCapabilityCatalog.WorkMemoryReadName);
        memory.ConfigureOutcomeReporting(sidecar);

        Assert.Equal(
            "content",
            await memory.Store.ReadAsync(
                "note.md",
                TestContext.Current.CancellationToken));
        Assert.Equal(
            PlanningToolDomainOutcome.Unreported,
            new AliProductionToolOutcomeRegistry(sidecar).Classify(Request(
                identity,
                "call-memory",
                AliCapabilityCatalog.WorkMemoryReadName,
                "content")));

        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-memory",
            AliCapabilityCatalog.WorkMemoryReadName,
            out var invocation));
        using (invocation)
        {
            Assert.Equal(
                "content",
                await memory.Store.ReadAsync(
                    "note.md",
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            PlanningToolDomainOutcome.Succeeded,
            new AliProductionToolOutcomeRegistry(sidecar).Classify(Request(
                identity,
                "call-memory",
                AliCapabilityCatalog.WorkMemoryReadName,
                "content")));
    }

    [Fact]
    public async Task ActualFileProvider_OpensAndClosesTheExactStoreInvocationScope()
    {
        const string callId = "call-real-file-read";
        using var fixture = new Fixture();
        var store = new AuditedAgentFileStore(
            new StubFileStore { ReadResult = "provider content" },
            new AgentFileActionAuditStore(fixture.Root, activeUsers: null));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity("real-file-read");
        var turn = Turn(identity, callId, AliCapabilityCatalog.FileReadName);
        store.ConfigureOutcomeReporting(sidecar);
        using var client = new ScriptedChatClient(
            ToolCall(
                callId,
                AliCapabilityCatalog.FileReadName,
                new Dictionary<string, object?> { ["fileName"] = "Workspace/file.txt" }),
            FinalAnswer("read complete"));
        var inner = client.AsHarnessAgent(new HarnessAgentOptions
        {
            MaximumIterationsPerRequest = 4,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableOpenTelemetry = true,
            FileAccessStore = store,
            FileAccessProviderOptions = new FileAccessProviderOptions
            {
                DisableReadOnlyToolApproval = true,
                DisableWriteToolApproval = true
            }
        });
        var agent = AliFrameworkProviderOutcomeMiddleware.WithOutcomeReporting(
            inner,
            sidecar,
            () => turn);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "provider content",
            await store.ReadAsync(
                "Workspace/file.txt",
                TestContext.Current.CancellationToken));
        Assert.Equal(0, sidecar.Count);

        var response = await agent.RunAsync(
            "Read Workspace/file.txt.",
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("read complete", response.Text, StringComparison.Ordinal);
        Assert.Equal(
            PlanningToolDomainOutcome.Succeeded,
            new AliProductionToolOutcomeRegistry(sidecar).Classify(Request(
                identity,
                callId,
                AliCapabilityCatalog.FileReadName,
                "ordinary provider return")));
    }

    private static AliCompletedToolOutcomeRequest Request(
        TurnIdentity identity,
        string callId,
        string toolName,
        object result) =>
        new(identity, callId, toolName, result);

    private static CoordinatorTurnContext Turn(
        TurnIdentity identity,
        string callId,
        string toolName)
    {
        var turn = new CoordinatorTurnContext(
            identity.ConversationId,
            "user-message",
            identity.AssistantMessageId,
            "request",
            _ => { },
            capturedUserSelection: null,
            observationIdentity: null);
        turn.RegisterToolPlan(new CoordinatorToolPlan(
            callId,
            toolName,
            "assessment",
            "plan",
            "next",
            "selection",
            "result",
            "{}"));
        turn.RegisterActionExecutionAuthority(new TestAuthority(identity));
        return turn;
    }

    private static TurnIdentity Identity(string suffix) =>
        new("fallback-user", "conversation", $"assistant-{suffix}");

    private static ChatResponse ToolCall(
        string callId,
        string name,
        IDictionary<string, object?> arguments)
    {
        var message = new ChatMessage(ChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent(callId, name, arguments));
        return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static ChatResponse FinalAnswer(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop
        };

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

    private sealed class StubFileStore : AgentFileStore
    {
        public string? ReadResult { get; init; }

        public override Task WriteAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public override Task<string?> ReadAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadResult);

        public override Task<bool> DeleteAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
            string directory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileStoreEntry>>([]);

        public override Task<bool> FileExistsAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
            string directory,
            string regexPattern,
            string? globPattern,
            bool recursive,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileSearchResult>>([]);

        public override Task CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : FinalAnswer("script exhausted"));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "ProjectAli.FrameworkOutcomeTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
