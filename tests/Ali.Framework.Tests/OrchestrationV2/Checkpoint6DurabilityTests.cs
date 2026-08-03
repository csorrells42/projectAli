using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class Checkpoint6DurabilityTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void CallTool_RequiresExactlyOneActiveAuthoritativeWorkNodeAfterDelta(
        int activeNodeCount,
        bool expectedValid)
    {
        var tool = AIFunctionFactory.Create(
            (string path) => path,
            "read_file",
            "Read one file.");
        var items = Enumerable.Range(1, Math.Max(activeNodeCount, 1))
            .Select(index => new OrchestrationWorkItemUpdate(
                $"work-{index}",
                $"Read requested input {index}",
                index <= activeNodeCount
                    ? OrchestrationWorkStatus.Active
                    : OrchestrationWorkStatus.Pending))
            .ToArray();
        var decision = new OrchestrationDecision(
            new OrchestrationWorkUpdate(0, items),
            [],
            new CallToolAction(
                tool.Name,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["path"] = JsonSerializer.SerializeToElement("README.md")
                },
                "Read the requested file",
                "The file contents become accepted evidence"));
        var context = new OrchestrationValidationContext(
            stateRevision: 7,
            selectedTools: [tool],
            workGraphRevision: 0,
            authoritativeWorkGraph: WorkGraphSnapshot.Empty);

        var result = new OrchestrationDecisionValidator().Validate(decision, context);

        Assert.Equal(expectedValid, result.IsValid);
        if (expectedValid)
        {
            Assert.Empty(result.Errors);
        }
        else
        {
            Assert.Contains(
                result.Errors,
                error => error.Contains(
                    $"exactly one Active work item after applying its work update; found {activeNodeCount}",
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AcceptedDecision_AtomicallySelectsExactCommittedWorkGraphReference()
    {
        using var directory = new TemporaryDirectory("Ali-Checkpoint6-State-");
        var identity = Identity();
        using var workGraphs = new DurableWorkGraphStore(directory.Path, "profile");
        var selectedCandidate = Assert.IsType<CommittedWorkGraphSnapshot>(
            (await workGraphs.CommitAsync(
                identity,
                expectedRevision: 0,
                Snapshot("selected-objective"),
                TestContext.Current.CancellationToken)).Current);
        var unreferencedSibling = Assert.IsType<CommittedWorkGraphSnapshot>(
            (await workGraphs.CommitAsync(
                identity,
                expectedRevision: 0,
                Snapshot("unreferenced-sibling-objective"),
                TestContext.Current.CancellationToken)).Current);
        Assert.NotEqual(selectedCandidate.Reference, unreferencedSibling.Reference);

        using var writer = new TurnTransitionWriter(
            directory.Path,
            "profile",
            new WorkGraphReferenceAdapter(workGraphs));
        var started = await writer.StartAsync(
            identity,
            "Original request",
            Bindings(),
            "turn-start",
            TestContext.Current.CancellationToken);
        var accepted = await writer.RecordPlanningDecisionAcceptedAsync(
            identity,
            expectedRevision: started.State!.Revision,
            decisionDigest: Digest("accepted-decision"),
            PlanningAcceptedActionKind.RequestUserInput,
            callId: null,
            toolName: null,
            workGraphRevision: selectedCandidate.Reference.Revision,
            materialClaimsDigest: Digest("no-material-claims"),
            correlationKey: "accepted-decision",
            workGraph: selectedCandidate.Reference,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, accepted.Status);
        Assert.Equal(2, accepted.State!.Revision);
        Assert.Equal(1, accepted.State.WorkGraphRevision);
        Assert.Equal(selectedCandidate.Reference, accepted.State.WorkGraphReference);
        Assert.NotEqual(unreferencedSibling.Reference, accepted.State.WorkGraphReference);

        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Equal(2, replay.Entries.Count);
        var decision = Assert.IsType<PlanningDecisionAcceptedTransition>(
            replay.Entries[1].Transition);
        Assert.Equal(selectedCandidate.Reference, decision.WorkGraph);
        Assert.Equal(selectedCandidate.Reference, replay.State!.WorkGraphReference);
        Assert.DoesNotContain(
            replay.Entries,
            entry => entry.Transition is WorkGraphReferencedTransition);
    }

    [Fact]
    public async Task StableEvidenceId_RetryReturnsSameRecord_AndCannotBindDifferentPayload()
    {
        using var directory = new TemporaryDirectory("Ali-Checkpoint6-Evidence-");
        var identity = Identity();
        var draft = OutcomeAndEvidenceTests.CreateDraft(
            "call-1",
            "read_file",
            OutcomeAndEvidenceTests.Json("{\"path\":\"README.md\"}"),
            OutcomeAndEvidenceTests.Json("{\"success\":true,\"text\":\"contents\"}")) with
        {
            EvidenceId = "stable-evidence-1"
        };
        var firstLedger = new EvidenceLedger(directory.Path, "profile");
        var first = await firstLedger.AppendAsync(
            identity,
            draft,
            TestContext.Current.CancellationToken);
        var reopenedLedger = new EvidenceLedger(directory.Path, "profile");

        var retry = await reopenedLedger.AppendAsync(
            identity,
            draft with { },
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Cursor, retry.Cursor);
        Assert.Equal(first.Checksum, retry.Checksum);
        Assert.Equal(first.Evidence.EvidenceId, retry.Evidence.EvidenceId);
        Assert.Equal(first.Evidence.ProjectionDigest, retry.Evidence.ProjectionDigest);
        Assert.Equal(first.Evidence.RecordMac, retry.Evidence.RecordMac);
        Assert.Equal(
            JsonSerializer.Serialize(first.Evidence),
            JsonSerializer.Serialize(retry.Evidence));
        await Assert.ThrowsAsync<InvalidDataException>(() => reopenedLedger.AppendAsync(
            identity,
            draft with
            {
                Result = OutcomeAndEvidenceTests.Json(
                    "{\"success\":true,\"text\":\"different contents\"}")
            },
            TestContext.Current.CancellationToken));
        var replay = await reopenedLedger.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Single(replay);
        Assert.Equal(first.Evidence.EvidenceId, replay[0].Evidence.EvidenceId);
    }

    [Fact]
    public async Task AcceptedPureReadDecision_PersistsExactCallAndRecoveryInterruptsWithoutReplay()
    {
        using var directory = new TemporaryDirectory("Ali-Checkpoint6-Coordinator-");
        var identity = Identity();
        var bindings = Bindings();
        var invocations = 0;
        var function = AIFunctionFactory.Create(
            (string path) =>
            {
                Interlocked.Increment(ref invocations);
                return path;
            },
            AliCapabilityCatalog.FileReadName,
            "Read a file by exact path.");
        var registry = AliProductionCapabilityCatalog.CreateRegistry([function]);
        var turnContext = new CoordinatorTurnContext(
            identity.ConversationId,
            "user-message",
            identity.AssistantMessageId,
            "Read the requested file.",
            publish: _ => { },
            observationIdentity: identity);

        using (var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile"))
        {
            await using var durableTurn = await coordinator.BeginTurnAsync(
                turnContext,
                bindings,
                acceptedPriorConversation: [],
                capabilityRegistry: registry,
                liveBindingsAccessor: null,
                TestContext.Current.CancellationToken);
            var decision = new OrchestrationDecision(
                new OrchestrationWorkUpdate(
                    0,
                    [
                        new OrchestrationWorkItemUpdate(
                            "work-1",
                            "Read the requested file",
                            OrchestrationWorkStatus.Active)
                    ]),
                [],
                new CallToolAction(
                    function.Name,
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["path"] = JsonSerializer.SerializeToElement("README.md")
                    },
                    "Read the requested file",
                    "The contents become accepted evidence"));

            var receipt = await durableTurn.OnDecisionAcceptedAsync(
                new AliPlanningDecisionAcceptedEvent(
                    identity.ConversationId,
                    identity.AssistantMessageId,
                    durableTurn.Input.StateRevision,
                    decision,
                    "call-1",
                    function.Name),
                TestContext.Current.CancellationToken);

            Assert.False(receipt.RequiresFreshPlanningPass);
            Assert.Equal(2, receipt.StateRevision);
            Assert.Equal(1, receipt.WorkGraphRevision);
        }

        using var writer = new TurnTransitionWriter(directory.Path, "profile");
        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Collection(
            replay.Entries,
            entry => Assert.IsType<TurnStartedTransition>(entry.Transition),
            entry => Assert.IsType<PlanningDecisionAcceptedTransition>(entry.Transition));
        var selectedReference = Assert.IsType<PlanningDecisionAcceptedTransition>(
            replay.Entries[1].Transition).WorkGraph;
        Assert.NotNull(selectedReference);
        Assert.Empty(replay.State!.PendingActions);
        Assert.DoesNotContain(
            replay.Entries,
            entry => entry.Transition is ActionPreparedTransition);
        var pendingCall = Assert.IsType<AcceptedCallRecoveryReference>(
            replay.State.PendingAcceptedCall);
        Assert.Equal("call-1", pendingCall.CallId);
        Assert.Equal(function.Name, pendingCall.ToolName);
        Assert.Equal(AcceptedCallExecutionClass.NonMutating, pendingCall.ExecutionClass);

        var protectedCall = await writer.ReadPendingAcceptedCallAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal("call-1", protectedCall.CallId);
        Assert.Equal(function.Name, protectedCall.ToolName);
        Assert.Equal(
            "README.md",
            protectedCall.CanonicalArguments.GetProperty("path").GetString());

        var recovery = await new TurnRecoveryService(writer, []).RecoverAsync(
            identity,
            bindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, recovery.Status);
        Assert.Empty(recovery.Actions);
        Assert.Equal(TurnControlState.Running, recovery.State!.Control);
        Assert.Empty(recovery.State.PendingActions);
        Assert.Null(recovery.State.PendingAcceptedCall);
        Assert.Equal(selectedReference, recovery.State.WorkGraphReference);
        Assert.Equal("Read the requested file.", recovery.OriginalRequest);
        Assert.Equal(0, Volatile.Read(ref invocations));

        var recoveredReplay = await writer.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.IsType<AcceptedCallInterruptedTransition>(
            recoveredReplay.Entries[^1].Transition);
    }

    [Fact]
    public async Task AuthoritativeStateProjection_IsValidBoundedJsonAndProjectsActiveWorkFirst()
    {
        using var directory = new TemporaryDirectory("Ali-Checkpoint6-Projection-");
        var identity = Identity();
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using var turn = await coordinator.BeginTurnAsync(
            new CoordinatorTurnContext(
                identity.ConversationId,
                "user-message",
                identity.AssistantMessageId,
                "Plan a deliberately large durable work graph.",
                publish: _ => { },
                observationIdentity: identity),
            Bindings(),
            acceptedPriorConversation: [],
            capabilityRegistry: null,
            liveBindingsAccessor: null,
            TestContext.Current.CancellationToken);
        var longObjective = new string('x', 2_000);
        var updates = Enumerable.Range(0, 95)
            .Select(index => new OrchestrationWorkItemUpdate(
                $"pending-{index:D3}",
                longObjective,
                OrchestrationWorkStatus.Pending))
            .Append(new OrchestrationWorkItemUpdate(
                "zz-active",
                longObjective,
                OrchestrationWorkStatus.Active))
            .ToArray();

        var receipt = await turn.OnDecisionAcceptedAsync(
            new AliPlanningDecisionAcceptedEvent(
                identity.ConversationId,
                identity.AssistantMessageId,
                turn.Input.StateRevision,
                new OrchestrationDecision(
                    new OrchestrationWorkUpdate(0, updates),
                    materialClaims: [],
                    new RequestUserInputAction(
                        "Which option should Ali use?",
                        "The option is not available.")),
                CallId: null,
                ToolName: null),
            TestContext.Current.CancellationToken);

        var projection = Assert.IsType<string>(receipt.AuthoritativeStateProjection);
        Assert.InRange(projection.Length, 1, 32_000);
        using var parsed = JsonDocument.Parse(projection);
        var graph = parsed.RootElement.GetProperty("workGraph");
        var projected = graph.GetProperty("items");
        Assert.Equal("zz-active", projected[0].GetProperty("id").GetString());
        Assert.Equal("Active", projected[0].GetProperty("status").GetString());
        Assert.True(graph.GetProperty("omitted").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(
            graph.GetProperty("nextOmittedWorkItemId").GetString()));
        Assert.Equal(
            graph.GetProperty("projectedCount").GetInt32(),
            projected.GetArrayLength());
    }

    [Fact]
    public async Task FiveThousandNodeOneNodeReceiptAndPass_UseOnlyCachedBoundedConsumers()
    {
        using var directory = new TemporaryDirectory("Ali-Checkpoint6-GraphConsumers-");
        var identity = Identity();
        var bindings = Bindings();
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using var turn = await coordinator.BeginTurnAsync(
            new CoordinatorTurnContext(
                identity.ConversationId,
                "user-message",
                identity.AssistantMessageId,
                "Exercise the authoritative work graph at scale.",
                publish: _ => { },
                observationIdentity: identity),
            bindings,
            acceptedPriorConversation: [],
            capabilityRegistry: null,
            liveBindingsAccessor: null,
            TestContext.Current.CancellationToken);
        var initialItems = Enumerable.Range(0, 5_000)
            .Select(index => new OrchestrationWorkItemUpdate(
                $"node-{index:D5}",
                $"Objective {index}",
                OrchestrationWorkStatus.Pending))
            .ToArray();
        var initialReceipt = await turn.OnDecisionAcceptedAsync(
            new AliPlanningDecisionAcceptedEvent(
                identity.ConversationId,
                identity.AssistantMessageId,
                turn.Input.StateRevision,
                new OrchestrationDecision(
                    new OrchestrationWorkUpdate(0, initialItems),
                    materialClaims: [],
                    new ExpandToolsAction("Establish the large authoritative work graph.")),
                CallId: null,
                ToolName: null),
            TestContext.Current.CancellationToken);
        var initialGraphRevision = Assert.IsType<long>(initialReceipt.WorkGraphRevision);
        var before = turn.CaptureWorkGraphConsumerDiagnostics();

        var oneNodeReceipt = await turn.OnDecisionAcceptedAsync(
            new AliPlanningDecisionAcceptedEvent(
                identity.ConversationId,
                identity.AssistantMessageId,
                initialReceipt.StateRevision,
                new OrchestrationDecision(
                    new OrchestrationWorkUpdate(
                        initialGraphRevision,
                        [
                            new OrchestrationWorkItemUpdate(
                                "node-02500",
                                "Objective 2500",
                                OrchestrationWorkStatus.Active)
                        ]),
                    materialClaims: [],
                    new CallToolAction(
                        "diagnostic-tool",
                        new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                        "Exercise one exact active work item.",
                        "The active item receives an execution receipt.")),
                CallId: "call-graph-consumer",
                ToolName: "diagnostic-tool"),
            TestContext.Current.CancellationToken);
        var pass = await turn.OnPlanningPassStartingAsync(
            new AliPlanningPassStartingEvent(
                identity.ConversationId,
                identity.AssistantMessageId,
                oneNodeReceipt.StateRevision,
                bindings),
            TestContext.Current.CancellationToken);
        var after = turn.CaptureWorkGraphConsumerDiagnostics();

        Assert.True(pass.CanPlan);
        Assert.True(after.CurrentAnalysisCacheHit);
        Assert.Equal(0, after.CurrentAnalysis.FullValidationPasses);
        Assert.Equal(0, after.CurrentAnalysis.FullValidationNodesVisited);
        Assert.Equal(0, after.CurrentAnalysis.FullDigestConstructionPasses);
        Assert.Equal(0, after.CurrentAnalysis.FullDigestNodesVisited);
        Assert.Equal(2, after.CurrentAnalysis.IncrementalDigestLeafUpdates);
        Assert.InRange(after.CurrentAnalysis.IncrementalDigestTreeNodesVisited, 1, 511);
        Assert.InRange(after.CurrentAnalysis.IncrementalDigestTreeNodesRehashed, 1, 511);

        var apply = Assert.IsType<WorkGraphApplyDiagnostics>(after.LastApply);
        Assert.True(apply.CurrentAnalysisCacheHit);
        Assert.Equal(1, apply.CandidateUpsertsVisited);
        Assert.Equal(1, apply.ChangedNodes);
        Assert.True(apply.UsedLocalizedStatusOrEvidenceValidation);
        Assert.Equal(0, apply.StructuralFullValidationPasses);
        Assert.Equal(0, apply.StructuralSnapshotNodesVisited);
        Assert.Equal(0, apply.CycleValidationPasses);

        Assert.Equal(2L, after.ProjectionCalls - before.ProjectionCalls);
        Assert.InRange(
            after.ProjectionIndexIdsVisited - before.ProjectionIndexIdsVisited,
            2L,
            130L);
        Assert.Equal(1L, after.ProgressVectorCalls - before.ProgressVectorCalls);
        Assert.Equal(1L, after.DependencyDigestCalls - before.DependencyDigestCalls);
        Assert.Equal(1L, after.ActiveSelectionCalls - before.ActiveSelectionCalls);

        Assert.Equal(1L, after.Store.ValidatedDeltaCommits - before.Store.ValidatedDeltaCommits);
        Assert.Equal(
            1L,
            after.Store.ValidatedDeltaNodesSerialized
            - before.Store.ValidatedDeltaNodesSerialized);
        Assert.Equal(
            0L,
            after.Store.ValidatedCheckpointNodesSerialized
            - before.Store.ValidatedCheckpointNodesSerialized);
        Assert.Equal(
            0L,
            after.Store.ParentSnapshotReconstructions
            - before.Store.ParentSnapshotReconstructions);
        Assert.Equal(
            0L,
            after.Store.NewCandidateReconstructions
            - before.Store.NewCandidateReconstructions);
        Assert.Equal(
            0L,
            after.Store.FullSnapshotDiffPasses - before.Store.FullSnapshotDiffPasses);
        Assert.Equal(
            0L,
            after.Store.FullSnapshotCloneValidationPasses
            - before.Store.FullSnapshotCloneValidationPasses);

        using var projection = JsonDocument.Parse(pass.AuthoritativeStateProjection!);
        var projectedGraph = projection.RootElement.GetProperty("workGraph");
        Assert.Equal(5_000, projectedGraph.GetProperty("total").GetInt32());
        Assert.Equal(
            "node-02500",
            projectedGraph.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task SixtyFourAcceptedGraphRevisions_PruneAfterJournalSelection_AndRecoverExactCheckpoint()
    {
        using var directory = new TemporaryDirectory("Ali-Checkpoint6-GraphPrune-");
        var identity = Identity();
        var bindings = Bindings();
        CommittedWorkGraphReference selectedReference;

        using (var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile"))
        {
            await using var turn = await coordinator.BeginTurnAsync(
                new CoordinatorTurnContext(
                    identity.ConversationId,
                    "user-message",
                    identity.AssistantMessageId,
                    "Advance one durable work item through checkpoint revision 64.",
                    publish: _ => { },
                    observationIdentity: identity),
                bindings,
                acceptedPriorConversation: [],
                capabilityRegistry: null,
                liveBindingsAccessor: null,
                TestContext.Current.CancellationToken);
            var stateRevision = turn.Input.StateRevision;
            long graphRevision = 0;

            for (var revision = 1;
                 revision <= DurableWorkGraphStore.MaximumDeltaChainLength;
                 revision++)
            {
                var status = revision % 2 == 0
                    ? OrchestrationWorkStatus.Active
                    : OrchestrationWorkStatus.Pending;
                var receipt = await turn.OnDecisionAcceptedAsync(
                    new AliPlanningDecisionAcceptedEvent(
                        identity.ConversationId,
                        identity.AssistantMessageId,
                        stateRevision,
                        new OrchestrationDecision(
                            new OrchestrationWorkUpdate(
                                graphRevision,
                                [
                                    new OrchestrationWorkItemUpdate(
                                        "work-1",
                                        "Advance the durable checkpoint boundary.",
                                        status)
                                ]),
                            materialClaims: [],
                            new ExpandToolsAction(
                                "Continue the bounded checkpoint integration exercise.")),
                        CallId: null,
                        ToolName: null),
                    TestContext.Current.CancellationToken);
                stateRevision = receipt.StateRevision;
                graphRevision = Assert.IsType<long>(receipt.WorkGraphRevision);

                if (revision == DurableWorkGraphStore.MaximumDeltaChainLength - 1)
                {
                    Assert.Equal(
                        DurableWorkGraphStore.MaximumDeltaChainLength - 1,
                        Directory.GetFiles(
                            directory.Path,
                            "revision-*.protected",
                            SearchOption.AllDirectories).Length);
                }
            }

            Assert.Equal(
                (long)DurableWorkGraphStore.MaximumDeltaChainLength,
                graphRevision);
            Assert.Single(Directory.GetFiles(
                directory.Path,
                "revision-*.protected",
                SearchOption.AllDirectories));

            using var reader = new TurnTransitionWriter(directory.Path, "profile");
            var replay = await reader.ReplayAsync(
                identity,
                TestContext.Current.CancellationToken);
            selectedReference = Assert.IsType<CommittedWorkGraphReference>(
                replay.State!.WorkGraphReference);
            Assert.Equal(
                (long)DurableWorkGraphStore.MaximumDeltaChainLength,
                selectedReference.Revision);
            Assert.Equal(1 + DurableWorkGraphStore.MaximumDeltaChainLength, replay.Entries.Count);
        }

        using var reopened = new AliPlanningStateCoordinator(directory.Path, "profile");
        var visibleIdentity = new TurnIdentity(
            identity.UserId,
            identity.ConversationId,
            "visible-after-revision-64-restart");
        var recovered = await reopened.ResumeTurnAsync(
            new CoordinatorTurnContext(
                visibleIdentity.ConversationId,
                "visible-user-message",
                visibleIdentity.AssistantMessageId,
                "Recover the exact checkpoint selected at revision 64.",
                publish: _ => { },
                observationIdentity: visibleIdentity),
            identity,
            bindings,
            "Continue from the exact revision-64 checkpoint.",
            "resume-exact-revision-64-checkpoint",
            capabilityRegistry: null,
            liveBindingsAccessor: null,
            TestContext.Current.CancellationToken);

        Assert.True(recovered.IsReady, recovered.FailureCode);
        await using var recoveredTurn = Assert.IsType<AliDurablePlanningTurn>(recovered.Turn);
        Assert.Equal(
            (long)DurableWorkGraphStore.MaximumDeltaChainLength,
            recoveredTurn.Input.WorkGraphRevision);
        var recoveredGraph = Assert.IsType<WorkGraphSnapshot>(
            recoveredTurn.Input.AuthoritativeWorkGraph);
        Assert.Equal(
            (long)DurableWorkGraphStore.MaximumDeltaChainLength,
            recoveredGraph.Revision);
        Assert.Equal(WorkNodeStatus.Active, recoveredGraph.Nodes["work-1"].Status);
        using var recoveredReader = new TurnTransitionWriter(directory.Path, "profile");
        var recoveredState = await recoveredReader.ReadAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal(selectedReference, recoveredState!.WorkGraphReference);
        Assert.Single(Directory.GetFiles(
            directory.Path,
            "revision-*.protected",
            SearchOption.AllDirectories));
    }

    private static WorkGraphSnapshot Snapshot(string objective) =>
        new(
            1,
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[]
                {
                    new KeyValuePair<string, WorkNode>(
                        "work-1",
                        new WorkNode(
                            "work-1",
                            objective,
                            ParentId: null,
                            WorkNodeStatus.Active,
                            ImmutableArray<string>.Empty,
                            ImmutableArray<string>.Empty))
                }));

    private static TurnIdentity Identity() =>
        new("user", "conversation", "assistant-message");

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

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class WorkGraphReferenceAdapter(
        DurableWorkGraphStore store) : ITurnCommittedReferenceValidator
    {
        public ValueTask<bool> IsEvidenceCommittedAsync(
            TurnIdentity identity,
            CommittedEvidenceReference evidence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> IsWorkGraphCommittedAsync(
            TurnIdentity identity,
            CommittedWorkGraphReference workGraph,
            CancellationToken cancellationToken) =>
            store.IsCommittedAsync(identity, workGraph, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
