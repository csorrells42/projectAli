using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Completion;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class CompletionCriticTests
{
    [Fact]
    public async Task SinglePageReview_UsesExactCompactStructuredProtocolAndReasoningEffort()
    {
        var client = new RecordingChatClient(VerdictResponse(
            complete: true,
            basis: "The exact answer satisfies the material request."));
        var profile = PlanningTestModelProfile.GptOss65K();
        var bindings = Bindings("accepted");
        var critic = Critic(client, profile, bindings, reasoningEffort: "high");

        var preparation = await critic.PrepareAsync(
            Request(pages: ["The accepted answer."]),
            TestContext.Current.CancellationToken);

        Assert.True(preparation.IsSuccessful);
        Assert.Equal(0, client.CallCount);
        var store = new TestReviewStore(new DurableBacking());
        var attempt = await critic.ReviewAsync(
            preparation.PreparedReview!,
            store,
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        Assert.True(attempt.Verdict!.Complete);
        Assert.False(attempt.ReusedCommittedVerdict);
        Assert.Equal(1, attempt.ModelCallCount);
        var request = Assert.Single(client.Requests);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(MeaiChatRole.System, request.Messages[0].Role);
        Assert.Equal(MeaiChatRole.User, request.Messages[1].Role);
        using var dossier = JsonDocument.Parse(request.Messages[1].Text!);
        var root = dossier.RootElement;
        Assert.Equal("exact-rendered-answer", root.GetProperty("auditScope").GetString());
        Assert.Equal(
            "Make the exact result available.",
            root.GetProperty("immutableOriginalRequest").GetString());
        Assert.Equal(
            "The accepted answer.",
            root.GetProperty("exactRenderedAnswer").GetProperty("text").GetString());
        Assert.True(root.TryGetProperty("authoritativeWorkGraph", out _));
        Assert.True(root.TryGetProperty("claimEvidenceCoverage", out _));
        Assert.True(root.TryGetProperty("citedAcceptedEvidence", out _));
        Assert.True(root.TryGetProperty("unresolvedFailuresAndPermissions", out _));
        Assert.True(root.TryGetProperty("relevantCapabilities", out _));

        var options = Assert.IsType<ChatOptions>(request.Options);
        Assert.Null(options.Tools);
        Assert.Equal(ChatToolMode.None, options.ToolMode);
        Assert.False(options.AllowMultipleToolCalls);
        Assert.Equal(profile.OutputTokenLimit, options.MaxOutputTokens);
        var responseFormat = Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        Assert.Equal(
            AliOrchestrationProtocol.BuildTransportSchema().GetRawText(),
            responseFormat.Schema!.Value.GetRawText());
        Assert.Equal(AliCompletionCriticProtocol.SchemaName, responseFormat.SchemaName);
        Assert.Contains("decisionJson", request.Messages[0].Text!, StringComparison.Ordinal);
        Assert.Contains("materialUnmetOutcomes", request.Messages[0].Text!, StringComparison.Ordinal);
        Assert.True(Assert.IsType<bool>(options.AdditionalProperties![
            AliInternalModelRoutingProperties.SuppressInjectedPersona]));
        Assert.Equal(
            "high",
            options.AdditionalProperties[
                AliInternalModelRoutingProperties.BoundReasoningEffort]);
    }

    [Fact]
    public async Task NativeReview_UsesOnlyExactCompactToolProtocol()
    {
        var client = new RecordingChatClient(NativeVerdictResponse(
            complete: true,
            basis: "The exact answer satisfies the material request."));
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ProtocolIdentity = RuntimeProtocolIdentities.NativeOpenAiTools
        };
        var critic = Critic(client, profile, Bindings("native"), reasoningEffort: "medium");
        var prepared = Assert.IsType<AliCompletionCriticPreparedReview>(
            (await critic.PrepareAsync(
                Request(pages: ["The accepted answer."]),
                TestContext.Current.CancellationToken)).PreparedReview);

        var attempt = await critic.ReviewAsync(
            prepared,
            new TestReviewStore(new DurableBacking()),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        var options = Assert.IsType<ChatOptions>(Assert.Single(client.Requests).Options);
        var tool = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(options.Tools!));
        Assert.Equal(AliCompletionCriticProtocol.SchemaName, tool.Name);
        Assert.Equal(
            AliOrchestrationProtocol.BuildTransportSchema().GetRawText(),
            tool.JsonSchema.GetRawText());
        Assert.NotEqual(ChatToolMode.None, options.ToolMode);
        Assert.Null(options.ResponseFormat);
    }

    [Fact]
    public async Task IdenticalCommittedReview_IsReusedWithoutSecondModelCallAfterStoreRecreation()
    {
        var client = new RecordingChatClient(VerdictResponse(
            complete: false,
            basis: "One material outcome remains.",
            unmet: ["Provide the missing artifact receipt."]));
        var profile = PlanningTestModelProfile.GptOss65K();
        var bindings = Bindings("accepted");
        var critic = Critic(client, profile, bindings, reasoningEffort: "medium");
        var request = Request(pages: ["Draft answer."]);
        var firstPrepared = Assert.IsType<AliCompletionCriticPreparedReview>(
            (await critic.PrepareAsync(request, TestContext.Current.CancellationToken))
            .PreparedReview);
        var durableBacking = new DurableBacking();

        var first = await critic.ReviewAsync(
            firstPrepared,
            new TestReviewStore(durableBacking),
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccessful);
        Assert.False(first.Verdict!.Complete);
        Assert.Equal(1, client.CallCount);

        // A new adapter instance represents process reconstruction over the same journal state.
        var afterRestartPrepared = Assert.IsType<AliCompletionCriticPreparedReview>(
            (await critic.PrepareAsync(request, TestContext.Current.CancellationToken))
            .PreparedReview);
        var afterRestart = await critic.ReviewAsync(
            afterRestartPrepared,
            new TestReviewStore(durableBacking),
            TestContext.Current.CancellationToken);

        Assert.True(afterRestart.IsSuccessful);
        Assert.True(afterRestart.ReusedCommittedVerdict);
        Assert.Equal(0, afterRestart.ModelCallCount);
        Assert.Equal(first.Identity.Digest, afterRestart.Identity.Digest);
        Assert.Equal(first.Verdict, afterRestart.Verdict);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task MultiPageReview_AuditsEveryExactPageThenFinalAndCannotOverrideRejectedPage()
    {
        var client = new RecordingChatClient(
            VerdictResponse(true, "Page one is materially supported."),
            VerdictResponse(
                false,
                "Page two omits the requested location.",
                ["State where the artifact was written."]),
            VerdictResponse(true, "The cross-page structure is internally consistent."));
        var critic = Critic(
            client,
            PlanningTestModelProfile.GptOss65K(),
            Bindings("accepted"),
            reasoningEffort: "high");
        var preparation = await critic.PrepareAsync(
            Request(pages: ["First committed page. ", "Second committed page."]),
            TestContext.Current.CancellationToken);

        var attempt = await critic.ReviewAsync(
            preparation.PreparedReview!,
            new TestReviewStore(new DurableBacking()),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        Assert.False(attempt.Verdict!.Complete);
        Assert.Contains(
            "State where the artifact was written.",
            attempt.Verdict.MaterialUnmetOutcomes);
        Assert.Equal(3, attempt.ModelCallCount);
        Assert.Equal(3, client.CallCount);
        Assert.All(client.Requests, static request => Assert.Equal(2, request.Messages.Count));
        Assert.NotSame(client.Requests[0].Messages, client.Requests[1].Messages);
        Assert.NotSame(client.Requests[1].Messages, client.Requests[2].Messages);

        using var firstPage = JsonDocument.Parse(client.Requests[0].Messages[1].Text!);
        using var secondPage = JsonDocument.Parse(client.Requests[1].Messages[1].Text!);
        Assert.Equal(
            "First committed page. ",
            firstPage.RootElement.GetProperty("page").GetProperty("text").GetString());
        Assert.Equal(
            "Second committed page.",
            secondPage.RootElement.GetProperty("page").GetProperty("text").GetString());
        Assert.Equal(
            firstPage.RootElement.GetProperty("page").GetProperty("SegmentHash").GetString(),
            secondPage.RootElement.GetProperty("page")
                .GetProperty("PreviousSegmentHash").GetString());

        using var final = JsonDocument.Parse(client.Requests[2].Messages[1].Text!);
        var finalRoot = final.RootElement;
        Assert.Equal("final-cross-page-coverage", finalRoot.GetProperty("auditScope").GetString());
        Assert.Equal(2, finalRoot.GetProperty("pageAudits").GetArrayLength());
        Assert.Equal(1, finalRoot.GetProperty("adjacentPageBoundaryProjections").GetArrayLength());
        Assert.False(finalRoot.GetProperty("pageAudits")[1].GetProperty("Complete").GetBoolean());
    }

    [Fact]
    public async Task MultiPageReview_ProjectsOnlyWholeClaimRelevantRecordsAndFinalUsesManifests()
    {
        var client = new RecordingChatClient(
            VerdictResponse(true, "First claim is supported."),
            VerdictResponse(true, "Second claim is supported."),
            VerdictResponse(true, "Cross-page coverage is complete."));
        var request = TwoClaimRequest();
        var critic = Critic(
            client,
            PlanningTestModelProfile.GptOss65K(),
            Bindings("claim-projection"),
            reasoningEffort: "high");
        var prepared = Assert.IsType<AliCompletionCriticPreparedReview>(
            (await critic.PrepareAsync(request, TestContext.Current.CancellationToken))
            .PreparedReview);

        var attempt = await critic.ReviewAsync(
            prepared,
            new TestReviewStore(new DurableBacking()),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        using var first = JsonDocument.Parse(client.Requests[0].Messages[1].Text!);
        using var second = JsonDocument.Parse(client.Requests[1].Messages[1].Text!);
        Assert.Equal(
            ["work-1"],
            first.RootElement.GetProperty("claimRelevantWorkGraph").GetProperty("nodes")
                .EnumerateArray().Select(static node => node.GetProperty("Id").GetString()));
        Assert.Equal(
            ["evidence-1"],
            first.RootElement.GetProperty("pageAcceptedEvidence")
                .EnumerateArray().Select(static evidence => evidence.GetProperty("EvidenceId").GetString()));
        Assert.Equal(
            ["work-2"],
            second.RootElement.GetProperty("claimRelevantWorkGraph").GetProperty("nodes")
                .EnumerateArray().Select(static node => node.GetProperty("Id").GetString()));
        Assert.Equal(
            ["evidence-2"],
            second.RootElement.GetProperty("pageAcceptedEvidence")
                .EnumerateArray().Select(static evidence => evidence.GetProperty("EvidenceId").GetString()));

        using var final = JsonDocument.Parse(client.Requests[2].Messages[1].Text!);
        var finalRoot = final.RootElement;
        Assert.False(finalRoot.TryGetProperty("authoritativeWorkGraph", out _));
        Assert.False(finalRoot.TryGetProperty("claimEvidenceCoverage", out _));
        Assert.False(finalRoot.TryGetProperty("citedAcceptedEvidence", out _));
        Assert.True(finalRoot.TryGetProperty("authoritativeWorkGraphManifest", out _));
        Assert.True(finalRoot.TryGetProperty("claimEvidenceCoverageManifest", out _));
        Assert.True(finalRoot.TryGetProperty("citedAcceptedEvidenceManifest", out _));
        Assert.DoesNotContain("unrelated large projection", client.Requests[2].Messages[1].Text!);
    }

    [Fact]
    public async Task CriticAdmission_ChargesExactVerdictSchemaForEveryModelCall()
    {
        var counter = new RecordingInputCounter();
        var client = new RecordingChatClient(
            VerdictResponse(true, "Page one."),
            VerdictResponse(true, "Page two."),
            VerdictResponse(true, "Final."));
        var critic = Critic(
            client,
            PlanningTestModelProfile.GptOss65K(),
            Bindings("schema-charge"),
            reasoningEffort: "high",
            counter);
        var prepared = Assert.IsType<AliCompletionCriticPreparedReview>(
            (await critic.PrepareAsync(
                Request(pages: ["First page. ", "Second page."]),
                TestContext.Current.CancellationToken)).PreparedReview);

        var attempt = await critic.ReviewAsync(
            prepared,
            new TestReviewStore(new DurableBacking()),
            TestContext.Current.CancellationToken);

        Assert.True(attempt.IsSuccessful);
        Assert.Equal(3, counter.Protocols.Count);
        Assert.All(counter.Protocols, protocol =>
        {
            Assert.NotNull(protocol);
            Assert.Equal(AliCompletionCriticProtocol.SchemaName, protocol!.Name);
            Assert.Equal(
                AliOrchestrationProtocol.BuildTransportSchema().GetRawText(),
                protocol.JsonSchema.GetRawText());
        });
    }

    [Fact]
    public async Task ReviewIdentity_BindsMaterialPagesEvidenceRuntimeGenerationAndReasoningEffort()
    {
        var client = new RecordingChatClient(VerdictResponse(true, "unused"));
        var profile = PlanningTestModelProfile.GptOss65K();
        var acceptedBindings = Bindings("accepted");

        async Task<AliCompletionCriticReviewIdentity> Identity(
            AliCompletionCriticRequest request,
            TurnRuntimeBindings bindings,
            string effort)
        {
            var prepared = await Critic(client, profile, bindings, effort)
                .PrepareAsync(request, TestContext.Current.CancellationToken);
            return prepared.PreparedReview!.Identity;
        }

        var basis = Request(stateRevision: 17, pages: ["Exact page."], evidenceProjection: "proof-a");
        var same = Request(stateRevision: 17, pages: ["Exact page."], evidenceProjection: "proof-a");
        var exact = await Identity(basis, acceptedBindings, "high");
        Assert.Equal(exact.Digest, (await Identity(same, acceptedBindings, "high")).Digest);
        Assert.Equal(
            exact.Digest,
            (await Identity(
                Request(stateRevision: 18, pages: ["Exact page."], evidenceProjection: "proof-a"),
                acceptedBindings,
                "high")).Digest);
        Assert.NotEqual(
            exact.Digest,
            (await Identity(
                Request(stateRevision: 17, pages: ["Changed page."], evidenceProjection: "proof-a"),
                acceptedBindings,
                "high")).Digest);
        Assert.NotEqual(
            exact.Digest,
            (await Identity(
                Request(stateRevision: 17, pages: ["Exact page."], evidenceProjection: "proof-b"),
                acceptedBindings,
                "high")).Digest);
        Assert.NotEqual(exact.Digest, (await Identity(basis, Bindings("changed"), "high")).Digest);
        Assert.NotEqual(exact.Digest, (await Identity(basis, acceptedBindings, "low")).Digest);
        Assert.Single(exact.RenderedPageHashes);
        Assert.Equal("high", exact.ReasoningEffort);
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData("{\"complete\":true,\"basis\":\"ok\",\"materialUnmetOutcomes\":[],\"extra\":1}")]
    [InlineData("{\"complete\":false,\"basis\":\"missing\",\"materialUnmetOutcomes\":[]}")]
    [InlineData("{\"complete\":true,\"basis\":\"not complete\",\"materialUnmetOutcomes\":[\"x\"]}")]
    [InlineData("{\"complete\":false,\"basis\":\"duplicate\",\"materialUnmetOutcomes\":[\"x\",\"x\"]}")]
    [InlineData("{\"complete\":false,\"basis\":\"   \",\"materialUnmetOutcomes\":[\"x\"]}")]
    public void Decoder_RejectsMalformedOrInconsistentTypedVerdicts(string json)
    {
        var decoded = AliCompletionCriticProtocol.DecodeCompatibility(new ChatResponse(
            new MeaiChatMessage(
                MeaiChatRole.Assistant,
                PlanningContractTests.TransportJson(json)))
        {
            FinishReason = ChatFinishReason.Stop
        });

        Assert.False(decoded.IsSuccess);
        Assert.Null(decoded.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(decoded.Error));
    }

    [Fact]
    public void Decoder_RejectsNonStopAndToolCallResponses()
    {
        var nonStop = new ChatResponse(new MeaiChatMessage(
            MeaiChatRole.Assistant,
            PlanningContractTests.TransportJson(
                "{\"complete\":true,\"basis\":\"ok\",\"materialUnmetOutcomes\":[]}")))
        {
            FinishReason = ChatFinishReason.Length
        };
        var toolMessage = new MeaiChatMessage(
            MeaiChatRole.Assistant,
            PlanningContractTests.TransportJson(
                "{\"complete\":true,\"basis\":\"ok\",\"materialUnmetOutcomes\":[]}"));
        toolMessage.Contents.Add(new FunctionCallContent("call-1", "not-allowed"));
        var toolCall = new ChatResponse(toolMessage)
        {
            FinishReason = ChatFinishReason.Stop
        };

        Assert.False(AliCompletionCriticProtocol.DecodeCompatibility(nonStop).IsSuccess);
        Assert.False(AliCompletionCriticProtocol.DecodeCompatibility(toolCall).IsSuccess);
    }

    [Fact]
    public async Task UnsupportedBoundProtocol_IsRejectedBeforeAuthorizationOrModelCall()
    {
        var client = new RecordingChatClient(VerdictResponse(true, "must not run"));
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            ProtocolIdentity = RuntimeProtocolIdentities.ChatOnly
        };
        var authorizationCalls = 0;
        var critic = new AliCompletionCritic(
            () => Snapshot(client, profile, "low"),
            _ => Bindings("unsupported"),
            (revision, _, _) =>
            {
                authorizationCalls++;
                return ValueTask.FromResult(
                    new AliCompletionDispatchAuthorization(true, revision));
            });

        var preparation = await critic.PrepareAsync(
            Request(pages: ["The accepted answer."]),
            TestContext.Current.CancellationToken);

        Assert.False(preparation.IsSuccessful);
        Assert.Equal(
            AliCompletionCriticFailureKind.DispatchBindingsChanged,
            Assert.IsType<AliCompletionCriticFailure>(preparation.Failure).Kind);
        Assert.Equal(0, authorizationCalls);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void ReviewStoreRules_RejectPreparedDuplicateReuseCommittedAndPermitChangedIdentity()
    {
        var identity = IdentityForRules("a");
        var changed = IdentityForRules("b");
        var prepared = AliCompletionCriticReviewStoreRules.Prepare(null, identity);

        Assert.Equal(
            AliCompletionCriticReviewDisposition.IdenticalReviewInProgress,
            AliCompletionCriticReviewStoreRules.ClassifyBegin(prepared, identity));
        Assert.Equal(
            AliCompletionCriticReviewDisposition.Start,
            AliCompletionCriticReviewStoreRules.ClassifyBegin(prepared, changed));

        var verdict = new AliCompletionCriticVerdict(true, "Complete.", []);
        var committed = AliCompletionCriticReviewStoreRules.Commit(prepared, identity, verdict);
        Assert.Equal(
            AliCompletionCriticReviewDisposition.ReuseCommitted,
            AliCompletionCriticReviewStoreRules.ClassifyBegin(committed, identity));
        Assert.Equal(verdict, committed.Verdict);
        Assert.Throws<InvalidDataException>(() =>
            AliCompletionCriticReviewStoreRules.Commit(committed, identity, verdict));
    }

    [Fact]
    public async Task DurableReviewStore_WritesAheadSuppressesDuplicatesAndReplaysCommittedVerdict()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var turnIdentity = new TurnIdentity(
            "critic-user",
            "critic-conversation",
            "critic-turn");
        var bindings = Bindings("durable");
        var outcome = "Create the still-missing deployment receipt.";
        AliCompletionCriticReviewIdentity reviewIdentity;
        long committedRevision;
        using (var coordinator = new AliPlanningStateCoordinator(directory.Path, "critic-profile"))
        {
            await using var turn = await coordinator.BeginTurnAsync(
                new CoordinatorTurnContext(
                    turnIdentity.ConversationId,
                    "critic-user-message",
                    turnIdentity.AssistantMessageId,
                    "Finish the exact requested work.",
                    publish: _ => { },
                    observationIdentity: turnIdentity),
                bindings,
                acceptedPriorConversation: [],
                capabilityRegistry: null,
                liveBindingsAccessor: () => bindings,
                TestContext.Current.CancellationToken);
            reviewIdentity = DurableIdentity(turn.Input.StateRevision, "durable");
            var store = (IAliCompletionCriticReviewStore)turn;

            var prepared = await store.BeginAsync(
                reviewIdentity,
                TestContext.Current.CancellationToken);
            Assert.Equal(AliCompletionCriticReviewDisposition.Start, prepared.Disposition);
            Assert.Equal(reviewIdentity.SourceStateRevision + 1, prepared.StoreSnapshot.StateRevision);

            var duplicatePrepared = await store.BeginAsync(
                reviewIdentity,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                AliCompletionCriticReviewDisposition.IdenticalReviewInProgress,
                duplicatePrepared.Disposition);
            Assert.Equal(
                prepared.StoreSnapshot.StateRevision,
                duplicatePrepared.StoreSnapshot.StateRevision);

            var verdict = new AliCompletionCriticVerdict(
                complete: false,
                basis: "The deployment receipt is not present.",
                materialUnmetOutcomes: [outcome]);
            var committed = await store.CommitAsync(
                reviewIdentity,
                verdict,
                TestContext.Current.CancellationToken);
            committedRevision = committed.StateRevision;
            using var projected = JsonDocument.Parse(committed.AuthoritativeStateProjection!);
            var criticProjection = projected.RootElement.GetProperty("completionCritic");
            Assert.False(criticProjection.GetProperty("complete").GetBoolean());
            Assert.Equal(
                outcome,
                Assert.Single(
                    criticProjection.GetProperty("materialUnmetOutcomes")
                        .EnumerateArray())
                    .GetString());
            Assert.False(criticProjection.TryGetProperty("basis", out _));

            var reused = await store.BeginAsync(
                reviewIdentity,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                AliCompletionCriticReviewDisposition.ReuseCommitted,
                reused.Disposition);
            Assert.Equal(verdict.Complete, reused.CommittedVerdict!.Complete);
            Assert.Equal(verdict.Basis, reused.CommittedVerdict.Basis);
            Assert.Equal(
                verdict.MaterialUnmetOutcomes,
                reused.CommittedVerdict.MaterialUnmetOutcomes);
            Assert.Equal(committedRevision, reused.StoreSnapshot.StateRevision);

            var sameMaterialAfterJournalAdvance = reviewIdentity with
            {
                SourceStateRevision = committedRevision
            };
            var reusedAfterAdvance = await store.BeginAsync(
                sameMaterialAfterJournalAdvance,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                AliCompletionCriticReviewDisposition.ReuseCommitted,
                reusedAfterAdvance.Disposition);
            Assert.Equal(committedRevision, reusedAfterAdvance.StoreSnapshot.StateRevision);
        }

        using var reader = new TurnTransitionWriter(directory.Path, "critic-profile");
        var replay = await reader.ReplayAsync(
            turnIdentity,
            TestContext.Current.CancellationToken);
        var durable = Assert.IsType<CompletionCriticReviewPersistenceState>(
            replay.State!.CompletionCriticReview);
        Assert.Equal(CompletionCriticReviewPersistenceStatus.Committed, durable.Status);
        Assert.Equal(reviewIdentity.Digest, durable.ReviewIdentityDigest);
        Assert.Equal(reviewIdentity.RenderedPageHashes, durable.RenderedPageHashes);
        Assert.Equal(reviewIdentity.RuntimeDigest, durable.RuntimeDigest);
        Assert.Equal(reviewIdentity.GenerationSettingsDigest, durable.GenerationSettingsDigest);
        Assert.Equal(reviewIdentity.ReasoningEffort, durable.ReasoningEffort);
        Assert.False(durable.Complete);
        Assert.Equal(outcome, Assert.Single(durable.MaterialUnmetOutcomes));
        Assert.Equal(committedRevision, replay.State.Revision);
        Assert.Single(
            replay.Entries,
            static entry => entry.Transition is CompletionCriticReviewPreparedTransition);
        Assert.Single(
            replay.Entries,
            static entry => entry.Transition is CompletionCriticVerdictCommittedTransition);
    }

    private static AliCompletionCritic Critic(
        IChatClient client,
        ModelProfile profile,
        TurnRuntimeBindings bindings,
        string reasoningEffort,
        IAliPlanningInputCounter? inputCounter = null) =>
        new(
            () => Snapshot(client, profile, reasoningEffort),
            _ => bindings,
            (revision, _, _) => ValueTask.FromResult(
                new AliCompletionDispatchAuthorization(true, revision)),
            new AliPlanningInputAdmission(inputCounter));

    private static BoundModelDispatchSnapshot Snapshot(
        IChatClient client,
        ModelProfile profile,
        string reasoningEffort) =>
        new(
            client,
            profile,
            new BoundRuntimeBindingMaterial(
                "test-provider",
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
                0.9,
                profile.StreamingEnabled,
                "test-thinking",
                true,
                reasoningEffort)
            {
                ProtocolIdentity = profile.ProtocolIdentity
            });

    private static AliCompletionCriticRequest Request(
        long stateRevision = 17,
        IReadOnlyList<string>? pages = null,
        string evidenceProjection = "accepted proof")
    {
        var exactPages = pages ?? ["The accepted answer."];
        var plan = new CompletionPlan(
            "answer-1",
            CompletionKind.Succeeded,
            requiredOutcomeIds: ["outcome-1"],
            requiredClaimIds: ["claim-1"],
            bindings: [new CompletionEvidenceBinding("claim-1", ["evidence-1"])],
            requestedFormat: "concise",
            requestedSections: ["Result"]);
        var draftStore = new AliAnswerDraftStore(plan.AnswerId, plan.RequiredClaimIds);
        for (var index = 0; index < exactPages.Count; index++)
        {
            var snapshot = draftStore.Snapshot();
            draftStore.Append(new AliAppendAnswerSegmentAction(
                plan.AnswerId,
                snapshot.NextSequence,
                snapshot.PreviousSegmentHash,
                exactPages[index],
                index == 0 ? ["claim-1"] : []));
        }

        var evidence = new AcceptedEvidenceProjection(
            "evidence-1",
            "call-1",
            "read-artifact",
            PlanningToolInvocationStatus.Returned,
            PlanningToolDomainOutcome.Succeeded,
            evidenceProjection,
            workItemId: "outcome-1");
        return new AliCompletionCriticRequest(
            stateRevision,
            "Make the exact result available.",
            [new AliCompletionCriticSteeringConstraint(3, "Keep the result concise.")],
            plan,
            draftStore.Finish(plan.AnswerId),
            WorkGraphSnapshot.Empty,
            [new AliCompletionCriticClaimCoverage(
                "claim-1",
                "The artifact was created.",
                MaterialClaimKind.Artifact,
                ["evidence-1"])],
            [evidence],
            [new AliCompletionCriticUnresolvedIssue(
                "permission-1",
                AliCompletionCriticIssueKind.PermissionRequired,
                "Publishing outside the workspace still requires permission.",
                ["evidence-1"])],
            [new AliCompletionCriticCapability(
                "files-write",
                "write-file",
                "Writes an exact workspace file.",
                "enabled",
                "workspace write permission")]);
    }

    private static AliCompletionCriticRequest TwoClaimRequest()
    {
        var plan = new CompletionPlan(
            "answer-claims",
            CompletionKind.Succeeded,
            requiredOutcomeIds: ["work-1", "work-2"],
            requiredClaimIds: ["claim-1", "claim-2"],
            bindings:
            [
                new CompletionEvidenceBinding("work-1", ["evidence-1"]),
                new CompletionEvidenceBinding("work-2", ["evidence-2"]),
                new CompletionEvidenceBinding("claim-1", ["evidence-1"]),
                new CompletionEvidenceBinding("claim-2", ["evidence-2"])
            ]);
        var draftStore = new AliAnswerDraftStore(plan.AnswerId, plan.RequiredClaimIds);
        var first = draftStore.Snapshot();
        draftStore.Append(new AliAppendAnswerSegmentAction(
            plan.AnswerId,
            first.NextSequence,
            first.PreviousSegmentHash,
            "First supported result. ",
            ["claim-1"]));
        var second = draftStore.Snapshot();
        draftStore.Append(new AliAppendAnswerSegmentAction(
            plan.AnswerId,
            second.NextSequence,
            second.PreviousSegmentHash,
            "Second supported result.",
            ["claim-2"]));

        AcceptedEvidenceProjection Evidence(string id, string workId, string projection) =>
            new(
                id,
                "call-" + id,
                "read-artifact",
                PlanningToolInvocationStatus.Returned,
                PlanningToolDomainOutcome.Succeeded,
                projection,
                workItemId: workId);
        WorkNode Node(string id, string evidenceId) =>
            new(
                id,
                "Complete " + id,
                ParentId: null,
                WorkNodeStatus.Satisfied,
                ImmutableArray<string>.Empty,
                [evidenceId]);
        var nodes = new[]
        {
            Node("work-1", "evidence-1"),
            Node("work-2", "evidence-2"),
            Node("work-unrelated", "evidence-unrelated")
        }.ToImmutableDictionary(static node => node.Id, StringComparer.Ordinal);

        return new AliCompletionCriticRequest(
            22,
            "Return both exact supported results.",
            acceptedSteeringConstraints: [],
            plan,
            draftStore.Finish(plan.AnswerId),
            new WorkGraphSnapshot(9, nodes),
            [
                new AliCompletionCriticClaimCoverage(
                    "claim-1", "First result exists.", MaterialClaimKind.Artifact, ["evidence-1"]),
                new AliCompletionCriticClaimCoverage(
                    "claim-2", "Second result exists.", MaterialClaimKind.Artifact, ["evidence-2"])
            ],
            [
                Evidence("evidence-1", "work-1", "first whole projection"),
                Evidence("evidence-2", "work-2", "second whole projection"),
                Evidence("evidence-unrelated", "work-unrelated", "unrelated large projection")
            ],
            unresolvedIssues: [],
            relevantCapabilities: []);
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

    private static ChatResponse VerdictResponse(
        bool complete,
        string basis,
        IReadOnlyList<string>? unmet = null) =>
        new(new MeaiChatMessage(
            MeaiChatRole.Assistant,
            PlanningContractTests.TransportJson(JsonSerializer.Serialize(new
            {
                complete,
                basis,
                materialUnmetOutcomes = unmet ?? []
            }))))
        {
            FinishReason = ChatFinishReason.Stop
        };

    private static ChatResponse NativeVerdictResponse(
        bool complete,
        string basis,
        IReadOnlyList<string>? unmet = null)
    {
        var message = new MeaiChatMessage(MeaiChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent(
            "critic-call-" + Guid.NewGuid().ToString("N"),
            AliCompletionCriticProtocol.SchemaName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AliOrchestrationProtocol.DecisionJsonPropertyName] = JsonSerializer.Serialize(new
                {
                    complete,
                    basis,
                    materialUnmetOutcomes = unmet ?? []
                })
            }));
        return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static AliCompletionCriticReviewIdentity IdentityForRules(string suffix)
    {
        string Digest(string value) =>
            TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value + suffix));
        return new AliCompletionCriticReviewIdentity(
            Digest("identity"),
            17,
            "answer-1",
            Digest("answer"),
            [Digest("page")],
            Digest("runtime"),
            Digest("model"),
            Digest("generation"),
            "high");
    }

    private static AliCompletionCriticReviewIdentity DurableIdentity(
        long sourceStateRevision,
        string suffix)
    {
        string Digest(string value) =>
            TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value + ":" + suffix));
        return new AliCompletionCriticReviewIdentity(
            Digest("review"),
            sourceStateRevision,
            "answer-durable",
            Digest("answer"),
            [Digest("page-0"), Digest("page-1")],
            Digest("runtime"),
            Digest("model"),
            Digest("generation"),
            "high");
    }

    private sealed record RecordedRequest(
        IReadOnlyList<MeaiChatMessage> Messages,
        ChatOptions? Options);

    private sealed class RecordingChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        internal List<RecordedRequest> Requests { get; } = [];

        internal int CallCount => Requests.Count;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(
                Array.AsReadOnly(messages.ToArray()),
                options));
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
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

    private sealed class RecordingInputCounter : IAliPlanningInputCounter
    {
        internal List<AIFunctionDeclaration?> Protocols { get; } = [];

        public AliPlanningInputCharge Count(
            ModelProfile profile,
            IReadOnlyList<MeaiChatMessage> messages,
            IReadOnlyList<AIFunctionDeclaration> selectedTools,
            AIFunctionDeclaration? protocol)
        {
            Protocols.Add(protocol);
            return new AliPlanningInputCharge(1, "recording-counter", CanSafelyCharge: true);
        }
    }

    private sealed class DurableBacking
    {
        internal object Sync { get; } = new();

        internal long Revision { get; set; } = 100;

        internal AliCompletionCriticReviewState? State { get; set; }
    }

    private sealed class TestReviewStore(DurableBacking backing) : IAliCompletionCriticReviewStore
    {
        public ValueTask<AliCompletionCriticReviewLease> BeginAsync(
            AliCompletionCriticReviewIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (backing.Sync)
            {
                var disposition = AliCompletionCriticReviewStoreRules.ClassifyBegin(
                    backing.State,
                    identity);
                if (disposition == AliCompletionCriticReviewDisposition.Start)
                {
                    backing.State = AliCompletionCriticReviewStoreRules.Prepare(
                        backing.State,
                        identity);
                    backing.Revision++;
                }

                return ValueTask.FromResult(new AliCompletionCriticReviewLease(
                    disposition,
                    disposition == AliCompletionCriticReviewDisposition.ReuseCommitted
                        ? backing.State!.Verdict
                        : null,
                    new AliCompletionCriticStoreSnapshot(
                        backing.Revision,
                        "{\"critic\":\"prepared-or-committed\"}")));
            }
        }

        public ValueTask<AliCompletionCriticStoreSnapshot> CommitAsync(
            AliCompletionCriticReviewIdentity identity,
            AliCompletionCriticVerdict verdict,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (backing.Sync)
            {
                backing.State = AliCompletionCriticReviewStoreRules.Commit(
                    backing.State
                    ?? throw new InvalidDataException("No critic review is prepared."),
                    identity,
                    verdict);
                backing.Revision++;
                return ValueTask.FromResult(new AliCompletionCriticStoreSnapshot(
                    backing.Revision,
                    "{\"critic\":\"committed\"}"));
            }
        }
    }
}
