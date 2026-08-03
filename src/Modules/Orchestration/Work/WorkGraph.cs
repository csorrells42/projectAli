using System.Collections.Immutable;

namespace Ali.Modules.Orchestration.Work;

public enum WorkNodeStatus
{
    Pending,
    Active,
    Satisfied,
    Impossible,
    Superseded
}

public sealed record WorkNode(
    string Id,
    string Objective,
    string? ParentId,
    WorkNodeStatus Status,
    ImmutableArray<string> DependsOn,
    ImmutableArray<string> EvidenceIds,
    string? SupersededById = null);

public sealed record WorkGraphSnapshot(
    long Revision,
    ImmutableDictionary<string, WorkNode> Nodes)
{
    public static WorkGraphSnapshot Empty { get; } = new(
        0,
        ImmutableDictionary.Create<string, WorkNode>(StringComparer.Ordinal));
}

public sealed record WorkGraphDelta(
    long ExpectedRevision,
    ImmutableArray<WorkNode> Upserts);

public sealed record WorkGraphApplyResult(
    bool Accepted,
    bool Changed,
    WorkGraphSnapshot Snapshot,
    ImmutableArray<string> Errors)
{
    internal WorkGraphApplyDiagnostics Diagnostics { get; init; } =
        WorkGraphApplyDiagnostics.Empty;

    internal WorkGraphApplier.ValidatedMutation? Mutation { get; init; }
}

internal sealed record WorkGraphApplyDiagnostics(
    bool CurrentAnalysisCacheHit,
    int CurrentSnapshotFullValidationPasses,
    long CurrentSnapshotNodesVisited,
    int CandidateUpsertsVisited,
    int ChangedNodes,
    bool UsedLocalizedStatusOrEvidenceValidation,
    int StructuralFullValidationPasses,
    long StructuralSnapshotNodesVisited,
    int CycleValidationPasses)
{
    internal static WorkGraphApplyDiagnostics Empty { get; } = new(
        CurrentAnalysisCacheHit: false,
        CurrentSnapshotFullValidationPasses: 0,
        CurrentSnapshotNodesVisited: 0,
        CandidateUpsertsVisited: 0,
        ChangedNodes: 0,
        UsedLocalizedStatusOrEvidenceValidation: false,
        StructuralFullValidationPasses: 0,
        StructuralSnapshotNodesVisited: 0,
        CycleValidationPasses: 0);
}

public static class WorkGraphApplier
{
    private static readonly object MutationAuthority = new();

    internal sealed class ValidatedMutation
    {
        private ValidatedMutation(
            WorkGraphSnapshot baseSnapshot,
            WorkGraphSnapshot snapshot,
            ImmutableArray<WorkNode> changedUpserts,
            bool structureChanged)
        {
            BaseSnapshot = baseSnapshot;
            Snapshot = snapshot;
            ChangedUpserts = changedUpserts;
            StructureChanged = structureChanged;
        }

        internal WorkGraphSnapshot BaseSnapshot { get; }

        internal WorkGraphSnapshot Snapshot { get; }

        internal ImmutableArray<WorkNode> ChangedUpserts { get; }

        internal bool StructureChanged { get; }

        internal static ValidatedMutation Create(
            object authority,
            WorkGraphSnapshot baseSnapshot,
            WorkGraphSnapshot snapshot,
            ImmutableArray<WorkNode> changedUpserts,
            bool structureChanged)
        {
            if (!ReferenceEquals(authority, MutationAuthority))
            {
                throw new InvalidOperationException(
                    "Only the authoritative work-graph applier can issue a validated mutation.");
            }

            return new ValidatedMutation(
                baseSnapshot,
                snapshot,
                changedUpserts,
                structureChanged);
        }
    }

    public static WorkGraphApplyResult Apply(
        WorkGraphSnapshot current,
        WorkGraphDelta delta,
        IReadOnlySet<string> knownEvidenceIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(knownEvidenceIds);

        var errors = new List<string>();
        if (delta.ExpectedRevision != current.Revision)
        {
            errors.Add(
                $"The delta expected revision {delta.ExpectedRevision}, but the current revision is {current.Revision}.");
        }

        if (delta.Upserts.IsDefault)
        {
            errors.Add("The work-graph delta must contain an initialized upsert list.");
            return Rejected(current, errors, WorkGraphApplyDiagnostics.Empty);
        }

        var currentAnalysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(
            current,
            out var currentAnalysisCacheHit);
        if (!currentAnalysis.IsValid)
        {
            errors.AddRange(currentAnalysis.Errors);
        }

        var currentDiagnostics = new WorkGraphApplyDiagnostics(
            CurrentAnalysisCacheHit: currentAnalysisCacheHit,
            CurrentSnapshotFullValidationPasses: currentAnalysisCacheHit
                ? 0
                : currentAnalysis.Diagnostics.FullValidationPasses,
            CurrentSnapshotNodesVisited: currentAnalysisCacheHit
                ? 0
                : currentAnalysis.Diagnostics.FullValidationNodesVisited,
            CandidateUpsertsVisited: 0,
            ChangedNodes: 0,
            UsedLocalizedStatusOrEvidenceValidation: false,
            StructuralFullValidationPasses: 0,
            StructuralSnapshotNodesVisited: 0,
            CycleValidationPasses: currentAnalysisCacheHit
                ? 0
                : currentAnalysis.Diagnostics.ParentCyclePasses
                  + currentAnalysis.Diagnostics.DependencyCyclePasses
                  + currentAnalysis.Diagnostics.SupersessionCyclePasses);
        if (errors.Count > 0)
        {
            return Rejected(current, errors, currentDiagnostics);
        }

        var merged = currentAnalysis.CanonicalNodes.ToBuilder();

        var deltaIds = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<WorkGraphNodeChange>();
        var structureChanged = false;
        var candidateUpsertsVisited = 0;
        foreach (var candidate in delta.Upserts)
        {
            candidateUpsertsVisited++;
            if (candidate is null)
            {
                errors.Add("A work-graph upsert cannot be null.");
                continue;
            }

            if (!ValidateNodeShape(candidate, knownEvidenceIds, errors))
            {
                continue;
            }

            if (!deltaIds.Add(candidate.Id))
            {
                errors.Add($"The delta contains duplicate upserts for node '{candidate.Id}'.");
                continue;
            }

            var normalized = candidate with
            {
                DependsOn = candidate.DependsOn
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray(),
                EvidenceIds = Clone(candidate.EvidenceIds)
            };

            if (merged.TryGetValue(candidate.Id, out var accepted))
            {
                ValidateAcceptedNodeUpdate(accepted, normalized, errors);
                if (NodeEquivalent(accepted, normalized))
                {
                    continue;
                }

                structureChanged |= !StructureEquivalent(accepted, normalized);
                changes.Add(new WorkGraphNodeChange(accepted, normalized));
            }
            else if (normalized.Status == WorkNodeStatus.Superseded)
            {
                errors.Add(
                    $"Work node '{candidate.Id}' must be accepted as Pending or Active before it can be superseded.");
            }
            else
            {
                structureChanged = true;
            }

            merged[candidate.Id] = normalized;
        }

        if (errors.Count > 0)
        {
            return Rejected(
                current,
                errors,
                currentDiagnostics with
                {
                    CandidateUpsertsVisited = candidateUpsertsVisited,
                    ChangedNodes = changes.Count
                });
        }

        if (changes.Count == 0 && merged.Count == currentAnalysis.CanonicalNodes.Count)
        {
            return new WorkGraphApplyResult(
                Accepted: true,
                Changed: false,
                Snapshot: current,
                Errors: ImmutableArray<string>.Empty)
            {
                Diagnostics = currentDiagnostics with
                {
                    CandidateUpsertsVisited = candidateUpsertsVisited
                }
            };
        }

        if (current.Revision == long.MaxValue)
        {
            return Rejected(
                current,
                ["The work-graph revision cannot advance beyond Int64.MaxValue."],
                currentDiagnostics with
                {
                    CandidateUpsertsVisited = candidateUpsertsVisited,
                    ChangedNodes = changes.Count
                });
        }

        var snapshot = new WorkGraphSnapshot(current.Revision + 1, merged.ToImmutable());
        WorkGraphSnapshotAnalysis childAnalysis;
        WorkGraphApplyDiagnostics diagnostics;
        if (structureChanged)
        {
            childAnalysis = WorkGraphSnapshotAnalysisCache.GetOrCreate(snapshot, out _);
            diagnostics = currentDiagnostics with
            {
                CandidateUpsertsVisited = candidateUpsertsVisited,
                ChangedNodes = changes.Count + (snapshot.Nodes.Count - currentAnalysis.CanonicalNodes.Count),
                StructuralFullValidationPasses = childAnalysis.Diagnostics.FullValidationPasses,
                StructuralSnapshotNodesVisited = childAnalysis.Diagnostics.FullValidationNodesVisited,
                CycleValidationPasses = currentDiagnostics.CycleValidationPasses
                    + childAnalysis.Diagnostics.ParentCyclePasses
                    + childAnalysis.Diagnostics.DependencyCyclePasses
                    + childAnalysis.Diagnostics.SupersessionCyclePasses
            };
            if (!childAnalysis.IsValid)
            {
                return Rejected(current, childAnalysis.Errors, diagnostics);
            }
        }
        else
        {
            // The accepted parent analysis proves all existing references and three DAGs. With
            // parent/dependency/supersession edges unchanged, no new cycle or missing reference
            // can appear. Only final dependency statuses can invalidate Active/Satisfied work,
            // which is checked exactly for changed nodes and their cached reverse dependents.
            ValidateLocalizedStatusOrEvidenceChanges(
                currentAnalysis,
                snapshot.Nodes,
                changes,
                errors);
            diagnostics = currentDiagnostics with
            {
                CandidateUpsertsVisited = candidateUpsertsVisited,
                ChangedNodes = changes.Count,
                UsedLocalizedStatusOrEvidenceValidation = true
            };
            if (errors.Count > 0)
            {
                return Rejected(current, errors, diagnostics);
            }

            childAnalysis = WorkGraphSnapshotAnalysisCache.AttachDerived(
                snapshot,
                currentAnalysis.DeriveStatusOrEvidenceOnly(snapshot, changes));
        }

        var changedUpserts = snapshot.Nodes.Count == currentAnalysis.CanonicalNodes.Count
            ? changes
                .Select(static change => change.Current)
                .OrderBy(static node => node.Id, StringComparer.Ordinal)
                .ToImmutableArray()
            : deltaIds
                .Select(id => snapshot.Nodes[id])
                .Where(node => !currentAnalysis.CanonicalNodes.TryGetValue(node.Id, out var previous)
                               || !NodeEquivalent(previous, node))
                .OrderBy(static node => node.Id, StringComparer.Ordinal)
                .ToImmutableArray();
        var mutation = ValidatedMutation.Create(
            MutationAuthority,
            current,
            snapshot,
            changedUpserts,
            structureChanged);
        return new WorkGraphApplyResult(
            Accepted: true,
            Changed: true,
            Snapshot: snapshot,
            Errors: ImmutableArray<string>.Empty)
        {
            Diagnostics = diagnostics,
            Mutation = mutation
        };
    }

    internal static bool ValidateNodeShape(
        WorkNode node,
        IReadOnlySet<string>? knownEvidenceIds,
        List<string> errors)
    {
        var initialErrorCount = errors.Count;
        if (string.IsNullOrWhiteSpace(node.Id))
        {
            errors.Add("A work node must have a nonblank identifier.");
        }
        else if (!string.Equals(node.Id, node.Id.Trim(), StringComparison.Ordinal))
        {
            errors.Add($"Work-node identifier '{node.Id}' cannot have surrounding whitespace.");
        }

        if (string.IsNullOrWhiteSpace(node.Objective))
        {
            errors.Add($"Work node '{node.Id}' must have a nonblank objective.");
        }

        if (!Enum.IsDefined(node.Status))
        {
            errors.Add($"Work node '{node.Id}' has an invalid status value.");
        }

        if (node.ParentId is not null &&
            (string.IsNullOrWhiteSpace(node.ParentId) ||
             !string.Equals(node.ParentId, node.ParentId.Trim(), StringComparison.Ordinal)))
        {
            errors.Add($"Work node '{node.Id}' has an invalid parent identifier.");
        }

        if (node.Status == WorkNodeStatus.Superseded)
        {
            if (string.IsNullOrWhiteSpace(node.SupersededById) ||
                !string.Equals(
                    node.SupersededById,
                    node.SupersededById.Trim(),
                    StringComparison.Ordinal))
            {
                errors.Add($"Superseded work node '{node.Id}' must identify its replacement.");
            }
        }
        else if (node.SupersededById is not null)
        {
            errors.Add(
                $"Work node '{node.Id}' cannot identify a replacement unless its status is Superseded.");
        }

        if (node.DependsOn.IsDefault)
        {
            errors.Add($"Work node '{node.Id}' must have an initialized dependency list.");
        }
        else
        {
            ValidateIdentifiers(node.Id, "dependency", node.DependsOn, knownIds: null, errors);
        }

        if (node.EvidenceIds.IsDefault)
        {
            errors.Add($"Work node '{node.Id}' must have an initialized evidence list.");
        }
        else
        {
            ValidateIdentifiers(node.Id, "evidence", node.EvidenceIds, knownEvidenceIds, errors);
            if (IsTerminal(node.Status) && node.EvidenceIds.Length == 0)
            {
                errors.Add($"Terminal work node '{node.Id}' must cite at least one known evidence item.");
            }
        }

        return errors.Count == initialErrorCount;
    }

    private static void ValidateIdentifiers(
        string nodeId,
        string kind,
        ImmutableArray<string> identifiers,
        IReadOnlySet<string>? knownIds,
        List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in identifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier) ||
                !string.Equals(identifier, identifier.Trim(), StringComparison.Ordinal))
            {
                errors.Add($"Work node '{nodeId}' contains an invalid {kind} identifier.");
                continue;
            }

            if (!seen.Add(identifier))
            {
                errors.Add($"Work node '{nodeId}' contains duplicate {kind} identifier '{identifier}'.");
            }

            if (knownIds is not null && !knownIds.Contains(identifier))
            {
                errors.Add($"Work node '{nodeId}' cites unknown evidence '{identifier}'.");
            }
        }
    }

    private static void ValidateAcceptedNodeUpdate(
        WorkNode accepted,
        WorkNode candidate,
        List<string> errors)
    {
        if (!string.Equals(accepted.Objective, candidate.Objective, StringComparison.Ordinal))
        {
            errors.Add($"Accepted work node '{candidate.Id}' cannot change its objective.");
        }

        if (!string.Equals(accepted.ParentId, candidate.ParentId, StringComparison.Ordinal))
        {
            errors.Add($"Accepted work node '{candidate.Id}' cannot change its parent.");
        }

        if (accepted.Status == WorkNodeStatus.Superseded &&
            !string.Equals(
                accepted.SupersededById,
                candidate.SupersededById,
                StringComparison.Ordinal))
        {
            errors.Add($"Superseded work node '{candidate.Id}' cannot change its replacement provenance.");
        }

        if (!IsLegalTransition(accepted.Status, candidate.Status))
        {
            errors.Add(
                $"Work node '{candidate.Id}' cannot transition from {accepted.Status} to {candidate.Status}.");
        }

        if (!IsEvidenceAppend(accepted.EvidenceIds, candidate.EvidenceIds))
        {
            errors.Add($"Accepted work node '{candidate.Id}' cannot erase or reorder evidence history.");
        }

        if (accepted.Status is WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible
            && candidate.Status == WorkNodeStatus.Superseded
            && (accepted.EvidenceIds.IsDefault
                || candidate.EvidenceIds.IsDefault
                || candidate.EvidenceIds.Length <= accepted.EvidenceIds.Length))
        {
            errors.Add(
                $"Terminal work node '{candidate.Id}' requires appended correction evidence before it can be superseded.");
        }

        if (IsTerminal(accepted.Status) &&
            (accepted.DependsOn.IsDefault ||
             candidate.DependsOn.IsDefault ||
             !accepted.DependsOn.SequenceEqual(candidate.DependsOn, StringComparer.Ordinal)))
        {
            errors.Add($"Terminal work node '{candidate.Id}' cannot change its dependencies.");
        }


        if (candidate.Status == WorkNodeStatus.Superseded &&
            accepted.Status != WorkNodeStatus.Superseded &&
            (accepted.DependsOn.IsDefault ||
             candidate.DependsOn.IsDefault ||
             !accepted.DependsOn.SequenceEqual(candidate.DependsOn, StringComparer.Ordinal)))
        {
            errors.Add($"Work node '{candidate.Id}' cannot change dependencies while being superseded.");
        }
    }

    private static void ValidateLocalizedStatusOrEvidenceChanges(
        WorkGraphSnapshotAnalysis currentAnalysis,
        IReadOnlyDictionary<string, WorkNode> candidateNodes,
        IReadOnlyList<WorkGraphNodeChange> changes,
        List<string> errors)
    {
        foreach (var change in changes)
        {
            var candidate = change.Current;
            if (!candidate.DependsOn.IsDefault
                && candidate.Status is (WorkNodeStatus.Active or WorkNodeStatus.Satisfied))
            {
                foreach (var dependencyId in candidate.DependsOn)
                {
                    if (candidateNodes.TryGetValue(dependencyId, out var dependency)
                        && dependency.Status != WorkNodeStatus.Satisfied)
                    {
                        errors.Add(
                            $"Work node '{candidate.Id}' cannot be {candidate.Status} while dependency '{dependencyId}' is {dependency.Status}.");
                    }
                }
            }

            if (change.Previous.Status == candidate.Status
                || !currentAnalysis.DependentsById.TryGetValue(candidate.Id, out var dependents))
            {
                continue;
            }

            foreach (var dependentId in dependents)
            {
                if (candidateNodes.TryGetValue(dependentId, out var dependent)
                    && dependent.Status is (WorkNodeStatus.Active or WorkNodeStatus.Satisfied)
                    && candidate.Status != WorkNodeStatus.Satisfied)
                {
                    errors.Add(
                        $"Work node '{dependent.Id}' cannot be {dependent.Status} while dependency '{candidate.Id}' is {candidate.Status}.");
                }
            }
        }
    }

    private static bool StructureEquivalent(WorkNode left, WorkNode right) =>
        string.Equals(left.ParentId, right.ParentId, StringComparison.Ordinal)
        && ArraysEqual(left.DependsOn, right.DependsOn)
        && string.Equals(left.SupersededById, right.SupersededById, StringComparison.Ordinal);

    private static bool IsEvidenceAppend(
        ImmutableArray<string> accepted,
        ImmutableArray<string> candidate)
    {
        if (accepted.IsDefault || candidate.IsDefault || candidate.Length < accepted.Length)
        {
            return false;
        }

        for (var index = 0; index < accepted.Length; index++)
        {
            if (!string.Equals(accepted[index], candidate[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLegalTransition(WorkNodeStatus accepted, WorkNodeStatus candidate) =>
        accepted switch
        {
            WorkNodeStatus.Pending => true,
            WorkNodeStatus.Active => candidate is WorkNodeStatus.Pending or
                WorkNodeStatus.Active or
                WorkNodeStatus.Satisfied or
                WorkNodeStatus.Impossible or
                WorkNodeStatus.Superseded,
            WorkNodeStatus.Satisfied => candidate is WorkNodeStatus.Satisfied or WorkNodeStatus.Superseded,
            WorkNodeStatus.Impossible => candidate is WorkNodeStatus.Impossible or WorkNodeStatus.Superseded,
            WorkNodeStatus.Superseded => candidate == WorkNodeStatus.Superseded,
            _ => false
        };

    private static bool IsTerminal(WorkNodeStatus status) =>
        status is WorkNodeStatus.Satisfied or WorkNodeStatus.Impossible or WorkNodeStatus.Superseded;

    private static bool NodeEquivalent(WorkNode left, WorkNode right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Objective, right.Objective, StringComparison.Ordinal) &&
        string.Equals(left.ParentId, right.ParentId, StringComparison.Ordinal) &&
        left.Status == right.Status &&
        ArraysEqual(left.DependsOn, right.DependsOn) &&
        ArraysEqual(left.EvidenceIds, right.EvidenceIds) &&
        string.Equals(left.SupersededById, right.SupersededById, StringComparison.Ordinal);

    internal static bool ArraysEqual(
        ImmutableArray<string> left,
        ImmutableArray<string> right) =>
        left.IsDefault == right.IsDefault &&
        (left.IsDefault || left.SequenceEqual(right, StringComparer.Ordinal));

    private static ImmutableArray<string> Clone(ImmutableArray<string> values)
    {
        var builder = ImmutableArray.CreateBuilder<string>(values.Length);
        foreach (var value in values)
        {
            builder.Add(value);
        }

        return builder.MoveToImmutable();
    }

    private static WorkGraphApplyResult Rejected(
        WorkGraphSnapshot current,
        IEnumerable<string> errors,
        WorkGraphApplyDiagnostics diagnostics) =>
        new(
            Accepted: false,
            Changed: false,
            Snapshot: current,
            Errors: errors
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray())
        {
            Diagnostics = diagnostics
        };
}
