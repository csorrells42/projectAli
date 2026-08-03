using System.Collections.Immutable;
using Ali.Modules.Orchestration.Work;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class WorkGraphAndActivityTests
{
    [Fact]
    public void Apply_AcceptsDecompositionAndEvidenceBackedTerminalTransitions()
    {
        var initial = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("root", "Complete the requested work"),
                    Node("child", "Build one required artifact", parentId: "root")
                ]),
            Evidence());

        Assert.True(initial.Accepted);
        Assert.True(initial.Changed);
        Assert.Equal(1, initial.Snapshot.Revision);

        var completed = WorkGraphApplier.Apply(
            initial.Snapshot,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "child",
                        "Build one required artifact",
                        WorkNodeStatus.Satisfied,
                        "root",
                        evidenceIds: ["e-child"]),
                    Node(
                        "root",
                        "Complete the requested work",
                        WorkNodeStatus.Satisfied,
                        dependsOn: ["child"],
                        evidenceIds: ["e-root"])
                ]),
            Evidence("e-child", "e-root"));

        Assert.True(completed.Accepted);
        Assert.True(completed.Changed);
        Assert.Equal(2, completed.Snapshot.Revision);
        Assert.Equal(WorkNodeStatus.Satisfied, completed.Snapshot.Nodes["root"].Status);
        Assert.Equal(WorkNodeStatus.Satisfied, completed.Snapshot.Nodes["child"].Status);

        var appendedEvidence = WorkGraphApplier.Apply(
            completed.Snapshot,
            new WorkGraphDelta(
                2,
                [
                    Node(
                        "root",
                        "Complete the requested work",
                        WorkNodeStatus.Satisfied,
                        dependsOn: ["child"],
                        evidenceIds: ["e-root", "e-review"])
                ]),
            Evidence("e-child", "e-root", "e-review"));

        Assert.True(appendedEvidence.Accepted);
        Assert.Equal(["e-root", "e-review"], appendedEvidence.Snapshot.Nodes["root"].EvidenceIds);
    }

    [Fact]
    public void Apply_RejectsStaleRevisionWithoutReplacingTheSnapshot()
    {
        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("root", "Objective")]),
            Evidence()).Snapshot;

        var result = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(0, [Node("root", "Objective")]),
            Evidence());

        Assert.False(result.Accepted);
        Assert.False(result.Changed);
        Assert.Same(current, result.Snapshot);
        Assert.Contains(result.Errors, error => error.Contains("expected revision", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_RejectsDuplicateAndMissingReferencesAndBothCycleKinds()
    {
        var duplicate = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [Node("same", "One"), Node("same", "One")]),
            Evidence());
        Assert.False(duplicate.Accepted);
        Assert.Contains(duplicate.Errors, error => error.Contains("duplicate upserts", StringComparison.Ordinal));

        var missing = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("one", "One", dependsOn: ["missing"])]),
            Evidence());
        Assert.False(missing.Accepted);
        Assert.Contains(missing.Errors, error => error.Contains("missing dependency", StringComparison.Ordinal));

        var missingParent = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("one", "One", parentId: "missing")]),
            Evidence());
        Assert.False(missingParent.Accepted);
        Assert.Contains(missingParent.Errors, error => error.Contains("missing parent", StringComparison.Ordinal));

        var duplicateDependency = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("dependency", "Dependency"),
                    Node("one", "One", dependsOn: ["dependency", "dependency"])
                ]),
            Evidence());
        Assert.False(duplicateDependency.Accepted);
        Assert.Contains(
            duplicateDependency.Errors,
            error => error.Contains("duplicate dependency", StringComparison.Ordinal));

        var self = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("one", "One", dependsOn: ["one"])]),
            Evidence());
        Assert.False(self.Accepted);
        Assert.Contains(self.Errors, error => error.Contains("depend on itself", StringComparison.Ordinal));

        var parentCycle = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("a", "A", parentId: "b"),
                    Node("b", "B", parentId: "a")
                ]),
            Evidence());
        Assert.False(parentCycle.Accepted);
        Assert.Contains(parentCycle.Errors, error => error.Contains("parent cycle", StringComparison.Ordinal));

        var dependencyCycle = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("a", "A", dependsOn: ["b"]),
                    Node("b", "B", dependsOn: ["a"])
                ]),
            Evidence());
        Assert.False(dependencyCycle.Accepted);
        Assert.Contains(dependencyCycle.Errors, error => error.Contains("dependency cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_KeepsAcceptedObjectiveAndParentImmutable()
    {
        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("root", "Root"),
                    Node("child", "Original objective", parentId: "root")
                ]),
            Evidence()).Snapshot;

        var changedObjective = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(1, [Node("child", "Replacement objective", parentId: "root")]),
            Evidence());
        Assert.False(changedObjective.Accepted);
        Assert.Contains(changedObjective.Errors, error => error.Contains("change its objective", StringComparison.Ordinal));

        var changedParent = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(1, [Node("child", "Original objective")]),
            Evidence());
        Assert.False(changedParent.Accepted);
        Assert.Contains(changedParent.Errors, error => error.Contains("change its parent", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_PreventsTerminalRegressionEvidenceErasureAndDependencyMutation()
    {
        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node(
                        "dependency",
                        "Dependency",
                        WorkNodeStatus.Satisfied,
                        evidenceIds: ["e-dependency"]),
                    Node(
                        "terminal",
                        "Terminal",
                        WorkNodeStatus.Satisfied,
                        dependsOn: ["dependency"],
                        evidenceIds: ["e-one", "e-two"])
                ]),
            Evidence("e-dependency", "e-one", "e-two")).Snapshot;

        var regressed = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "terminal",
                        "Terminal",
                        WorkNodeStatus.Pending,
                        dependsOn: ["dependency"],
                        evidenceIds: ["e-one", "e-two"])
                ]),
            Evidence("e-dependency", "e-one", "e-two"));
        Assert.False(regressed.Accepted);
        Assert.Contains(regressed.Errors, error => error.Contains("cannot transition", StringComparison.Ordinal));

        var erased = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "terminal",
                        "Terminal",
                        WorkNodeStatus.Satisfied,
                        dependsOn: ["dependency"],
                        evidenceIds: ["e-one"])
                ]),
            Evidence("e-dependency", "e-one", "e-two"));
        Assert.False(erased.Accepted);
        Assert.Contains(erased.Errors, error => error.Contains("erase or reorder", StringComparison.Ordinal));

        var reordered = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "terminal",
                        "Terminal",
                        WorkNodeStatus.Satisfied,
                        dependsOn: ["dependency"],
                        evidenceIds: ["e-two", "e-one"])
                ]),
            Evidence("e-dependency", "e-one", "e-two"));
        Assert.False(reordered.Accepted);

        var changedDependencies = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "terminal",
                        "Terminal",
                        WorkNodeStatus.Satisfied,
                        evidenceIds: ["e-one", "e-two"])
                ]),
            Evidence("e-dependency", "e-one", "e-two"));
        Assert.False(changedDependencies.Accepted);
        Assert.Contains(
            changedDependencies.Errors,
            error => error.Contains("cannot change its dependencies", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_SupersedesOnlyAcceptedLiveWorkWithEvidenceAndImmutableProvenance()
    {
        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("mistaken", "Mistaken decomposition", WorkNodeStatus.Active),
                    Node("replacement", "Corrected decomposition"),
                    Node("other", "Other decomposition")
                ]),
            Evidence()).Snapshot;

        var superseded = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "mistaken",
                        "Mistaken decomposition",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-correction"],
                        supersededById: "replacement")
                ]),
            Evidence("e-correction"));

        Assert.True(superseded.Accepted);
        Assert.Equal(WorkNodeStatus.Superseded, superseded.Snapshot.Nodes["mistaken"].Status);
        Assert.Equal("replacement", superseded.Snapshot.Nodes["mistaken"].SupersededById);
        Assert.Equal(["e-correction"], superseded.Snapshot.Nodes["mistaken"].EvidenceIds);

        var changedProvenance = WorkGraphApplier.Apply(
            superseded.Snapshot,
            new WorkGraphDelta(
                2,
                [
                    Node(
                        "mistaken",
                        "Mistaken decomposition",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-correction"],
                        supersededById: "other")
                ]),
            Evidence("e-correction"));

        Assert.False(changedProvenance.Accepted);
        Assert.Contains(
            changedProvenance.Errors,
            error => error.Contains("replacement provenance", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_RejectsUnsupportedOrCyclicSupersession()
    {
        var introducedAsSuperseded = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node(
                        "retired",
                        "Retired",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-retired"],
                        supersededById: "replacement"),
                    Node("replacement", "Replacement")
                ]),
            Evidence("e-retired"));
        Assert.False(introducedAsSuperseded.Accepted);
        Assert.Contains(
            introducedAsSuperseded.Errors,
            error => error.Contains("accepted as Pending or Active", StringComparison.Ordinal));

        var completed = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node(
                        "completed",
                        "Completed",
                        WorkNodeStatus.Satisfied,
                        evidenceIds: ["e-completed"]),
                    Node("replacement", "Replacement")
                ]),
            Evidence("e-completed")).Snapshot;
        var completedToSuperseded = WorkGraphApplier.Apply(
            completed,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "completed",
                        "Completed",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-completed", "e-correction"],
                        supersededById: "replacement")
                ]),
            Evidence("e-completed", "e-correction"));
        Assert.True(completedToSuperseded.Accepted);
        Assert.Equal(
            WorkNodeStatus.Superseded,
            completedToSuperseded.Snapshot.Nodes["completed"].Status);
        Assert.Equal(
            ["e-completed", "e-correction"],
            completedToSuperseded.Snapshot.Nodes["completed"].EvidenceIds);

        var terminalWithoutCorrectionEvidence = WorkGraphApplier.Apply(
            completed,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "completed",
                        "Completed",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-completed"],
                        supersededById: "replacement")
                ]),
            Evidence("e-completed"));
        Assert.False(terminalWithoutCorrectionEvidence.Accepted);
        Assert.Contains(
            terminalWithoutCorrectionEvidence.Errors,
            error => error.Contains("appended correction evidence", StringComparison.Ordinal));

        var impossible = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node(
                        "impossible",
                        "Initially impossible",
                        WorkNodeStatus.Impossible,
                        evidenceIds: ["e-impossible"]),
                    Node("replacement", "Replacement")
                ]),
            Evidence("e-impossible")).Snapshot;
        var impossibleToSuperseded = WorkGraphApplier.Apply(
            impossible,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "impossible",
                        "Initially impossible",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-impossible", "e-correction"],
                        supersededById: "replacement")
                ]),
            Evidence("e-impossible", "e-correction"));
        Assert.True(impossibleToSuperseded.Accepted);
        Assert.Equal(
            WorkNodeStatus.Superseded,
            impossibleToSuperseded.Snapshot.Nodes["impossible"].Status);

        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("a", "A"), Node("b", "B")]),
            Evidence()).Snapshot;

        var missingEvidence = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [Node("a", "A", WorkNodeStatus.Superseded, supersededById: "b")]),
            Evidence());
        Assert.False(missingEvidence.Accepted);
        Assert.Contains(
            missingEvidence.Errors,
            error => error.Contains("must cite at least one known evidence", StringComparison.Ordinal));

        var missingReplacement = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "a",
                        "A",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-a"],
                        supersededById: "missing")
                ]),
            Evidence("e-a"));
        Assert.False(missingReplacement.Accepted);
        Assert.Contains(
            missingReplacement.Errors,
            error => error.Contains("missing replacement", StringComparison.Ordinal));

        var selfReplacement = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "a",
                        "A",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-a"],
                        supersededById: "a")
                ]),
            Evidence("e-a"));
        Assert.False(selfReplacement.Accepted);
        Assert.Contains(selfReplacement.Errors, error => error.Contains("supersede itself", StringComparison.Ordinal));

        var cycle = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node(
                        "a",
                        "A",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-a"],
                        supersededById: "b"),
                    Node(
                        "b",
                        "B",
                        WorkNodeStatus.Superseded,
                        evidenceIds: ["e-b"],
                        supersededById: "a")
                ]),
            Evidence("e-a", "e-b"));
        Assert.False(cycle.Accepted);
        Assert.Contains(cycle.Errors, error => error.Contains("supersession cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_RequiresKnownDistinctEvidenceAndSatisfiedDependencies()
    {
        var unknownEvidence = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [Node("terminal", "Terminal", WorkNodeStatus.Satisfied, evidenceIds: ["unknown"])]),
            Evidence());
        Assert.False(unknownEvidence.Accepted);
        Assert.Contains(unknownEvidence.Errors, error => error.Contains("unknown evidence", StringComparison.Ordinal));

        var duplicateEvidence = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [Node(
                    "terminal",
                    "Terminal",
                    WorkNodeStatus.Satisfied,
                    evidenceIds: ["e-one", "e-one"])]),
            Evidence("e-one"));
        Assert.False(duplicateEvidence.Accepted);
        Assert.Contains(duplicateEvidence.Errors, error => error.Contains("duplicate evidence", StringComparison.Ordinal));

        var unmetDependency = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("dependency", "Dependency"),
                    Node("active", "Active", WorkNodeStatus.Active, dependsOn: ["dependency"])
                ]),
            Evidence());
        Assert.False(unmetDependency.Accepted);
        Assert.Contains(unmetDependency.Errors, error => error.Contains("while dependency", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_AllowsAWorkingBranchToReturnFromActiveToPending()
    {
        var active = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("branch", "Branch", WorkNodeStatus.Active)]),
            Evidence()).Snapshot;

        var deactivated = WorkGraphApplier.Apply(
            active,
            new WorkGraphDelta(1, [Node("branch", "Branch")]),
            Evidence());

        Assert.True(deactivated.Accepted);
        Assert.Equal(WorkNodeStatus.Pending, deactivated.Snapshot.Nodes["branch"].Status);
    }

    [Fact]
    public void Apply_TreatsCanonicalDependencyOrderAsANoOpAndKeepsRevisionStable()
    {
        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node("a", "A"),
                    Node("b", "B"),
                    Node("root", "Root", dependsOn: ["b", "a"])
                ]),
            Evidence()).Snapshot;
        Assert.Equal(["a", "b"], current.Nodes["root"].DependsOn);

        var noOp = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(1, [Node("root", "Root", dependsOn: ["a", "b"])]),
            Evidence());

        Assert.True(noOp.Accepted);
        Assert.False(noOp.Changed);
        Assert.Same(current, noOp.Snapshot);
        Assert.Equal(1, noOp.Snapshot.Revision);
    }

    [Fact]
    public void Apply_AcceptsFiveHundredUniqueNodesWithoutAPlannerStepCap()
    {
        var nodes = Enumerable.Range(0, 500)
            .Select(index => Node(
                $"node-{index:D3}",
                $"Objective {index}",
                parentId: index == 0 ? null : $"node-{index - 1:D3}"))
            .ToImmutableArray();

        var result = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, nodes),
            Evidence());

        Assert.True(result.Accepted);
        Assert.Equal(500, result.Snapshot.Nodes.Count);
    }

    [Fact]
    public void PersistentMerkleMap_IsCanonicalAcrossInsertionHistoryAndPathLocalOnUpdate()
    {
        var values = Enumerable.Range(0, 512)
            .Select(index => new KeyValuePair<string, string>(
                $"key-{index:D4}",
                $"value-{index}"))
            .ToArray();
        var forward = PersistentMerkleStringMap.Create(values);
        var reverse = PersistentMerkleStringMap.Create(values.Reverse());

        Assert.Equal(forward.RootDigest, reverse.RootDigest);
        Assert.Equal(
            forward.DomainDigest("test-domain"),
            reverse.DomainDigest("test-domain"));

        var changed = forward.Set("key-0256", "replacement", out var update);
        Assert.True(update.Changed);
        Assert.InRange(update.NodesVisited, 1, 127);
        Assert.InRange(update.NodesRehashed, 1, 127);
        Assert.NotEqual(forward.RootDigest, changed.RootDigest);

        var restored = changed.Set("key-0256", "value-256", out var restoreUpdate);
        Assert.True(restoreUpdate.Changed);
        Assert.Equal(forward.RootDigest, restored.RootDigest);

        var removed = forward.Remove("key-0100", out var removal);
        Assert.True(removal.Changed);
        var reinserted = removed.Set("key-0100", "value-100", out var reinsertion);
        Assert.True(reinsertion.Changed);
        Assert.Equal(forward.RootDigest, reinserted.RootDigest);
    }

    [Fact]
    public void FiveThousandNodeStatusAndEvidenceUpdates_UseOnlyLocalizedValidation()
    {
        var nodes = Enumerable.Range(0, 5_000)
            .Select(index => Node($"node-{index:D5}", $"Objective {index}"))
            .ToImmutableArray();
        var initial = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, nodes),
            Evidence());
        Assert.True(initial.Accepted);
        var untouched = initial.Snapshot.Nodes["node-00000"];

        var activated = WorkGraphApplier.Apply(
            initial.Snapshot,
            new WorkGraphDelta(
                1,
                [Node("node-02500", "Objective 2500", WorkNodeStatus.Active)]),
            Evidence());

        Assert.True(activated.Accepted);
        Assert.True(activated.Diagnostics.CurrentAnalysisCacheHit);
        Assert.Equal(0, activated.Diagnostics.CurrentSnapshotFullValidationPasses);
        Assert.Equal(0, activated.Diagnostics.CurrentSnapshotNodesVisited);
        Assert.Equal(1, activated.Diagnostics.CandidateUpsertsVisited);
        Assert.Equal(1, activated.Diagnostics.ChangedNodes);
        Assert.True(activated.Diagnostics.UsedLocalizedStatusOrEvidenceValidation);
        Assert.Equal(0, activated.Diagnostics.StructuralFullValidationPasses);
        Assert.Equal(0, activated.Diagnostics.StructuralSnapshotNodesVisited);
        Assert.Equal(0, activated.Diagnostics.CycleValidationPasses);
        Assert.Same(untouched, activated.Snapshot.Nodes["node-00000"]);

        var activatedAnalysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            activated.Snapshot,
            out var activatedCacheHit);
        Assert.True(activatedCacheHit);
        Assert.Equal(0, activatedAnalysis.Diagnostics.FullDigestConstructionPasses);
        Assert.Equal(0, activatedAnalysis.Diagnostics.FullDigestNodesVisited);
        Assert.Equal(2, activatedAnalysis.Diagnostics.IncrementalDigestLeafUpdates);
        Assert.InRange(
            activatedAnalysis.Diagnostics.IncrementalDigestTreeNodesVisited,
            1,
            511);
        Assert.InRange(
            activatedAnalysis.Diagnostics.IncrementalDigestTreeNodesRehashed,
            1,
            511);

        // Exercise the Phase-B progress/dependency/fingerprint hooks. Reading these values must
        // only consume roots already maintained by the one-node mutation above.
        var activatedProgress = ProgressVector.CreateFromWorkGraphAnalysis(
            evidenceCursor: 41,
            workGraph: activatedAnalysis);
        Assert.Equal(
            activatedAnalysis.OutcomeCoverageDigest,
            activatedProgress.OutcomeCoverageDigest);
        Assert.Equal(
            activatedAnalysis.ProgressDependencyStateDigest,
            activatedProgress.DependencyStateDigest);
        Assert.Equal(64, activatedAnalysis.ActionDependencyStateDigest.Length);
        Assert.Equal(64, activatedAnalysis.PlanningIdentityDigest.Length);

        // A separately built analysis must commit to the exact same logical graph. This proves
        // the persistent Merkle shape is canonical rather than mutation-history dependent.
        var rebuiltActivated = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            new WorkGraphSnapshot(activated.Snapshot.Revision, activated.Snapshot.Nodes),
            out var rebuiltActivatedCacheHit);
        Assert.False(rebuiltActivatedCacheHit);
        Assert.Equal(
            rebuiltActivated.OutcomeCoverageDigest,
            activatedAnalysis.OutcomeCoverageDigest);
        Assert.Equal(
            rebuiltActivated.ProgressDependencyStateDigest,
            activatedAnalysis.ProgressDependencyStateDigest);
        Assert.Equal(
            rebuiltActivated.ActionDependencyStateDigest,
            activatedAnalysis.ActionDependencyStateDigest);
        Assert.Equal(
            rebuiltActivated.PlanningIdentityDigest,
            activatedAnalysis.PlanningIdentityDigest);

        var evidenceAppended = WorkGraphApplier.Apply(
            activated.Snapshot,
            new WorkGraphDelta(
                2,
                [Node(
                    "node-02500",
                    "Objective 2500",
                    WorkNodeStatus.Active,
                    evidenceIds: ["e-2500"])]),
            Evidence("e-2500"));

        Assert.True(evidenceAppended.Accepted);
        Assert.True(evidenceAppended.Diagnostics.CurrentAnalysisCacheHit);
        Assert.True(evidenceAppended.Diagnostics.UsedLocalizedStatusOrEvidenceValidation);
        Assert.Equal(0, evidenceAppended.Diagnostics.StructuralFullValidationPasses);
        Assert.Equal(0, evidenceAppended.Diagnostics.CycleValidationPasses);
        Assert.Equal(5_000, evidenceAppended.Snapshot.Nodes.Count);
        Assert.Same(untouched, evidenceAppended.Snapshot.Nodes["node-00000"]);

        var beforeAnalysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            activated.Snapshot,
            out var beforeCacheHit);
        var afterAnalysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            evidenceAppended.Snapshot,
            out var afterCacheHit);
        Assert.True(beforeCacheHit);
        Assert.True(afterCacheHit);
        Assert.Equal(0, afterAnalysis.Diagnostics.FullDigestConstructionPasses);
        Assert.Equal(0, afterAnalysis.Diagnostics.FullDigestNodesVisited);
        Assert.Equal(1, afterAnalysis.Diagnostics.IncrementalDigestLeafUpdates);
        Assert.InRange(
            afterAnalysis.Diagnostics.IncrementalDigestTreeNodesVisited,
            1,
            255);
        Assert.InRange(
            afterAnalysis.Diagnostics.IncrementalDigestTreeNodesRehashed,
            1,
            255);
        Assert.Equal(
            beforeAnalysis.OutcomeCoverageDigest,
            afterAnalysis.OutcomeCoverageDigest);
        Assert.Equal(
            beforeAnalysis.ProgressDependencyStateDigest,
            afterAnalysis.ProgressDependencyStateDigest);
        Assert.Equal(
            beforeAnalysis.ActionDependencyStateDigest,
            afterAnalysis.ActionDependencyStateDigest);
        Assert.NotEqual(
            beforeAnalysis.PlanningIdentityDigest,
            afterAnalysis.PlanningIdentityDigest);

        var evidenceProgress = ProgressVector.CreateFromWorkGraphAnalysis(
            evidenceCursor: 42,
            workGraph: afterAnalysis);
        Assert.Equal(afterAnalysis.OutcomeCoverageDigest, evidenceProgress.OutcomeCoverageDigest);
        Assert.Equal(
            afterAnalysis.ProgressDependencyStateDigest,
            evidenceProgress.DependencyStateDigest);
        Assert.Equal(64, evidenceProgress.MaterialFingerprint.Length);

        var rebuiltEvidence = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            new WorkGraphSnapshot(
                evidenceAppended.Snapshot.Revision,
                evidenceAppended.Snapshot.Nodes),
            out var rebuiltEvidenceCacheHit);
        Assert.False(rebuiltEvidenceCacheHit);
        Assert.Equal(
            rebuiltEvidence.PlanningIdentityDigest,
            afterAnalysis.PlanningIdentityDigest);

        var satisfied = WorkGraphApplier.Apply(
            evidenceAppended.Snapshot,
            new WorkGraphDelta(
                3,
                [Node(
                    "node-02500",
                    "Objective 2500",
                    WorkNodeStatus.Satisfied,
                    evidenceIds: ["e-2500"])]),
            Evidence("e-2500"));
        Assert.True(satisfied.Accepted);
        var satisfiedAnalysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            satisfied.Snapshot,
            out var satisfiedCacheHit);
        Assert.True(satisfiedCacheHit);
        Assert.Equal(0, satisfiedAnalysis.Diagnostics.FullDigestConstructionPasses);
        Assert.Equal(0, satisfiedAnalysis.Diagnostics.FullDigestNodesVisited);
        Assert.Equal(3, satisfiedAnalysis.Diagnostics.IncrementalDigestLeafUpdates);
        Assert.InRange(
            satisfiedAnalysis.Diagnostics.IncrementalDigestTreeNodesVisited,
            1,
            767);
        Assert.InRange(
            satisfiedAnalysis.Diagnostics.IncrementalDigestTreeNodesRehashed,
            1,
            767);
        Assert.NotEqual(
            afterAnalysis.OutcomeCoverageDigest,
            satisfiedAnalysis.OutcomeCoverageDigest);
        var satisfiedProgress = ProgressVector.CreateFromWorkGraphAnalysis(
            evidenceCursor: 43,
            workGraph: satisfiedAnalysis);
        Assert.Equal(
            satisfiedAnalysis.OutcomeCoverageDigest,
            satisfiedProgress.OutcomeCoverageDigest);
        Assert.Equal(
            satisfiedAnalysis.ProgressDependencyStateDigest,
            satisfiedProgress.DependencyStateDigest);

        var rebuiltSatisfied = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            new WorkGraphSnapshot(satisfied.Snapshot.Revision, satisfied.Snapshot.Nodes),
            out var rebuiltSatisfiedCacheHit);
        Assert.False(rebuiltSatisfiedCacheHit);
        Assert.Equal(
            rebuiltSatisfied.OutcomeCoverageDigest,
            satisfiedAnalysis.OutcomeCoverageDigest);
        Assert.Equal(
            rebuiltSatisfied.ProgressDependencyStateDigest,
            satisfiedAnalysis.ProgressDependencyStateDigest);
        Assert.Equal(
            rebuiltSatisfied.PlanningIdentityDigest,
            satisfiedAnalysis.PlanningIdentityDigest);
    }

    [Fact]
    public void StructuralDelta_StillRunsFullCycleValidationAndRejectsTheCycle()
    {
        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("a", "A"), Node("b", "B")]),
            Evidence()).Snapshot;

        var cycle = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [
                    Node("a", "A", dependsOn: ["b"]),
                    Node("b", "B", dependsOn: ["a"])
                ]),
            Evidence());

        Assert.False(cycle.Accepted);
        Assert.Contains(cycle.Errors, error => error.Contains("dependency cycle", StringComparison.Ordinal));
        Assert.True(cycle.Diagnostics.CurrentAnalysisCacheHit);
        Assert.Equal(1, cycle.Diagnostics.StructuralFullValidationPasses);
        Assert.Equal(2, cycle.Diagnostics.StructuralSnapshotNodesVisited);
        Assert.Equal(3, cycle.Diagnostics.CycleValidationPasses);
        Assert.False(cycle.Diagnostics.UsedLocalizedStatusOrEvidenceValidation);
    }

    [Fact]
    public void SupersedingSatisfiedDependency_CannotBypassDependentValidation()
    {
        var current = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(
                0,
                [
                    Node(
                        "dependency",
                        "Dependency",
                        WorkNodeStatus.Satisfied,
                        evidenceIds: ["e-dependency"]),
                    Node(
                        "consumer",
                        "Consumer",
                        WorkNodeStatus.Active,
                        dependsOn: ["dependency"]),
                    Node("replacement", "Replacement")
                ]),
            Evidence("e-dependency")).Snapshot;

        var superseded = WorkGraphApplier.Apply(
            current,
            new WorkGraphDelta(
                1,
                [Node(
                    "dependency",
                    "Dependency",
                    WorkNodeStatus.Superseded,
                    evidenceIds: ["e-dependency", "e-correction"],
                    supersededById: "replacement")]),
            Evidence("e-dependency", "e-correction"));

        Assert.False(superseded.Accepted);
        Assert.Contains(
            superseded.Errors,
            error => error.Contains("consumer", StringComparison.Ordinal)
                     && error.Contains("dependency", StringComparison.Ordinal));
        Assert.Equal(1, superseded.Diagnostics.StructuralFullValidationPasses);
    }

    [Fact]
    public void CallerCreatedMalformedSnapshot_IsFullyValidatedBeforeLocalizedUpdate()
    {
        var malformed = new WorkGraphSnapshot(
            7,
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[]
                {
                    new KeyValuePair<string, WorkNode>(
                        "active",
                        Node(
                            "active",
                            "Active",
                            WorkNodeStatus.Active,
                            dependsOn: ["missing"]))
                }));

        var rejected = WorkGraphApplier.Apply(
            malformed,
            new WorkGraphDelta(
                7,
                [Node(
                    "active",
                    "Active",
                    WorkNodeStatus.Active,
                    dependsOn: ["missing"],
                    evidenceIds: ["e-one"])]),
            Evidence("e-one"));

        Assert.False(rejected.Accepted);
        Assert.Same(malformed, rejected.Snapshot);
        Assert.Contains(rejected.Errors, error => error.Contains("missing dependency", StringComparison.Ordinal));
        Assert.Equal(1, rejected.Diagnostics.CurrentSnapshotFullValidationPasses);
        Assert.Equal(1, rejected.Diagnostics.CurrentSnapshotNodesVisited);
        Assert.Equal(0, rejected.Diagnostics.StructuralFullValidationPasses);
        Assert.Null(rejected.Mutation);
    }

    private static WorkNode Node(
        string id,
        string objective,
        WorkNodeStatus status = WorkNodeStatus.Pending,
        string? parentId = null,
        IEnumerable<string>? dependsOn = null,
        IEnumerable<string>? evidenceIds = null,
        string? supersededById = null) =>
        new(
            id,
            objective,
            parentId,
            status,
            dependsOn?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
            evidenceIds?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
            supersededById);

    private static HashSet<string> Evidence(params string[] ids) =>
        new(ids, StringComparer.Ordinal);
}
