using System.Runtime.CompilerServices;
using System.Net;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class CompletionComposerTests
{
    public static TheoryData<ChatFinishReason> ExplicitNonStopFinishReasons => new()
    {
        ChatFinishReason.Length,
        ChatFinishReason.ToolCalls,
        ChatFinishReason.ContentFilter,
        new ChatFinishReason("provider-specific-incomplete")
    };

    [Fact]
    public async Task OversizedCompletionDossier_IsFullyPagedWithoutChangingConfiguredLimits()
    {
        var client = new AutoCompositionChatClient();
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 512
        };
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, profile, useNativeProtocol: false),
            bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request(string.Concat(Enumerable.Repeat("exact dossier material ", 4_000))),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        var committed = Assert.IsType<AliCommittedAnswerDraft>(attempt.CommittedDraft);
        Assert.True(committed.Segments.Count > 1);
        Assert.Equal(committed.Text, attempt.Response!.Text);
        Assert.Equal(profile.OutputTokenLimit, client.Requests[0].Options!.MaxOutputTokens);
        var protocol = AliAnswerCompositionProtocol.CreateDeclaration();
        foreach (var request in client.Requests)
        {
            var charge = AliModelAwarePlanningInputCounter.Instance.Count(
                profile,
                request.Messages,
                selectedTools: [],
                protocol);
            Assert.True(charge.CanSafelyCharge);
            Assert.True(
                charge.ChargedTokens <= profile.ContextTokens - profile.OutputTokenLimit,
                $"Composition prompt charged {charge.ChargedTokens} tokens against a "
                + $"{profile.ContextTokens - profile.OutputTokenLimit} token input budget.");
        }

        var projected = client.Requests
            .Select(request => ParseCompositionInput(request.Messages))
            .Select(root => root.GetProperty("projectionPage").GetProperty("text").GetString())
            .Where(static text => !string.IsNullOrEmpty(text));
        Assert.Equal(
            AliCompletionProjectionPager.BuildSource(
                Request(string.Concat(Enumerable.Repeat("exact dossier material ", 4_000)))),
            string.Concat(projected));
    }

    [Fact]
    public async Task ShortAnswer_CommitsOneSegmentAndThenFinishesThroughOnlyCompositionProtocol()
    {
        var client = new AutoCompositionChatClient(firstSegmentText: "Completed exactly.");
        var counter = new RecordingCounter();
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ContextTokens = 4_096,
            OutputTokenLimit = 512
        };
        var bindings = Bindings("exact");
        var composer = new AliCompletionComposer(
            () => Snapshot(client, profile, reasoningEffort: "high"),
            _ => bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)),
            new AliPlanningInputAdmission(counter));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Return the exact accepted result."),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        Assert.Equal("Completed exactly.", attempt.Response!.Text);
        var committed = Assert.IsType<AliCommittedAnswerDraft>(attempt.CommittedDraft);
        Assert.Single(committed.Segments);
        Assert.Equal(2, counter.CallCount);
        Assert.Empty(counter.SelectedTools!);
        Assert.Equal(AliAnswerCompositionProtocol.ToolName, counter.Protocol!.Name);
        Assert.Equal(2, client.CallCount);
        var exactMessages = client.Requests[0].Messages;
        Assert.Equal(2, exactMessages.Count);
        Assert.Equal(MeaiChatRole.System, exactMessages[0].Role);
        Assert.Equal(MeaiChatRole.User, exactMessages[1].Role);

        var options = Assert.IsType<ChatOptions>(client.Requests[0].Options);
        Assert.Equal(
            AliAnswerCompositionProtocol.ToolName,
            Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(options.Tools!)).Name);
        Assert.Equal(
            AliOrchestrationProtocol.BuildTransportSchema().GetRawText(),
            Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(options.Tools!))
                .JsonSchema.GetRawText());
        Assert.Null(options.ResponseFormat);
        Assert.Equal(profile.OutputTokenLimit, options.MaxOutputTokens);
        Assert.True(Assert.IsType<bool>(options.AdditionalProperties![
            AliInternalModelRoutingProperties.SuppressInjectedPersona]));
        Assert.Equal(
            "high",
            options.AdditionalProperties[
                AliInternalModelRoutingProperties.BoundReasoningEffort]);
    }

    [Fact]
    public void CompletionDossier_IncludesExactRawWorkItemEvidenceBinding()
    {
        var basis = Request("Return the accepted result.");
        var request = new TemporaryCompletionRequest(
            basis.ImmutableOriginalRequest,
            basis.AuthoritativeInput,
            basis.AcceptedDecision,
            basis.PlannerResponse,
            requiredOutcomes: [],
            requiredClaims: [],
            citedEvidence:
            [
                new AcceptedEvidenceProjection(
                    "evidence-1",
                    "call-1",
                    "read-file",
                    PlanningToolInvocationStatus.Returned,
                    PlanningToolDomainOutcome.Succeeded,
                    "accepted projection",
                    workItemId: "work-1")
            ]);

        var messages = AliCompletionComposer.BuildMessages(request);

        using var composition = JsonDocument.Parse(messages[1].Text!);
        using var dossier = JsonDocument.Parse(
            composition.RootElement
                .GetProperty("projectionPage")
                .GetProperty("text")
                .GetString()!);
        var evidence = Assert.Single(
            dossier.RootElement.GetProperty("citedAcceptedEvidence").EnumerateArray());
        Assert.Equal("evidence-1", evidence.GetProperty("evidenceId").GetString());
        Assert.Equal("work-1", evidence.GetProperty("workItemId").GetString());
    }

    [Fact]
    public void CompletionCounter_DoesNotChargeAPhantomOrchestrationProtocol()
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            DisplayName = "generic model",
            PackageId = "generic/model",
            Family = "generic"
        };
        var messages = new[] { new MeaiChatMessage(MeaiChatRole.User, "exact dossier") };
        var protocol = AliOrchestrationProtocol.CreateDeclaration([]);

        var completion = AliModelAwarePlanningInputCounter.Instance.Count(
            profile,
            messages,
            selectedTools: [],
            protocol: null);
        var planning = AliModelAwarePlanningInputCounter.Instance.Count(
            profile,
            messages,
            selectedTools: [],
            protocol: protocol);
        var exactProtocolCharge = AliModelAwarePlanningInputCounter.ToolSegmentOverheadTokens
            + Encoding.UTF8.GetByteCount(protocol.Name)
            + Encoding.UTF8.GetByteCount(protocol.Description ?? string.Empty)
            + Encoding.UTF8.GetByteCount(protocol.JsonSchema.GetRawText());

        Assert.True(completion.CanSafelyCharge);
        Assert.Equal(
            exactProtocolCharge,
            planning.ChargedTokens - completion.ChargedTokens);
    }

    [Theory]
    [InlineData(false, 11)]
    [InlineData(true, 12)]
    public async Task UnacceptedOrWrongRevisionDispatch_MakesZeroComposerCalls(
        bool canCompose,
        long authorizedRevision)
    {
        var client = new RecordingChatClient(CompleteResponse("must not run"));
        var profile = PlanningTestModelProfile.GptOss65K();
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, profile),
            bindings,
            (_, _, _) => ValueTask.FromResult(new AliCompletionDispatchAuthorization(
                canCompose,
                authorizedRevision,
                ["model"])));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Return the accepted answer."),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, client.CallCount);
        var failure = Assert.IsType<TemporaryCompletionFailure>(attempt.Failure);
        Assert.Equal(
            TemporaryCompletionFailureKind.CompletionDispatchBindingsChanged,
            failure.Kind);
        Assert.Contains("Changed bindings: model", failure.UserVisibleMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeSwitchAfterAuthorizationSnapshot_FailsClosedBeforeEitherComposerCall()
    {
        var fallback = new SwitchingTestRuntime(
            PlanningTestModelProfile.GptOss65K() with { PackageId = "model-before" },
            CompleteResponse("from bound model"));
        var candidate = new SwitchingTestRuntime(
            PlanningTestModelProfile.GptOss65K() with { PackageId = "model-after" },
            CompleteResponse("from switched model"));
        var switching = new SafeActivatingLocalRuntime(fallback, candidate);
        var bindings = Bindings("accepted");
        var composer = new AliCompletionComposer(
            () => ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch(),
            _ => bindings,
            async (revision, _, cancellationToken) =>
            {
                var health = await switching.CheckCandidateAsync(cancellationToken);
                Assert.True(health.Succeeded);
                Assert.True(switching.ActivateLastHealthChecked());
                return new AliCompletionDispatchAuthorization(true, revision);
            });
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Use the already accepted bound model."),
            TestContext.Current.CancellationToken);

        Assert.False(attempt.IsSuccessful);
        Assert.Null(attempt.Response);
        var failure = Assert.IsType<TemporaryCompletionFailure>(attempt.Failure);
        Assert.Equal(
            TemporaryCompletionFailureKind.CompletionOutputIncomplete,
            failure.Kind);
        Assert.Contains(
            "did not return a complete final answer",
            failure.UserVisibleMessage,
            StringComparison.Ordinal);
        Assert.Equal(0, fallback.CallCount);
        Assert.Equal(0, candidate.CallCount);
        Assert.Equal("model-after", switching.ActiveProfile.PackageId);
    }

    [Fact]
    public async Task RuntimeWithoutExactBoundEnvelope_FailsClosedBeforeAnyLegacyDispatch()
    {
        var legacy = new UnboundTestRuntime(PlanningTestModelProfile.GptOss65K());
        var switching = new SafeActivatingLocalRuntime(legacy, candidateRuntime: null);
        var bindings = Bindings("accepted");
        var composer = new AliCompletionComposer(
            () => ((IBoundModelDispatchSource)switching).CaptureBoundModelDispatch(),
            _ => bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Do not guess at a hidden legacy envelope."),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, legacy.StreamCallCount);
        var failure = Assert.IsType<TemporaryCompletionFailure>(attempt.Failure);
        Assert.Equal(
            TemporaryCompletionFailureKind.CompletionDispatchBindingsChanged,
            failure.Kind);
        Assert.Contains("No composer call ran", failure.UserVisibleMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposerTransportFailure_BecomesTypedRecoverablePauseWithoutErrorLeakage()
    {
        var client = new ThrowingChatClient("SECRET_TRANSPORT_DETAIL");
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, PlanningTestModelProfile.GptOss65K()),
            bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Return a complete answer."),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        var failure = Assert.IsType<TemporaryCompletionFailure>(attempt.Failure);
        Assert.Equal(
            TemporaryCompletionFailureKind.CompletionOutputIncomplete,
            failure.Kind);
        Assert.DoesNotContain("SECRET_TRANSPORT_DETAIL", failure.UserVisibleMessage, StringComparison.Ordinal);
        Assert.Null(attempt.Response);
    }

    [Fact]
    public async Task ExplicitLengthFinish_DiscardsPartialOutputAndRegeneratesFromCommittedCursor()
    {
        const string partialCanary = "PARTIAL_OUTPUT_MUST_NOT_BE_JOURNALED";
        var client = new AutoCompositionChatClient(
            firstSegmentText: "complete segment",
            responseOverride: (index, _) => index == 0
                ? new ChatResponse(new MeaiChatMessage(
                    MeaiChatRole.Assistant,
                    partialCanary))
                {
                    FinishReason = ChatFinishReason.Length
                }
                : null);
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, PlanningTestModelProfile.GptOss65K()),
            bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Return a complete answer."),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        Assert.Equal("complete segment", attempt.Response!.Text);
        Assert.DoesNotContain(partialCanary, attempt.Response.Text, StringComparison.Ordinal);
        Assert.Equal(3, client.CallCount);
        var clippedInput = ParseCompositionInput(client.Requests[0].Messages);
        var regeneratedInput = ParseCompositionInput(client.Requests[1].Messages);
        Assert.Equal(
            clippedInput.GetProperty("nextSequence").GetInt32(),
            regeneratedInput.GetProperty("nextSequence").GetInt32());
        Assert.Equal(
            clippedInput.GetProperty("previousSegmentHash").GetString(),
            regeneratedInput.GetProperty("previousSegmentHash").GetString());
        Assert.Equal(
            clippedInput.GetProperty("projectionPage").GetProperty("cursor").GetInt64(),
            regeneratedInput.GetProperty("projectionPage").GetProperty("cursor").GetInt64());
    }

    [Fact]
    public async Task MalformedEnvelope_IsDiscardedAndRegeneratedFromCommittedCursor()
    {
        var client = new AutoCompositionChatClient(
            firstSegmentText: "valid replacement",
            responseOverride: (index, _) => index == 0
                ? CompleteResponse("{not-valid-composition-json")
                : null);
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, PlanningTestModelProfile.GptOss65K()),
            bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Return a complete answer."),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        Assert.Equal("valid replacement", attempt.Response!.Text);
        Assert.Equal(3, client.CallCount);
        var malformedInput = ParseCompositionInput(client.Requests[0].Messages);
        var regeneratedInput = ParseCompositionInput(client.Requests[1].Messages);
        Assert.Equal(
            malformedInput.GetProperty("nextSequence").GetInt32(),
            regeneratedInput.GetProperty("nextSequence").GetInt32());
        Assert.Equal(
            malformedInput.GetProperty("projectionPage").GetProperty("pageDigest").GetString(),
            regeneratedInput.GetProperty("projectionPage").GetProperty("pageDigest").GetString());
        Assert.NotNull(client.Requests[0].Options!.Tools);
        Assert.All(client.Requests, request =>
        {
            Assert.NotNull(request.Options!.Tools);
            Assert.Null(request.Options.ResponseFormat);
        });
    }

    [Fact]
    public async Task RepeatedInvalidJsonAtUnchangedMaterialState_BecomesTypedPause()
    {
        var client = new AutoCompositionChatClient(
            responseOverride: (_, _) => CompleteResponse("{not-valid-composition-json"));
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, PlanningTestModelProfile.GptOss65K()),
            bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Return a complete answer."),
            TestContext.Current.CancellationToken);

        Assert.False(attempt.IsSuccessful);
        Assert.Equal(2, client.CallCount);
        Assert.All(client.Requests, request =>
        {
            Assert.NotNull(request.Options!.Tools);
            Assert.Null(request.Options.ResponseFormat);
        });
        Assert.Equal(
            TemporaryCompletionFailureKind.CompletionOutputIncomplete,
            Assert.IsType<TemporaryCompletionFailure>(attempt.Failure).Kind);
    }

    [Fact]
    public async Task LaterTransportFailure_ReportsInMemorySegmentsWereDiscarded()
    {
        var client = new AutoCompositionChatClient(
            firstSegmentText: "accepted only in this attempt",
            responseOverride: (index, input) => index switch
            {
                0 => NativeAppend(input, "accepted only in this attempt"),
                _ => throw new HttpRequestException("SECRET_LATER_TRANSPORT_DETAIL")
            });
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, PlanningTestModelProfile.GptOss65K()),
            bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            Request("Return a complete answer."),
            TestContext.Current.CancellationToken);

        Assert.False(attempt.IsSuccessful);
        Assert.Equal(2, client.CallCount);
        Assert.Null(attempt.CommittedDraft);
        var failure = Assert.IsType<TemporaryCompletionFailure>(attempt.Failure);
        Assert.Contains("1 complete answer segment(s)", failure.UserVisibleMessage, StringComparison.Ordinal);
        Assert.Contains("held only in memory and were discarded", failure.UserVisibleMessage, StringComparison.Ordinal);
        Assert.Contains("explicit resume will recompose", failure.UserVisibleMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_LATER_TRANSPORT_DETAIL", failure.UserVisibleMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishBeforeClaimCoverage_IsRejectedUntilACommittedSegmentCoversClaim()
    {
        var client = new AutoCompositionChatClient(
            firstSegmentText: "unused",
            responseOverride: (index, input) => index switch
            {
                0 => NativeAppend(input, "first segment", coveredClaimIds: []),
                1 => NativeFinish(input),
                _ => null
            });
        var bindings = Bindings("accepted");
        var composer = Composer(
            Snapshot(client, PlanningTestModelProfile.GptOss65K()),
            bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)));
        var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);

        var attempt = await bridge.CompleteAsync(
            RequestWithClaim("Return the accepted claim."),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        var committed = Assert.IsType<AliCommittedAnswerDraft>(attempt.CommittedDraft);
        Assert.Equal(["claim-1"], committed.CoveredClaimIds);
        Assert.Equal(4, client.CallCount);
        var prematureFinish = ParseCompositionInput(client.Requests[1].Messages);
        var repaired = ParseCompositionInput(client.Requests[2].Messages);
        Assert.Equal(
            prematureFinish.GetProperty("nextSequence").GetInt32(),
            repaired.GetProperty("nextSequence").GetInt32());
        Assert.Equal(
            prematureFinish.GetProperty("previousSegmentHash").GetString(),
            repaired.GetProperty("previousSegmentHash").GetString());
    }

    [Theory]
    [MemberData(nameof(ExplicitNonStopFinishReasons))]
    public async Task EveryExplicitNonStopFinishReason_IsRejected(
        ChatFinishReason finishReason)
    {
        var response = new ChatResponse(new MeaiChatMessage(
            MeaiChatRole.Assistant,
            "non-final output"))
        {
            FinishReason = finishReason
        };
        var bridge = new TemporaryCompletionBridge((_, _) =>
            ValueTask.FromResult(response));

        var attempt = await bridge.CompleteAsync(
            Request("Return only a complete answer."),
            TestContext.Current.CancellationToken);

        Assert.Null(attempt.Response);
        Assert.Equal(
            TemporaryCompletionFailureKind.CompletionOutputIncomplete,
            Assert.IsType<TemporaryCompletionFailure>(attempt.Failure).Kind);
    }

    [Fact]
    public void CompatibilityDecoder_RejectsUnexpectedToolCallAlongsideValidPayload()
    {
        var message = new MeaiChatMessage(
            MeaiChatRole.Assistant,
            PlanningContractTests.TransportJson(
                """
                {
                  "kind": "finishAnswer",
                  "answerId": "answer-1"
                }
                """));
        message.Contents.Add(new FunctionCallContent(
            "unexpected-call",
            AliAnswerCompositionProtocol.ToolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)));
        var response = new ChatResponse(message)
        {
            FinishReason = ChatFinishReason.Stop
        };

        var decoded = AliAnswerCompositionDecoder.DecodeCompatibility(response);

        Assert.False(decoded.IsSuccess);
        Assert.Contains("unexpected tool call", decoded.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceCompletion_IsRejected(string output)
    {
        var response = new ChatResponse(new MeaiChatMessage(
            MeaiChatRole.Assistant,
            output))
        {
            FinishReason = ChatFinishReason.Stop
        };
        var bridge = new TemporaryCompletionBridge((_, _) =>
            ValueTask.FromResult(response));

        var attempt = await bridge.CompleteAsync(
            Request("Return a nonempty complete answer."),
            TestContext.Current.CancellationToken);

        Assert.Null(attempt.Response);
        Assert.Equal(
            TemporaryCompletionFailureKind.CompletionOutputIncomplete,
            Assert.IsType<TemporaryCompletionFailure>(attempt.Failure).Kind);
    }

    [Fact]
    public void OpenAiRuntimeSnapshot_SeparatesProviderFromModelAndCapturesAllGenerationSettings()
    {
        var options = new OpenAiCompatibleRuntimeOptions(
            Enabled: true,
            Endpoint: new Uri("http://127.0.0.1:1234/v1/"),
            Model: "openai/gpt-oss-20b",
            DisplayName: "GPT OSS",
            Family: "gpt-oss",
            Size: "20b",
            Quantization: "q4",
            ContextTokens: 65_536,
            OutputTokenLimit: 8_192,
            Temperature: 0.2,
            TopP: 0.85,
            StreamingEnabled: false,
            SupportsVision: false,
            SupportsToolCalls: true,
            AllowPrivateLanEndpoint: false)
        {
            Engine = LocalRuntimeEngines.LmStudio,
            ReasoningEffort = "high",
            ThinkingEnabled = true,
            ThinkingControl = ModelThinkingControl.GptOssReasoningEffort
        };
        using var httpClient = new HttpClient();
        using var runtime = new OpenAiCompatibleLocalModelRuntime(httpClient, options);

        var snapshot = ((IBoundModelDispatchSource)runtime).CaptureBoundModelDispatch();

        Assert.Same(runtime, snapshot.ChatClient);
        Assert.Equal(LocalRuntimeEngines.LmStudio, snapshot.RuntimeBinding.Engine);
        Assert.DoesNotContain(options.Model, snapshot.RuntimeBinding.ToString(), StringComparison.Ordinal);
        Assert.Equal(options.Model, snapshot.ModelBinding.PackageId);
        Assert.Equal(65_536, snapshot.GenerationSettingsBinding.ContextTokens);
        Assert.Equal(8_192, snapshot.GenerationSettingsBinding.OutputTokenLimit);
        Assert.Equal(0.2, snapshot.GenerationSettingsBinding.Temperature);
        Assert.Equal(0.85, snapshot.GenerationSettingsBinding.TopP);
        Assert.Equal(false, snapshot.GenerationSettingsBinding.StreamingEnabled);
        Assert.Equal(true, snapshot.GenerationSettingsBinding.ThinkingEnabled);
        Assert.Equal("GptOssReasoningEffort", snapshot.GenerationSettingsBinding.ThinkingControl);
        Assert.Equal("high", snapshot.GenerationSettingsBinding.ReasoningEffort);
    }

    [Fact]
    public async Task BoundReasoningAndSuppressedPersona_AreTheExactSerializedTransportEnvelope()
    {
        var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        var options = new OpenAiCompatibleRuntimeOptions(
            Enabled: true,
            Endpoint: new Uri("http://127.0.0.1:1234/v1/"),
            Model: "openai/gpt-oss-20b",
            DisplayName: "GPT OSS",
            Family: "gpt-oss",
            Size: "20b",
            Quantization: "q4",
            ContextTokens: 65_536,
            OutputTokenLimit: 8_192,
            Temperature: 0.2,
            TopP: 0.85,
            StreamingEnabled: false,
            SupportsVision: false,
            SupportsToolCalls: true,
            AllowPrivateLanEndpoint: false)
        {
            Engine = LocalRuntimeEngines.LmStudio,
            ReasoningEffort = "high",
            ThinkingEnabled = true,
            ThinkingControl = ModelThinkingControl.GptOssReasoningEffort
        };
        using var runtime = new OpenAiCompatibleLocalModelRuntime(httpClient, options);
        var snapshot = ((IBoundModelDispatchSource)runtime).CaptureBoundModelDispatch();
        runtime.SetReasoningEffort("low");
        var exactMessages = new[]
        {
            new MeaiChatMessage(MeaiChatRole.System, "exact composer system"),
            new MeaiChatMessage(MeaiChatRole.User, "exact composer dossier")
        };
        var requestOptions = new ChatOptions
        {
            Tools = [],
            ToolMode = ChatToolMode.None,
            MaxOutputTokens = snapshot.Profile.OutputTokenLimit,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AliInternalModelRoutingProperties.SuppressInjectedPersona] = true,
                [AliInternalModelRoutingProperties.BoundReasoningEffort] =
                    snapshot.GenerationSettingsBinding.ReasoningEffort
            }
        };

        var response = await snapshot.ChatClient.GetResponseAsync(
            exactMessages,
            requestOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = payload.RootElement;
        Assert.Equal("high", root.GetProperty("chat_template_kwargs")
            .GetProperty("reasoning_effort").GetString());
        var serializedMessages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, serializedMessages.Length);
        Assert.Equal("system", serializedMessages[0].GetProperty("role").GetString());
        Assert.Equal("exact composer system", serializedMessages[0].GetProperty("content").GetString());
        Assert.Equal("user", serializedMessages[1].GetProperty("role").GetString());
        Assert.Equal("exact composer dossier", serializedMessages[1].GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Null);
    }

    private static AliCompletionComposer Composer(
        BoundModelDispatchSnapshot snapshot,
        TurnRuntimeBindings bindings,
        Func<long, TurnRuntimeBindings, CancellationToken,
            ValueTask<AliCompletionDispatchAuthorization>> authorize) =>
        new(() => snapshot, _ => bindings, authorize);

    private static BoundModelDispatchSnapshot Snapshot(
        IChatClient client,
        ModelProfile profile,
        string reasoningEffort = "low",
        bool useNativeProtocol = true)
    {
        var exactProfile = profile with
        {
            ProtocolIdentity = useNativeProtocol
                ? RuntimeProtocolIdentities.NativeOpenAiTools
                : RuntimeProtocolIdentities.StructuredDecision
        };
        return new BoundModelDispatchSnapshot(
            client,
            exactProfile,
            new BoundRuntimeBindingMaterial(
                "test-provider",
                "test-client",
                exactProfile.RuntimeKind,
                exactProfile.RuntimeLocation,
                exactProfile.RuntimeEndpoint)
            {
                ProtocolIdentity = exactProfile.ProtocolIdentity,
                CapabilityProfileIdentity = exactProfile.CapabilityProfileIdentity
            },
            new BoundModelBindingMaterial(
                exactProfile.ProfileId,
                exactProfile.PackageId,
                exactProfile.Family,
                exactProfile.Size,
                exactProfile.Quantization,
                exactProfile.SupportsVision,
                exactProfile.SupportsToolCalls)
            {
                CapabilityProfileIdentity = exactProfile.CapabilityProfileIdentity
            },
            new BoundGenerationSettingsBindingMaterial(
                exactProfile.ContextTokens,
                exactProfile.OutputTokenLimit,
                exactProfile.Temperature,
                0.9,
                exactProfile.StreamingEnabled,
                "test-thinking",
                true,
                reasoningEffort)
            {
                ProtocolIdentity = exactProfile.ProtocolIdentity
            });
    }

    private static TemporaryCompletionRequest Request(string immutableRequest)
    {
        var plan = new CompletionPlan(
            "answer-1",
            CompletionKind.Succeeded,
            requiredOutcomeIds: [],
            requiredClaimIds: [],
            bindings: [],
            requestedFormat: "concise");
        var decision = new OrchestrationDecision(
            workUpdate: null,
            materialClaims: [],
            nextAction: new BeginCompletionAction(plan));
        return new TemporaryCompletionRequest(
            immutableRequest,
            new AliPlanningTurnInput(11, "{\"status\":\"accepted\"}"),
            decision,
            new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, "accepted")),
            requiredOutcomes: [],
            requiredClaims: [],
            citedEvidence: []);
    }

    private static TemporaryCompletionRequest RequestWithClaim(string immutableRequest)
    {
        var claim = new OrchestrationMaterialClaim(
            "claim-1",
            "The answer includes the accepted result.",
            MaterialClaimKind.Completion,
            evidenceIds: []);
        var plan = new CompletionPlan(
            "answer-1",
            CompletionKind.Succeeded,
            requiredOutcomeIds: [],
            requiredClaimIds: [claim.ClaimId],
            bindings: [],
            requestedFormat: "concise");
        var decision = new OrchestrationDecision(
            workUpdate: null,
            materialClaims: [claim],
            nextAction: new BeginCompletionAction(plan));
        return new TemporaryCompletionRequest(
            immutableRequest,
            new AliPlanningTurnInput(11, "{\"status\":\"accepted\"}"),
            decision,
            new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, "accepted")),
            requiredOutcomes: [],
            requiredClaims: [claim],
            citedEvidence: []);
    }

    private static TurnRuntimeBindings Bindings(string suffix)
    {
        string Digest(string name) =>
            TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(name + ":" + suffix));
        return new TurnRuntimeBindings(
            Digest("profile"),
            Digest("runtime"),
            Digest("model"),
            Digest("generation"),
            Digest("registry"),
            Digest("permissions"),
            Digest("mcp"),
            Digest("attachments"),
            Digest("artifacts"));
    }

    private static ChatResponse CompleteResponse(string text) =>
        new(new MeaiChatMessage(MeaiChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop
        };

    private static JsonElement ParseCompositionInput(
        IReadOnlyList<MeaiChatMessage> messages)
    {
        using var document = JsonDocument.Parse(messages[^1].Text!);
        return document.RootElement.Clone();
    }

    private static ChatResponse NativeAppend(
        JsonElement input,
        string text,
        IReadOnlyList<string>? coveredClaimIds = null)
    {
        var message = new MeaiChatMessage(MeaiChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent(
            "composition-call-" + Guid.NewGuid().ToString("N"),
            AliAnswerCompositionProtocol.ToolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AliOrchestrationProtocol.DecisionJsonPropertyName] = JsonSerializer.Serialize(new
                {
                    kind = "appendSegment",
                    answerId = input.GetProperty("answerId").GetString(),
                    sequence = input.GetProperty("nextSequence").GetInt32(),
                    previousSegmentHash = input.GetProperty("previousSegmentHash").GetString(),
                    text,
                    coveredClaimIds = coveredClaimIds?.ToArray() ?? []
                })
            }));
        return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static ChatResponse NativeFinish(JsonElement input)
    {
        var message = new MeaiChatMessage(MeaiChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent(
            "composition-call-" + Guid.NewGuid().ToString("N"),
            AliAnswerCompositionProtocol.ToolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AliOrchestrationProtocol.DecisionJsonPropertyName] = JsonSerializer.Serialize(new
                {
                    kind = "finishAnswer",
                    answerId = input.GetProperty("answerId").GetString()
                })
            }));
        return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static ChatResponse CompatibilityAppend(
        JsonElement input,
        string text,
        IReadOnlyList<string>? coveredClaimIds = null) =>
        CompleteResponse(PlanningContractTests.TransportJson(JsonSerializer.Serialize(new
        {
            kind = "appendSegment",
            answerId = input.GetProperty("answerId").GetString(),
            sequence = input.GetProperty("nextSequence").GetInt32(),
            previousSegmentHash = input.GetProperty("previousSegmentHash").GetString(),
            text,
            coveredClaimIds = coveredClaimIds?.ToArray() ?? []
        })));

    private static ChatResponse CompatibilityFinish(JsonElement input) =>
        CompleteResponse(PlanningContractTests.TransportJson(JsonSerializer.Serialize(new
        {
            kind = "finishAnswer",
            answerId = input.GetProperty("answerId").GetString()
        })));

    private sealed record RecordedCompositionRequest(
        IReadOnlyList<MeaiChatMessage> Messages,
        ChatOptions? Options);

    private sealed class AutoCompositionChatClient : IChatClient
    {
        private readonly string _firstSegmentText;
        private readonly Func<int, JsonElement, ChatResponse?>? _override;

        internal AutoCompositionChatClient(
            string firstSegmentText = "segment-0",
            Func<int, JsonElement, ChatResponse?>? responseOverride = null)
        {
            _firstSegmentText = firstSegmentText;
            _override = responseOverride;
        }

        internal List<RecordedCompositionRequest> Requests { get; } = [];

        internal int CallCount => Requests.Count;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var exactMessages = messages.ToArray();
            var input = ParseCompositionInput(exactMessages);
            var index = Requests.Count;
            Requests.Add(new RecordedCompositionRequest(
                Array.AsReadOnly(exactMessages),
                options));
            var overridden = _override?.Invoke(index, input);
            if (overridden is not null)
            {
                return Task.FromResult(overridden);
            }

            var pageText = input.GetProperty("projectionPage").GetProperty("text").GetString();
            var nextClaims = input.GetProperty("requiredClaimCoverage")
                .GetProperty("nextIds")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray();
            var useNativeProtocol = options?.Tools is { Count: > 0 };
            if (!string.IsNullOrEmpty(pageText) || nextClaims.Length > 0)
            {
                var sequence = input.GetProperty("nextSequence").GetInt32();
                var text = sequence == 0 ? _firstSegmentText : $" segment-{sequence}";
                return Task.FromResult(useNativeProtocol
                    ? NativeAppend(input, text, nextClaims)
                    : CompatibilityAppend(input, text, nextClaims));
            }

            return Task.FromResult(useNativeProtocol
                ? NativeFinish(input)
                : CompatibilityFinish(input));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var exact = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in exact.ToChatResponseUpdates())
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

    private sealed class RecordingCounter : IAliPlanningInputCounter
    {
        internal int CallCount { get; private set; }

        internal IReadOnlyList<MeaiChatMessage>? Messages { get; private set; }

        internal IReadOnlyList<AIFunctionDeclaration>? SelectedTools { get; private set; }

        internal AIFunctionDeclaration? Protocol { get; private set; }

        public AliPlanningInputCharge Count(
            ModelProfile profile,
            IReadOnlyList<MeaiChatMessage> messages,
            IReadOnlyList<AIFunctionDeclaration> selectedTools,
            AIFunctionDeclaration? protocol)
        {
            CallCount++;
            Messages = messages;
            SelectedTools = selectedTools;
            Protocol = protocol;
            return new AliPlanningInputCharge(100, "recording", CanSafelyCharge: true);
        }
    }

    private sealed class RecordingChatClient(ChatResponse response) : IChatClient
    {
        internal int CallCount { get; private set; }

        internal IEnumerable<MeaiChatMessage>? RawMessages { get; private set; }

        internal ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            RawMessages = messages;
            Options = options;
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var exact = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in exact.ToChatResponseUpdates())
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

    private sealed class ThrowingChatClient(string secret) : IChatClient
    {
        internal int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new HttpRequestException(secret);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = await GetResponseAsync(messages, options, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class SwitchingTestRuntime(
        ModelProfile profile,
        ChatResponse response) : ILocalModelRuntime, IChatClient, IBoundModelDispatchSource
    {
        internal int CallCount { get; private set; }

        public ModelProfile ActiveProfile { get; } = profile;

        BoundModelDispatchSnapshot IBoundModelDispatchSource.CaptureBoundModelDispatch() =>
            Snapshot(this, ActiveProfile);

        public async IAsyncEnumerable<ModelToken> StreamChatAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new ModelToken(response.Text ?? string.Empty, Ali.Modules.Evidence.EvidenceStatus.Unverified);
        }

        public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeHealthCheck(
                true,
                "ready",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var exact = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in exact.ToChatResponseUpdates())
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

    private sealed class UnboundTestRuntime(ModelProfile profile) : ILocalModelRuntime
    {
        internal int StreamCallCount { get; private set; }

        public ModelProfile ActiveProfile { get; } = profile;

        public async IAsyncEnumerable<ModelToken> StreamChatAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamCallCount++;
            await Task.Yield();
            yield return new ModelToken(
                "legacy output",
                Ali.Modules.Evidence.EvidenceStatus.Unverified);
        }

        public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeHealthCheck(
                true,
                "ready",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        internal List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}"
                )
            };
        }
    }
}
