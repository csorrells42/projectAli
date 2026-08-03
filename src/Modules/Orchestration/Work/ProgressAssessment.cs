using System.Collections.Immutable;
using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Orchestration.Work;

public sealed record ActionIdentity
{
    private ActionIdentity(
        string registryRevision,
        string workItemId,
        string toolName,
        string argumentsDigest,
        string targetVersionsDigest,
        string permissionStateDigest,
        string dependencyStateDigest,
        string fingerprint)
    {
        RegistryRevision = registryRevision;
        WorkItemId = workItemId;
        ToolName = toolName;
        ArgumentsDigest = argumentsDigest;
        TargetVersionsDigest = targetVersionsDigest;
        PermissionStateDigest = permissionStateDigest;
        DependencyStateDigest = dependencyStateDigest;
        Fingerprint = fingerprint;
    }

    public string RegistryRevision { get; }

    public string WorkItemId { get; }

    public string ToolName { get; }

    public string ArgumentsDigest { get; }

    public string TargetVersionsDigest { get; }

    public string PermissionStateDigest { get; }

    public string DependencyStateDigest { get; }

    public string Fingerprint { get; }

    public static ActionIdentity Create(
        string registryRevision,
        string workItemId,
        string toolName,
        ReadOnlySpan<byte> argumentsJsonUtf8,
        IReadOnlyDictionary<string, string>? targetVersions,
        string permissionState,
        string dependencyState)
    {
        RequireIdentityPart(registryRevision, nameof(registryRevision));
        RequireIdentityPart(workItemId, nameof(workItemId));
        RequireIdentityPart(toolName, nameof(toolName));
        RequireIdentityPart(permissionState, nameof(permissionState));
        RequireIdentityPart(dependencyState, nameof(dependencyState));

        var argumentsDigest = WorkIdentityCanonicalizer.CanonicalJsonDigest(argumentsJsonUtf8);
        var targetVersionsDigest = WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            targetVersions);
        var permissionStateDigest = WorkIdentityCanonicalizer.DigestParts(
            "action-permission-state-v1",
            permissionState);
        var dependencyStateDigest = WorkIdentityCanonicalizer.DigestParts(
            "action-dependency-state-v1",
            dependencyState);
        var fingerprint = WorkIdentityCanonicalizer.DigestParts(
            "action-identity-v1",
            registryRevision,
            workItemId,
            toolName,
            argumentsDigest,
            targetVersionsDigest,
            permissionStateDigest,
            dependencyStateDigest);

        return new ActionIdentity(
            registryRevision,
            workItemId,
            toolName,
            argumentsDigest,
            targetVersionsDigest,
            permissionStateDigest,
            dependencyStateDigest,
            fingerprint);
    }

    private static void RequireIdentityPart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Action identity parts cannot be blank.", parameterName);
        }
    }
}

public sealed record EffectIdentity
{
    private EffectIdentity(string effectKind, string targetsDigest, string fingerprint)
    {
        EffectKind = effectKind;
        TargetsDigest = targetsDigest;
        Fingerprint = fingerprint;
    }

    public string EffectKind { get; }

    public string TargetsDigest { get; }

    public string Fingerprint { get; }

    public static EffectIdentity Create(
        string effectKind,
        IReadOnlyDictionary<string, string>? targets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectKind);
        var targetsDigest = WorkIdentityCanonicalizer.MapDigest("effect-targets-v1", targets);
        return new EffectIdentity(
            effectKind,
            targetsDigest,
            WorkIdentityCanonicalizer.DigestParts(
                "effect-identity-v1",
                effectKind,
                targetsDigest));
    }
}

public enum EffectResultKind
{
    Applied,
    NoEffect
}

public sealed record EffectOutcomeIdentity
{
    private EffectOutcomeIdentity(
        EffectResultKind resultKind,
        string outcomeFingerprint,
        string? noEffectFingerprint)
    {
        ResultKind = resultKind;
        OutcomeFingerprint = outcomeFingerprint;
        NoEffectFingerprint = noEffectFingerprint;
    }

    public EffectResultKind ResultKind { get; }

    public string OutcomeFingerprint { get; }

    public string? NoEffectFingerprint { get; }

    public static EffectOutcomeIdentity Create(
        ToolInvocationOutcome invocation,
        EffectResultKind resultKind,
        string normalizedCode,
        IReadOnlyDictionary<string, string>? stableFields = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedCode);
        if (!Enum.IsDefined(resultKind))
        {
            throw new ArgumentOutOfRangeException(nameof(resultKind));
        }

        var stableFieldsDigest = WorkIdentityCanonicalizer.MapDigest(
            "effect-outcome-stable-fields-v1",
            stableFields);
        var outcomeFingerprint = WorkIdentityCanonicalizer.DigestParts(
            "effect-outcome-v1",
            ((int)resultKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)invocation.InvocationStatus).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)invocation.DomainOutcome).ToString(System.Globalization.CultureInfo.InvariantCulture),
            invocation.FailureCode ?? string.Empty,
            normalizedCode,
            stableFieldsDigest);
        var noEffectFingerprint = resultKind == EffectResultKind.NoEffect
            ? WorkIdentityCanonicalizer.DigestParts(
                "equivalent-no-effect-v1",
                normalizedCode,
                stableFieldsDigest)
            : null;

        return new EffectOutcomeIdentity(
            resultKind,
            outcomeFingerprint,
            noEffectFingerprint);
    }
}

public sealed record ProgressVector
{
    private ProgressVector(
        long evidenceCursor,
        long workGraphRevision,
        string outcomeCoverageDigest,
        string artifactVersionsDigest,
        string diagnosticStateDigest,
        string testStateDigest,
        string permissionStateDigest,
        string dependencyStateDigest,
        string materialFingerprint)
    {
        EvidenceCursor = evidenceCursor;
        WorkGraphRevision = workGraphRevision;
        OutcomeCoverageDigest = outcomeCoverageDigest;
        ArtifactVersionsDigest = artifactVersionsDigest;
        DiagnosticStateDigest = diagnosticStateDigest;
        TestStateDigest = testStateDigest;
        PermissionStateDigest = permissionStateDigest;
        DependencyStateDigest = dependencyStateDigest;
        MaterialFingerprint = materialFingerprint;
    }

    public long EvidenceCursor { get; }

    public long WorkGraphRevision { get; }

    public string OutcomeCoverageDigest { get; }

    public string ArtifactVersionsDigest { get; }

    public string DiagnosticStateDigest { get; }

    public string TestStateDigest { get; }

    public string PermissionStateDigest { get; }

    public string DependencyStateDigest { get; }

    public string MaterialFingerprint { get; }

    public static ProgressVector Create(
        long evidenceCursor,
        long workGraphRevision,
        IEnumerable<string>? satisfiedOutcomeIds = null,
        IReadOnlyDictionary<string, string>? artifactVersions = null,
        IReadOnlyDictionary<string, string>? diagnosticStates = null,
        IReadOnlyDictionary<string, string>? testStates = null,
        IReadOnlyDictionary<string, string>? permissionStates = null,
        IReadOnlyDictionary<string, string>? dependencyStates = null)
    {
        if (evidenceCursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceCursor));
        }

        if (workGraphRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workGraphRevision));
        }

        var outcomeCoverageDigest = WorkIdentityCanonicalizer.SetDigest(
            "progress-outcome-coverage-v1",
            satisfiedOutcomeIds);
        var artifactVersionsDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-artifact-versions-v1",
            artifactVersions);
        var diagnosticStateDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-diagnostic-state-v1",
            diagnosticStates);
        var testStateDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-test-state-v1",
            testStates);
        var permissionStateDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-permission-state-v1",
            permissionStates);
        var dependencyStateDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-dependency-state-v1",
            dependencyStates);
        return CreateFromDigests(
            evidenceCursor,
            workGraphRevision,
            outcomeCoverageDigest,
            artifactVersionsDigest,
            diagnosticStateDigest,
            testStateDigest,
            permissionStateDigest,
            dependencyStateDigest);
    }

    internal static ProgressVector CreateFromWorkGraphAnalysis(
        long evidenceCursor,
        WorkGraphSnapshotAnalysis workGraph,
        IReadOnlyDictionary<string, string>? artifactVersions = null,
        IReadOnlyDictionary<string, string>? diagnosticStates = null,
        IReadOnlyDictionary<string, string>? testStates = null,
        IReadOnlyDictionary<string, string>? permissionStates = null)
    {
        ArgumentNullException.ThrowIfNull(workGraph);
        if (!workGraph.IsValid)
        {
            throw new ArgumentException(
                "Progress cannot be measured from an invalid work-graph analysis.",
                nameof(workGraph));
        }

        if (evidenceCursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceCursor));
        }

        var artifactVersionsDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-artifact-versions-v1",
            artifactVersions);
        var diagnosticStateDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-diagnostic-state-v1",
            diagnosticStates);
        var testStateDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-test-state-v1",
            testStates);
        var permissionStateDigest = WorkIdentityCanonicalizer.MapDigest(
            "progress-permission-state-v1",
            permissionStates);
        return CreateFromDigests(
            evidenceCursor,
            workGraph.Snapshot.Revision,
            workGraph.OutcomeCoverageDigest,
            artifactVersionsDigest,
            diagnosticStateDigest,
            testStateDigest,
            permissionStateDigest,
            workGraph.ProgressDependencyStateDigest);
    }

    private static ProgressVector CreateFromDigests(
        long evidenceCursor,
        long workGraphRevision,
        string outcomeCoverageDigest,
        string artifactVersionsDigest,
        string diagnosticStateDigest,
        string testStateDigest,
        string permissionStateDigest,
        string dependencyStateDigest)
    {
        var materialFingerprint = WorkIdentityCanonicalizer.DigestParts(
            "progress-material-state-v1",
            outcomeCoverageDigest,
            artifactVersionsDigest,
            diagnosticStateDigest,
            testStateDigest,
            permissionStateDigest,
            dependencyStateDigest);

        return new ProgressVector(
            evidenceCursor,
            workGraphRevision,
            outcomeCoverageDigest,
            artifactVersionsDigest,
            diagnosticStateDigest,
            testStateDigest,
            permissionStateDigest,
            dependencyStateDigest,
            materialFingerprint);
    }
}

public enum ProgressDisposition
{
    Advanced,
    ReplanRequired,
    ReopenDecomposition
}

public enum ProgressReason
{
    MaterialStateChanged,
    NoValidatedStateChange,
    ExactActionAlreadyNonAdvancing,
    EquivalentNoEffectRepeated,
    ReportedEffectWithoutValidatedStateChange,
    DistinctNoProgressLimitReached
}

public sealed record PlannedActionAssessment(
    bool CanExecute,
    ProgressReason? Reason,
    string? PriorActionFingerprint);

public sealed record ProgressAssessment(
    ProgressDisposition Disposition,
    ProgressReason Reason,
    ProgressHistory History,
    bool AddedEvidence,
    bool ChangedWorkGraphRevision,
    bool ChangedMaterialState);

public sealed record ProgressAttempt
{
    internal ProgressAttempt(
        ActionIdentity action,
        EffectIdentity effect,
        EffectOutcomeIdentity outcome,
        ProgressVector before,
        ProgressVector after,
        bool materiallyAdvanced)
    {
        Action = action;
        Effect = effect;
        Outcome = outcome;
        Before = before;
        After = after;
        MateriallyAdvanced = materiallyAdvanced;
    }

    public ActionIdentity Action { get; }

    public EffectIdentity Effect { get; }

    public EffectOutcomeIdentity Outcome { get; }

    public ProgressVector Before { get; }

    public ProgressVector After { get; }

    public bool MateriallyAdvanced { get; }
}

public sealed class ProgressHistory
{
    // This is a memory-retention boundary, not an execution-step limit. The durable
    // transition journal remains complete and long-running work can continue while
    // the oldest in-memory diagnostic/loop-detection entries are evicted.
    internal const int MaximumRetainedAttempts = 1024;
    internal const int MaximumDistinctNoProgressIdentitiesPerMaterialState = 256;

    private readonly ImmutableArray<ProgressAttemptFingerprint> _fingerprints;
    // These compact indexes are correctness state, not diagnostic projections. They retain
    // distinct non-advancing identities for only the current material state, so eviction of the
    // bounded projections cannot make a previously rejected action executable again. The
    // explicit per-state ceiling fails closed before an adversarial planner can grow them without
    // bound. Advancing attempts clear both indexes, so long-running productive work remains open.
    private readonly ImmutableHashSet<NonAdvancingActionKey> _nonAdvancingActions;
    private readonly ImmutableHashSet<EquivalentNoEffectKey> _equivalentNoEffects;
    private readonly string? _indexedMaterialFingerprint;

    private ProgressHistory(
        ImmutableArray<ProgressAttempt> attempts,
        ImmutableArray<ProgressAttemptFingerprint> fingerprints,
        ImmutableHashSet<NonAdvancingActionKey> nonAdvancingActions,
        ImmutableHashSet<EquivalentNoEffectKey> equivalentNoEffects,
        string? indexedMaterialFingerprint)
    {
        Attempts = attempts;
        _fingerprints = fingerprints;
        _nonAdvancingActions = nonAdvancingActions;
        _equivalentNoEffects = equivalentNoEffects;
        _indexedMaterialFingerprint = indexedMaterialFingerprint;
    }

    public static ProgressHistory Empty { get; } = new(
        ImmutableArray<ProgressAttempt>.Empty,
        ImmutableArray<ProgressAttemptFingerprint>.Empty,
        ImmutableHashSet<NonAdvancingActionKey>.Empty,
        ImmutableHashSet<EquivalentNoEffectKey>.Empty,
        indexedMaterialFingerprint: null);

    public ImmutableArray<ProgressAttempt> Attempts { get; }

    internal ProgressHistory Append(ProgressAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return AppendFingerprint(
            Attempts.Add(attempt),
            ProgressAttemptFingerprint.From(attempt));
    }

    internal ProgressHistory Restore(ProgressAttemptFingerprint attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return AppendFingerprint(Attempts, attempt);
    }

    internal ImmutableArray<ProgressAttemptFingerprint> Fingerprints => _fingerprints;

    internal int NonAdvancingActionIdentityCount => _nonAdvancingActions.Count;

    internal int EquivalentNoEffectIdentityCount => _equivalentNoEffects.Count;

    internal bool ContainsNonAdvancingAction(string actionFingerprint, string materialFingerprint) =>
        string.Equals(_indexedMaterialFingerprint, materialFingerprint, StringComparison.Ordinal)
        && _nonAdvancingActions.Contains(
            new NonAdvancingActionKey(actionFingerprint, materialFingerprint));

    internal bool ContainsEquivalentNoEffect(
        string effectFingerprint,
        string noEffectFingerprint,
        string materialFingerprint) =>
        string.Equals(_indexedMaterialFingerprint, materialFingerprint, StringComparison.Ordinal)
        && _equivalentNoEffects.Contains(
            new EquivalentNoEffectKey(
                effectFingerprint,
                noEffectFingerprint,
                materialFingerprint));

    internal bool ReachedDistinctNoProgressLimit(string materialFingerprint) =>
        string.Equals(_indexedMaterialFingerprint, materialFingerprint, StringComparison.Ordinal)
        && _nonAdvancingActions.Count >= MaximumDistinctNoProgressIdentitiesPerMaterialState;

    private ProgressHistory AppendFingerprint(
        ImmutableArray<ProgressAttempt> attempts,
        ProgressAttemptFingerprint fingerprint)
    {
        var nonAdvancingActions = _nonAdvancingActions;
        var equivalentNoEffects = _equivalentNoEffects;
        var indexedMaterialFingerprint = _indexedMaterialFingerprint;
        if (fingerprint.MateriallyAdvanced)
        {
            nonAdvancingActions = ImmutableHashSet<NonAdvancingActionKey>.Empty;
            equivalentNoEffects = ImmutableHashSet<EquivalentNoEffectKey>.Empty;
            indexedMaterialFingerprint = fingerprint.AfterMaterialFingerprint;
        }
        else if (!string.Equals(
                     indexedMaterialFingerprint,
                     fingerprint.BeforeMaterialFingerprint,
                     StringComparison.Ordinal))
        {
            nonAdvancingActions = ImmutableHashSet<NonAdvancingActionKey>.Empty;
            equivalentNoEffects = ImmutableHashSet<EquivalentNoEffectKey>.Empty;
            indexedMaterialFingerprint = fingerprint.BeforeMaterialFingerprint;
        }

        if (!fingerprint.MateriallyAdvanced)
        {
            if (nonAdvancingActions.Count < MaximumDistinctNoProgressIdentitiesPerMaterialState)
            {
                nonAdvancingActions = nonAdvancingActions.Add(
                    new NonAdvancingActionKey(
                        fingerprint.ActionFingerprint,
                        fingerprint.BeforeMaterialFingerprint));
                if (fingerprint.NoEffectFingerprint is not null)
                {
                    equivalentNoEffects = equivalentNoEffects.Add(
                        new EquivalentNoEffectKey(
                            fingerprint.EffectFingerprint,
                            fingerprint.NoEffectFingerprint,
                            fingerprint.BeforeMaterialFingerprint));
                }
            }
        }

        return CreateBounded(
            attempts,
            _fingerprints.Add(fingerprint),
            nonAdvancingActions,
            equivalentNoEffects,
            indexedMaterialFingerprint);
    }

    private static ProgressHistory CreateBounded(
        ImmutableArray<ProgressAttempt> attempts,
        ImmutableArray<ProgressAttemptFingerprint> fingerprints,
        ImmutableHashSet<NonAdvancingActionKey> nonAdvancingActions,
        ImmutableHashSet<EquivalentNoEffectKey> equivalentNoEffects,
        string? indexedMaterialFingerprint)
    {
        if (attempts.Length > MaximumRetainedAttempts)
        {
            attempts = attempts.RemoveRange(0, attempts.Length - MaximumRetainedAttempts);
        }

        if (fingerprints.Length > MaximumRetainedAttempts)
        {
            fingerprints = fingerprints.RemoveRange(
                0,
                fingerprints.Length - MaximumRetainedAttempts);
        }

        return new ProgressHistory(
            attempts,
            fingerprints,
            nonAdvancingActions,
            equivalentNoEffects,
            indexedMaterialFingerprint);
    }

    private readonly record struct NonAdvancingActionKey(
        string ActionFingerprint,
        string MaterialFingerprint);

    private readonly record struct EquivalentNoEffectKey(
        string EffectFingerprint,
        string NoEffectFingerprint,
        string MaterialFingerprint);
}

internal sealed record ProgressAttemptFingerprint(
    string ActionFingerprint,
    string EffectFingerprint,
    string? NoEffectFingerprint,
    string BeforeMaterialFingerprint,
    string AfterMaterialFingerprint,
    bool MateriallyAdvanced)
{
    internal static ProgressAttemptFingerprint From(ProgressAttempt attempt) =>
        new(
            attempt.Action.Fingerprint,
            attempt.Effect.Fingerprint,
            attempt.Outcome.NoEffectFingerprint,
            attempt.Before.MaterialFingerprint,
            attempt.After.MaterialFingerprint,
            attempt.MateriallyAdvanced);
}

public static class ProgressDetector
{
    public static PlannedActionAssessment AssessPlannedAction(
        ProgressHistory history,
        ActionIdentity action,
        ProgressVector current)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(current);

        if (history.ContainsNonAdvancingAction(
                action.Fingerprint,
                current.MaterialFingerprint))
        {
            return new PlannedActionAssessment(
                CanExecute: false,
                Reason: ProgressReason.ExactActionAlreadyNonAdvancing,
                PriorActionFingerprint: action.Fingerprint);
        }

        if (history.ReachedDistinctNoProgressLimit(current.MaterialFingerprint))
        {
            return new PlannedActionAssessment(
                CanExecute: false,
                Reason: ProgressReason.DistinctNoProgressLimitReached,
                PriorActionFingerprint: null);
        }

        return new PlannedActionAssessment(
            CanExecute: true,
            Reason: null,
            PriorActionFingerprint: null);
    }

    public static ProgressAssessment Assess(
        ProgressHistory history,
        ActionIdentity action,
        EffectIdentity effect,
        EffectOutcomeIdentity outcome,
        ProgressVector before,
        ProgressVector after)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (after.EvidenceCursor < before.EvidenceCursor)
        {
            throw new ArgumentException("The evidence cursor cannot regress.", nameof(after));
        }

        if (after.WorkGraphRevision < before.WorkGraphRevision)
        {
            throw new ArgumentException("The work-graph revision cannot regress.", nameof(after));
        }

        var materiallyAdvanced = !string.Equals(
            before.MaterialFingerprint,
            after.MaterialFingerprint,
            StringComparison.Ordinal);
        var disposition = ProgressDisposition.Advanced;
        var reason = ProgressReason.MaterialStateChanged;

        if (!materiallyAdvanced)
        {
            if (HasExactNonAdvancingAttempt(history, action, before))
            {
                disposition = ProgressDisposition.ReplanRequired;
                reason = ProgressReason.ExactActionAlreadyNonAdvancing;
            }
            else if (HasEquivalentNoEffectAttempt(history, effect, outcome, before))
            {
                disposition = ProgressDisposition.ReopenDecomposition;
                reason = ProgressReason.EquivalentNoEffectRepeated;
            }
            else if (outcome.ResultKind == EffectResultKind.Applied)
            {
                disposition = ProgressDisposition.ReplanRequired;
                reason = ProgressReason.ReportedEffectWithoutValidatedStateChange;
            }
            else
            {
                disposition = ProgressDisposition.ReplanRequired;
                reason = ProgressReason.NoValidatedStateChange;
            }
        }

        var attempt = new ProgressAttempt(
            action,
            effect,
            outcome,
            before,
            after,
            materiallyAdvanced);
        return new ProgressAssessment(
            disposition,
            reason,
            history.Append(attempt),
            AddedEvidence: after.EvidenceCursor > before.EvidenceCursor,
            ChangedWorkGraphRevision: after.WorkGraphRevision > before.WorkGraphRevision,
            ChangedMaterialState: materiallyAdvanced);
    }

    private static bool HasExactNonAdvancingAttempt(
        ProgressHistory history,
        ActionIdentity action,
        ProgressVector before)
    {
        return history.ContainsNonAdvancingAction(
            action.Fingerprint,
            before.MaterialFingerprint);
    }

    private static bool HasEquivalentNoEffectAttempt(
        ProgressHistory history,
        EffectIdentity effect,
        EffectOutcomeIdentity outcome,
        ProgressVector before)
    {
        if (outcome.NoEffectFingerprint is null)
        {
            return false;
        }

        // Exact repeats are handled first. If this action has not been seen, any matching
        // normalized effect/no-effect identity was necessarily produced by an alternate action.
        return history.ContainsEquivalentNoEffect(
            effect.Fingerprint,
            outcome.NoEffectFingerprint,
            before.MaterialFingerprint);
    }
}
