using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AliFrameworkProviderOutcomeTests
{
    [Fact]
    public async Task ModeGet_UsesTheExactSessionStateAsSuccessEvidence()
    {
        using var provider = new AgentModeProvider(new AgentModeProviderOptions());
        var session = new TestSession();
        var test = Begin(AliCapabilityCatalog.GetAgentModeName, "call-mode-get");
        using (test.Invocation)
        {
            await AliFrameworkProviderOutcomeMiddleware.VerifyModeReturnAsync(
                provider,
                session,
                AliCapabilityCatalog.GetAgentModeName,
                new AIFunctionArguments(),
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Succeeded, test.Classify());
    }

    [Fact]
    public async Task ModeSet_RequiresAnExactRequestedAndObservedStateMatch()
    {
        using var provider = new AgentModeProvider(new AgentModeProviderOptions());
        var session = new TestSession();
        await provider.SetModeAsync(
            session,
            "execute",
            TestContext.Current.CancellationToken);
        var test = Begin(AliCapabilityCatalog.SetAgentModeName, "call-mode-set");
        using (test.Invocation)
        {
            await AliFrameworkProviderOutcomeMiddleware.VerifyModeReturnAsync(
                provider,
                session,
                AliCapabilityCatalog.SetAgentModeName,
                new AIFunctionArguments { ["mode"] = "execute" },
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Succeeded, test.Classify());
    }

    [Fact]
    public async Task ModeSet_MismatchedObservedStateIsFailureEvidence()
    {
        using var provider = new AgentModeProvider(new AgentModeProviderOptions());
        var session = new TestSession();
        await provider.SetModeAsync(
            session,
            "plan",
            TestContext.Current.CancellationToken);
        var test = Begin(AliCapabilityCatalog.SetAgentModeName, "call-mode-mismatch");
        using (test.Invocation)
        {
            await AliFrameworkProviderOutcomeMiddleware.VerifyModeReturnAsync(
                provider,
                session,
                AliCapabilityCatalog.SetAgentModeName,
                new AIFunctionArguments { ["mode"] = "execute" },
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Failed, test.Classify());
    }

    [Fact]
    public async Task ActualHarnessModeProvider_ExecutesInsideTheExactInvocationScope()
    {
        const string callId = "call-real-mode-set";
        using var client = new ScriptedChatClient(
            ToolCall(
                callId,
                AliCapabilityCatalog.SetAgentModeName,
                new Dictionary<string, object?>
                {
                    ["mode"] = JsonSerializer.SerializeToElement("execute")
                }),
            FinalAnswer("mode changed"));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity("real-mode-set");
        var turn = Turn(identity, callId, AliCapabilityCatalog.SetAgentModeName);
        var inner = client.AsHarnessAgent(new HarnessAgentOptions
        {
            MaximumIterationsPerRequest = 4,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = false,
            DisableOpenTelemetry = true
        });
        var agent = AliFrameworkProviderOutcomeMiddleware.WithOutcomeReporting(
            inner,
            sidecar,
            () => turn);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await agent.RunAsync(
            "Switch to execute mode.",
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("mode changed", response.Text, StringComparison.Ordinal);
        Assert.Equal(
            PlanningToolDomainOutcome.Succeeded,
            new AliProductionToolOutcomeRegistry(sidecar).Classify(
                new AliCompletedToolOutcomeRequest(
                    identity,
                    callId,
                    AliCapabilityCatalog.SetAgentModeName,
                    "ordinary provider return")));
    }

    [Fact]
    public async Task ActualHarnessSkillsProvider_VerifiesMissingSkillFromExactRunInventory()
    {
        const string callId = "call-real-missing-skill";
        using var client = new ScriptedChatClient(
            ToolCall(
                callId,
                AliCapabilityCatalog.LoadAgentSkillName,
                new Dictionary<string, object?>
                {
                    ["skillName"] = JsonSerializer.SerializeToElement("missing-skill")
                }),
            FinalAnswer("skill checked"));
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var skillsSource = new AliOutcomeReportingAgentSkillsSource(
            new StubSkillsSource(new StubSkill()),
            sidecar);
        var identity = Identity("real-missing-skill");
        var turn = Turn(identity, callId, AliCapabilityCatalog.LoadAgentSkillName);
        var inner = client.AsHarnessAgent(new HarnessAgentOptions
        {
            MaximumIterationsPerRequest = 4,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = false,
            AgentSkillsSource = skillsSource,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableOpenTelemetry = true,
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule]
            }
        });
        var agent = AliFrameworkProviderOutcomeMiddleware.WithOutcomeReporting(
            inner,
            sidecar,
            () => turn,
            skillsSource);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await agent.RunAsync(
            "Load a missing skill.",
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("skill checked", response.Text, StringComparison.Ordinal);
        Assert.Equal(
            PlanningToolDomainOutcome.Failed,
            new AliProductionToolOutcomeRegistry(sidecar).Classify(
                new AliCompletedToolOutcomeRequest(
                    identity,
                    callId,
                    AliCapabilityCatalog.LoadAgentSkillName,
                    "ordinary provider error text")));
    }

    [Fact]
    public async Task MissingModeVerification_RemainsFailClosedUnreported()
    {
        var test = Begin(AliCapabilityCatalog.GetAgentModeName, "call-mode-missing");
        using (test.Invocation)
        {
            await AliFrameworkProviderOutcomeMiddleware.VerifyModeReturnAsync(
                provider: null,
                session: null,
                AliCapabilityCatalog.GetAgentModeName,
                new AIFunctionArguments(),
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Unreported, test.Classify());
    }

    [Fact]
    public async Task ModeSignal_CannotCrossAnExactInvocationCorrelationBoundary()
    {
        using var provider = new AgentModeProvider(new AgentModeProviderOptions());
        var session = new TestSession();
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var identity = Identity("mode-correlation");
        var turn = Turn(identity, "call-file", AliCapabilityCatalog.FileReadName);
        Assert.True(sidecar.TryEnterInvocation(
            turn,
            "call-file",
            AliCapabilityCatalog.FileReadName,
            out var invocation));
        using (invocation)
        {
            await AliFrameworkProviderOutcomeMiddleware.VerifyModeReturnAsync(
                provider,
                session,
                AliCapabilityCatalog.GetAgentModeName,
                new AIFunctionArguments(),
                sidecar);
        }

        Assert.Equal(0, sidecar.Count);
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.GetAgentModeName)]
    [InlineData(AliCapabilityCatalog.SetAgentModeName)]
    [InlineData(AliCapabilityCatalog.LoadAgentSkillName)]
    [InlineData(AliCapabilityCatalog.ReadAgentSkillResourceName)]
    [InlineData(AliCapabilityCatalog.RunAgentSkillScriptName)]
    public void ProviderInvocationException_IsExactFailureEvidenceForAllFiveTools(
        string toolName)
    {
        var test = Begin(toolName, $"call-{toolName}");
        using (test.Invocation)
        {
            AliFrameworkProviderOutcomeMiddleware.ReportInvocationFailure(
                toolName,
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Failed, test.Classify());
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.LoadAgentSkillName)]
    [InlineData(AliCapabilityCatalog.ReadAgentSkillResourceName)]
    [InlineData(AliCapabilityCatalog.RunAgentSkillScriptName)]
    public void MissingSkill_IsVerifiedAgainstTheExactRunInventory(string toolName)
    {
        var test = Begin(toolName, $"call-missing-{toolName}");
        using (test.Invocation)
        {
            AliFrameworkProviderOutcomeMiddleware.VerifySkillReturn(
                toolName,
                new AIFunctionArguments { ["skillName"] = "missing-skill" },
                new HashSet<string>(StringComparer.Ordinal) { "available-skill" },
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Failed, test.Classify());
    }

    [Fact]
    public void ExistingSkillWithoutItsTypedBoundarySignal_RemainsUnreported()
    {
        var test = Begin(AliCapabilityCatalog.LoadAgentSkillName, "call-existing-unsignaled");
        using (test.Invocation)
        {
            AliFrameworkProviderOutcomeMiddleware.VerifySkillReturn(
                AliCapabilityCatalog.LoadAgentSkillName,
                new AIFunctionArguments { ["skillName"] = "available-skill" },
                new HashSet<string>(StringComparer.Ordinal) { "available-skill" },
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Unreported, test.Classify());
    }

    [Fact]
    public void MissingRunInventory_CannotInferASkillOutcome()
    {
        var test = Begin(AliCapabilityCatalog.LoadAgentSkillName, "call-no-inventory");
        using (test.Invocation)
        {
            AliFrameworkProviderOutcomeMiddleware.VerifySkillReturn(
                AliCapabilityCatalog.LoadAgentSkillName,
                new AIFunctionArguments { ["skillName"] = "missing-skill" },
                skillNames: null,
                test.Sidecar);
        }

        Assert.Equal(PlanningToolDomainOutcome.Unreported, test.Classify());
    }

    [Fact]
    public async Task LoadSkill_ContentReturnAndExceptionProduceExactTerminalSignals()
    {
        var succeeded = Begin(AliCapabilityCatalog.LoadAgentSkillName, "call-load-ok");
        var successfulSkill = AliOutcomeReportingAgentSkillsSource.WrapSkill(
            new StubSkill(),
            succeeded.Sidecar);
        using (succeeded.Invocation)
        {
            Assert.Equal(
                "skill content",
                await successfulSkill.GetContentAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(PlanningToolDomainOutcome.Succeeded, succeeded.Classify());

        var failed = Begin(AliCapabilityCatalog.LoadAgentSkillName, "call-load-fail");
        var failingSkill = AliOutcomeReportingAgentSkillsSource.WrapSkill(
            new StubSkill
            {
                Content = _ => ValueTask.FromException<string>(
                    new InvalidOperationException("load failed"))
            },
            failed.Sidecar);
        using (failed.Invocation)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await failingSkill.GetContentAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(PlanningToolDomainOutcome.Failed, failed.Classify());
    }

    [Fact]
    public async Task ReadSkillResource_FoundNotFoundAndExceptionProduceExactSignals()
    {
        var found = Begin(AliCapabilityCatalog.ReadAgentSkillResourceName, "call-resource-ok");
        var foundSkill = AliOutcomeReportingAgentSkillsSource.WrapSkill(
            new StubSkill { Resource = (_, _) => ValueTask.FromResult<AgentSkillResource?>(new StubResource()) },
            found.Sidecar);
        using (found.Invocation)
        {
            var resource = await foundSkill.GetResourceAsync(
                "reference.md",
                TestContext.Current.CancellationToken);
            Assert.NotNull(resource);
            Assert.Equal(
                "resource content",
                await resource!.ReadAsync(
                    null,
                    TestContext.Current.CancellationToken));
        }
        Assert.Equal(PlanningToolDomainOutcome.Succeeded, found.Classify());

        var notFound = Begin(AliCapabilityCatalog.ReadAgentSkillResourceName, "call-resource-missing");
        var missingSkill = AliOutcomeReportingAgentSkillsSource.WrapSkill(
            new StubSkill { Resource = (_, _) => ValueTask.FromResult<AgentSkillResource?>(null) },
            notFound.Sidecar);
        using (notFound.Invocation)
        {
            Assert.Null(await missingSkill.GetResourceAsync(
                "missing.md",
                TestContext.Current.CancellationToken));
        }
        Assert.Equal(PlanningToolDomainOutcome.Failed, notFound.Classify());

        var failed = Begin(AliCapabilityCatalog.ReadAgentSkillResourceName, "call-resource-fail");
        var failingSkill = AliOutcomeReportingAgentSkillsSource.WrapSkill(
            new StubSkill
            {
                Resource = (_, _) => ValueTask.FromResult<AgentSkillResource?>(
                    new StubResource { Failure = new IOException("resource failed") })
            },
            failed.Sidecar);
        using (failed.Invocation)
        {
            var resource = await failingSkill.GetResourceAsync(
                "reference.md",
                TestContext.Current.CancellationToken);
            Assert.NotNull(resource);
            await Assert.ThrowsAsync<IOException>(() => resource!.ReadAsync(
                null,
                TestContext.Current.CancellationToken));
        }
        Assert.Equal(PlanningToolDomainOutcome.Failed, failed.Classify());
    }

    [Fact]
    public async Task RunSkillScript_ReturnAndExceptionProduceExactTerminalSignals()
    {
        var succeeded = Begin(AliCapabilityCatalog.RunAgentSkillScriptName, "call-script-ok");
        var innerSkill = new StubSkill();
        var successfulScript = new StubScript();
        innerSkill.Script = (_, _) => ValueTask.FromResult<AgentSkillScript?>(successfulScript);
        var wrappedSkill = AliOutcomeReportingAgentSkillsSource.WrapSkill(innerSkill, succeeded.Sidecar);
        using (succeeded.Invocation)
        {
            var script = await wrappedSkill.GetScriptAsync(
                "run.ps1",
                TestContext.Current.CancellationToken);
            Assert.NotNull(script);
            Assert.Equal("script result", await script!.RunAsync(
                wrappedSkill,
                arguments: null,
                serviceProvider: null,
                cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Same(innerSkill, successfulScript.ObservedOwner);
        Assert.Equal(PlanningToolDomainOutcome.Succeeded, succeeded.Classify());

        var failed = Begin(AliCapabilityCatalog.RunAgentSkillScriptName, "call-script-fail");
        var failingInner = new StubSkill();
        failingInner.Script = (_, _) => ValueTask.FromResult<AgentSkillScript?>(
            new StubScript { Failure = new InvalidOperationException("script failed") });
        var failingWrapped = AliOutcomeReportingAgentSkillsSource.WrapSkill(
            failingInner,
            failed.Sidecar);
        using (failed.Invocation)
        {
            var script = await failingWrapped.GetScriptAsync(
                "run.ps1",
                TestContext.Current.CancellationToken);
            Assert.NotNull(script);
            await Assert.ThrowsAsync<InvalidOperationException>(() => script!.RunAsync(
                failingWrapped,
                arguments: null,
                serviceProvider: null,
                cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Equal(PlanningToolDomainOutcome.Failed, failed.Classify());
    }

    [Fact]
    public async Task SkillBoundaryOutsideItsExactInvocationScopeCannotSignal()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var wrappedSkill = AliOutcomeReportingAgentSkillsSource.WrapSkill(
            new StubSkill(),
            sidecar);

        Assert.Equal(
            "skill content",
            await wrappedSkill.GetContentAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, sidecar.Count);
    }

    private static OutcomeTest Begin(string toolName, string callId)
    {
        var identity = Identity(callId);
        var turn = Turn(identity, callId, toolName);
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        Assert.True(sidecar.TryEnterInvocation(turn, callId, toolName, out var invocation));
        Assert.NotNull(invocation);
        return new OutcomeTest(identity, callId, toolName, sidecar, invocation!);
    }

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
        new("user", "conversation", $"assistant-{suffix}");

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

    private sealed record OutcomeTest(
        TurnIdentity Identity,
        string CallId,
        string ToolName,
        AliFrameworkToolOutcomeSidecar Sidecar,
        IDisposable Invocation)
    {
        public PlanningToolDomainOutcome Classify() =>
            new AliProductionToolOutcomeRegistry(Sidecar).Classify(
                new AliCompletedToolOutcomeRequest(
                    Identity,
                    CallId,
                    ToolName,
                    "ordinary provider return"));
    }

    private sealed class TestSession : AgentSession
    {
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

    private sealed class StubSkill : AgentSkill
    {
        public override AgentSkillFrontmatter Frontmatter { get; } =
            new("test-skill", "A test skill.");

        public Func<CancellationToken, ValueTask<string>> Content { get; init; } =
            _ => ValueTask.FromResult("skill content");

        public Func<string, CancellationToken, ValueTask<AgentSkillResource?>> Resource { get; init; } =
            (_, _) => ValueTask.FromResult<AgentSkillResource?>(null);

        public Func<string, CancellationToken, ValueTask<AgentSkillScript?>> Script { get; set; } =
            (_, _) => ValueTask.FromResult<AgentSkillScript?>(null);

        public override ValueTask<string> GetContentAsync(
            CancellationToken cancellationToken = default) =>
            Content(cancellationToken);

        public override ValueTask<AgentSkillResource?> GetResourceAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Resource(name, cancellationToken);

        public override ValueTask<AgentSkillScript?> GetScriptAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Script(name, cancellationToken);
    }

    private sealed class StubSkillsSource(params AgentSkill[] skills) : AgentSkillsSource
    {
        public override Task<IList<AgentSkill>> GetSkillsAsync(
            AgentSkillsSourceContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult<IList<AgentSkill>>(skills);
    }

    private sealed class StubResource : AgentSkillResource
    {
        public StubResource() : base("reference.md", "A test resource.")
        {
        }

        public Exception? Failure { get; init; }

        public override Task<object?> ReadAsync(
            IServiceProvider? serviceProvider,
            CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult<object?>("resource content")
                : Task.FromException<object?>(Failure);
    }

    private sealed class StubScript : AgentSkillScript
    {
        public StubScript() : base("run.ps1", "A test script.")
        {
        }

        public Exception? Failure { get; init; }

        public AgentSkill? ObservedOwner { get; private set; }

        public override Task<object?> RunAsync(
            AgentSkill skill,
            JsonElement? arguments,
            IServiceProvider? serviceProvider,
            CancellationToken cancellationToken = default)
        {
            ObservedOwner = skill;
            return Failure is null
                ? Task.FromResult<object?>("script result")
                : Task.FromException<object?>(Failure);
        }
    }

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
}
