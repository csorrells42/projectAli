using System.Collections.Immutable;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ali.Modules.Orchestration.Completion;

internal sealed record AliCompletionCriticSteeringConstraint
{
    internal AliCompletionCriticSteeringConstraint(long sequence, string text)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Sequence = sequence;
        Text = text;
    }

    internal long Sequence { get; }

    internal string Text { get; }
}

internal enum AliCompletionCriticIssueKind
{
    Failure,
    PermissionDenied,
    PermissionRequired
}

internal sealed record AliCompletionCriticUnresolvedIssue
{
    internal AliCompletionCriticUnresolvedIssue(
        string issueId,
        AliCompletionCriticIssueKind kind,
        string summary,
        IEnumerable<string>? evidenceIds = null)
    {
        TurnStateIntegrity.RequireBoundedValue(issueId, 256, nameof(issueId));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TurnStateIntegrity.RequireBoundedValue(summary, 4_000, nameof(summary));
        var exactEvidenceIds = SnapshotIdentifiers(evidenceIds, nameof(evidenceIds));
        IssueId = issueId;
        Kind = kind;
        Summary = summary;
        EvidenceIds = Array.AsReadOnly(exactEvidenceIds);
    }

    internal string IssueId { get; }

    internal AliCompletionCriticIssueKind Kind { get; }

    internal string Summary { get; }

    internal IReadOnlyList<string> EvidenceIds { get; }

    private static string[] SnapshotIdentifiers(
        IEnumerable<string>? source,
        string parameterName)
    {
        var values = (source ?? [])
            .Select(static value => value ?? string.Empty)
            .ToArray();
        if (values.Length > 256
            || values.Any(static value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 256)
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException(
                "Issue evidence identifiers must be unique bounded values.",
                parameterName);
        }

        return values;
    }
}

internal sealed record AliCompletionCriticCapability
{
    internal AliCompletionCriticCapability(
        string capabilityId,
        string toolName,
        string description,
        string availability,
        string permissionRequirement)
    {
        TurnStateIntegrity.RequireBoundedValue(capabilityId, 256, nameof(capabilityId));
        TurnStateIntegrity.RequireBoundedValue(toolName, 256, nameof(toolName));
        TurnStateIntegrity.RequireBoundedValue(description, 4_000, nameof(description));
        TurnStateIntegrity.RequireBoundedValue(availability, 512, nameof(availability));
        TurnStateIntegrity.RequireBoundedValue(
            permissionRequirement,
            512,
            nameof(permissionRequirement));
        CapabilityId = capabilityId;
        ToolName = toolName;
        Description = description;
        Availability = availability;
        PermissionRequirement = permissionRequirement;
    }

    internal string CapabilityId { get; }

    internal string ToolName { get; }

    internal string Description { get; }

    internal string Availability { get; }

    internal string PermissionRequirement { get; }
}

internal sealed record AliCompletionCriticClaimCoverage
{
    internal AliCompletionCriticClaimCoverage(
        string claimId,
        string statement,
        MaterialClaimKind kind,
        IEnumerable<string>? evidenceIds)
    {
        TurnStateIntegrity.RequireBoundedValue(claimId, 256, nameof(claimId));
        TurnStateIntegrity.RequireBoundedValue(statement, 4_000, nameof(statement));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var exactEvidenceIds = (evidenceIds ?? [])
            .Select(static value => value ?? string.Empty)
            .ToArray();
        if (exactEvidenceIds.Length > 256
            || exactEvidenceIds.Any(static value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 256)
            || exactEvidenceIds.Distinct(StringComparer.Ordinal).Count()
                != exactEvidenceIds.Length)
        {
            throw new ArgumentException(
                "Claim evidence identifiers must be unique bounded values.",
                nameof(evidenceIds));
        }

        ClaimId = claimId;
        Statement = statement;
        Kind = kind;
        EvidenceIds = Array.AsReadOnly(exactEvidenceIds);
    }

    internal string ClaimId { get; }

    internal string Statement { get; }

    internal MaterialClaimKind Kind { get; }

    internal IReadOnlyList<string> EvidenceIds { get; }
}

/// <summary>
/// Immutable, state-native input for one completion review. Every collection is snapshotted before
/// a review identity is calculated, so caller mutation cannot rebind an authorized critic call.
/// </summary>
internal sealed record AliCompletionCriticRequest
{
    internal AliCompletionCriticRequest(
        long stateRevision,
        string immutableOriginalRequest,
        IEnumerable<AliCompletionCriticSteeringConstraint>? acceptedSteeringConstraints,
        CompletionPlan completionPlan,
        AliCommittedAnswerDraft committedDraft,
        WorkGraphSnapshot authoritativeWorkGraph,
        IEnumerable<AliCompletionCriticClaimCoverage>? claimEvidenceCoverage,
        IEnumerable<AcceptedEvidenceProjection>? citedEvidence,
        IEnumerable<AliCompletionCriticUnresolvedIssue>? unresolvedIssues,
        IEnumerable<AliCompletionCriticCapability>? relevantCapabilities)
    {
        if (stateRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateRevision));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(immutableOriginalRequest);
        ArgumentNullException.ThrowIfNull(completionPlan);
        ArgumentNullException.ThrowIfNull(committedDraft);
        ArgumentNullException.ThrowIfNull(authoritativeWorkGraph);

        StateRevision = stateRevision;
        ImmutableOriginalRequest = immutableOriginalRequest;
        AcceptedSteeringConstraints = Array.AsReadOnly(
            (acceptedSteeringConstraints ?? [])
            .OrderBy(static item => item.Sequence)
            .ToArray());
        CompletionPlan = Snapshot(completionPlan);
        CommittedDraft = Snapshot(committedDraft);
        AuthoritativeWorkGraph = Snapshot(authoritativeWorkGraph);
        ClaimEvidenceCoverage = Array.AsReadOnly(
            (claimEvidenceCoverage ?? [])
            .OrderBy(static item => item.ClaimId, StringComparer.Ordinal)
            .ToArray());
        CitedEvidence = Array.AsReadOnly(
            (citedEvidence ?? [])
            .OrderBy(static item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray());
        UnresolvedIssues = Array.AsReadOnly(
            (unresolvedIssues ?? [])
            .OrderBy(static item => item.IssueId, StringComparer.Ordinal)
            .ToArray());
        RelevantCapabilities = Array.AsReadOnly(
            (relevantCapabilities ?? [])
            .OrderBy(static item => item.CapabilityId, StringComparer.Ordinal)
            .ToArray());

        ValidateCollectionIdentities();
        AliCompletionCriticAnswerIntegrity.Validate(CompletionPlan, CommittedDraft);
    }

    internal long StateRevision { get; }

    internal string ImmutableOriginalRequest { get; }

    internal IReadOnlyList<AliCompletionCriticSteeringConstraint> AcceptedSteeringConstraints { get; }

    internal CompletionPlan CompletionPlan { get; }

    internal AliCommittedAnswerDraft CommittedDraft { get; }

    internal WorkGraphSnapshot AuthoritativeWorkGraph { get; }

    internal IReadOnlyList<AliCompletionCriticClaimCoverage> ClaimEvidenceCoverage { get; }

    internal IReadOnlyList<AcceptedEvidenceProjection> CitedEvidence { get; }

    internal IReadOnlyList<AliCompletionCriticUnresolvedIssue> UnresolvedIssues { get; }

    internal IReadOnlyList<AliCompletionCriticCapability> RelevantCapabilities { get; }

    private void ValidateCollectionIdentities()
    {
        if (AcceptedSteeringConstraints.Count > 512
            || AcceptedSteeringConstraints
                .Select(static item => item.Sequence)
                .Distinct()
                .Count() != AcceptedSteeringConstraints.Count)
        {
            throw new ArgumentException(
                "Accepted steering sequences must be unique and bounded.",
                nameof(AcceptedSteeringConstraints));
        }

        RequireUniqueBoundedCount(
            ClaimEvidenceCoverage,
            static item => item.ClaimId,
            256,
            nameof(ClaimEvidenceCoverage));
        RequireUniqueBoundedCount(
            CitedEvidence,
            static item => item.EvidenceId,
            1_024,
            nameof(CitedEvidence));
        RequireUniqueBoundedCount(
            UnresolvedIssues,
            static item => item.IssueId,
            256,
            nameof(UnresolvedIssues));
        RequireUniqueBoundedCount(
            RelevantCapabilities,
            static item => item.CapabilityId,
            1_024,
            nameof(RelevantCapabilities));

        var knownEvidence = CitedEvidence
            .Select(static item => item.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        if (ClaimEvidenceCoverage.Any(item =>
                item.EvidenceIds.Any(evidenceId => !knownEvidence.Contains(evidenceId)))
            || UnresolvedIssues.Any(item =>
                item.EvidenceIds.Any(evidenceId => !knownEvidence.Contains(evidenceId))))
        {
            throw new ArgumentException(
                "Critic coverage and unresolved issues may cite only supplied accepted evidence.");
        }

        var requiredClaimIds = CompletionPlan.RequiredClaimIds.ToHashSet(StringComparer.Ordinal);
        if (ClaimEvidenceCoverage.Any(item => !requiredClaimIds.Contains(item.ClaimId))
            || requiredClaimIds.Any(requiredId =>
                ClaimEvidenceCoverage.All(item => !string.Equals(
                    item.ClaimId,
                    requiredId,
                    StringComparison.Ordinal))))
        {
            throw new ArgumentException(
                "The critic claim coverage must exactly cover the completion plan claim IDs.");
        }
    }

    private static void RequireUniqueBoundedCount<T>(
        IReadOnlyList<T> values,
        Func<T, string> identity,
        int maximumCount,
        string parameterName)
    {
        if (values.Count > maximumCount
            || values.Select(identity).Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new ArgumentException(
                "Critic projections must have unique identities and remain within protocol bounds.",
                parameterName);
        }
    }

    private static CompletionPlan Snapshot(CompletionPlan value) =>
        new(
            value.AnswerId,
            value.CompletionKind,
            value.RequiredOutcomeIds,
            value.RequiredClaimIds,
            value.Bindings.Select(static binding =>
                new CompletionEvidenceBinding(binding.SubjectId, binding.EvidenceIds)),
            value.RequestedFormat,
            value.RequestedSections);

    private static AliCommittedAnswerDraft Snapshot(AliCommittedAnswerDraft value) =>
        new(
            value.AnswerId,
            value.Text,
            value.AnswerDigest,
            Array.AsReadOnly(value.Segments.Select(static segment =>
                new AliCommittedAnswerSegment(
                    segment.AnswerId,
                    segment.Sequence,
                    segment.PreviousSegmentHash,
                    segment.SegmentHash,
                    segment.CommittedOffset,
                    segment.Text,
                    Array.AsReadOnly(segment.CoveredClaimIds.ToArray())))
                .ToArray()),
            Array.AsReadOnly(value.CoveredClaimIds.ToArray()));

    private static WorkGraphSnapshot Snapshot(WorkGraphSnapshot value) =>
        new(
            value.Revision,
            value.Nodes
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToImmutableDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value with
                    {
                        DependsOn = pair.Value.DependsOn.ToImmutableArray(),
                        EvidenceIds = pair.Value.EvidenceIds.ToImmutableArray()
                    },
                    StringComparer.Ordinal));
}

internal sealed record AliCompletionCriticVerdict
{
    internal const int MaximumAggregateOutcomeCharacters = 16_000;

    internal AliCompletionCriticVerdict(
        bool complete,
        string basis,
        IEnumerable<string>? materialUnmetOutcomes)
    {
        TurnStateIntegrity.RequireBoundedValue(basis, 4_000, nameof(basis));
        var outcomes = (materialUnmetOutcomes ?? [])
            .Select(static value => value ?? string.Empty)
            .ToArray();
        if (outcomes.Length > 64
            || outcomes.Any(static value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 2_000)
            || outcomes.Distinct(StringComparer.Ordinal).Count() != outcomes.Length)
        {
            throw new ArgumentException(
                "Material unmet outcomes must be unique bounded values.",
                nameof(materialUnmetOutcomes));
        }

        if (outcomes.Sum(static value => (long)value.Length)
            > MaximumAggregateOutcomeCharacters)
        {
            throw new ArgumentException(
                "Material unmet outcomes exceed the aggregate typed-verdict bound.",
                nameof(materialUnmetOutcomes));
        }

        if (complete != (outcomes.Length == 0))
        {
            throw new ArgumentException(
                "A complete verdict has no unmet outcomes, while an incomplete verdict has at least one.",
                nameof(materialUnmetOutcomes));
        }

        Complete = complete;
        Basis = basis;
        MaterialUnmetOutcomes = Array.AsReadOnly(outcomes);
    }

    internal bool Complete { get; }

    internal string Basis { get; }

    internal IReadOnlyList<string> MaterialUnmetOutcomes { get; }
}

internal sealed record AliCompletionCriticReviewIdentity(
    string Digest,
    long SourceStateRevision,
    string AnswerId,
    string AnswerDigest,
    IReadOnlyList<string> RenderedPageHashes,
    string RuntimeDigest,
    string ModelDigest,
    string GenerationSettingsDigest,
    string ReasoningEffort)
{
    internal void Validate()
    {
        TurnStateIntegrity.RequireDigest(Digest, nameof(Digest));
        if (SourceStateRevision < 0)
        {
            throw new InvalidDataException("A critic source-state revision cannot be negative.");
        }

        TurnStateIntegrity.RequireBoundedValue(AnswerId, 256, nameof(AnswerId));
        TurnStateIntegrity.RequireDigest(AnswerDigest, nameof(AnswerDigest));
        TurnStateIntegrity.RequireDigest(RuntimeDigest, nameof(RuntimeDigest));
        TurnStateIntegrity.RequireDigest(ModelDigest, nameof(ModelDigest));
        TurnStateIntegrity.RequireDigest(GenerationSettingsDigest, nameof(GenerationSettingsDigest));
        TurnStateIntegrity.RequireBoundedValue(ReasoningEffort, 128, nameof(ReasoningEffort));
        if (RenderedPageHashes.Count == 0 || RenderedPageHashes.Count > 256)
        {
            throw new InvalidDataException("A critic identity requires one to 256 rendered page hashes.");
        }

        foreach (var hash in RenderedPageHashes)
        {
            TurnStateIntegrity.RequireDigest(hash, nameof(RenderedPageHashes));
        }
    }
}

internal enum AliCompletionCriticFailureKind
{
    DispatchBindingsChanged,
    InputNotAdmitted,
    OutputIncomplete,
    IdenticalReviewInProgress,
    ReviewStoreFailure
}

internal sealed record AliCompletionCriticFailure(
    AliCompletionCriticFailureKind Kind,
    string UserVisibleMessage,
    IReadOnlyList<string>? ChangedBindings = null);

internal sealed record AliCompletionCriticStoreSnapshot(
    long StateRevision,
    string? AuthoritativeStateProjection = null);

internal sealed record AliCompletionCriticAttempt(
    AliCompletionCriticReviewIdentity Identity,
    AliCompletionCriticVerdict? Verdict,
    AliCompletionCriticFailure? Failure,
    AliCompletionCriticStoreSnapshot? StoreSnapshot,
    bool ReusedCommittedVerdict,
    int ModelCallCount)
{
    internal bool IsSuccessful => Verdict is not null && Failure is null;

    internal static AliCompletionCriticAttempt Completed(
        AliCompletionCriticReviewIdentity identity,
        AliCompletionCriticVerdict verdict,
        AliCompletionCriticStoreSnapshot storeSnapshot,
        bool reusedCommittedVerdict,
        int modelCallCount) =>
        new(
            identity,
            verdict,
            Failure: null,
            storeSnapshot,
            reusedCommittedVerdict,
            modelCallCount);

    internal static AliCompletionCriticAttempt Failed(
        AliCompletionCriticReviewIdentity identity,
        AliCompletionCriticFailure failure,
        AliCompletionCriticStoreSnapshot? storeSnapshot = null,
        int modelCallCount = 0) =>
        new(
            identity,
            Verdict: null,
            failure,
            storeSnapshot,
            ReusedCommittedVerdict: false,
            modelCallCount);
}

internal sealed record AliCompletionCriticPreparationAttempt(
    AliCompletionCriticPreparedReview? PreparedReview,
    AliCompletionCriticFailure? Failure)
{
    internal bool IsSuccessful => PreparedReview is not null && Failure is null;

    internal static AliCompletionCriticPreparationAttempt Prepared(
        AliCompletionCriticPreparedReview prepared) =>
        new(prepared, Failure: null);

    internal static AliCompletionCriticPreparationAttempt Failed(
        AliCompletionCriticFailure failure) =>
        new(PreparedReview: null, failure);
}

internal sealed record AliCompletionCriticPreparedReview(
    AliCompletionCriticRequest Request,
    AliCompletionCriticReviewIdentity Identity,
    BoundModelDispatchSnapshot Dispatch,
    TurnRuntimeBindings DispatchBindings,
    IReadOnlyList<IReadOnlyList<MeaiChatMessage>> PageAuditMessages,
    IReadOnlyList<MeaiChatMessage>? SinglePageFinalMessages);
