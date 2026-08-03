using System.Runtime.CompilerServices;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class CompletionPublicationSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteComposerFinish_PersistsTypedInterimAndNeverJournalsPartialFinal(
        bool omitFinishReason)
    {
        const string evidenceId = "completion-proof";
        const string workItemId = "work-1";
        const string partialCanary = "PARTIAL_FINAL_CANARY_MUST_NEVER_BE_JOURNALED";
        const string projection = "The requested file read returned accepted evidence.";
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity(
            "user",
            "completion-publication-conversation",
            "completion-publication-turn");
        var bindings = CompletionPauseRecoveryTestsBindings();
        var function = AIFunctionFactory.Create(
            (string path) => path,
            AliCapabilityCatalog.FileReadName,
            "Read a file by exact path.");
        var registry = AliProductionCapabilityCatalog.CreateRegistry([function]);
        var exactArguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["path"] = JsonSerializer.SerializeToElement("README.md")
        };

        using (var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile"))
        {
            await using var turn = await coordinator.BeginTurnAsync(
                new CoordinatorTurnContext(
                    identity.ConversationId,
                    "user-message",
                    identity.AssistantMessageId,
                    "Read the file and report the accepted result.",
                    publish: _ => { },
                    observationIdentity: identity),
                bindings,
                acceptedPriorConversation: [],
                capabilityRegistry: registry,
                liveBindingsAccessor: () => bindings,
                TestContext.Current.CancellationToken);
            var acceptedCall = await turn.OnDecisionAcceptedAsync(
                new AliPlanningDecisionAcceptedEvent(
                    identity.ConversationId,
                    identity.AssistantMessageId,
                    turn.Input.StateRevision,
                    new OrchestrationDecision(
                        new OrchestrationWorkUpdate(
                            0,
                            [new OrchestrationWorkItemUpdate(
                                workItemId,
                                "Read the requested file",
                                OrchestrationWorkStatus.Active)]),
                        materialClaims: [],
                        nextAction: new CallToolAction(
                            function.Name,
                            exactArguments,
                            "Read the requested file",
                            "The contents become accepted evidence")),
                    CallId: "call-1",
                    ToolName: function.Name),
                TestContext.Current.CancellationToken);
            var startedAt = DateTimeOffset.UtcNow.AddMilliseconds(-10);
            var evidenceReceipt = await turn.OnToolResultObservedAsync(
                new AliPlanningToolResultObservedEvent(
                    identity.ConversationId,
                    identity.AssistantMessageId,
                    acceptedCall.StateRevision,
                    evidenceId,
                    "call-1",
                    function.Name,
                    PlanningToolInvocationStatus.Returned,
                    PlanningToolDomainOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(exactArguments),
                    JsonSerializer.SerializeToElement(new
                    {
                        success = true,
                        content = "accepted result"
                    }),
                    startedAt,
                    DateTimeOffset.UtcNow,
                    projection,
                    AliPlanningProjectionSafety.Digest(projection)),
                TestContext.Current.CancellationToken);
            Assert.Equal(evidenceId, evidenceReceipt.EvidenceId);
            var decisionJson = $$"""
            {
              "workUpdate": {
                "baseRevision": 1,
                "items": [
                  {
                    "workItemId": "{{workItemId}}",
                    "outcome": "Read the requested file",
                    "status": "satisfied",
                    "parentId": null,
                    "supersededById": null,
                    "dependencyIds": [],
                    "evidenceIds": ["{{evidenceId}}"]
                  }
                ]
              },
              "materialClaims": [],
              "nextAction": {
                "kind": "beginCompletion",
                "plan": {
                  "answerId": "answer-1",
                  "completionKind": "succeeded",
                  "requiredOutcomeIds": ["{{workItemId}}"],
                  "requiredClaimIds": [],
                  "bindings": [
                    {
                      "subjectId": "{{workItemId}}",
                      "evidenceIds": ["{{evidenceId}}"]
                    }
                  ],
                  "requestedFormat": "concise",
                  "requestedSections": []
                }
              }
            }
            """;
            using var plannerModel = new SingleResponseChatClient(
                new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, decisionJson))
                {
                    FinishReason = ChatFinishReason.Stop
                });
            var incompleteResponse = new ChatResponse(new MeaiChatMessage(
                MeaiChatRole.Assistant,
                partialCanary));
            if (!omitFinishReason)
            {
                incompleteResponse.FinishReason = ChatFinishReason.Length;
            }

            using var composerModel = new SingleResponseChatClient(incompleteResponse);
            var composer = new AliCompletionComposer(
                () => Snapshot(composerModel),
                _ => bindings,
                turn.AuthorizeCompletionDispatchAsync);
            var bridge = TemporaryCompletionBridge.FromComposer(composer.ComposeAsync);
            using var planningClient = new AliOrchestrationPlanningClient(
                plannerModel,
                () => false,
                PlanningTestModelProfile.GptOss65K,
                completionBridge: bridge,
                boundDispatchAccessor: () => Snapshot(plannerModel),
                dispatchBindingsFactory: _ => bindings);
            using var activeScope = planningClient.BeginTurn(
                new CoordinatorTurnContext(
                    identity.ConversationId,
                    "user-message",
                    identity.AssistantMessageId,
                    "Read the file and report the accepted result.",
                    publish: _ => { },
                    observationIdentity: identity),
                turn.Input,
                turn,
                durableIdentity: identity,
                immutableOriginalRequest: "Read the file and report the accepted result.");

            var response = await planningClient.GetResponseAsync(
                [],
                new ChatOptions(),
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(partialCanary, response.Text, StringComparison.Ordinal);
            var prepared = Assert.IsType<AliPreparedInterimResponse>(
                planningClient.PreparedInterimResponse);
            Assert.Equal(AliPlanningInterimKind.CompletionOutputIncomplete, prepared.Kind);
            Assert.DoesNotContain(partialCanary, prepared.AnswerText, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() =>
                planningClient.RequirePreparedFinalPublication());
            await turn.CommitInterimPublicationAsync(
                prepared,
                TestContext.Current.CancellationToken);
        }

        using var writer = new TurnTransitionWriter(directory.Path, "profile");
        var replay = await writer.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Null(replay.State!.FinalPublication);
        Assert.Equal(TurnControlState.SuspendedRuntime, replay.State.Control);
        Assert.Equal(
            InterimPublicationReason.CompletionOutputIncomplete,
            replay.State.InterimPublication!.Reason);
        Assert.Equal(
            InterimPublicationStatus.Committed,
            replay.State.InterimPublication.Status);
        Assert.DoesNotContain(
            replay.Entries,
            entry => entry.Transition is FinalPublicationPreparedTransition);
        var durableInterim = await writer.ReadInterimPublicationTextAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(partialCanary, durableInterim, StringComparison.Ordinal);
        Assert.Contains("partial composer output was discarded", durableInterim, StringComparison.Ordinal);

        var protectedFiles = Directory
            .EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var path in protectedFiles)
        {
            var bytes = await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(
                partialCanary,
                System.Text.Encoding.UTF8.GetString(bytes),
                StringComparison.Ordinal);
        }
    }

    private static TurnRuntimeBindings CompletionPauseRecoveryTestsBindings()
    {
        string Digest(string value) => TurnStateIntegrity.Digest(value);
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

    private static BoundModelDispatchSnapshot Snapshot(IChatClient client)
    {
        var profile = PlanningTestModelProfile.GptOss65K() with
        {
            SupportsToolCalls = false
        };
        return new BoundModelDispatchSnapshot(
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
    }

    private sealed class SingleResponseChatClient(ChatResponse response) : IChatClient
    {
        private int _used;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(0, Interlocked.Exchange(ref _used, 1));
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
}
