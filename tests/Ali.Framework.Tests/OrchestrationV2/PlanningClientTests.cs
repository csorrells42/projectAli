using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.ToolDiscovery;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class PlanningClientTests
{
    public static TheoryData<ChatFinishReason> ExplicitCompatibilityNonStopReasons => new()
    {
        ChatFinishReason.Length,
        ChatFinishReason.ToolCalls,
        ChatFinishReason.ContentFilter,
        new ChatFinishReason("provider-specific-incomplete")
    };

    [Fact]
    public async Task FinalRenderer_IsAppliedBeforeExactPublicationPreparation()
    {
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Forecast ready.\"}")));
        var observer = new RecordingTransitionObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            finalAnswerRenderer: static (activeTurn, answer) =>
                FinalAnswerRenderer.Compose(answer, activeTurn.WebSources));
        var turn = CreateTurn("weather", out _);
        turn.WebSources.Add(new CoordinatorSourceItem(
            "Weather [Office]",
            "weather",
            "https://example.test/weather",
            DateTimeOffset.UtcNow,
            "Clear"));
        using var scope = client.BeginTurn(turn, Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        var expected = FinalAnswerRenderer.Compose("Forecast ready.", turn.WebSources);
        Assert.Equal(expected, response.Text);
        var preparedEvent = Assert.Single(observer.Publications);
        Assert.Equal(expected, preparedEvent.AnswerText);
        Assert.Equal(TurnStateIntegrity.Digest(expected), preparedEvent.AnswerDigest);
        var prepared = client.RequirePreparedFinalPublication();
        Assert.Equal("publication_assistant-message", prepared.PublicationId);
        Assert.Equal("assistant-message", prepared.AssistantMessageId);
        Assert.Equal(expected, prepared.AnswerText);
        Assert.Equal(preparedEvent.AnswerDigest, prepared.AnswerDigest);
    }

    [Fact]
    public async Task InvalidFinalRendererOutput_FailsBeforePublicationPreparation()
    {
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Model answer\"}")));
        var observer = new RecordingTransitionObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            finalAnswerRenderer: static (_, _) => "   ");
        using var scope = client.BeginTurn(
            CreateTurn("hello", out _),
            Input(),
            observer);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync(
                [],
                new ChatOptions(),
                TestContext.Current.CancellationToken));

        Assert.Empty(observer.Publications);
    }

    [Fact]
    public async Task Hello_UsesOneModelPassAndZeroSemanticSelections()
    {
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Hello!\"}")));
        var semantic = new RecordingSemanticCatalog([]);
        var observer = new RecordingTransitionObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            semantic);
        var turn = CreateTurn("hello", out _);
        using var scope = client.BeginTurn(turn, Input(), observer);
        var rawFrameworkHistory = new[]
        {
            new ChatMessage(ChatRole.Assistant, "REJECTED_ASSISTANT_CANARY")
        };

        var response = await client.GetResponseAsync(
            rawFrameworkHistory,
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Hello!", response.Text);
        Assert.Single(inner.Requests);
        Assert.Equal(0, semantic.SelectCount);
        Assert.Single(observer.Decisions);
        Assert.DoesNotContain(
            inner.Requests[0].Messages,
            message => message.Text?.Contains("REJECTED_ASSISTANT_CANARY", StringComparison.Ordinal) == true);
        Assert.Null(inner.Requests[0].Options.Tools);
        Assert.NotNull(inner.Requests[0].Options.ResponseFormat);
    }

    [Fact]
    public async Task LargeAuthoritativeWorkGraph_DoesNotDuplicateEveryWorkIdIntoTheModelPrompt()
    {
        var nodes = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        for (var index = 0; index < 5_000; index++)
        {
            var id = $"work-id-canary-{index:D5}";
            nodes.Add(
                id,
                new WorkNode(
                    id,
                    "Retain this authoritative work outcome.",
                    ParentId: null,
                    WorkNodeStatus.Pending,
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty));
        }

        var graph = new WorkGraphSnapshot(4, nodes.ToImmutable());
        var input = new AliPlanningTurnInput(
            stateRevision: 12,
            stateProjection: "{\"workGraph\":{\"total\":5000,\"projectedCount\":0,\"items\":[]}}",
            workGraphRevision: graph.Revision,
            authoritativeWorkGraph: graph);
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"requestUserInput\",\"question\":\"Which outcome should I handle next?\",\"missingInformation\":\"The next priority is not known.\"}")));
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(
            CreateTurn("Give me a direct response.", out _),
            input,
            new RecordingTransitionObserver(advanceDecisionWithoutCall: true));

        await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(inner.Requests);
        var modelText = string.Join("\n", request.Messages.Select(message => message.Text));
        Assert.Empty(input.KnownWorkItemIds);
        Assert.DoesNotContain("knownWorkItemIds", modelText, StringComparison.Ordinal);
        Assert.DoesNotContain("work-id-canary-04999", modelText, StringComparison.Ordinal);
        Assert.True(modelText.Length < 20_000, $"Planning prompt was {modelText.Length:N0} characters.");
    }

    [Fact]
    public async Task FiveThousandNodeReceipt_RefreshesPlannerFingerprintFromCachedMerkleRoot()
    {
        var initialNodes = Enumerable.Range(0, 5_000)
            .Select(index => new WorkNode(
                $"node-{index:D5}",
                $"Objective {index}",
                ParentId: null,
                WorkNodeStatus.Pending,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty))
            .ToImmutableArray();
        var initial = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, initialNodes),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(initial.Accepted);
        var activated = WorkGraphApplier.Apply(
            initial.Snapshot,
            new WorkGraphDelta(
                initial.Snapshot.Revision,
                [initial.Snapshot.Nodes["node-02500"] with { Status = WorkNodeStatus.Active }]),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(activated.Accepted);

        var tool = ReadFileTool();
        var callAction = PlanningContractTests.CallToolJson(
            tool.Name,
            "path",
            "README.md");
        var updateDecision = $$"""
        {
          "workUpdate": {
            "baseRevision": {{initial.Snapshot.Revision}},
            "items": [
              {
                "workItemId": "node-02500",
                "outcome": "Objective 2500",
                "status": "active",
                "parentId": null,
                "supersededById": null,
                "dependencyIds": [],
                "evidenceIds": []
              }
            ]
          },
          "materialClaims": [],
          "nextAction": {{callAction}}
        }
        """;
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(updateDecision));
        var observer = new RecordingTransitionObserver(
            authoritativeWorkGraph: activated.Snapshot);
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            new RecordingSemanticCatalog([tool]));
        using var scope = client.BeginTurn(
            CreateTurn("Read the requested file.", out _),
            new AliPlanningTurnInput(
                stateRevision: 12,
                stateProjection:
                    "{\"workGraph\":{\"total\":5000,\"projectedCount\":0,\"items\":[]}}",
                workGraphRevision: initial.Snapshot.Revision,
                authoritativeWorkGraph: initial.Snapshot),
            observer);
        var before = client.CaptureWorkGraphConsumerDiagnostics();

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);
        var after = client.CaptureWorkGraphConsumerDiagnostics();

        Assert.Single(
            response.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>());
        Assert.Equal(1L, after.FingerprintReads - before.FingerprintReads);
        Assert.Equal(0L, after.AnalysisCacheMisses - before.AnalysisCacheMisses);
        Assert.Equal(
            0L,
            after.FullDigestConstructionPasses - before.FullDigestConstructionPasses);
        Assert.Equal(0L, after.FullDigestNodesVisited - before.FullDigestNodesVisited);
        Assert.Equal(2, observer.Decisions.Count);
        Assert.Single(observer.Decisions, decision => decision.Decision.WorkUpdate is not null);
    }

    [Fact]
    public void AuthoritativeWorkGraph_DoesNotEnumerateLegacyKnownWorkItemIds()
    {
        var input = new AliPlanningTurnInput(
            stateRevision: 0,
            stateProjection: "No work has been accepted yet.",
            knownWorkItemIds: Enumerable.Range(0, 1).Select(_ => UnexpectedFallbackId()),
            workGraphRevision: 0,
            authoritativeWorkGraph: WorkGraphSnapshot.Empty);

        Assert.Empty(input.KnownWorkItemIds);

        static string UnexpectedFallbackId() =>
            throw new InvalidOperationException("The fallback ID source must not be enumerated.");
    }

    [Fact]
    public void AuthoritativeWorkGraph_MembershipOverridesLegacyFallbackIds()
    {
        var decision = DependentWorkDecision("legacy-parent");
        var context = new OrchestrationValidationContext(
            stateRevision: 0,
            selectedTools: [],
            knownWorkItemIds: ["legacy-parent"],
            workGraphRevision: 0,
            authoritativeWorkGraph: WorkGraphSnapshot.Empty);

        var result = new OrchestrationDecisionValidator().Validate(decision, context);

        Assert.Empty(context.KnownWorkItemIds);
        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("unknown parent 'legacy-parent'", StringComparison.Ordinal));
    }

    [Fact]
    public void NonAuthoritativePlanningContext_RetainsLegacyFallbackMembership()
    {
        var result = new OrchestrationDecisionValidator().Validate(
            DependentWorkDecision("legacy-parent"),
            new OrchestrationValidationContext(
                stateRevision: 0,
                selectedTools: [],
                knownWorkItemIds: ["legacy-parent"],
                workGraphRevision: 0));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task TypedRecoveryResume_RebuildsNonemptyModelInputWithoutSyntheticSteering()
    {
        const string sourceCommandId = "typed-recovery-command-canary";
        const string originalRequest = "Continue the exact durable request.";
        var stateProjection = $$"""
        {
          "acceptedUserResolutions": {
            "retainedTotal": 1,
            "projectedCount": 1,
            "items": [
              {
                "stateRevision": 7,
                "sourceCommandId": "{{sourceCommandId}}",
                "kind": "Action",
                "reason": "ActionReconciliationRequired",
                "subjectId": "effect-001",
                "subjectPreparedRevision": 3,
                "outcome": "ActionConfirmedAbsent"
              }
            ]
          }
        }
        """;
        var input = new AliPlanningTurnInput(
            stateRevision: 7,
            stateProjection: stateProjection,
            workGraphRevision: 0,
            authoritativeWorkGraph: WorkGraphSnapshot.Empty);
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Continued from typed state.\"}")));
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(
            CreateTurn(originalRequest, out _),
            input,
            new RecordingTransitionObserver());

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, string.Empty)],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(inner.Requests);
        Assert.NotEmpty(request.Messages);
        Assert.All(
            request.Messages,
            message => Assert.False(string.IsNullOrWhiteSpace(message.Text)));
        var modelText = string.Join("\n", request.Messages.Select(message => message.Text));
        Assert.Contains(originalRequest, modelText, StringComparison.Ordinal);
        Assert.Contains(sourceCommandId, modelText, StringComparison.Ordinal);
        Assert.Contains("ActionConfirmedAbsent", modelText, StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted user steering #", modelText, StringComparison.Ordinal);
        Assert.DoesNotContain("The action did not happen", modelText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpandTools_IsTheOnlyPathThatCallsSemanticSelection()
    {
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path", "README.md")));
        var semantic = new RecordingSemanticCatalog([tool]);
        var observer = new RecordingTransitionObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            semantic);
        var turn = CreateTurn("read README", out var activity);
        using var scope = client.BeginTurn(turn, Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("read_file", call.Name);
        Assert.Equal(1, semantic.SelectCount);
        Assert.Equal(2, inner.Requests.Count);
        var expandableTransportSchema = AliOrchestrationProtocol
            .BuildTransportSchema([ExpandableGroupId(tool)])
            .GetRawText();
        var exhaustedTransportSchema = AliOrchestrationProtocol
            .BuildTransportSchema([])
            .GetRawText();
        Assert.Equal(expandableTransportSchema, SchemaText(inner.Requests[0]));
        Assert.Equal(exhaustedTransportSchema, SchemaText(inner.Requests[1]));
        Assert.DoesNotContain(
            "\"toolName\":\"read_file\"",
            MessageText(inner.Requests[0]),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"toolName\":\"read_file\"",
            MessageText(inner.Requests[1]),
            StringComparison.Ordinal);
        Assert.Single(activity, item => item.ActivityKind == AgentActivityKind.ToolCall);
        Assert.True(turn.TryGetToolPlan(call.CallId, out var plan));
        Assert.NotNull(plan);
    }

    [Fact]
    public async Task InitialPlanningPass_UsesCompactGroupManifestAndExactGroupInstruction()
    {
        var tool = AIFunctionFactory.Create(
            (string path) => path,
            AliCapabilityCatalog.FileReadName,
            "Read a file by exact path.");
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Ready.\"}")));
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(
            CreateTurn("hello", out _),
            Input(),
            new RecordingTransitionObserver());

        await client.GetResponseAsync(
            [],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var request = Assert.Single(inner.Requests);
        var text = string.Join("\n", request.Messages.Select(message => message.Text));
        Assert.Contains("groupId=files; status=enabled", text, StringComparison.Ordinal);
        Assert.Contains("expandableGroupIds array", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{tool.Name}: {tool.Description}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedExactExpansionWithNoSelectionChange_SuspendsOnSecondAttempt()
    {
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))));
        var semantic = new RecordingSemanticCatalog([]);
        var observer = new RecordingTransitionObserver();
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            semantic);
        using var scope = client.BeginTurn(CreateTurn("read a file", out _), Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains("repeatedly opened the same tool group", response.Text, StringComparison.Ordinal);
        Assert.Equal(2, semantic.SelectCount);
        Assert.Equal(2, observer.Decisions.Count);
        Assert.Equal(
            "planner-expansion-made-no-change",
            Assert.Single(observer.Suspensions).ReasonCode);
        Assert.Equal(AliPlanningInterimKind.ProtocolSuspended, client.PreparedInterimResponse!.Kind);
    }

    [Fact]
    public async Task RejectedDraft_IsAbsentFromNextAuthoritativePassAndActivity()
    {
        const string rejectedCanary = "REJECTED_DRAFT_CANARY";
        var malformed = $$"""
        {
          "workUpdate": null,
          "materialClaims": [],
          "nextAction": {
            "kind": "answerDirectly",
            "answer": "{{rejectedCanary}}",
            "unexpected": true
          }
        }
        """;
        var inner = new ScriptedChatClient(
            Compatibility(malformed),
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Recovered cleanly\"}")));
        var observer = new RecordingTransitionObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        var turn = CreateTurn("hello", out var activity);
        using var scope = client.BeginTurn(turn, Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Recovered cleanly", response.Text);
        Assert.Equal(2, inner.Requests.Count);
        Assert.DoesNotContain(
            inner.Requests[1].Messages,
            message => message.Text?.Contains(rejectedCanary, StringComparison.Ordinal) == true);
        Assert.Single(observer.Decisions);
        Assert.Empty(activity);
    }

    [Theory]
    [MemberData(nameof(ExplicitCompatibilityNonStopReasons))]
    public async Task ExplicitIncompleteCompatibilityFinish_DiscardsDecodableDecisionBeforeValidation(
        ChatFinishReason finishReason)
    {
        const string partialCanary = "PARTIAL_DECISION_MUST_NOT_RUN";
        var partial = Compatibility(PlanningContractTests.DecisionJson(
            "{\"kind\":\"answerDirectly\",\"answer\":\""
            + partialCanary + "\"}"));
        partial.FinishReason = finishReason;
        var inner = new ScriptedChatClient(partial);
        var observer = new RecordingTransitionObserver();
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(CreateTurn("hello", out _), Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Single(inner.Requests);
        Assert.Empty(observer.Decisions);
        Assert.Empty(observer.Publications);
        Assert.Equal(
            "planner-output-incomplete",
            Assert.Single(observer.Suspensions).ReasonCode);
        Assert.DoesNotContain(partialCanary, response.Text, StringComparison.Ordinal);
        Assert.Contains("partial response was discarded", response.Text, StringComparison.Ordinal);
        Assert.Equal(
            AliPlanningInterimKind.ProtocolSuspended,
            client.PreparedInterimResponse!.Kind);
    }

    [Fact]
    public async Task MissingCompatibilityFinish_DiscardsDecodableDecisionAndSuspendsTyped()
    {
        const string partialCanary = "NULL_FINISH_DECISION_MUST_NOT_RUN";
        var partial = Compatibility(PlanningContractTests.DecisionJson(
            "{\"kind\":\"answerDirectly\",\"answer\":\""
            + partialCanary + "\"}"));
        partial.FinishReason = null;
        Assert.Null(partial.FinishReason);
        var inner = new ScriptedChatClient(partial);
        var observer = new RecordingTransitionObserver();
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(CreateTurn("hello", out _), Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Single(inner.Requests);
        Assert.Empty(observer.Decisions);
        Assert.Empty(observer.Publications);
        Assert.Empty(observer.Results);
        Assert.Equal(
            "planner-output-incomplete",
            Assert.Single(observer.Suspensions).ReasonCode);
        Assert.DoesNotContain(partialCanary, response.Text, StringComparison.Ordinal);
        Assert.Contains("partial response was discarded", response.Text, StringComparison.Ordinal);
        Assert.Equal(
            AliPlanningInterimKind.ProtocolSuspended,
            client.PreparedInterimResponse!.Kind);
    }

    [Fact]
    public async Task NativeToolCallsFinish_WithExactProtocolCall_IsAccepted()
    {
        var decisionJson = PlanningContractTests.DecisionJson(
            "{\"kind\":\"answerDirectly\",\"answer\":\"Native protocol complete.\"}");
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [AliOrchestrationProtocol.DecisionJsonPropertyName] = decisionJson
        };
        var message = new ChatMessage(ChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent(
            "protocol-call",
            OrchestrationProtocolCapability.ToolName,
            arguments));
        var native = new ChatResponse(message)
        {
            FinishReason = ChatFinishReason.ToolCalls
        };
        var inner = new ScriptedChatClient(native);
        var observer = new RecordingTransitionObserver();
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => true,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(CreateTurn("hello", out _), Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Native protocol complete.", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Single(observer.Decisions);
        Assert.Empty(observer.Suspensions);
        Assert.Single(observer.Publications);
    }

    [Fact]
    public async Task NativeProtocolFailure_RetriesInCleanCompatibilityMode()
    {
        var nativeFailure = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "not a protocol call"))
        {
            FinishReason = ChatFinishReason.Stop
        };
        var inner = new ScriptedChatClient(
            nativeFailure,
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Compatibility worked\"}")));
        var observer = new RecordingTransitionObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => true,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(CreateTurn("hello", out _), Input(), observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Compatibility worked", response.Text);
        Assert.Equal(2, inner.Requests.Count);
        var nativeTools = Assert.IsAssignableFrom<IEnumerable<AITool>>(inner.Requests[0].Options.Tools);
        Assert.Single(nativeTools);
        Assert.Equal(
            "submit_orchestration_decision",
            Assert.IsAssignableFrom<AIFunctionDeclaration>(nativeTools.Single()).Name);
        Assert.Null(inner.Requests[1].Options.Tools);
        Assert.NotNull(inner.Requests[1].Options.ResponseFormat);
    }

    [Fact]
    public async Task ExactReplyConstraint_CannotReplaceTheMandatoryOuterProtocol()
    {
        const string rejectedCanary = "ORANGE_CANARY";
        var plainResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, rejectedCanary))
        {
            FinishReason = ChatFinishReason.Stop
        };
        var inner = new ScriptedChatClient(
            plainResponse,
            Compatibility(rejectedCanary),
            Compatibility(rejectedCanary));
        var observer = new RecordingTransitionObserver();
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => true,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(
            CreateTurn($"Reply exactly {rejectedCanary}; do not call tools.", out _),
            Input(),
            observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, inner.Requests.Count);
        Assert.NotNull(inner.Requests[0].Options.Tools);
        Assert.All(inner.Requests.Skip(1), request =>
        {
            Assert.Null(request.Options.Tools);
            Assert.NotNull(request.Options.ResponseFormat);
        });
        Assert.All(inner.Requests, request =>
            Assert.Contains(
                request.Messages,
                message => message.Role == ChatRole.System
                    && message.Text?.Contains(
                        "mandatory response transport",
                        StringComparison.Ordinal) == true));
        Assert.Empty(observer.Decisions);
        Assert.Empty(observer.Publications);
        Assert.Equal(
            "planner-protocol-invalid",
            Assert.Single(observer.Suspensions).ReasonCode);
        Assert.Equal(
            AliPlanningInterimKind.ProtocolSuspended,
            client.PreparedInterimResponse!.Kind);
        Assert.DoesNotContain(rejectedCanary, response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolArgumentNormalizer_IsAppliedThenStrictlyValidated()
    {
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path_alias", "README.md")));
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            new RecordingSemanticCatalog([tool]),
            toolArgumentNormalizer: (_, arguments) =>
            {
                arguments["path"] = arguments["path_alias"];
                arguments.Remove("path_alias");
                return arguments;
            });
        using var scope = client.BeginTurn(
            CreateTurn("read README", out _),
            Input(),
            new RecordingTransitionObserver());

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.True(call.Arguments!.ContainsKey("path"));
        Assert.False(call.Arguments.ContainsKey("path_alias"));
    }

    [Fact]
    public async Task GenericSuccessProperty_DoesNotClassifyToolOwnedOutcome()
    {
        const string secretCanary = "RESULT_SECRET_CANARY";
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path", "README.md")),
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"The tool returned a failure.\"}")));
        var observer = new RecordingTransitionObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            new RecordingSemanticCatalog([tool]));
        using var scope = client.BeginTurn(CreateTurn("read README", out _), Input(), observer);
        var options = new ChatOptions { Tools = [tool] };
        var callResponse = await client.GetResponseAsync(
            [],
            options,
            TestContext.Current.CancellationToken);
        var call = Assert.Single(callResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        var resultMessage = new ChatMessage(ChatRole.Tool, string.Empty);
        resultMessage.Contents.Add(new FunctionResultContent(
            call.CallId,
            new { success = true, token = secretCanary, payload = new string('x', 4_000) }));

        var response = await client.GetResponseAsync(
            [resultMessage],
            options,
            TestContext.Current.CancellationToken);

        Assert.Equal("The tool returned a failure.", response.Text);
        var observed = Assert.Single(observer.Results);
        Assert.Equal(PlanningToolDomainOutcome.Unreported, observed.DomainOutcome);
        Assert.Equal("README.md", observed.Arguments.GetProperty("path").GetString());
        Assert.True(observed.Result.GetProperty("success").GetBoolean());
        Assert.Equal(secretCanary, observed.Result.GetProperty("token").GetString());
        Assert.True(observed.CompletedAtUtc >= observed.StartedAtUtc);
        Assert.DoesNotContain(secretCanary, observed.BoundedRedactedProjection, StringComparison.Ordinal);
        Assert.True(observed.BoundedRedactedProjection.Length <= 1_810);
    }

    [Fact]
    public async Task CompletedOutcomeClassifier_ReceivesExactDurableTurnCallAndToolIdentity()
    {
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path", "README.md")),
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Evidence accepted.\"}")));
        var observer = new RecordingTransitionObserver();
        AliCompletedToolOutcomeRequest? capturedRequest = null;
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            new RecordingSemanticCatalog([tool]),
            completedToolOutcomeClassifier: request =>
            {
                capturedRequest = request;
                return PlanningToolDomainOutcome.Succeeded;
            });
        var durableIdentity = new TurnIdentity(
            "durable-user",
            "durable-conversation",
            "durable-assistant");
        using var scope = client.BeginTurn(
            CreateTurn("read README", out _),
            Input(),
            observer,
            durableIdentity: durableIdentity);
        var options = new ChatOptions { Tools = [tool] };
        var callResponse = await client.GetResponseAsync(
            [],
            options,
            TestContext.Current.CancellationToken);
        var call = Assert.Single(callResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        var resultMessage = new ChatMessage(ChatRole.Tool, string.Empty);
        resultMessage.Contents.Add(new FunctionResultContent(call.CallId, "exact result"));

        await client.GetResponseAsync(
            [resultMessage],
            options,
            TestContext.Current.CancellationToken);

        var classified = Assert.IsType<AliCompletedToolOutcomeRequest>(capturedRequest);
        Assert.Equal(durableIdentity, classified.TurnIdentity);
        Assert.Equal(call.CallId, classified.CallId);
        Assert.Equal("read_file", classified.ToolName);
        Assert.Equal("exact result", classified.Result);
        Assert.Equal(
            PlanningToolDomainOutcome.Succeeded,
            Assert.Single(observer.Results).DomainOutcome);
    }

    [Fact]
    public async Task EvidenceReceiptWorkItemBinding_IsRetainedInNextAuthoritativePass()
    {
        const string workItemId = "work-read-requested-file";
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path", "README.md")),
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Evidence retained.\"}")));
        var observer = new RecordingTransitionObserver(
            evidenceWorkItemId: workItemId);
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            new RecordingSemanticCatalog([tool]));
        using var scope = client.BeginTurn(
            CreateTurn("read README", out _),
            Input(),
            observer);
        var options = new ChatOptions { Tools = [tool] };
        var callResponse = await client.GetResponseAsync(
            [],
            options,
            TestContext.Current.CancellationToken);
        var call = Assert.Single(callResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        var resultMessage = new ChatMessage(ChatRole.Tool, string.Empty);
        resultMessage.Contents.Add(new FunctionResultContent(call.CallId, "accepted result"));

        var response = await client.GetResponseAsync(
            [resultMessage],
            options,
            TestContext.Current.CancellationToken);

        Assert.Equal("Evidence retained.", response.Text);
        Assert.Equal(3, inner.Requests.Count);
        var nextAuthoritativePrompt = string.Join(
            "\n",
            inner.Requests[2].Messages.Select(message => message.Text));
        Assert.Contains(
            $"\"workItemId\":\"{workItemId}\"",
            nextAuthoritativePrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedToolResult_IsWithheldBeforeEvidenceCaptureAndCannotBecomeSuccess()
    {
        const string secretCanary = "OVERSIZED_RESULT_SECRET_CANARY";
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path", "README.md")),
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"The oversized result was withheld.\"}")));
        var observer = new RecordingTransitionObserver();
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            new RecordingSemanticCatalog([tool]));
        using var scope = client.BeginTurn(CreateTurn("read README", out _), Input(), observer);
        var options = new ChatOptions { Tools = [tool] };
        var callResponse = await client.GetResponseAsync(
            [],
            options,
            TestContext.Current.CancellationToken);
        var call = Assert.Single(callResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        var resultMessage = new ChatMessage(ChatRole.Tool, string.Empty);
        resultMessage.Contents.Add(new FunctionResultContent(
            call.CallId,
            new
            {
                success = true,
                payload = secretCanary + new string('x',
                    BoundedToolResultCapture.MaximumCapturedResultBytes)
            }));

        var response = await client.GetResponseAsync(
            [resultMessage],
            options,
            TestContext.Current.CancellationToken);

        Assert.Equal("The oversized result was withheld.", response.Text);
        var observed = Assert.Single(observer.Results);
        Assert.Equal(PlanningToolDomainOutcome.Unreported, observed.DomainOutcome);
        Assert.True(observed.Result.GetProperty("withheld").GetBoolean());
        Assert.Equal(
            "result-exceeded-capture-limit",
            observed.Result.GetProperty("reason").GetString());
        Assert.Equal(
            BoundedToolResultCapture.MaximumCapturedResultBytes,
            observed.Result.GetProperty("maximumBytes").GetInt32());
        Assert.DoesNotContain(secretCanary, observed.Result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretCanary,
            observed.BoundedRedactedProjection,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FaultedToolTerminalObserver_RetryReusesExactImmutableEvent()
    {
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                ExpandToolsJson(tool))),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path", "README.md")),
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"answerDirectly\",\"answer\":\"Recovered after the durable retry.\"}")));
        var observer = new FaultOnceToolTerminalObserver();
        var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            new RecordingSemanticCatalog([tool]));
        using var scope = client.BeginTurn(CreateTurn("read README", out _), Input(), observer);
        var options = new ChatOptions { Tools = [tool] };
        var callResponse = await client.GetResponseAsync(
            [],
            options,
            TestContext.Current.CancellationToken);
        var call = Assert.Single(callResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        var resultMessage = new ChatMessage(ChatRole.Tool, string.Empty);
        resultMessage.Contents.Add(new FunctionResultContent(
            call.CallId,
            new { success = true, path = "README.md" }));

        await Assert.ThrowsAsync<InjectedToolTerminalObserverException>(() =>
            client.GetResponseAsync(
                [resultMessage],
                options,
                TestContext.Current.CancellationToken));
        var firstAttempt = Assert.Single(observer.Results);
        var firstBytes = JsonSerializer.SerializeToUtf8Bytes(firstAttempt);

        var response = await client.GetResponseAsync(
            [resultMessage],
            options,
            TestContext.Current.CancellationToken);

        Assert.Equal("Recovered after the durable retry.", response.Text);
        Assert.Equal(2, observer.Results.Count);
        var retry = observer.Results[1];
        Assert.Same(firstAttempt, retry);
        Assert.Equal(firstAttempt.CompletedAtUtc, retry.CompletedAtUtc);
        Assert.Equal(firstAttempt.ExpectedStateRevision, retry.ExpectedStateRevision);
        Assert.Equal(firstAttempt.ProposedEvidenceId, retry.ProposedEvidenceId);
        Assert.Equal(firstAttempt.ProjectionDigest, retry.ProjectionDigest);
        Assert.Equal(firstBytes, JsonSerializer.SerializeToUtf8Bytes(retry));
    }

    private static OrchestrationDecision DependentWorkDecision(string parentId) => new(
        new OrchestrationWorkUpdate(
            0,
            [
                new OrchestrationWorkItemUpdate(
                    "dependent-work",
                    "Complete work whose parent must already be accepted.",
                    OrchestrationWorkStatus.Pending,
                    parentId: parentId)
            ]),
        [],
        new RequestUserInputAction(
            "Which outcome should Ali handle next?",
            "The next priority is not known."));

    private static AIFunction ReadFileTool() => AIFunctionFactory.Create(
        (string path) => path,
        "read_file",
        "Read a file by exact path.");

    private static string ExpandToolsJson(AIFunctionDeclaration tool)
    {
        return JsonSerializer.Serialize(new
        {
            kind = "expandTools",
            need = ExpandableGroupId(tool)
        });
    }

    private static string ExpandableGroupId(AIFunctionDeclaration tool)
    {
        var bucket = Assert.Single(
            LiveSemanticToolDirectory.CreateBoundedDirectoryBuckets([tool]),
            candidate => candidate.ToolNames.Contains(tool.Name, StringComparer.Ordinal));
        return bucket.Id;
    }

    private static AliPlanningTurnInput Input() => new(
        0,
        "No work has been accepted yet.",
        workGraphRevision: 0,
        authoritativeWorkGraph: WorkGraphSnapshot.Empty);

    private static CoordinatorTurnContext CreateTurn(
        string request,
        out List<AssistantStreamChunk> activity)
    {
        activity = [];
        return new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            request,
            activity.Add);
    }

    private static ChatResponse Compatibility(string decisionJson) =>
        new(new ChatMessage(
            ChatRole.Assistant,
            PlanningContractTests.TransportJson(decisionJson)))
        {
            FinishReason = ChatFinishReason.Stop
        };

    private static string MessageText(RecordedRequest request) =>
        string.Join("\n", request.Messages.Select(message => message.Text));

    private static string SchemaText(RecordedRequest request)
    {
        var schema = Assert.IsType<ChatResponseFormatJson>(request.Options.ResponseFormat).Schema;
        Assert.True(schema.HasValue);
        return schema.Value.GetRawText();
    }

    private sealed record RecordedRequest(
        IReadOnlyList<ChatMessage> Messages,
        ChatOptions Options);

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        internal List<RecordedRequest> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new RecordedRequest(messages.ToArray(), options?.Clone() ?? new ChatOptions()));
            return Task.FromResult(_responses.Dequeue());
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

    private sealed class RecordingSemanticCatalog(
        IReadOnlyList<AIFunctionDeclaration> selected) : ISemanticToolCatalog
    {
        internal int SelectCount { get; private set; }

        public Task<SemanticToolSelection> SelectAsync(
            string need,
            IReadOnlyList<AIFunctionDeclaration> liveTools,
            IReadOnlyCollection<string> retainedToolNames,
            CancellationToken cancellationToken)
        {
            SelectCount++;
            return Task.FromResult(new SemanticToolSelection(
                selected,
                ["test"],
                "Selected test tools",
                false,
                "selected"));
        }

        public Task<SemanticToolDiscoveryResult> DiscoverAsync(
            string need,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SemanticToolDiscoveryResult(need, [], [], "not used"));
    }

    private sealed class RecordingTransitionObserver(
        bool advanceDecisionWithoutCall = false,
        WorkGraphSnapshot? authoritativeWorkGraph = null,
        string? evidenceWorkItemId = null) : IAliPlanningTransitionObserver
    {
        internal List<AliPlanningDecisionAcceptedEvent> Decisions { get; } = [];

        internal List<AliPlanningToolResultObservedEvent> Results { get; } = [];

        internal List<AliPlanningSuspendedEvent> Suspensions { get; } = [];

        internal List<AliPlanningPublicationPreparedEvent> Publications { get; } = [];

        public ValueTask<AliPlanningTransitionReceipt> OnDecisionAcceptedAsync(
            AliPlanningDecisionAcceptedEvent accepted,
            CancellationToken cancellationToken)
        {
            Decisions.Add(accepted);
            var revision = accepted.CallId is null && !advanceDecisionWithoutCall
                ? accepted.ExpectedStateRevision
                : accepted.ExpectedStateRevision + 1;
            var updatedGraph = accepted.Decision.WorkUpdate is null
                ? null
                : authoritativeWorkGraph;
            return ValueTask.FromResult(new AliPlanningTransitionReceipt(
                revision,
                WorkGraphRevision: updatedGraph?.Revision,
                AuthoritativeWorkGraph: updatedGraph));
        }

        public ValueTask<AliPlanningEvidenceReceipt> OnToolResultObservedAsync(
            AliPlanningToolResultObservedEvent observed,
            CancellationToken cancellationToken)
        {
            Results.Add(observed);
            return ValueTask.FromResult(new AliPlanningEvidenceReceipt(
                observed.ExpectedStateRevision + 1,
                observed.ProposedEvidenceId,
                WorkItemId: evidenceWorkItemId));
        }

        public ValueTask<AliPlanningTransitionReceipt> OnPlanningSuspendedAsync(
            AliPlanningSuspendedEvent suspended,
            CancellationToken cancellationToken)
        {
            Suspensions.Add(suspended);
            return ValueTask.FromResult(new AliPlanningTransitionReceipt(
                suspended.ExpectedStateRevision + 1));
        }

        public ValueTask<AliPlanningTransitionReceipt> OnInterimResponsePreparedAsync(
            AliPlanningInterimPreparedEvent prepared,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningTransitionReceipt(
                prepared.ExpectedStateRevision + 1));

        public ValueTask<AliPlanningPublicationReceipt> OnFinalAnswerPreparedAsync(
            AliPlanningPublicationPreparedEvent prepared,
            CancellationToken cancellationToken)
        {
            Publications.Add(prepared);
            return ValueTask.FromResult(new AliPlanningPublicationReceipt(
                prepared.ExpectedStateRevision + 1,
                prepared.PublicationId,
                prepared.AnswerDigest));
        }
    }

    private sealed class FaultOnceToolTerminalObserver : IAliPlanningTransitionObserver
    {
        private bool _shouldFault = true;

        internal List<AliPlanningToolResultObservedEvent> Results { get; } = [];

        public ValueTask<AliPlanningTransitionReceipt> OnDecisionAcceptedAsync(
            AliPlanningDecisionAcceptedEvent accepted,
            CancellationToken cancellationToken)
        {
            var revision = accepted.CallId is null
                ? accepted.ExpectedStateRevision
                : accepted.ExpectedStateRevision + 1;
            return ValueTask.FromResult(new AliPlanningTransitionReceipt(revision));
        }

        public ValueTask<AliPlanningEvidenceReceipt> OnToolResultObservedAsync(
            AliPlanningToolResultObservedEvent observed,
            CancellationToken cancellationToken)
        {
            Results.Add(observed);
            if (_shouldFault)
            {
                _shouldFault = false;
                throw new InjectedToolTerminalObserverException();
            }

            return ValueTask.FromResult(new AliPlanningEvidenceReceipt(
                observed.ExpectedStateRevision + 1,
                observed.ProposedEvidenceId));
        }

        public ValueTask<AliPlanningTransitionReceipt> OnPlanningSuspendedAsync(
            AliPlanningSuspendedEvent suspended,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningTransitionReceipt(
                suspended.ExpectedStateRevision + 1));

        public ValueTask<AliPlanningTransitionReceipt> OnInterimResponsePreparedAsync(
            AliPlanningInterimPreparedEvent prepared,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningTransitionReceipt(
                prepared.ExpectedStateRevision + 1));

        public ValueTask<AliPlanningPublicationReceipt> OnFinalAnswerPreparedAsync(
            AliPlanningPublicationPreparedEvent prepared,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningPublicationReceipt(
                prepared.ExpectedStateRevision + 1,
                prepared.PublicationId,
                prepared.AnswerDigest));
    }

    private sealed class InjectedToolTerminalObserverException : Exception;
}
