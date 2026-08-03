using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Ali.Modules.Orchestration.Work;

internal sealed record WorkGraphAnalysisDiagnostics(
    int FullValidationPasses,
    long FullValidationNodesVisited,
    int ParentCyclePasses,
    int DependencyCyclePasses,
    int SupersessionCyclePasses,
    int FullDigestConstructionPasses,
    long FullDigestNodesVisited,
    int IncrementalDigestLeafUpdates,
    int IncrementalDigestTreeNodesVisited,
    int IncrementalDigestTreeNodesRehashed);

internal sealed record WorkGraphNodeChange(WorkNode Previous, WorkNode Current);

/// <summary>
/// A validated, immutable material index for one exact work-graph snapshot. The index is cached by
/// snapshot identity; it never changes the public snapshot contract or persisted representation.
/// </summary>
internal sealed class WorkGraphSnapshotAnalysis
{
    private readonly PersistentMerkleStringMap _outcomeCoverageIdentity;
    private readonly PersistentMerkleStringMap _statusIdentity;
    private readonly PersistentMerkleStringMap _planningIdentity;
    private readonly string _outcomeCoverageDigest;
    private readonly string _progressDependencyStateDigest;
    private readonly string _actionDependencyStateDigest;
    private readonly string _planningIdentityDigest;

    private WorkGraphSnapshotAnalysis(
        WorkGraphSnapshot snapshot,
        ImmutableDictionary<string, WorkNode> canonicalNodes,
        ImmutableSortedSet<string> orderedNodeIds,
        ImmutableSortedDictionary<string, string> statusById,
        ImmutableSortedSet<string> pendingIds,
        ImmutableSortedSet<string> activeIds,
        ImmutableSortedSet<string> satisfiedIds,
        ImmutableSortedSet<string> impossibleIds,
        ImmutableSortedSet<string> supersededIds,
        ImmutableDictionary<string, ImmutableSortedSet<string>> dependentsById,
        ImmutableArray<string> errors,
        WorkGraphAnalysisDiagnostics diagnostics,
        PersistentMerkleStringMap outcomeCoverageIdentity,
        PersistentMerkleStringMap statusIdentity,
        PersistentMerkleStringMap planningIdentity)
    {
        Snapshot = snapshot;
        CanonicalNodes = canonicalNodes;
        OrderedNodeIds = orderedNodeIds;
        StatusById = statusById;
        PendingIds = pendingIds;
        ActiveIds = activeIds;
        SatisfiedIds = satisfiedIds;
        ImpossibleIds = impossibleIds;
        SupersededIds = supersededIds;
        DependentsById = dependentsById;
        Errors = errors;
        Diagnostics = diagnostics;
        _outcomeCoverageIdentity = outcomeCoverageIdentity;
        _statusIdentity = statusIdentity;
        _planningIdentity = planningIdentity;
        if (Errors.IsEmpty
            && (_outcomeCoverageIdentity.Count != SatisfiedIds.Count
                || _statusIdentity.Count != CanonicalNodes.Count
                || _planningIdentity.Count != CanonicalNodes.Count))
        {
            throw new InvalidOperationException(
                "A valid work-graph analysis must have exact Merkle index cardinalities.");
        }

        _outcomeCoverageDigest = _outcomeCoverageIdentity.DomainDigest(
            "progress-outcome-coverage-merkle-v2");
        _progressDependencyStateDigest = _statusIdentity.DomainDigest(
            "progress-dependency-state-merkle-v2");
        _actionDependencyStateDigest = _statusIdentity.DomainDigest(
            "planning-dependency-state-merkle-v2");
        _planningIdentityDigest = WorkIdentityCanonicalizer.DigestParts(
            "ali-planning-authoritative-work-graph-merkle-v2",
            Snapshot.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CanonicalNodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _planningIdentity.RootDigest);
    }

    internal WorkGraphSnapshot Snapshot { get; }

    internal ImmutableDictionary<string, WorkNode> CanonicalNodes { get; }

    internal ImmutableSortedSet<string> OrderedNodeIds { get; }

    internal ImmutableSortedDictionary<string, string> StatusById { get; }

    internal ImmutableSortedSet<string> PendingIds { get; }

    internal ImmutableSortedSet<string> ActiveIds { get; }

    internal ImmutableSortedSet<string> SatisfiedIds { get; }

    internal ImmutableSortedSet<string> ImpossibleIds { get; }

    internal ImmutableSortedSet<string> SupersededIds { get; }

    internal ImmutableDictionary<string, ImmutableSortedSet<string>> DependentsById { get; }

    internal ImmutableArray<string> Errors { get; }

    internal WorkGraphAnalysisDiagnostics Diagnostics { get; }

    internal bool IsValid => Errors.IsEmpty;

    internal int EligibleCount =>
        PendingIds.Count + ActiveIds.Count + SatisfiedIds.Count + ImpossibleIds.Count;

    internal string OutcomeCoverageDigest => _outcomeCoverageDigest;

    internal string ProgressDependencyStateDigest => _progressDependencyStateDigest;

    internal string ActionDependencyStateDigest => _actionDependencyStateDigest;

    internal string PlanningIdentityDigest => _planningIdentityDigest;

    internal IEnumerable<string> EnumerateEligibleIds(int maximumCount)
    {
        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var yielded = 0;
        foreach (var bucket in new[] { ActiveIds, PendingIds, ImpossibleIds, SatisfiedIds })
        {
            foreach (var id in bucket)
            {
                if (yielded == maximumCount)
                {
                    yield break;
                }

                yielded++;
                yield return id;
            }
        }
    }

    internal bool TryGetSoleActiveId(out string? activeId)
    {
        if (ActiveIds.Count == 1)
        {
            activeId = ActiveIds.Min;
            return true;
        }

        activeId = null;
        return false;
    }

    internal WorkGraphSnapshotAnalysis DeriveStatusOrEvidenceOnly(
        WorkGraphSnapshot child,
        IReadOnlyList<WorkGraphNodeChange> changes)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(changes);
        if (!IsValid)
        {
            throw new InvalidOperationException(
                "A work-graph material index cannot be derived from an invalid snapshot.");
        }

        var statusById = StatusById.ToBuilder();
        var pending = PendingIds.ToBuilder();
        var active = ActiveIds.ToBuilder();
        var satisfied = SatisfiedIds.ToBuilder();
        var impossible = ImpossibleIds.ToBuilder();
        var superseded = SupersededIds.ToBuilder();
        var outcomeCoverageIdentity = _outcomeCoverageIdentity;
        var statusIdentity = _statusIdentity;
        var planningIdentity = _planningIdentity;
        var incrementalLeafUpdates = 0;
        var incrementalNodesVisited = 0;
        var incrementalNodesRehashed = 0;
        foreach (var change in changes)
        {
            if (!string.Equals(change.Previous.Id, change.Current.Id, StringComparison.Ordinal)
                || !string.Equals(change.Previous.ParentId, change.Current.ParentId, StringComparison.Ordinal)
                || !WorkGraphApplier.ArraysEqual(change.Previous.DependsOn, change.Current.DependsOn)
                || !string.Equals(
                    change.Previous.SupersededById,
                    change.Current.SupersededById,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A localized work-graph analysis cannot cross a structural change.");
            }

            planningIdentity = planningIdentity.Set(
                change.Current.Id,
                PlanningNodeIdentityDigest(change.Current),
                out var planningUpdate);
            AddDigestWork(planningUpdate);

            if (change.Previous.Status == change.Current.Status)
            {
                continue;
            }

            RemoveStatus(
                change.Previous.Status,
                change.Previous.Id,
                pending,
                active,
                satisfied,
                impossible,
                superseded);
            AddStatus(
                change.Current.Status,
                change.Current.Id,
                pending,
                active,
                satisfied,
                impossible,
                superseded);
            statusById[change.Current.Id] = change.Current.Status.ToString();
            statusIdentity = statusIdentity.Set(
                change.Current.Id,
                change.Current.Status.ToString(),
                out var statusUpdate);
            AddDigestWork(statusUpdate);

            if (change.Previous.Status == WorkNodeStatus.Satisfied)
            {
                outcomeCoverageIdentity = outcomeCoverageIdentity.Remove(
                    change.Current.Id,
                    out var outcomeRemoval);
                AddDigestWork(outcomeRemoval);
            }
            else if (change.Current.Status == WorkNodeStatus.Satisfied)
            {
                outcomeCoverageIdentity = outcomeCoverageIdentity.Set(
                    change.Current.Id,
                    "satisfied",
                    out var outcomeAddition);
                AddDigestWork(outcomeAddition);
            }
        }

        var canonicalNodes = child.Nodes.WithComparers(StringComparer.Ordinal);
        return new WorkGraphSnapshotAnalysis(
            child,
            canonicalNodes,
            OrderedNodeIds,
            statusById.ToImmutable(),
            pending.ToImmutable(),
            active.ToImmutable(),
            satisfied.ToImmutable(),
            impossible.ToImmutable(),
            superseded.ToImmutable(),
            DependentsById,
            ImmutableArray<string>.Empty,
            new WorkGraphAnalysisDiagnostics(
                FullValidationPasses: 0,
                FullValidationNodesVisited: 0,
                ParentCyclePasses: 0,
                DependencyCyclePasses: 0,
                SupersessionCyclePasses: 0,
                FullDigestConstructionPasses: 0,
                FullDigestNodesVisited: 0,
                IncrementalDigestLeafUpdates: incrementalLeafUpdates,
                IncrementalDigestTreeNodesVisited: incrementalNodesVisited,
                IncrementalDigestTreeNodesRehashed: incrementalNodesRehashed),
            outcomeCoverageIdentity,
            statusIdentity,
            planningIdentity);

        void AddDigestWork(PersistentMerkleMapUpdate update)
        {
            if (update.Changed)
            {
                incrementalLeafUpdates++;
            }

            incrementalNodesVisited += update.NodesVisited;
            incrementalNodesRehashed += update.NodesRehashed;
        }
    }

    internal static WorkGraphSnapshotAnalysis CreateFull(WorkGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var errors = new List<string>();
        if (snapshot.Revision < 0)
        {
            errors.Add("The current work-graph revision cannot be negative.");
        }

        if (snapshot.Nodes is null)
        {
            errors.Add("The current work graph must contain an initialized node map.");
            return Invalid(snapshot, errors, nodesVisited: 0);
        }

        var nodes = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Nodes)
        {
            var node = pair.Value;
            if (node is null || !string.Equals(pair.Key, node.Id, StringComparison.Ordinal))
            {
                errors.Add($"The current node-map key '{pair.Key}' does not match its node identifier.");
                continue;
            }

            if (!nodes.TryAdd(pair.Key, node))
            {
                errors.Add($"The current work graph contains duplicate node identifier '{pair.Key}'.");
            }
        }

        var canonicalNodes = nodes.ToImmutable();
        var orderedIds = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var statusById = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var pending = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var active = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var satisfied = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var impossible = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var superseded = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        var dependentBuilders = new Dictionary<string, ImmutableSortedSet<string>.Builder>(
            StringComparer.Ordinal);

        foreach (var node in canonicalNodes.Values)
        {
            WorkGraphApplier.ValidateNodeShape(node, knownEvidenceIds: null, errors);
            orderedIds.Add(node.Id);
            statusById[node.Id] = node.Status.ToString();
            AddStatus(node.Status, node.Id, pending, active, satisfied, impossible, superseded);

            if (node.ParentId is not null)
            {
                if (string.Equals(node.Id, node.ParentId, StringComparison.Ordinal))
                {
                    errors.Add($"Work node '{node.Id}' cannot be its own parent.");
                }
                else if (!canonicalNodes.ContainsKey(node.ParentId))
                {
                    errors.Add($"Work node '{node.Id}' references missing parent '{node.ParentId}'.");
                }
            }

            if (node.SupersededById is not null)
            {
                if (string.Equals(node.Id, node.SupersededById, StringComparison.Ordinal))
                {
                    errors.Add($"Work node '{node.Id}' cannot supersede itself.");
                }
                else if (!canonicalNodes.ContainsKey(node.SupersededById))
                {
                    errors.Add(
                        $"Superseded work node '{node.Id}' references missing replacement '{node.SupersededById}'.");
                }
            }

            if (!node.DependsOn.IsDefault)
            {
                foreach (var dependencyId in node.DependsOn)
                {
                    if (string.Equals(node.Id, dependencyId, StringComparison.Ordinal))
                    {
                        errors.Add($"Work node '{node.Id}' cannot depend on itself.");
                    }
                    else if (!canonicalNodes.ContainsKey(dependencyId))
                    {
                        errors.Add($"Work node '{node.Id}' references missing dependency '{dependencyId}'.");
                    }
                    else
                    {
                        if (!dependentBuilders.TryGetValue(dependencyId, out var dependents))
                        {
                            dependents = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
                            dependentBuilders.Add(dependencyId, dependents);
                        }

                        dependents.Add(node.Id);
                    }
                }
            }

            ValidateSatisfiedDependencies(node, canonicalNodes, errors);
        }

        var parentCycles = ContainsCycle(
            canonicalNodes,
            static node => node.ParentId is { } parentId
                ? new[] { parentId }
                : Array.Empty<string>());
        if (parentCycles)
        {
            errors.Add("The work graph contains a parent cycle.");
        }

        var dependencyCyclePasses = 0;
        if (canonicalNodes.Values.All(static node => !node.DependsOn.IsDefault))
        {
            dependencyCyclePasses = 1;
            if (ContainsCycle(canonicalNodes, static node => node.DependsOn))
            {
                errors.Add("The work graph contains a dependency cycle.");
            }
        }

        var supersessionCycles = ContainsCycle(
            canonicalNodes,
            static node => node.SupersededById is { } replacementId
                ? new[] { replacementId }
                : Array.Empty<string>());
        if (supersessionCycles)
        {
            errors.Add("The work graph contains a supersession cycle.");
        }

        var normalizedErrors = NormalizeErrors(errors);
        var dependentsById = dependentBuilders.ToImmutableDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutable(),
            StringComparer.Ordinal);
        var fullDiagnostics = new WorkGraphAnalysisDiagnostics(
            FullValidationPasses: 1,
            FullValidationNodesVisited: canonicalNodes.Count,
            ParentCyclePasses: 1,
            DependencyCyclePasses: dependencyCyclePasses,
            SupersessionCyclePasses: 1,
            FullDigestConstructionPasses: normalizedErrors.IsEmpty ? 3 : 0,
            FullDigestNodesVisited: normalizedErrors.IsEmpty
                ? canonicalNodes.Count + statusById.Count + satisfied.Count
                : 0,
            IncrementalDigestLeafUpdates: 0,
            IncrementalDigestTreeNodesVisited: 0,
            IncrementalDigestTreeNodesRehashed: 0);
        if (!normalizedErrors.IsEmpty)
        {
            return new WorkGraphSnapshotAnalysis(
                snapshot,
                canonicalNodes,
                orderedIds.ToImmutable(),
                statusById.ToImmutable(),
                pending.ToImmutable(),
                active.ToImmutable(),
                satisfied.ToImmutable(),
                impossible.ToImmutable(),
                superseded.ToImmutable(),
                dependentsById,
                normalizedErrors,
                fullDiagnostics,
                PersistentMerkleStringMap.Empty,
                PersistentMerkleStringMap.Empty,
                PersistentMerkleStringMap.Empty);
        }

        var outcomeCoverageIdentity = PersistentMerkleStringMap.Create(
            satisfied.Select(static id => new KeyValuePair<string, string>(id, "satisfied")));
        var statusIdentity = PersistentMerkleStringMap.Create(statusById);
        var planningIdentity = PersistentMerkleStringMap.Create(
            orderedIds.Select(id => new KeyValuePair<string, string>(
                id,
                PlanningNodeIdentityDigest(canonicalNodes[id]))));
        return new WorkGraphSnapshotAnalysis(
            snapshot,
            canonicalNodes,
            orderedIds.ToImmutable(),
            statusById.ToImmutable(),
            pending.ToImmutable(),
            active.ToImmutable(),
            satisfied.ToImmutable(),
            impossible.ToImmutable(),
            superseded.ToImmutable(),
            dependentsById,
            normalizedErrors,
            fullDiagnostics,
            outcomeCoverageIdentity,
            statusIdentity,
            planningIdentity);
    }

    private static void ValidateSatisfiedDependencies(
        WorkNode node,
        IReadOnlyDictionary<string, WorkNode> nodes,
        List<string> errors)
    {
        if (node.DependsOn.IsDefault
            || node.Status is not (WorkNodeStatus.Active or WorkNodeStatus.Satisfied))
        {
            return;
        }

        foreach (var dependencyId in node.DependsOn)
        {
            if (nodes.TryGetValue(dependencyId, out var dependency)
                && dependency.Status != WorkNodeStatus.Satisfied)
            {
                errors.Add(
                    $"Work node '{node.Id}' cannot be {node.Status} while dependency '{dependencyId}' is {dependency.Status}.");
            }
        }
    }

    private static bool ContainsCycle(
        IReadOnlyDictionary<string, WorkNode> nodes,
        Func<WorkNode, IEnumerable<string>> successors)
    {
        var incoming = nodes.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            foreach (var successor in successors(node))
            {
                if (incoming.ContainsKey(successor))
                {
                    incoming[successor]++;
                }
            }
        }

        var ready = new Queue<string>(
            incoming
                .Where(static pair => pair.Value == 0)
                .Select(static pair => pair.Key)
                .Order(StringComparer.Ordinal));
        var visited = 0;
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            visited++;
            foreach (var successor in successors(nodes[id]))
            {
                if (!incoming.ContainsKey(successor))
                {
                    continue;
                }

                incoming[successor]--;
                if (incoming[successor] == 0)
                {
                    ready.Enqueue(successor);
                }
            }
        }

        return visited != nodes.Count;
    }

    private static string PlanningNodeIdentityDigest(WorkNode node)
    {
        var parts = new List<string>
        {
            "ali-planning-authoritative-work-node-v2",
            node.Id,
            node.Objective,
            node.ParentId is null ? "0" : "1"
        };
        if (node.ParentId is not null)
        {
            parts.Add(node.ParentId);
        }

        parts.Add(((int)node.Status).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        parts.Add(node.DependsOn.IsDefault
            ? "-1"
            : node.DependsOn.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        if (!node.DependsOn.IsDefault)
        {
            parts.AddRange(node.DependsOn);
        }

        parts.Add(node.EvidenceIds.IsDefault
            ? "-1"
            : node.EvidenceIds.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        if (!node.EvidenceIds.IsDefault)
        {
            parts.AddRange(node.EvidenceIds);
        }

        parts.Add(node.SupersededById is null ? "0" : "1");
        if (node.SupersededById is not null)
        {
            parts.Add(node.SupersededById);
        }

        return WorkIdentityCanonicalizer.DigestParts(parts);
    }

    private static WorkGraphSnapshotAnalysis Invalid(
        WorkGraphSnapshot snapshot,
        IEnumerable<string> errors,
        long nodesVisited)
    {
        var emptyNodes = ImmutableDictionary<string, WorkNode>.Empty.WithComparers(
            StringComparer.Ordinal);
        var emptyIds = ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        return new WorkGraphSnapshotAnalysis(
            snapshot,
            emptyNodes,
            emptyIds,
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
            emptyIds,
            emptyIds,
            emptyIds,
            emptyIds,
            emptyIds,
            ImmutableDictionary<string, ImmutableSortedSet<string>>.Empty.WithComparers(
                StringComparer.Ordinal),
            NormalizeErrors(errors),
            new WorkGraphAnalysisDiagnostics(
                1,
                nodesVisited,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0),
            PersistentMerkleStringMap.Empty,
            PersistentMerkleStringMap.Empty,
            PersistentMerkleStringMap.Empty);
    }

    private static ImmutableArray<string> NormalizeErrors(IEnumerable<string> errors) =>
        errors
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

    private static void AddStatus(
        WorkNodeStatus status,
        string id,
        ImmutableSortedSet<string>.Builder pending,
        ImmutableSortedSet<string>.Builder active,
        ImmutableSortedSet<string>.Builder satisfied,
        ImmutableSortedSet<string>.Builder impossible,
        ImmutableSortedSet<string>.Builder superseded)
    {
        _ = status switch
        {
            WorkNodeStatus.Pending => pending.Add(id),
            WorkNodeStatus.Active => active.Add(id),
            WorkNodeStatus.Satisfied => satisfied.Add(id),
            WorkNodeStatus.Impossible => impossible.Add(id),
            WorkNodeStatus.Superseded => superseded.Add(id),
            _ => false
        };
    }

    private static void RemoveStatus(
        WorkNodeStatus status,
        string id,
        ImmutableSortedSet<string>.Builder pending,
        ImmutableSortedSet<string>.Builder active,
        ImmutableSortedSet<string>.Builder satisfied,
        ImmutableSortedSet<string>.Builder impossible,
        ImmutableSortedSet<string>.Builder superseded)
    {
        _ = status switch
        {
            WorkNodeStatus.Pending => pending.Remove(id),
            WorkNodeStatus.Active => active.Remove(id),
            WorkNodeStatus.Satisfied => satisfied.Remove(id),
            WorkNodeStatus.Impossible => impossible.Remove(id),
            WorkNodeStatus.Superseded => superseded.Remove(id),
            _ => false
        };
    }
}

internal static class WorkGraphSnapshotAnalysisCache
{
    private sealed class Holder(WorkGraphSnapshotAnalysis analysis)
    {
        internal WorkGraphSnapshotAnalysis Analysis { get; } = analysis;
    }

    private static readonly ConditionalWeakTable<WorkGraphSnapshot, Holder> Cache = new();

    internal static WorkGraphSnapshotAnalysis GetOrCreate(
        WorkGraphSnapshot snapshot,
        out bool cacheHit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Cache.TryGetValue(snapshot, out var existing))
        {
            cacheHit = true;
            return existing.Analysis;
        }

        var created = WorkGraphSnapshotAnalysis.CreateFull(snapshot);
        var selected = Cache.GetValue(snapshot, _ => new Holder(created)).Analysis;
        cacheHit = !ReferenceEquals(created, selected);
        return selected;
    }

    internal static WorkGraphSnapshotAnalysis AttachDerived(
        WorkGraphSnapshot snapshot,
        WorkGraphSnapshotAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(analysis);
        if (!ReferenceEquals(snapshot, analysis.Snapshot))
        {
            throw new InvalidOperationException(
                "A cached work-graph material index crossed its snapshot identity.");
        }

        return Cache.GetValue(snapshot, _ => new Holder(analysis)).Analysis;
    }
}
