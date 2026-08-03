using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.Planning;

/// <summary>
/// Read-only, stable-cursor access to durable evidence that did not fit in the current prompt.
/// Implementations must bind every returned page to one immutable evidence-journal snapshot.
/// </summary>
internal interface IAliPlanningEvidencePager
{
    Task<AliPlanningEvidencePage> ReadEvidencePageAsync(
        long afterCursor,
        long? snapshotCursor,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed record AliPlanningEvidencePageItem(
    long Cursor,
    string RecordDigest,
    AcceptedEvidenceProjection Evidence,
    string CapabilityGroup,
    string ProviderId,
    string RegistryRevision,
    string EffectKind,
    string StableOutcomeCode,
    string? FailureCode,
    IReadOnlyList<EvidenceArtifactDraft> Artifacts,
    EvidencePermissionMetadata Permission,
    EvidenceSourceMetadata Source,
    string NoEffectFingerprint,
    string TargetDigest,
    string NormalizedResultDigest,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset RecordedAtUtc);

public sealed record AliPlanningEvidencePage(
    long RequestedAfterCursor,
    long SnapshotCursor,
    long CurrentCursor,
    bool SnapshotAvailable,
    long? NextCursor,
    bool HasMore,
    IReadOnlyList<AliPlanningEvidencePageItem> Items);

public sealed record AliPlanningWorkPageItem(
    string WorkItemId,
    string Outcome,
    string Status,
    string? ParentId,
    string? SupersededById,
    IReadOnlyList<string> DependencyIds,
    IReadOnlyList<string> EvidenceIds);

public sealed record AliPlanningWorkPage(
    string? RequestedAfterWorkItemId,
    long SnapshotRevision,
    long CurrentRevision,
    bool SnapshotAvailable,
    string? NextWorkItemId,
    bool HasMore,
    IReadOnlyList<AliPlanningWorkPageItem> Items);
