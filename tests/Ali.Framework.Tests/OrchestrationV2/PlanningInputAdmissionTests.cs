using System.Runtime.CompilerServices;
using System.Text;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class PlanningInputAdmissionTests
{
    [Fact]
    public void GptOss65K_NormalPlanningPromptIsAdmittedWithO200kCounter()
    {
        var admission = new AliPlanningInputAdmission();
        var tool = AIFunctionFactory.Create(
            (string path) => path,
            "read_file",
            "Read a file by exact path.");
        var protocol = AliOrchestrationProtocol.CreateDeclaration([tool]);
        var result = admission.Evaluate(
            PlanningTestModelProfile.GptOss65K(),
            [
                new ChatMessage(ChatRole.System, "Follow the typed orchestration protocol."),
                new ChatMessage(ChatRole.User, "Inspect the repository and report exact evidence.")
            ],
            [tool],
            protocol);

        Assert.True(result.IsAdmitted, result.FailureCode);
        Assert.InRange(result.CalculatedInputCharge!.Value, 1, 57_344);
        Assert.Equal(
            "gpt-oss-o200k-harmony-text-exact-with-conservative-protocol-reserve",
            result.CounterMode);
    }

    [Fact]
    public void InjectedCounter_AdmitsExactFitAndRejectsOneTokenOver()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 1_000,
            OutputTokenLimit = 200
        };
        var messages = new[] { new ChatMessage(ChatRole.User, "exact input") };
        var protocol = AliOrchestrationProtocol.CreateDeclaration([]);

        var exact = new AliPlanningInputAdmission(new FixedChargeCounter(800, "fixed-exact"))
            .Evaluate(profile, messages, [], protocol);
        var over = new AliPlanningInputAdmission(new FixedChargeCounter(801, "fixed-over"))
            .Evaluate(profile, messages, [], protocol);

        Assert.True(exact.IsAdmitted);
        Assert.Equal(800, exact.InputBudget);
        Assert.Equal(800, exact.CalculatedInputCharge);
        Assert.False(over.IsAdmitted);
        Assert.Equal("model-input-exceeds-configured-context", over.FailureCode);
    }

    [Fact]
    public void UnknownModelCounter_ChargesAuthorBinaryAndToolMaterialWithoutBase64Allocation()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            DisplayName = "Generic local model",
            PackageId = "local/generic-model",
            Family = "generic"
        };
        var protocol = AliOrchestrationProtocol.CreateDeclaration([]);
        var baselineMessage = new ChatMessage(ChatRole.User, "abc");
        var baseline = AliModelAwarePlanningInputCounter.Instance.Count(
            profile,
            [baselineMessage],
            [],
            protocol);

        var authorMessage = new ChatMessage(ChatRole.User, "abc")
        {
            AuthorName = "ali-user"
        };
        var withAuthor = AliModelAwarePlanningInputCounter.Instance.Count(
            profile,
            [authorMessage],
            [],
            protocol);
        Assert.Equal(
            Encoding.UTF8.GetByteCount("ali-user"),
            withAuthor.ChargedTokens - baseline.ChargedTokens);

        var binaryMessage = new ChatMessage(ChatRole.User, "abc");
        binaryMessage.Contents.Add(new DataContent(
            new byte[] { 1, 2, 3, 4 },
            "image/png")
        {
            Name = "a.bin"
        });
        var withBinary = AliModelAwarePlanningInputCounter.Instance.Count(
            profile,
            [binaryMessage],
            [],
            protocol);
        var expectedBinaryCharge = AliModelAwarePlanningInputCounter.ContentSegmentOverheadTokens
            + Encoding.UTF8.GetByteCount("image/png")
            + Encoding.UTF8.GetByteCount("a.bin")
            + AliModelAwarePlanningInputCounter.BinarySegmentOverheadTokens
            + 8; // Four bytes require eight Base64 transport characters.
        Assert.Equal(
            expectedBinaryCharge,
            withBinary.ChargedTokens - baseline.ChargedTokens);

        var tool = AIFunctionFactory.Create(
            (string path) => path,
            "read_file",
            "Read a file by exact path.");
        var withTool = AliModelAwarePlanningInputCounter.Instance.Count(
            profile,
            [baselineMessage],
            [tool],
            protocol);
        var expectedToolCharge = AliModelAwarePlanningInputCounter.ToolSegmentOverheadTokens
            + Encoding.UTF8.GetByteCount(tool.Name)
            + Encoding.UTF8.GetByteCount(tool.Description ?? string.Empty)
            + Encoding.UTF8.GetByteCount(tool.JsonSchema.GetRawText());
        Assert.Equal(
            expectedToolCharge,
            withTool.ChargedTokens - baseline.ChargedTokens);
        Assert.Equal("utf8-byte-upper-bound", withTool.CounterMode);
        Assert.True(withTool.CanSafelyCharge);
    }

    [Fact]
    public async Task OversizedAttachment_MakesZeroModelCallsAndPreservesExactProjection()
    {
        var bytes = Enumerable.Range(0, 100_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var projection = AliPlanningAttachmentProjection.Capture(
            [new DataContent(bytes, "application/octet-stream") { Name = "large.bin" }]);
        var model = new CountingChatClient();
        var observer = new AdmissionObserver();
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 1_024
        };
        using var client = new AliOrchestrationPlanningClient(
            model,
            () => false,
            () => profile);
        using var scope = client.BeginTurn(
            Turn(),
            Input(),
            observer,
            projection);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, model.CallCount);
        Assert.Equal(AliPlanningInterimKind.ModelInputNotAdmitted, observer.PreparedKind);
        Assert.Equal(
            AliPlanningInterimKind.ModelInputNotAdmitted,
            client.PreparedInterimResponse!.Kind);
        Assert.Contains("calculated input charge", response.Text, StringComparison.Ordinal);
        Assert.Contains("counter mode: gpt-oss-o200k-", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(bytes), response.Text, StringComparison.Ordinal);
        var retained = Assert.IsType<DataContent>(projection.Materialize().Single());
        Assert.Equal(bytes, retained.Data.ToArray());
        Assert.Equal("large.bin", retained.Name);
    }

    [Fact]
    public async Task OversizedProtectedHistory_IsDurablySuspendedWithoutModelCallOrContentLoss()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "admission-turn");
        var bindings = Bindings();
        var exactHistory = string.Concat(Enumerable.Repeat(
            "This exact accepted assistant history remains referential context. ",
            4_000));
        var accepted = new[]
        {
            new AcceptedConversationInput(
                "history-assistant",
                0,
                exactHistory,
                AcceptedConversationRole.Assistant)
        };
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using var durableTurn = await coordinator.BeginTurnAsync(
            Turn(identity),
            bindings,
            accepted,
            capabilityRegistry: null,
            liveBindingsAccessor: null,
            TestContext.Current.CancellationToken);
        var model = new CountingChatClient();
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 1_024
        };
        using var client = new AliOrchestrationPlanningClient(
            model,
            () => false,
            () => profile,
            boundDispatchAccessor: () => Snapshot(model, profile),
            dispatchBindingsFactory: _ => bindings);
        using var scope = client.BeginTurn(
            Turn(identity),
            durableTurn.Input,
            durableTurn,
            durableIdentity: identity,
            immutableOriginalRequest: "Complete the exact durable request.");

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, model.CallCount);
        Assert.DoesNotContain(exactHistory[..64], response.Text, StringComparison.Ordinal);
        var prepared = Assert.IsType<AliPreparedInterimResponse>(client.PreparedInterimResponse);
        Assert.Equal(AliPlanningInterimKind.ModelInputNotAdmitted, prepared.Kind);
        await durableTurn.CommitInterimPublicationAsync(
            prepared,
            TestContext.Current.CancellationToken);

        var recovery = await coordinator.RecoverTurnAsync(
            identity,
            bindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnControlState.SuspendedRuntime, recovery.State!.Control);
        Assert.Equal(
            InterimPublicationReason.ModelInputNotAdmitted,
            recovery.State.InterimPublication!.Reason);
        Assert.Equal(
            "model-input-not-admitted",
            AliDurablePlanningTurn.InterimReasonCode(
                recovery.State.InterimPublication.Reason));

        using var reader = new TurnTransitionWriter(directory.Path, "profile");
        var recoveredHistory = await reader.ReadAcceptedPriorConversationAsync(
            identity,
            TestContext.Current.CancellationToken);
        var exact = Assert.Single(recoveredHistory);
        Assert.Equal(AcceptedConversationRole.Assistant, exact.Role);
        Assert.Equal(exactHistory, exact.Text);
    }

    [Fact]
    public async Task ExplicitResume_StillOversized_RerunsAdmissionAndMakesZeroModelCalls()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "still-oversized-turn");
        var bindings = Bindings();
        var exactHistory = string.Concat(Enumerable.Repeat(
            "This accepted assistant context remains exact across the explicit retry. ",
            4_000));
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 1_024
        };
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using (var initialTurn = await coordinator.BeginTurnAsync(
                         Turn(identity),
                         bindings,
                         [new AcceptedConversationInput(
                             "history-assistant",
                             0,
                             exactHistory,
                             AcceptedConversationRole.Assistant)],
                         capabilityRegistry: null,
                         liveBindingsAccessor: () => bindings,
                         TestContext.Current.CancellationToken))
        {
            var firstModel = new CountingChatClient();
            using var firstClient = new AliOrchestrationPlanningClient(
                firstModel,
                () => false,
                () => profile,
                boundDispatchAccessor: () => Snapshot(firstModel, profile),
                dispatchBindingsFactory: _ => bindings);
            using var firstScope = firstClient.BeginTurn(
                Turn(identity),
                initialTurn.Input,
                initialTurn,
                durableIdentity: identity,
                immutableOriginalRequest: "Complete the exact durable request.");
            _ = await firstClient.GetResponseAsync(
                [],
                new ChatOptions(),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, firstModel.CallCount);
            await initialTurn.CommitInterimPublicationAsync(
                Assert.IsType<AliPreparedInterimResponse>(firstClient.PreparedInterimResponse),
                TestContext.Current.CancellationToken);
        }

        var visible = VisibleTurn(identity, "still-oversized-visible");
        var resumedAttempt = await coordinator.ResumeTurnAsync(
            visible,
            identity,
            bindings,
            "Retry the exact preserved request.",
            "still-oversized-steering",
            capabilityRegistry: null,
            liveBindingsAccessor: () => bindings,
            TestContext.Current.CancellationToken);

        Assert.True(resumedAttempt.IsReady, resumedAttempt.FailureCode);
        await using var resumed = Assert.IsType<AliDurablePlanningTurn>(resumedAttempt.Turn);
        Assert.Equal(exactHistory, resumed.Input.AcceptedPriorConversation[0].Text);
        Assert.True(resumed.Input.AcceptedPriorConversation[^1].IsSteering);
        var persistedAfterClear = await coordinator.RecoverTurnAsync(
            identity,
            bindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnControlState.Running, persistedAfterClear.State!.Control);
        Assert.Null(persistedAfterClear.State.InterimPublication);

        var retryModel = new CountingChatClient();
        using var retryClient = new AliOrchestrationPlanningClient(
            retryModel,
            () => false,
            () => profile,
            boundDispatchAccessor: () => Snapshot(retryModel, profile),
            dispatchBindingsFactory: _ => bindings);
        using var retryScope = retryClient.BeginTurn(
            visible,
            resumed.Input,
            resumed,
            durableIdentity: identity,
            immutableOriginalRequest: resumed.ImmutableOriginalRequest);
        _ = await retryClient.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, retryModel.CallCount);
        Assert.Equal(
            AliPlanningInterimKind.ModelInputNotAdmitted,
            retryClient.PreparedInterimResponse!.Kind);
    }

    [Fact]
    public async Task ExplicitResume_WithLargerValidContext_AdoptsOnlyGenerationBindingAndCallsModel()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "larger-context-turn");
        var initialBindings = Bindings();
        var largerBindings = initialBindings with
        {
            GenerationSettingsDigest = Digest("generation-larger-context")
        };
        var attachmentBytes = Enumerable.Range(0, 10_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var attachments = AliPlanningAttachmentProjection.Capture(
            [new DataContent(attachmentBytes, "application/octet-stream") { Name = "exact.bin" }]);
        var largerProfile = PlanningTestModelProfile.GptOss65K() with
        {
            ProtocolIdentity = RuntimeProtocolIdentities.NativeOpenAiTools,
            CapabilityProfileIdentity = "test-probed-native-tools-v1"
        };
        var smallProfile = largerProfile with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 1_024
        };
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using (var initialTurn = await coordinator.BeginTurnAsync(
                         Turn(identity),
                         initialBindings,
                         acceptedPriorConversation: [],
                         capabilityRegistry: null,
                         liveBindingsAccessor: () => initialBindings,
                         TestContext.Current.CancellationToken))
        {
            var firstModel = new CountingChatClient();
            using var firstClient = new AliOrchestrationPlanningClient(
                firstModel,
                () => false,
                () => smallProfile,
                boundDispatchAccessor: () => Snapshot(firstModel, smallProfile),
                dispatchBindingsFactory: _ => initialBindings);
            using var firstScope = firstClient.BeginTurn(
                Turn(identity),
                initialTurn.Input,
                initialTurn,
                attachments,
                identity,
                "Complete the exact durable request.");
            _ = await firstClient.GetResponseAsync(
                [],
                new ChatOptions(),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, firstModel.CallCount);
            await initialTurn.CommitInterimPublicationAsync(
                Assert.IsType<AliPreparedInterimResponse>(firstClient.PreparedInterimResponse),
                TestContext.Current.CancellationToken);
        }

        var visible = VisibleTurn(identity, "larger-context-visible");
        var resumedAttempt = await coordinator.ResumeTurnAsync(
            visible,
            identity,
            largerBindings,
            "Retry with the deliberately enlarged context.",
            "larger-context-steering",
            capabilityRegistry: null,
            liveBindingsAccessor: () => largerBindings,
            TestContext.Current.CancellationToken);

        Assert.True(resumedAttempt.IsReady, resumedAttempt.FailureCode);
        await using var resumed = Assert.IsType<AliDurablePlanningTurn>(resumedAttempt.Turn);
        var persisted = await coordinator.RecoverTurnAsync(
            identity,
            largerBindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(largerBindings, persisted.State!.Bindings);
        Assert.Equal(TurnControlState.Running, persisted.State.Control);
        Assert.Null(persisted.State.InterimPublication);
        Assert.Equal(attachmentBytes, Assert.IsType<DataContent>(attachments.Materialize().Single())
            .Data.ToArray());

        var nativeDecision = PlanningContractTests.DecisionJson(
            "{\"kind\":\"answerDirectly\",\"answer\":\"Admitted after context expansion.\"}");
        var nativeMessage = new ChatMessage(ChatRole.Assistant, string.Empty);
        nativeMessage.Contents.Add(new FunctionCallContent(
            "call-admitted-after-context-expansion",
            OrchestrationProtocolCapability.ToolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AliOrchestrationProtocol.DecisionJsonPropertyName] = nativeDecision
            }));
        var model = new CountingChatClient(new ChatResponse(nativeMessage)
        {
            FinishReason = ChatFinishReason.ToolCalls
        });
        using var client = new AliOrchestrationPlanningClient(
            model,
            () => false,
            () => largerProfile,
            boundDispatchAccessor: () => Snapshot(model, largerProfile),
            dispatchBindingsFactory: _ => largerBindings);
        using var scope = client.BeginTurn(
            visible,
            resumed.Input,
            resumed,
            attachments,
            identity,
            resumed.ImmutableOriginalRequest);
        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        // The exact bound native profile stays on its one proven transport.
        Assert.Equal(1, model.CallCount);
        Assert.Equal("Admitted after context expansion.", response.Text);
    }

    [Fact]
    public async Task ExplicitResume_WithUnrelatedBindingChange_FailsWithExactBindingAndLeavesPause()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "unrelated-binding-turn");
        var initialBindings = Bindings();
        var changedBindings = initialBindings with
        {
            PermissionDigest = Digest("changed-permissions")
        };
        var attachmentBytes = new byte[10_000];
        var attachments = AliPlanningAttachmentProjection.Capture(
            [new DataContent(attachmentBytes, "application/octet-stream") { Name = "exact.bin" }]);
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 1_024
        };
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using (var initialTurn = await coordinator.BeginTurnAsync(
                         Turn(identity),
                         initialBindings,
                         acceptedPriorConversation: [],
                         capabilityRegistry: null,
                         liveBindingsAccessor: () => initialBindings,
                         TestContext.Current.CancellationToken))
        {
            var model = new CountingChatClient();
            using var client = new AliOrchestrationPlanningClient(
                model,
                () => false,
                () => profile,
                boundDispatchAccessor: () => Snapshot(model, profile),
                dispatchBindingsFactory: _ => initialBindings);
            using var scope = client.BeginTurn(
                Turn(identity),
                initialTurn.Input,
                initialTurn,
                attachments,
                identity,
                "Complete the exact durable request.");
            _ = await client.GetResponseAsync(
                [],
                new ChatOptions(),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, model.CallCount);
            await initialTurn.CommitInterimPublicationAsync(
                Assert.IsType<AliPreparedInterimResponse>(client.PreparedInterimResponse),
                TestContext.Current.CancellationToken);
        }

        var attempt = await coordinator.ResumeTurnAsync(
            VisibleTurn(identity, "unrelated-binding-visible"),
            identity,
            changedBindings,
            "Do not accept unrelated binding changes.",
            "unrelated-binding-steering",
            capabilityRegistry: null,
            liveBindingsAccessor: () => changedBindings,
            TestContext.Current.CancellationToken);

        Assert.False(attempt.IsReady);
        Assert.Equal(
            "model-input-admission-unrelated-bindings-changed",
            attempt.FailureCode);
        Assert.Equal(["permissions"], attempt.Recovery.ChangedBindings);
        Assert.Equal(initialBindings, attempt.Recovery.State!.Bindings);
        Assert.Equal(TurnControlState.SuspendedRuntime, attempt.Recovery.State.Control);
        Assert.Equal(0, attempt.Recovery.State.SteeringCursor);
        Assert.Equal(
            InterimPublicationReason.ModelInputNotAdmitted,
            attempt.Recovery.State.InterimPublication!.Reason);
    }

    [Fact]
    public async Task ExplicitResume_WithoutExactReattachment_FailsClosedAndMakesZeroModelCalls()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "missing-attachment-turn");
        var attachedBindings = Bindings();
        var missingAttachmentBindings = attachedBindings with
        {
            AttachmentDigest = Digest("no-current-turn-attachments")
        };
        var attachmentBytes = new byte[10_000];
        var attachments = AliPlanningAttachmentProjection.Capture(
            [new DataContent(attachmentBytes, "application/octet-stream") { Name = "exact.bin" }]);
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 1_024
        };
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        var model = new CountingChatClient();
        await using (var initialTurn = await coordinator.BeginTurnAsync(
                         Turn(identity),
                         attachedBindings,
                         acceptedPriorConversation: [],
                         capabilityRegistry: null,
                         liveBindingsAccessor: () => attachedBindings,
                         TestContext.Current.CancellationToken))
        {
            using var client = new AliOrchestrationPlanningClient(
                model,
                () => false,
                () => profile,
                boundDispatchAccessor: () => Snapshot(model, profile),
                dispatchBindingsFactory: _ => attachedBindings);
            using var scope = client.BeginTurn(
                Turn(identity),
                initialTurn.Input,
                initialTurn,
                attachments,
                identity,
                "Complete the exact durable request.");
            _ = await client.GetResponseAsync(
                [],
                new ChatOptions(),
                TestContext.Current.CancellationToken);
            await initialTurn.CommitInterimPublicationAsync(
                Assert.IsType<AliPreparedInterimResponse>(client.PreparedInterimResponse),
                TestContext.Current.CancellationToken);
        }

        var attempt = await coordinator.ResumeTurnAsync(
            VisibleTurn(identity, "missing-attachment-visible"),
            identity,
            missingAttachmentBindings,
            "Resume without reattaching the original bytes.",
            "missing-attachment-steering",
            capabilityRegistry: null,
            liveBindingsAccessor: () => missingAttachmentBindings,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, model.CallCount);
        Assert.False(attempt.IsReady);
        Assert.Equal(
            "model-input-admission-unrelated-bindings-changed",
            attempt.FailureCode);
        Assert.Equal(["attachments"], attempt.Recovery.ChangedBindings);
        Assert.Equal(attachedBindings, attempt.Recovery.State!.Bindings);
        Assert.Equal(TurnControlState.SuspendedRuntime, attempt.Recovery.State.Control);
        Assert.Equal(0, attempt.Recovery.State.SteeringCursor);
        Assert.Equal(
            InterimPublicationReason.ModelInputNotAdmitted,
            attempt.Recovery.State.InterimPublication!.Reason);
    }

    [Fact]
    public void InvalidOutputReserveAtOrAboveContext_IsRejectedWithoutCounting()
    {
        var counter = new FixedChargeCounter(1, "must-not-run");
        var admission = new AliPlanningInputAdmission(counter);
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 8_192,
            OutputTokenLimit = 8_192
        };

        var result = admission.Evaluate(
            profile,
            [new ChatMessage(ChatRole.User, "preserve me")],
            [],
            AliOrchestrationProtocol.CreateDeclaration([]));

        Assert.False(result.IsAdmitted);
        Assert.Equal(0, counter.CallCount);
        Assert.Equal("configuration-validation", result.CounterMode);
        Assert.Equal("invalid-model-context-settings", result.FailureCode);
    }

    private static AliPlanningTurnInput Input() => new(
        0,
        "No work has been accepted yet.",
        workGraphRevision: 0,
        authoritativeWorkGraph: WorkGraphSnapshot.Empty);

    private static CoordinatorTurnContext Turn() => new(
        "conversation",
        "user-message",
        "assistant-message",
        "Complete the exact request.",
        _ => { });

    private static CoordinatorTurnContext Turn(TurnIdentity identity) => new(
        identity.ConversationId,
        "user-message",
        identity.AssistantMessageId,
        "Complete the exact durable request.",
        _ => { },
        observationIdentity: identity);

    private static CoordinatorTurnContext VisibleTurn(
        TurnIdentity durableIdentity,
        string assistantMessageId)
    {
        var visibleIdentity = new TurnIdentity(
            durableIdentity.UserId,
            durableIdentity.ConversationId,
            assistantMessageId);
        return new CoordinatorTurnContext(
            durableIdentity.ConversationId,
            "visible-user-message",
            assistantMessageId,
            "Resume the exact durable request.",
            _ => { },
            observationIdentity: visibleIdentity);
    }

    private static TurnRuntimeBindings Bindings() => new(
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
        ModelProfile profile) =>
        new(
            client,
            profile,
            new BoundRuntimeBindingMaterial(
                "test-runtime",
                "test-client",
                profile.RuntimeKind,
                profile.RuntimeLocation,
                profile.RuntimeEndpoint)
            {
                ProtocolIdentity = profile.ProtocolIdentity,
                CapabilityProfileIdentity = profile.CapabilityProfileIdentity
            },
            new BoundModelBindingMaterial(
                profile.ProfileId,
                profile.PackageId,
                profile.Family,
                profile.Size,
                profile.Quantization,
                profile.SupportsVision,
                profile.SupportsToolCalls)
            {
                CapabilityProfileIdentity = profile.CapabilityProfileIdentity
            },
            new BoundGenerationSettingsBindingMaterial(
                profile.ContextTokens,
                profile.OutputTokenLimit,
                profile.Temperature,
                TopP: 0.9,
                StreamingEnabled: profile.StreamingEnabled,
                ThinkingControl: "test",
                ThinkingEnabled: false,
                ReasoningEffort: "low")
            {
                ProtocolIdentity = profile.ProtocolIdentity
            });

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class FixedChargeCounter(long charge, string mode) : IAliPlanningInputCounter
    {
        internal int CallCount { get; private set; }

        public AliPlanningInputCharge Count(
            ModelProfile profile,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<AIFunctionDeclaration> selectedTools,
            AIFunctionDeclaration? protocol)
        {
            CallCount++;
            return new AliPlanningInputCharge(charge, mode, CanSafelyCharge: true);
        }
    }

    private sealed class CountingChatClient(ChatResponse? response = null) : IChatClient
    {
        internal int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return response is null
                ? throw new InvalidOperationException("The rejected input must not reach the model.")
                : Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = await GetResponseAsync(messages, options, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class AdmissionObserver : IAliPlanningTransitionObserver
    {
        internal AliPlanningInterimKind? PreparedKind { get; private set; }

        public ValueTask<AliPlanningTransitionReceipt> OnDecisionAcceptedAsync(
            AliPlanningDecisionAcceptedEvent accepted,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No decision can be accepted for rejected input.");

        public ValueTask<AliPlanningEvidenceReceipt> OnToolResultObservedAsync(
            AliPlanningToolResultObservedEvent observed,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No tool result is expected.");

        public ValueTask<AliPlanningTransitionReceipt> OnPlanningSuspendedAsync(
            AliPlanningSuspendedEvent suspended,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningTransitionReceipt(
                suspended.ExpectedStateRevision));

        public ValueTask<AliPlanningTransitionReceipt> OnInterimResponsePreparedAsync(
            AliPlanningInterimPreparedEvent prepared,
            CancellationToken cancellationToken)
        {
            PreparedKind = prepared.Kind;
            return ValueTask.FromResult(new AliPlanningTransitionReceipt(
                prepared.ExpectedStateRevision + 1));
        }

        public ValueTask<AliPlanningPublicationReceipt> OnFinalAnswerPreparedAsync(
            AliPlanningPublicationPreparedEvent prepared,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No final answer is expected.");
    }
}
