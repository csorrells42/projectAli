using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.Work;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class PlanningEvidenceAuthorityTests
{
    [Fact]
    public async Task LargeCurrentGraph_ResolvesOnlyEvidenceExplicitlyCitedByCandidate()
    {
        var nodes = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        for (var index = 0; index < 2_000; index++)
        {
            var id = $"accepted-work-{index:D4}";
            nodes.Add(
                id,
                new WorkNode(
                    id,
                    "Previously accepted work.",
                    ParentId: null,
                    WorkNodeStatus.Pending,
                    ImmutableArray<string>.Empty,
                    ImmutableArray.Create($"old-evidence-{index:D4}")));
        }

        var graph = new WorkGraphSnapshot(7, nodes.ToImmutable());
        var input = new AliPlanningTurnInput(
            stateRevision: 11,
            stateProjection: "{\"workGraph\":{\"total\":2000,\"items\":[]}}",
            acceptedEvidence: [],
            knownWorkItemIds: graph.Nodes.Keys,
            workGraphRevision: graph.Revision,
            authoritativeWorkGraph: graph);
        const string candidateEvidenceId = "candidate-evidence";
        const string candidateWorkItemId = "candidate-work";
        var response = PlanningDecision($$"""
            {
              "workUpdate": {
                "baseRevision": 7,
                "items": [
                  {
                    "workItemId": "{{candidateWorkItemId}}",
                    "outcome": "Use one exact candidate receipt.",
                    "status": "pending",
                    "parentId": null,
                    "supersededById": null,
                    "dependencyIds": [],
                    "evidenceIds": ["{{candidateEvidenceId}}"]
                  }
                ]
              },
              "materialClaims": [],
              "nextAction": {
                "kind": "requestUserInput",
                "question": "What should Ali do next?",
                "missingInformation": "The next outcome is not known."
              }
            }
            """);
        using var inner = new SingleResponseChatClient(response);
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            () => PlanningTestModelProfile.GptOss65K() with
            {
                ContextTokens = 262_144
            });
        var observer = new RecordingEvidenceAuthority(
            [Projection(candidateEvidenceId, "candidate projection", candidateWorkItemId)],
            authoritativeGraph: null,
            workGraphRevision: 8);
        using var scope = client.BeginTurn(
            new CoordinatorTurnContext(
                "conversation",
                "user-message",
                "assistant-message",
                "Continue the accepted plan.",
                publish: _ => { }),
            input,
            observer);

        var result = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("What should Ali do next?", result.Text);
        var request = Assert.Single(observer.Requests);
        Assert.Equal([candidateEvidenceId], request.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ColdOutcomeAndClaimEvidence_ReachesCompletionBridgeExactly()
    {
        const string outcomeEvidenceId = "cold-outcome-evidence";
        const string claimEvidenceId = "cold-claim-evidence";
        const string outcomeCanary = "COLD_OUTCOME_PROJECTION_CANARY";
        const string claimCanary = "COLD_CLAIM_PROJECTION_CANARY";
        var outcome = new WorkNode(
            "outcome-1",
            "Produce the exact requested result.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.Create(outcomeEvidenceId));
        var graph = new WorkGraphSnapshot(
            7,
            ImmutableDictionary.Create<string, WorkNode>(StringComparer.Ordinal)
                .Add(outcome.Id, outcome));
        var acceptedEvidence = new[]
        {
            Projection(outcomeEvidenceId, outcomeCanary, "outcome-1"),
            Projection(claimEvidenceId, claimCanary)
        };
        var response = PlanningDecision($$"""
            {
              "workUpdate": null,
              "materialClaims": [
                {
                  "claimId": "claim-1",
                  "statement": "The requested result was produced.",
                  "kind": "completion",
                  "evidenceIds": ["{{claimEvidenceId}}"]
                }
              ],
              "nextAction": {
                "kind": "beginCompletion",
                "plan": {
                  "answerId": "answer-1",
                  "completionKind": "succeeded",
                  "requiredOutcomeIds": ["outcome-1"],
                  "requiredClaimIds": ["claim-1"],
                  "bindings": [
                    {
                      "subjectId": "outcome-1",
                      "evidenceIds": ["{{outcomeEvidenceId}}"]
                    },
                    {
                      "subjectId": "claim-1",
                      "evidenceIds": ["{{claimEvidenceId}}"]
                    }
                  ],
                  "requestedFormat": "concise",
                  "requestedSections": []
                }
              }
            }
            """);
        using var inner = new SingleResponseChatClient(response);
        TemporaryCompletionRequest? captured = null;
        var bridge = new TemporaryCompletionBridge((request, _) =>
        {
            captured = request;
            return ValueTask.FromResult(Complete("Completed exactly."));
        });
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            completionBridge: bridge);
        var observer = new RecordingEvidenceAuthority(
            acceptedEvidence,
            graph,
            graph.Revision);
        using var scope = client.BeginTurn(
            new CoordinatorTurnContext(
                "conversation",
                "user-message",
                "assistant-message",
                "Complete the accepted plan.",
                publish: _ => { }),
            new AliPlanningTurnInput(
                stateRevision: 11,
                stateProjection: "{\"workGraph\":{\"total\":1,\"items\":[]}}",
                acceptedEvidence: [],
                workGraphRevision: graph.Revision,
                authoritativeWorkGraph: graph),
            observer);

        var result = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Completed exactly.", result.Text);
        var exact = Assert.IsType<TemporaryCompletionRequest>(captured);
        Assert.Equal("outcome-1", Assert.Single(exact.RequiredOutcomes).Id);
        Assert.Equal("claim-1", Assert.Single(exact.RequiredClaims).ClaimId);
        Assert.Equal(
            [outcomeEvidenceId, claimEvidenceId],
            exact.CitedEvidence.Select(item => item.EvidenceId));
        Assert.Equal(outcomeCanary, exact.CitedEvidence[0].Projection);
        Assert.Equal(claimCanary, exact.CitedEvidence[1].Projection);
        Assert.Empty(exact.AuthoritativeInput.AcceptedEvidence);
        Assert.Single(observer.Requests);
        Assert.Equal(
            [claimEvidenceId, outcomeEvidenceId],
            observer.Requests[0].Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LargeTerminalGraph_UsesOneBatchAuthorityLookupAndBoundedComposerDossier()
    {
        var nodes = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        var acceptedEvidence = new List<AcceptedEvidenceProjection>();
        for (var index = 0; index < 300; index++)
        {
            var outcomeId = $"terminal-{index:D3}";
            var evidenceId = $"terminal-proof-{index:D3}";
            nodes.Add(
                outcomeId,
                new WorkNode(
                    outcomeId,
                    "Completed authoritative outcome.",
                    ParentId: null,
                    WorkNodeStatus.Satisfied,
                    ImmutableArray<string>.Empty,
                    ImmutableArray.Create(evidenceId)));
            acceptedEvidence.Add(Projection(
                evidenceId,
                "projection-" + evidenceId,
                outcomeId));
        }

        var graph = new WorkGraphSnapshot(13, nodes.ToImmutable());
        var response = PlanningDecision("""
            {
              "workUpdate": null,
              "materialClaims": [],
              "nextAction": {
                "kind": "beginCompletion",
                "plan": {
                  "answerId": "answer-large",
                  "completionKind": "succeeded",
                  "requiredOutcomeIds": ["terminal-000"],
                  "requiredClaimIds": [],
                  "bindings": [
                    {
                      "subjectId": "terminal-000",
                      "evidenceIds": ["terminal-proof-000"]
                    }
                  ],
                  "requestedFormat": "concise",
                  "requestedSections": []
                }
              }
            }
            """);
        using var inner = new SingleResponseChatClient(response);
        TemporaryCompletionRequest? captured = null;
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            completionBridge: new TemporaryCompletionBridge((request, _) =>
            {
                captured = request;
                return ValueTask.FromResult(Complete("Complete."));
            }));
        var observer = new RecordingEvidenceAuthority(
            acceptedEvidence,
            graph,
            graph.Revision);
        using var scope = client.BeginTurn(
            new CoordinatorTurnContext(
                "conversation",
                "user-message",
                "assistant-message",
                "Complete every accepted outcome.",
                publish: _ => { }),
            new AliPlanningTurnInput(
                stateRevision: 21,
                stateProjection: "{\"workGraph\":{\"total\":300,\"items\":[]}}",
                acceptedEvidence: [],
                workGraphRevision: graph.Revision,
                authoritativeWorkGraph: graph),
            observer);

        var result = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Complete.", result.Text);
        var lookup = Assert.Single(observer.Requests);
        Assert.Equal(300, lookup.Count);
        Assert.Equal(300, lookup.Distinct(StringComparer.Ordinal).Count());
        var dossier = Assert.IsType<TemporaryCompletionRequest>(captured);
        Assert.Single(dossier.RequiredOutcomes);
        Assert.Single(dossier.CitedEvidence);
        Assert.Equal("terminal-proof-000", dossier.CitedEvidence[0].EvidenceId);
    }

    private static AcceptedEvidenceProjection Projection(
        string evidenceId,
        string projection,
        string? workItemId = null) =>
        new(
            evidenceId,
            "call-" + evidenceId,
            "test-tool",
            PlanningToolInvocationStatus.Returned,
            PlanningToolDomainOutcome.Succeeded,
            projection,
            workItemId);

    private static ChatResponse Complete(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop
        };

    private static ChatResponse PlanningDecision(string decisionJson) =>
        Complete(PlanningContractTests.TransportJson(decisionJson));

    private sealed class RecordingEvidenceAuthority :
        IAliPlanningTransitionObserver,
        IAliPlanningEvidenceAuthority
    {
        private readonly IReadOnlyDictionary<string, AcceptedEvidenceProjection> _acceptedEvidence;
        private readonly WorkGraphSnapshot? _authoritativeGraph;
        private readonly long _workGraphRevision;

        internal RecordingEvidenceAuthority(
            IEnumerable<AcceptedEvidenceProjection> acceptedEvidence,
            WorkGraphSnapshot? authoritativeGraph,
            long workGraphRevision)
        {
            _acceptedEvidence = acceptedEvidence.ToDictionary(
                item => item.EvidenceId,
                item => item,
                StringComparer.Ordinal);
            _authoritativeGraph = authoritativeGraph;
            _workGraphRevision = workGraphRevision;
        }

        internal List<IReadOnlyCollection<string>> Requests { get; } = [];

        public Task<IReadOnlyDictionary<string, AcceptedEvidenceProjection>>
            ResolveEvidenceAsync(
                IReadOnlyCollection<string> evidenceIds,
                CancellationToken cancellationToken)
        {
            Requests.Add(evidenceIds.ToArray());
            IReadOnlyDictionary<string, AcceptedEvidenceProjection> result = evidenceIds
                .Where(_acceptedEvidence.ContainsKey)
                .ToDictionary(
                    id => id,
                    id => _acceptedEvidence[id],
                    StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public ValueTask<AliPlanningTransitionReceipt> OnDecisionAcceptedAsync(
            AliPlanningDecisionAcceptedEvent accepted,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningTransitionReceipt(
                accepted.ExpectedStateRevision + 1,
                WorkGraphRevision: _workGraphRevision,
                AuthoritativeWorkGraph: _authoritativeGraph));

        public ValueTask<AliPlanningEvidenceReceipt> OnToolResultObservedAsync(
            AliPlanningToolResultObservedEvent observed,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No tool result is expected in this test.");

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
                prepared.AnswerDigest,
                WorkGraphRevision: _workGraphRevision,
                AuthoritativeWorkGraph: _authoritativeGraph));
    }

    private sealed class SingleResponseChatClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

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

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
