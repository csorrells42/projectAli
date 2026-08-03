using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.ToolDiscovery;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

/// <summary>
/// Deterministic CP8 regression evidence. These tests exercise in-process fault seams and
/// simulated scale; they are not real-duration, hardware, or power-loss certification.
/// </summary>
public sealed class Checkpoint8RegressionEvidenceTests
{
    private const int AdvancingSteps = 500;
    private const int PropertyIterations = 2_000;

    [Fact]
    public void FiveHundredStepWorkGraphRun_AdvancesWithoutAStepCap()
    {
        var nodes = Enumerable.Range(0, AdvancingSteps)
            .Select(index => Node($"work-{index:D3}", WorkNodeStatus.Pending))
            .ToImmutableArray();
        var initial = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, nodes),
            Evidence());

        Assert.True(initial.Accepted);
        var snapshot = initial.Snapshot;
        for (var index = 0; index < AdvancingSteps; index++)
        {
            var before = snapshot;
            var advanced = WorkGraphApplier.Apply(
                before,
                new WorkGraphDelta(
                    before.Revision,
                    [Node($"work-{index:D3}", WorkNodeStatus.Active)]),
                Evidence());

            Assert.True(advanced.Accepted, string.Join(Environment.NewLine, advanced.Errors));
            Assert.True(advanced.Changed);
            Assert.Equal(before.Revision + 1, advanced.Snapshot.Revision);
            Assert.Equal(AdvancingSteps, advanced.Snapshot.Nodes.Count);
            Assert.Equal(
                WorkNodeStatus.Active,
                advanced.Snapshot.Nodes[$"work-{index:D3}"].Status);
            Assert.True(advanced.Diagnostics.UsedLocalizedStatusOrEvidenceValidation);
            Assert.Equal(0, advanced.Diagnostics.StructuralFullValidationPasses);
            snapshot = advanced.Snapshot;
        }

        Assert.Equal(AdvancingSteps + 1, snapshot.Revision);
        Assert.All(snapshot.Nodes.Values, node => Assert.Equal(WorkNodeStatus.Active, node.Status));
    }

    [Fact]
    public void RandomizedGraphSequence_PreservesRevisionAndEvidenceInvariants()
    {
        const int nodeCount = 64;
        var knownEvidence = Enumerable.Range(0, nodeCount)
            .SelectMany(index => new[] { $"e-{index:D2}", $"review-{index:D2}" })
            .ToHashSet(StringComparer.Ordinal);
        var initial = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                Enumerable.Range(0, nodeCount)
                    .Select(index => Node($"node-{index:D2}", WorkNodeStatus.Pending))
                    .ToImmutableArray()),
            knownEvidence);
        Assert.True(initial.Accepted);

        var random = new Random(0xC08_2026);
        var snapshot = initial.Snapshot;
        var acceptedCount = 0;
        var changedCount = 0;
        var rejectedCount = 0;
        for (var iteration = 0; iteration < PropertyIterations; iteration++)
        {
            var before = snapshot;
            var nodeIndex = random.Next(nodeCount);
            var nodeId = $"node-{nodeIndex:D2}";
            var acceptedNode = before.Nodes[nodeId];
            var scenario = random.Next(6);
            var expectedAccepted = scenario is 0 or 1;
            var expectedRevision = scenario == 2
                ? before.Revision - 1
                : before.Revision;
            var candidate = scenario switch
            {
                0 => acceptedNode.Status is WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible
                    ? acceptedNode
                    : acceptedNode with { Status = WorkNodeStatus.Active },
                1 => AdvanceTerminalEvidence(acceptedNode, nodeIndex),
                2 => acceptedNode,
                3 => acceptedNode with
                {
                    Status = acceptedNode.Status is WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible
                        ? acceptedNode.Status
                        : WorkNodeStatus.Satisfied,
                    EvidenceIds = acceptedNode.EvidenceIds.Add($"unknown-{iteration:D4}")
                },
                4 => acceptedNode with { Objective = acceptedNode.Objective + " altered" },
                _ => acceptedNode.Status is WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible
                    ? acceptedNode with { EvidenceIds = ImmutableArray<string>.Empty }
                    : acceptedNode with
                    {
                        Status = WorkNodeStatus.Satisfied,
                        EvidenceIds = [$"e-{nodeIndex:D2}", $"e-{nodeIndex:D2}"]
                    }
            };

            var result = WorkGraphApplier.Apply(
                before,
                new WorkGraphDelta(expectedRevision, [candidate]),
                knownEvidence);

            Assert.Equal(expectedAccepted, result.Accepted);
            if (result.Accepted)
            {
                acceptedCount++;
                Assert.Equal(
                    before.Revision + (result.Changed ? 1 : 0),
                    result.Snapshot.Revision);
                if (!result.Changed)
                {
                    Assert.Same(before, result.Snapshot);
                }
                else
                {
                    changedCount++;
                }
            }
            else
            {
                rejectedCount++;
                Assert.False(result.Changed);
                Assert.Same(before, result.Snapshot);
            }

            snapshot = result.Snapshot;
            Assert.Equal(nodeCount, snapshot.Nodes.Count);
            Assert.All(
                snapshot.Nodes.Values,
                node =>
                {
                    Assert.All(node.EvidenceIds, evidenceId => Assert.Contains(evidenceId, knownEvidence));
                    if (node.Status is WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible)
                    {
                        Assert.NotEmpty(node.EvidenceIds);
                    }
                });
        }

        Assert.True(acceptedCount > 0);
        Assert.True(changedCount > 0);
        Assert.True(rejectedCount > 0);
        Assert.InRange(snapshot.Revision, 2L, PropertyIterations + 1L);
    }

    [Fact]
    public async Task RecordedDecisionTrace_ReplaysToEquivalentDecisionsAndVisibleActivity()
    {
        var first = await RunPlannerTraceAsync();
        var replay = await RunPlannerTraceAsync();

        Assert.Equal(first.Decisions, replay.Decisions);
        Assert.Equal(first.VisibleActivity, replay.VisibleActivity);
        Assert.Equal("read_file", first.ReturnedToolName);
        Assert.Equal(2, first.Decisions.Length);
        Assert.Single(first.VisibleActivity);
        Assert.Contains("ToolCall", first.VisibleActivity[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RegressionBoundary.JournalCommitFault)]
    [InlineData(RegressionBoundary.PermissionDenial)]
    [InlineData(RegressionBoundary.MalformedPlannerOutput)]
    [InlineData(RegressionBoundary.StaleSourceHash)]
    [InlineData(RegressionBoundary.WriterLockContention)]
    public async Task ExistingFaultBoundaries_FailClosedAndRecoverDeterministically(
        RegressionBoundary boundary)
    {
        switch (boundary)
        {
            case RegressionBoundary.JournalCommitFault:
                await VerifyJournalCommitFaultAsync();
                break;
            case RegressionBoundary.PermissionDenial:
                await VerifyPermissionDenialAsync();
                break;
            case RegressionBoundary.MalformedPlannerOutput:
                await VerifyMalformedPlannerOutputAsync();
                break;
            case RegressionBoundary.StaleSourceHash:
                await VerifyStaleSourceHashAsync();
                break;
            case RegressionBoundary.WriterLockContention:
                await VerifyWriterLockContentionAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary), boundary, null);
        }
    }

    private static WorkNode AdvanceTerminalEvidence(WorkNode accepted, int nodeIndex)
    {
        var primary = $"e-{nodeIndex:D2}";
        var review = $"review-{nodeIndex:D2}";
        if (accepted.Status is not (WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible))
        {
            return accepted with
            {
                Status = WorkNodeStatus.Satisfied,
                EvidenceIds = [primary]
            };
        }

        return accepted.EvidenceIds.Contains(review, StringComparer.Ordinal)
            ? accepted
            : accepted with { EvidenceIds = accepted.EvidenceIds.Add(review) };
    }

    private static WorkNode Node(string id, WorkNodeStatus status) => new(
        id,
        $"Complete {id}",
        ParentId: null,
        status,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty);

    private static HashSet<string> Evidence(params string[] ids) =>
        new(ids, StringComparer.Ordinal);

    private static async Task<PlannerTrace> RunPlannerTraceAsync()
    {
        var tool = ReadFileTool();
        var inner = new ScriptedChatClient(
            Compatibility(PlanningContractTests.DecisionJson(
                "{\"kind\":\"expandTools\",\"need\":\"Read the requested file\"}")),
            Compatibility(PlanningContractTests.ToolDecisionJson(
                "read_file", "path", "README.md")));
        var semantic = new RecordingSemanticCatalog([tool]);
        var observer = new RecordingTransitionObserver();
        var visibleActivity = new List<string>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "read README",
            chunk =>
            {
                if (chunk.IsActivity)
                {
                    visibleActivity.Add(
                        $"{chunk.ActivityKind}|{chunk.Text}|{chunk.ActivityDetail}");
                }
            });
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K,
            semantic);
        using var scope = client.BeginTurn(
            turn,
            new AliPlanningTurnInput(
                0,
                "No work has been accepted yet.",
                workGraphRevision: 0,
                authoritativeWorkGraph: WorkGraphSnapshot.Empty),
            observer);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);
        var call = Assert.Single(
            response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>());
        return new PlannerTrace(
            observer.Decisions
                .Select(accepted =>
                    $"{accepted.ExpectedStateRevision}|{accepted.ToolName}|"
                    + JsonSerializer.Serialize(accepted.Decision))
                .ToArray(),
            visibleActivity.ToArray(),
            call.Name);
    }

    private static async Task VerifyJournalCommitFaultAsync()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("journal-fault");
        var injected = false;
        using (var faulted = new TurnTransitionWriter(
                   directory.Path,
                   "cp8-regression",
                   referenceValidator: null,
                   boundary =>
                   {
                       if (!injected && boundary == TurnJournalCommitBoundary.HeadCommitted)
                       {
                           injected = true;
                           throw new InjectedJournalFault();
                       }
                   }))
        {
            await Assert.ThrowsAsync<InjectedJournalFault>(() => faulted.StartAsync(
                identity,
                "request",
                Bindings("journal"),
                "start-once",
                TestContext.Current.CancellationToken));
        }

        using var recovered = new TurnTransitionWriter(directory.Path, "cp8-regression");
        var retry = await recovered.StartAsync(
            identity,
            "request",
            Bindings("journal"),
            "start-once",
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(1, retry.State!.Revision);
        Assert.Single((await recovered.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken)).Entries);
    }

    private static async Task VerifyPermissionDenialAsync()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "request",
            static _ => { },
            observationIdentity: Identity("permission"));
        turn.RegisterToolPlan(new CoordinatorToolPlan(
            "call-denied",
            "protected_tool",
            "assessment",
            "plan",
            "next",
            "selected",
            "returned",
            "{}"));
        turn.RegisterActionExecutionAuthority(new TestAuthority());
        turn.RecordPermissionDecision(AgentToolApprovalChoice.Deny);
        Assert.True(turn.TryEnterActiveToolInvocation(
            "call-denied",
            "protected_tool",
            out var invocation));
        using var invocationScope = Assert.IsAssignableFrom<IDisposable>(invocation);
        var innerCalls = 0;
        var inner = AIFunctionFactory.Create(
            () => ++innerCalls,
            "protected_tool",
            "Protected test operation.");
        var guarded = new AliToolPermissionPolicy(() => turn)
            .Apply(inner, requiresApproval: true);

        var result = await guarded.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, innerCalls);
        Assert.Contains("denied", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task VerifyMalformedPlannerOutputAsync()
    {
        const string rejectedCanary = "CP8_REJECTED_DRAFT_CANARY";
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
        var activity = new List<string>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "hello",
            chunk => activity.Add(chunk.Text));
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var scope = client.BeginTurn(
            turn,
            new AliPlanningTurnInput(
                0,
                "No work has been accepted yet.",
                workGraphRevision: 0,
                authoritativeWorkGraph: WorkGraphSnapshot.Empty),
            observer);

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

    private static async Task VerifyStaleSourceHashAsync()
    {
        using var directory = new TemporaryDirectory();
        var source = System.IO.Path.Combine(directory.Path, "source");
        var storeRoot = System.IO.Path.Combine(directory.Path, "store");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(storeRoot);
        var target = System.IO.Path.Combine(source, "Stale.cs");
        await File.WriteAllTextAsync(
            target,
            "internal sealed class Before { }",
            TestContext.Current.CancellationToken);
        var store = new AliSourceChangeSetStore(storeRoot, "cp8-regression");
        var changeSet = await store.CreateAsync(
            source,
            [AliSourceChangeRequest.ReplaceText("Stale.cs", "internal sealed class Proposed { }")],
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            target,
            "internal sealed class External { }",
            TestContext.Current.CancellationToken);
        var publisher = new AliSourceChangeSetPublisher(
            store,
            new AliSourceChangeSetValidator(store));
        var grant = AliSourcePublicationGrant.Issue(
            changeSet,
            AliSourceChangeSetStore.Hash("cp8-regression-authorization"u8));

        var receipt = await publisher.PublishAsync(
            changeSet,
            grant,
            TestContext.Current.CancellationToken);

        Assert.Equal(AliSourcePublicationState.RolledBack, receipt.State);
        Assert.Equal(
            "internal sealed class External { }",
            await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    private static async Task VerifyWriterLockContentionAsync()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("writer-lock");
        using var writer = new TurnTransitionWriter(directory.Path, "cp8-regression");
        var started = await writer.StartAsync(
            identity,
            "request",
            Bindings("lock"),
            "start",
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.Committed, started.Status);
        var leasePath = Assert.Single(
            Directory.GetFiles(directory.Path, ".writer.lock", SearchOption.AllDirectories));

        using (var heldLease = new FileStream(
                   leasePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                   TestContext.Current.CancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                writer.ReplayAsync(identity, timeout.Token));
        }

        var replay = await writer.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, replay.State!.Revision);
        Assert.Single(replay.Entries);
    }

    private static AIFunction ReadFileTool() => AIFunctionFactory.Create(
        (string path) => path,
        "read_file",
        "Read a file by exact path.");

    private static ChatResponse Compatibility(string json) =>
        new(new ChatMessage(ChatRole.Assistant, json))
        {
            FinishReason = ChatFinishReason.Stop
        };

    private static TurnIdentity Identity(string suffix) =>
        new("user", "conversation", "assistant-" + suffix);

    private static TurnRuntimeBindings Bindings(string suffix) => new(
        TurnStateIntegrity.Digest("profile-" + suffix),
        TurnStateIntegrity.Digest("runtime-" + suffix),
        TurnStateIntegrity.Digest("model-" + suffix),
        TurnStateIntegrity.Digest("settings-" + suffix),
        TurnStateIntegrity.Digest("capabilities-" + suffix),
        TurnStateIntegrity.Digest("permissions-" + suffix),
        TurnStateIntegrity.Digest("mcp-" + suffix),
        TurnStateIntegrity.Digest("attachments-" + suffix),
        TurnStateIntegrity.Digest("artifacts-" + suffix));

    public enum RegressionBoundary
    {
        JournalCommitFault,
        PermissionDenial,
        MalformedPlannerOutput,
        StaleSourceHash,
        WriterLockContention
    }

    private sealed record PlannerTrace(
        string[] Decisions,
        string[] VisibleActivity,
        string ReturnedToolName);

    private sealed record RecordedRequest(IReadOnlyList<ChatMessage> Messages);

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        internal List<RecordedRequest> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new RecordedRequest(messages.ToArray()));
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
        public Task<SemanticToolSelection> SelectAsync(
            string need,
            IReadOnlyList<AIFunctionDeclaration> liveTools,
            IReadOnlyCollection<string> retainedToolNames,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SemanticToolSelection(
                selected,
                ["cp8-regression"],
                "Selected deterministic regression tools",
                false,
                "selected"));

        public Task<SemanticToolDiscoveryResult> DiscoverAsync(
            string need,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SemanticToolDiscoveryResult(need, [], [], "not used"));
    }

    private sealed class RecordingTransitionObserver : IAliPlanningTransitionObserver
    {
        internal List<AliPlanningDecisionAcceptedEvent> Decisions { get; } = [];

        public ValueTask<AliPlanningTransitionReceipt> OnDecisionAcceptedAsync(
            AliPlanningDecisionAcceptedEvent accepted,
            CancellationToken cancellationToken)
        {
            Decisions.Add(accepted);
            return ValueTask.FromResult(new AliPlanningTransitionReceipt(
                accepted.CallId is null
                    ? accepted.ExpectedStateRevision
                    : accepted.ExpectedStateRevision + 1));
        }

        public ValueTask<AliPlanningEvidenceReceipt> OnToolResultObservedAsync(
            AliPlanningToolResultObservedEvent observed,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningEvidenceReceipt(
                observed.ExpectedStateRevision + 1,
                observed.ProposedEvidenceId));

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

    private sealed class TestAuthority : ICoordinatorActionExecutionAuthority
    {
        public TurnIdentity DurableIdentity { get; } = Identity("permission");

        public ValueTask<CapabilityInvocationAuthorization> PrepareExecutionAsync(
            CapabilityInvocationLease lease,
            string callId,
            AIFunctionArguments arguments,
            bool requiresApproval,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-Cp8-Regression",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class InjectedJournalFault : Exception;
}
