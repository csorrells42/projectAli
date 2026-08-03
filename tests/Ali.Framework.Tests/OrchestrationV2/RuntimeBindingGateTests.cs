using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Ali.Modules.ToolDiscovery;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class RuntimeBindingGateTests
{
    public static TheoryData<string> ExactBindingChanges => new()
    {
        "assistant-profile",
        "runtime",
        "model",
        "generation-settings",
        "capability-registry",
        "permissions",
        "mcp",
        "attachments",
        "artifacts"
    };

    [Fact]
    public async Task PlanningPass_UnchangedBindings_PermitsExactlyOneModelRequest()
    {
        using var directory = new TemporaryDirectory("Ali-RuntimeBinding-Planning-");
        var identity = Identity();
        var bindings = Bindings();
        var turnContext = TurnContext(identity, "Say hello.");
        var model = new CallbackChatClient(_ => Compatibility(
            PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Hello.\"}")));

        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using var durableTurn = await coordinator.BeginTurnAsync(
            turnContext,
            bindings,
            acceptedPriorConversation: [],
            capabilityRegistry: null,
            liveBindingsAccessor: () => bindings with { },
            TestContext.Current.CancellationToken);
        using var planner = new AliOrchestrationPlanningClient(
            model,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            boundDispatchAccessor: () => Snapshot(model),
            dispatchBindingsFactory: _ => bindings);
        using var turnScope = planner.BeginTurn(turnContext, durableTurn.Input, durableTurn);

        var response = await planner.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Hello.", response.Text);
        Assert.Equal(1, model.RequestCount);
    }

    [Theory]
    [MemberData(nameof(ExactBindingChanges))]
    public async Task PlanningPass_AnyExactBindingChange_SuspendsBeforeAnotherModelRequest(
        string changedBinding)
    {
        using var directory = new TemporaryDirectory("Ali-RuntimeBinding-Replan-");
        var identity = Identity();
        var original = Bindings();
        var current = original;
        var tool = ReadTool();
        var turnContext = TurnContext(identity, "Inspect the repository.");
        var model = new CallbackChatClient(requestNumber =>
        {
            Assert.Equal(1, requestNumber);
            current = Change(original, changedBinding);
            return Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"expandTools\",\"need\":\"Inspect the repository\"}"));
        });

        using (var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile"))
        {
            await using var durableTurn = await coordinator.BeginTurnAsync(
                turnContext,
                original,
                acceptedPriorConversation: [],
                capabilityRegistry: null,
                liveBindingsAccessor: () => current,
                TestContext.Current.CancellationToken);
            using var planner = new AliOrchestrationPlanningClient(
                model,
                () => false,
                PlanningTestModelProfile.GptOss65K,
                new FixedSemanticCatalog([tool]),
                boundDispatchAccessor: () => Snapshot(model),
                dispatchBindingsFactory: _ => current);
            using var turnScope = planner.BeginTurn(turnContext, durableTurn.Input, durableTurn);

            var response = await planner.GetResponseAsync(
                [],
                new ChatOptions { Tools = [tool] },
                TestContext.Current.CancellationToken);

            Assert.Contains("runtime bindings changed", response.Text, StringComparison.Ordinal);
            Assert.Contains(changedBinding, response.Text, StringComparison.Ordinal);
            var interim = Assert.IsType<AliPreparedInterimResponse>(
                planner.PreparedInterimResponse);
            Assert.Equal(AliPlanningInterimKind.RuntimeSuspended, interim.Kind);
            Assert.Equal(response.Text, interim.AnswerText);
            Assert.Equal(TurnStateIntegrity.Digest(interim.AnswerText), interim.AnswerDigest);
            Assert.Equal(1, model.RequestCount);
        }

        using var writer = new TurnTransitionWriter(directory.Path, "profile");
        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.NotNull(replay.State);
        Assert.Equal(TurnControlState.Running, replay.State.Control);
        Assert.Equal(
            InterimPublicationStatus.Prepared,
            replay.State.InterimPublication!.Status);
        Assert.Equal(
            InterimPublicationReason.RuntimeBindingsChanged,
            replay.State.InterimPublication.Reason);
    }

    [Fact]
    public async Task PlanningPass_RuntimeSwitchAfterBoundSnapshot_RetriesWithFreshSnapshot()
    {
        using var directory = new TemporaryDirectory("Ali-RuntimeBinding-SnapshotRace-");
        var identity = Identity();
        var bindings = Bindings();
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = true
        };
        var before = new BoundRuntime(
            profile with { PackageId = "model-before" },
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"from bound planner\"}")));
        var after = new BoundRuntime(
            profile with { PackageId = "model-after" },
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"from switched planner\"}")));
        var switching = new SafeActivatingLocalRuntime(before, after);
        var health = await switching.CheckCandidateAsync(TestContext.Current.CancellationToken);
        Assert.True(health.Succeeded);
        var turnContext = TurnContext(identity, "Use the already-bound planning model.");

        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using var durableTurn = await coordinator.BeginTurnAsync(
            turnContext,
            bindings,
            acceptedPriorConversation: [],
            capabilityRegistry: null,
            liveBindingsAccessor: () => bindings,
            TestContext.Current.CancellationToken);
        using var planner = new AliOrchestrationPlanningClient(
            switching,
            () => switching.ActiveProfile.SupportsToolCalls,
            () => switching.ActiveProfile,
            boundDispatchAccessor: () =>
                ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch(),
            dispatchBindingsFactory: _ =>
            {
                Assert.True(switching.ActivateLastHealthChecked());
                return bindings;
            });
        using var turnScope = planner.BeginTurn(turnContext, durableTurn.Input, durableTurn);

        var response = await planner.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("from switched planner", response.Text);
        Assert.Equal(0, before.RequestCount);
        Assert.Equal(1, after.RequestCount);
        Assert.Equal("model-after", switching.ActiveProfile.PackageId);
    }

    [Fact]
    public async Task BoundDispatchCapturedBeforeActivation_FailsClosedWithoutCallingEitherRuntime()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = false
        };
        var active = new BoundRuntime(
            profile with { PackageId = "active-model" },
            Compatibility("{}"));
        var candidate = new BoundRuntime(
            profile with { PackageId = "candidate-model" },
            Compatibility("{}"));
        var switching = new SafeActivatingLocalRuntime(active, candidate);
        var health = await switching.CheckCandidateAsync(
            TestContext.Current.CancellationToken);
        Assert.True(health.Succeeded);
        var stale = ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch();

        Assert.True(switching.ActivateLastHealthChecked());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stale.ChatClient.GetResponseAsync(
                [],
                new ChatOptions(),
                TestContext.Current.CancellationToken));

        Assert.Contains("active runtime changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, active.RequestCount);
        Assert.Equal(0, candidate.RequestCount);
        Assert.Equal("candidate-model", switching.ActiveProfile.PackageId);
    }

    [Fact]
    public void ServiceDiscovery_DoesNotExposeConcreteRuntimeOrDispatchClient()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            PackageId = "service-discovery-model",
            SupportsToolCalls = false
        };
        var metadata = new ChatClientMetadata(
            "test-provider",
            new Uri("http://127.0.0.1:1234"),
            profile.PackageId);
        var arbitraryService = new ArbitraryRuntimeService();
        var runtime = new BoundRuntime(
            profile,
            Compatibility("{}"),
            metadata,
            arbitraryService);
        var switching = new SafeActivatingLocalRuntime(runtime, candidateRuntime: null);
        var facade = (IChatClient)switching;

        Assert.Same(switching, facade.GetService(typeof(IChatClient)));
        Assert.Same(switching, facade.GetService(typeof(ILocalModelRuntime)));
        Assert.Same(switching, facade.GetService(typeof(SafeActivatingLocalRuntime)));
        Assert.Null(facade.GetService(typeof(BoundRuntime)));
        Assert.Null(facade.GetService(typeof(ArbitraryRuntimeService)));
        Assert.Same(metadata, facade.GetService(typeof(ChatClientMetadata)));

        var pinned = ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch();

        Assert.NotSame(runtime, pinned.ChatClient);
        Assert.Same(pinned.ChatClient, pinned.ChatClient.GetService(typeof(IChatClient)));
        Assert.Null(pinned.ChatClient.GetService(typeof(ILocalModelRuntime)));
        Assert.Null(pinned.ChatClient.GetService(typeof(BoundRuntime)));
        Assert.Null(pinned.ChatClient.GetService(typeof(ArbitraryRuntimeService)));
        Assert.Same(metadata, pinned.ChatClient.GetService(typeof(ChatClientMetadata)));
    }

    [Fact]
    public async Task BoundDispatchUseAfterCandidateProbe_RearmsRequiredActiveRuntimeUnload()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = false
        };
        var active = new BoundRuntime(
            profile with { PackageId = "active-model" },
            Compatibility("{}"));
        var candidate = new BoundRuntime(
            profile with { PackageId = "candidate-model" },
            Compatibility("{}"));
        var switching = new SafeActivatingLocalRuntime(active, candidate);

        var firstCheck = await switching.CheckCandidateAsync(
            TestContext.Current.CancellationToken);
        Assert.True(firstCheck.Succeeded);
        Assert.Equal(1, active.UnloadCount);

        var pinned = ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch();
        await pinned.ChatClient.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        var secondCheck = await switching.CheckCandidateAsync(
            TestContext.Current.CancellationToken);
        Assert.True(secondCheck.Succeeded);
        Assert.Equal(2, active.UnloadCount);
        Assert.Equal(1, active.RequestCount);
        Assert.Equal(2, candidate.HealthCheckCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InFlightDispatch_PreventsCandidateUnloadUntilRequestOrStreamCompletes(
        int dispatchKind)
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = false
        };
        var active = new GateControlledRuntime(
            profile with { PackageId = "active-model" },
            Compatibility("{}"),
            blockResponse: true);
        var candidate = new GateControlledRuntime(
            profile with { PackageId = "candidate-model" },
            Compatibility("{}"));
        var switching = new SafeActivatingLocalRuntime(active, candidate);
        var pinned = ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch();
        IChatClient dispatchClient = dispatchKind == 0 ? switching : pinned.ChatClient;

        var dispatch = dispatchKind switch
        {
            2 => ConsumeStreamingAsync(
                dispatchClient,
                TestContext.Current.CancellationToken),
            3 => ConsumeRuntimeStreamingAsync(
                switching,
                TestContext.Current.CancellationToken),
            _ => ConsumeResponseAsync(
                dispatchClient,
                TestContext.Current.CancellationToken)
        };
        await active.ResponseEntered.WaitAsync(TestContext.Current.CancellationToken);

        var candidateCheck = switching.CheckCandidateAsync(
            TestContext.Current.CancellationToken);
        Assert.False(candidateCheck.IsCompleted);
        Assert.Equal(0, active.UnloadCount);

        active.ReleaseResponse();
        await dispatch;
        var health = await candidateCheck;

        Assert.True(health.Succeeded);
        Assert.Equal(1, active.RequestCount);
        Assert.Equal(1, active.UnloadCount);
        Assert.Equal(1, candidate.HealthCheckCount);
    }

    [Fact]
    public async Task TransitionStarted_BlocksThenRejectsStalePinnedDispatchWithoutInnerCall()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = false
        };
        var fallback = new GateControlledRuntime(
            profile with { PackageId = "fallback-model" },
            Compatibility("{}"));
        var candidate = new GateControlledRuntime(
            profile with { PackageId = "candidate-model" },
            Compatibility("{}"),
            blockShutdown: true);
        var switching = new SafeActivatingLocalRuntime(fallback, candidate);
        var health = await switching.CheckCandidateAsync(
            TestContext.Current.CancellationToken);
        Assert.True(health.Succeeded);
        Assert.True(switching.ActivateLastHealthChecked());
        var stale = ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch();

        var transition = switching.RevertToFallbackAsync(
            TestContext.Current.CancellationToken);
        await candidate.ShutdownEntered.WaitAsync(TestContext.Current.CancellationToken);
        var staleCall = stale.ChatClient.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.False(staleCall.IsCompleted);
        Assert.Equal(0, candidate.RequestCount);
        candidate.ReleaseShutdown();
        await transition;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => staleCall);

        Assert.Contains("active runtime changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, candidate.RequestCount);
        Assert.Equal(0, fallback.RequestCount);
        Assert.Equal(1, candidate.ShutdownCount);
        Assert.True(switching.IsUsingFallback);
    }

    [Fact]
    public async Task ActivateWhileDispatchIsInFlight_ReturnsFalseWithoutBlockingUiThread()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = false
        };
        var active = new GateControlledRuntime(
            profile with { PackageId = "active-model" },
            Compatibility("{}"),
            blockResponse: true);
        var candidate = new GateControlledRuntime(
            profile with { PackageId = "candidate-model" },
            Compatibility("{}"));
        var switching = new SafeActivatingLocalRuntime(active, candidate);
        var health = await switching.CheckCandidateAsync(
            TestContext.Current.CancellationToken);
        Assert.True(health.Succeeded);
        var pinned = ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch();
        var dispatch = pinned.ChatClient.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);
        await active.ResponseEntered.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(switching.ActivateLastHealthChecked());
        Assert.Equal("active-model", switching.ActiveProfile.PackageId);

        active.ReleaseResponse();
        await dispatch;
        Assert.True(switching.ActivateLastHealthChecked());
        Assert.Equal("candidate-model", switching.ActiveProfile.PackageId);
    }

    [Fact]
    public async Task DurablePlanningPass_WithoutExactDispatchEnvelope_FailsClosedBeforeModel()
    {
        using var directory = new TemporaryDirectory("Ali-RuntimeBinding-UnboundPlanner-");
        var identity = Identity();
        var bindings = Bindings();
        var turnContext = TurnContext(identity, "Do not guess at a planner model envelope.");
        var model = new CallbackChatClient(_ => Compatibility(
            PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"must not run\"}")));

        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using var durableTurn = await coordinator.BeginTurnAsync(
            turnContext,
            bindings,
            acceptedPriorConversation: [],
            capabilityRegistry: null,
            liveBindingsAccessor: () => bindings,
            TestContext.Current.CancellationToken);
        using var planner = new AliOrchestrationPlanningClient(
            model,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var turnScope = planner.BeginTurn(turnContext, durableTurn.Input, durableTurn);

        var response = await planner.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, model.RequestCount);
        Assert.Contains("runtime bindings changed", response.Text, StringComparison.Ordinal);
        Assert.Equal(
            AliPlanningInterimKind.RuntimeSuspended,
            planner.PreparedInterimResponse!.Kind);
    }

    [Fact]
    public async Task ExecutionBindingChange_BlocksAtTerminalGuardBeforeInnerEffect()
    {
        using var directory = new TemporaryDirectory("Ali-RuntimeBinding-Execution-");
        var identity = Identity();
        var original = Bindings();
        var current = original;
        var invoked = 0;
        var function = AIFunctionFactory.Create(
            (Func<string>)(() =>
            {
                Interlocked.Increment(ref invoked);
                return "ok";
            }),
            AliCapabilityCatalog.GitStatusName,
            "Read repository status.");
        var registry = AliProductionCapabilityCatalog.CreateRegistry([function]);
        var runtimeState = RuntimeState(registry);
        var inventory = CapabilityTerminalToolInventory.Create([function], registry);
        var owner = new CapabilitySettingsSnapshotOwner(
            registry,
            new CapabilityResolver(),
            CapabilityRuntimeAvailabilityFactory.Create(inventory, runtimeState),
            new MemoryCapabilitySettingsPersistence());
        var turnContext = TurnContext(identity, "Read repository status.");

        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using var durableTurn = await coordinator.BeginTurnAsync(
            turnContext,
            original,
            acceptedPriorConversation: [],
            capabilityRegistry: registry,
            liveBindingsAccessor: () => current,
            TestContext.Current.CancellationToken);
        var decision = new OrchestrationDecision(
            new OrchestrationWorkUpdate(
                0,
                [
                    new OrchestrationWorkItemUpdate(
                        "work-status",
                        "Read repository status",
                        OrchestrationWorkStatus.Active)
                ]),
            [],
            new CallToolAction(
                function.Name,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                "Read repository status",
                "Repository state becomes accepted evidence"));
        var accepted = await durableTurn.OnDecisionAcceptedAsync(
            new AliPlanningDecisionAcceptedEvent(
                identity.ConversationId,
                identity.AssistantMessageId,
                durableTurn.Input.StateRevision,
                decision,
                "call-1",
                function.Name),
            TestContext.Current.CancellationToken);
        Assert.False(accepted.RequiresFreshPlanningPass);

        var terminal = new TerminalCapabilityEnforcementProvider(
            owner,
            () => runtimeState,
            actionExecutionBoundary: (lease, arguments, requiresApproval, cancellationToken) =>
                durableTurn.PrepareExecutionAsync(
                    lease,
                    "call-1",
                    arguments,
                    requiresApproval,
                    cancellationToken));
        var context = await terminal.ApplyTerminalContextAsync(
            new AIContext { Tools = [function] },
            TestContext.Current.CancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!));

        current = Change(original, "runtime");
        var result = await guarded.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
            && reason.DependencyId == "runtime-bindings");
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task SemanticBindingFingerprint_IsStableAndBindsSchemaAndEmbeddingSpace()
    {
        using var httpClient = new HttpClient();
        await using var qdrant = new QdrantServiceManager(Path.GetTempPath());
        var settings = new LocalVectorLibrarySettings();
        var liveSettings = settings;
        var catalog = new QdrantSemanticToolCatalog(httpClient, qdrant, () => liveSettings);
        var textTool = (AIFunctionDeclaration)AIFunctionFactory.Create(
            (string value) => value,
            "schema_tool",
            "Use the supplied value.");
        var numberTool = (AIFunctionDeclaration)AIFunctionFactory.Create(
            (int value) => value,
            "schema_tool",
            "Use the supplied value.");

        var baseline = catalog.CaptureBindingFingerprint([textTool]);

        Assert.Equal(baseline, catalog.CaptureBindingFingerprint([textTool]));
        Assert.NotEqual(baseline, catalog.CaptureBindingFingerprint([numberTool]));

        liveSettings = settings with { EmbeddingModel = settings.EmbeddingModel + "-changed" };
        Assert.NotEqual(baseline, catalog.CaptureBindingFingerprint([textTool]));

        liveSettings = settings with { EmbeddingDimensions = settings.EmbeddingDimensions + 1 };
        Assert.NotEqual(baseline, catalog.CaptureBindingFingerprint([textTool]));

        liveSettings = settings with { };
        Assert.Equal(baseline, catalog.CaptureBindingFingerprint([textTool]));
    }

    private static TurnRuntimeBindings Change(
        TurnRuntimeBindings original,
        string changedBinding)
    {
        var changed = Digest("changed-" + changedBinding);
        return changedBinding switch
        {
            "assistant-profile" => original with { AssistantProfileDigest = changed },
            "runtime" => original with { RuntimeDigest = changed },
            "model" => original with { ModelDigest = changed },
            "generation-settings" => original with { GenerationSettingsDigest = changed },
            "capability-registry" => original with { CapabilityRegistryDigest = changed },
            "permissions" => original with { PermissionDigest = changed },
            "mcp" => original with { McpDigest = changed },
            "attachments" => original with { AttachmentDigest = changed },
            "artifacts" => original with { ArtifactDigest = changed },
            _ => throw new ArgumentOutOfRangeException(nameof(changedBinding), changedBinding, null)
        };
    }

    private static TurnRuntimeBindings Bindings() =>
        new(
            Digest("profile"),
            Digest("runtime"),
            Digest("model"),
            Digest("generation"),
            Digest("registry"),
            Digest("permission"),
            Digest("mcp"),
            Digest("attachment"),
            Digest("artifact"));

    private static BoundModelDispatchSnapshot Snapshot(
        IChatClient client,
        ModelProfile? modelProfile = null)
    {
        var profile = modelProfile ?? (PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = false
        });
        return new BoundModelDispatchSnapshot(
            client,
            profile,
            new BoundRuntimeBindingMaterial(
                "test-runtime",
                "test-client",
                profile.RuntimeKind,
                profile.RuntimeLocation,
                profile.RuntimeEndpoint),
            new BoundModelBindingMaterial(
                profile.ProfileId,
                profile.PackageId,
                profile.Family,
                profile.Size,
                profile.Quantization,
                profile.SupportsVision,
                profile.SupportsToolCalls),
            new BoundGenerationSettingsBindingMaterial(
                profile.ContextTokens,
                profile.OutputTokenLimit,
                profile.Temperature,
                TopP: 0.9,
                StreamingEnabled: profile.StreamingEnabled,
                ThinkingControl: "test",
                ThinkingEnabled: false,
                ReasoningEffort: "low"));
    }

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private static TurnIdentity Identity() =>
        new("user", "conversation", "assistant-message");

    private static CoordinatorTurnContext TurnContext(TurnIdentity identity, string request) =>
        new(
            identity.ConversationId,
            "user-message",
            identity.AssistantMessageId,
            request,
            publish: _ => { },
            observationIdentity: identity);

    private static AIFunction ReadTool() => AIFunctionFactory.Create(
        (string path) => path,
        "read_file",
        "Read a file by exact path.");

    private static ChatResponse Compatibility(string json) =>
        new(new ChatMessage(ChatRole.Assistant, json))
        {
            FinishReason = ChatFinishReason.Stop
        };

    private static async Task ConsumeResponseAsync(
        IChatClient client,
        CancellationToken cancellationToken) =>
        _ = await client.GetResponseAsync([], new ChatOptions(), cancellationToken);

    private static async Task ConsumeStreamingAsync(
        IChatClient client,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in client
                           .GetStreamingResponseAsync([], new ChatOptions(), cancellationToken)
                           .ConfigureAwait(false))
        {
        }
    }

    private static async Task ConsumeRuntimeStreamingAsync(
        ILocalModelRuntime runtime,
        CancellationToken cancellationToken)
    {
        var request = new ChatRequest(
            "gate-test-conversation",
            "gate-test-user-message",
            "hold the dispatch lease",
            Array.Empty<Ali.Modules.Runtime.ChatMessage>());
        await foreach (var _ in runtime
                           .StreamChatAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
        }
    }

    private static CapabilityRuntimeStateSnapshot RuntimeState(
        CanonicalCapabilityRegistry registry) =>
        new(
            "user",
            "provider-revision",
            [AliProductionCapabilityCatalog.ProviderId],
            targetResolution: null,
            "permission-revision",
            registry.Descriptors
                .Select(descriptor => descriptor.Permission.PolicyId)
                .Distinct(StringComparer.Ordinal),
            "mcp-revision",
            readyIncomingMcpToolNames: [],
            enabledOutgoingMcpToolNames: registry.Descriptors
                .Where(descriptor => descriptor.McpExposure.Exposed)
                .Select(descriptor => descriptor.ToolName),
            "reconciler-revision",
            registry.Descriptors
                .Where(descriptor => descriptor.Effect.ReconcilerId is not null)
                .Select(descriptor => descriptor.Effect.ReconcilerId!)
                .Distinct(StringComparer.Ordinal));

    private sealed class CallbackChatClient(
        Func<int, ChatResponse> responseFactory) : IChatClient
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestNumber = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(responseFactory(requestNumber));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
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

    private sealed class BoundRuntime(
        ModelProfile profile,
        ChatResponse response,
        ChatClientMetadata? metadata = null,
        object? discoveredService = null) :
        ILocalModelRuntime,
        IModelSwitchAwareRuntime,
        IChatClient,
        IBoundModelDispatchSource
    {
        private int _healthCheckCount;
        private int _requestCount;
        private int _unloadCount;

        public ModelProfile ActiveProfile { get; } = profile;

        public string RuntimeIdentity { get; } = profile.PackageId;

        internal int HealthCheckCount => Volatile.Read(ref _healthCheckCount);

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal int UnloadCount => Volatile.Read(ref _unloadCount);

        BoundModelDispatchSnapshot IBoundModelDispatchSource.CaptureBoundModelDispatch() =>
            Snapshot(this, ActiveProfile);

        public async IAsyncEnumerable<ModelToken> StreamChatAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new ModelToken(
                response.Text ?? string.Empty,
                Ali.Modules.Evidence.EvidenceStatus.Unverified);
        }

        public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _healthCheckCount);
            return Task.FromResult(new RuntimeHealthCheck(
                true,
                "ready",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));
        }

        public Task UnloadForModelSwitchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _unloadCount);
            return Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var exact = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in exact.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey is not null)
            {
                return null;
            }

            if (serviceType == typeof(ChatClientMetadata))
            {
                return metadata;
            }

            if (discoveredService is not null
                && serviceType.IsInstanceOfType(discoveredService))
            {
                return discoveredService;
            }

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ArbitraryRuntimeService
    {
    }

    private sealed class GateControlledRuntime :
        ILocalModelRuntime,
        IModelSwitchAwareRuntime,
        IChatClient,
        IBoundModelDispatchSource
    {
        private readonly ChatResponse _response;
        private readonly TaskCompletionSource<bool> _responseEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _responseRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _shutdownEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _shutdownRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _healthCheckCount;
        private int _requestCount;
        private int _shutdownCount;
        private int _unloadCount;

        internal GateControlledRuntime(
            ModelProfile profile,
            ChatResponse response,
            bool blockResponse = false,
            bool blockShutdown = false)
        {
            ActiveProfile = profile;
            RuntimeIdentity = profile.PackageId;
            _response = response;
            if (!blockResponse)
            {
                _responseRelease.TrySetResult(true);
            }

            if (!blockShutdown)
            {
                _shutdownRelease.TrySetResult(true);
            }
        }

        public ModelProfile ActiveProfile { get; }

        public string RuntimeIdentity { get; }

        internal int HealthCheckCount => Volatile.Read(ref _healthCheckCount);

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal int ShutdownCount => Volatile.Read(ref _shutdownCount);

        internal int UnloadCount => Volatile.Read(ref _unloadCount);

        internal Task ResponseEntered => _responseEntered.Task;

        internal Task ShutdownEntered => _shutdownEntered.Task;

        internal void ReleaseResponse() => _responseRelease.TrySetResult(true);

        internal void ReleaseShutdown() => _shutdownRelease.TrySetResult(true);

        BoundModelDispatchSnapshot IBoundModelDispatchSource.CaptureBoundModelDispatch() =>
            Snapshot(this, ActiveProfile);

        public async IAsyncEnumerable<ModelToken> StreamChatAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var response = await GetResponseAsync([], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            yield return new ModelToken(
                response.Text ?? string.Empty,
                Ali.Modules.Evidence.EvidenceStatus.Unverified);
        }

        public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _healthCheckCount);
            return Task.FromResult(new RuntimeHealthCheck(
                true,
                "ready",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));
        }

        public Task UnloadForModelSwitchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _unloadCount);
            return Task.CompletedTask;
        }

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _shutdownCount);
            _shutdownEntered.TrySetResult(true);
            await _shutdownRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            _responseEntered.TrySetResult(true);
            await _responseRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
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

    private sealed class FixedSemanticCatalog(
        IReadOnlyList<AIFunctionDeclaration> selected) : ISemanticToolCatalog
    {
        public Task<SemanticToolSelection> SelectAsync(
            string need,
            IReadOnlyList<AIFunctionDeclaration> liveTools,
            IReadOnlyCollection<string> retainedToolNames,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SemanticToolSelection(
                selected,
                ["test"],
                "Test directory",
                UsedSemanticIndex: false,
                "Selected."));

        public Task<SemanticToolDiscoveryResult> DiscoverAsync(
            string need,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SemanticToolDiscoveryResult(need, [], [], "Not used."));
    }

    private sealed class MemoryCapabilitySettingsPersistence :
        ICapabilityAvailabilitySettingsPersistence
    {
        private CapabilityAvailabilitySettings _settings =
            CapabilityAvailabilitySettings.CreateDefault();

        public CapabilityAvailabilityLoadResult Load() =>
            CapabilityAvailabilityLoadResult.Loaded(_settings);

        public CapabilityAvailabilitySaveResult Save(
            string expectedRevision,
            CapabilityAvailabilitySettings settings)
        {
            if (!string.Equals(expectedRevision, _settings.Revision, StringComparison.Ordinal))
            {
                return CapabilityAvailabilitySaveResult.Conflict(_settings);
            }

            _settings = new CapabilityAvailabilitySettings(settings.GroupSelections);
            return CapabilityAvailabilitySaveResult.Saved(_settings);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
