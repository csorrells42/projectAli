using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Orchestration.Evidence;

public sealed record EvidenceArtifactDraft(
    string ArtifactId,
    string Kind,
    string? BeforeVersion,
    string? AfterVersion);

public sealed record EvidenceArtifactReference(
    string ArtifactIdDigest,
    string Kind,
    string? BeforeVersionDigest,
    string? AfterVersionDigest);

public sealed record EvidencePermissionMetadata(
    string Decision,
    string Scope);

public sealed record EvidenceSourceMetadata(
    string Kind,
    string ProviderId,
    string TrustBoundary,
    DateTimeOffset? FreshAtUtc,
    string StateRevision = "unknown");

public sealed record EvidenceSourceProjection(
    string Kind,
    string ProviderIdDigest,
    string TrustBoundary,
    DateTimeOffset? FreshAtUtc,
    string StateRevisionDigest);

public sealed record ProtectedEvidenceIdentity(
    string CallId,
    string ToolName,
    string CapabilityGroup,
    string ProviderId,
    string RegistryRevision,
    string StableOutcomeCode,
    string? FailureCode,
    EvidenceArtifactDraft[] Artifacts,
    EvidenceSourceMetadata Source);

/// <summary>
/// Full tool evidence supplied to the shadow ledger. JsonElement values are cloned before any
/// asynchronous work starts and are written only to the CurrentUser-protected payload.
/// </summary>
public sealed record EvidenceDraft
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public required string CapabilityGroup { get; init; }

    public required string ProviderId { get; init; }

    public required string RegistryRevision { get; init; }

    public required string EffectKind { get; init; }

    public JsonElement Arguments { get; init; }

    public JsonElement Result { get; init; }

    /// <summary>
    /// Adapter-supplied stable target/effect identity. It must exclude display prose and timestamps.
    /// The ledger stores only a keyed digest; the complete value stays protected.
    /// </summary>
    public required JsonElement NormalizedTarget { get; init; }

    /// <summary>
    /// Adapter-supplied stable outcome material. It must exclude display prose and timestamps.
    /// The ledger stores only a keyed digest; the complete value stays protected.
    /// </summary>
    public required JsonElement NormalizedEffectResult { get; init; }

    public required ToolInvocationOutcome Outcome { get; init; }

    public required string StableOutcomeCode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset CompletedAtUtc { get; init; }

    public EvidenceArtifactDraft[] Artifacts { get; init; } = [];

    public EvidencePermissionMetadata Permission { get; init; } =
        new("unknown", "unknown");

    public JsonElement ProtectedPermissionReceipt { get; init; }

    public EvidenceSourceMetadata Source { get; init; } =
        new("tool", "unknown", "unknown", null);

    public JsonElement ProtectedProvenance { get; init; }
}

public sealed class EvidenceRecord
{
    internal EvidenceRecord(StoredEvidenceRecord stored)
    {
        EvidenceId = stored.EvidenceId;
        TurnBindingDigest = stored.TurnBindingDigest;
        CallIdDigest = stored.CallIdDigest;
        ToolNameDigest = stored.ToolNameDigest;
        CapabilityGroupDigest = stored.CapabilityGroupDigest;
        ProviderIdDigest = stored.ProviderIdDigest;
        RegistryRevisionDigest = stored.RegistryRevisionDigest;
        EffectKind = stored.EffectKind;
        ArgumentsDigest = stored.ArgumentsDigest;
        TargetDigest = stored.TargetDigest;
        NormalizedResultDigest = stored.NormalizedResultDigest;
        InvocationStatus = stored.InvocationStatus;
        DomainOutcome = stored.DomainOutcome;
        FailureCodeDigest = stored.FailureCodeDigest;
        StableOutcomeCodeDigest = stored.StableOutcomeCodeDigest;
        ResultDigest = stored.ResultDigest;
        NoEffectFingerprint = stored.NoEffectFingerprint;
        StartedAtUtc = stored.StartedAtUtc;
        CompletedAtUtc = stored.CompletedAtUtc;
        RecordedAtUtc = stored.RecordedAtUtc;
        Artifacts = Array.AsReadOnly(stored.Artifacts.Select(item => item with { }).ToArray());
        Permission = stored.Permission with { };
        PermissionReceiptDigest = stored.PermissionReceiptDigest;
        Source = stored.Source with { };
        ProtectedPayloadReference = stored.ProtectedPayloadReference;
        ProtectedPayloadDigest = stored.ProtectedPayloadDigest;
        MetadataDigest = stored.MetadataDigest;
        ProjectionDigest = stored.ProjectionDigest;
        RecordMac = stored.RecordMac;
    }

    public string EvidenceId { get; }
    public string TurnBindingDigest { get; }
    public string CallIdDigest { get; }
    public string ToolNameDigest { get; }
    public string CapabilityGroupDigest { get; }
    public string ProviderIdDigest { get; }
    public string RegistryRevisionDigest { get; }
    public string EffectKind { get; }
    public string ArgumentsDigest { get; }
    public string TargetDigest { get; }
    public string NormalizedResultDigest { get; }
    public InvocationStatus InvocationStatus { get; }
    public DomainOutcome DomainOutcome { get; }
    public string? FailureCodeDigest { get; }
    public string StableOutcomeCodeDigest { get; }
    public string ResultDigest { get; }
    public string NoEffectFingerprint { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public IReadOnlyList<EvidenceArtifactReference> Artifacts { get; }
    public EvidencePermissionMetadata Permission { get; }
    public string PermissionReceiptDigest { get; }
    public EvidenceSourceProjection Source { get; }
    public string ProtectedPayloadReference { get; }
    public string ProtectedPayloadDigest { get; }
    public string MetadataDigest { get; }
    public string ProjectionDigest { get; }
    public string RecordMac { get; }
}

internal sealed record StoredEvidenceRecord(
    string EvidenceId,
    string TurnBindingDigest,
    string CallIdDigest,
    string ToolNameDigest,
    string CapabilityGroupDigest,
    string ProviderIdDigest,
    string RegistryRevisionDigest,
    string EffectKind,
    string ArgumentsDigest,
    string TargetDigest,
    string NormalizedResultDigest,
    InvocationStatus InvocationStatus,
    DomainOutcome DomainOutcome,
    string? FailureCodeDigest,
    string StableOutcomeCodeDigest,
    string ResultDigest,
    string NoEffectFingerprint,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset RecordedAtUtc,
    EvidenceArtifactReference[] Artifacts,
    EvidencePermissionMetadata Permission,
    string PermissionReceiptDigest,
    EvidenceSourceProjection Source,
    string ProtectedPayloadReference,
    string ProtectedPayloadDigest,
    string MetadataDigest,
    string ProjectionDigest,
    string RecordMac);

public sealed class EvidenceCursorRecord
{
    internal EvidenceCursorRecord(long cursor, EvidenceRecord evidence, string checksum)
    {
        Cursor = cursor;
        Evidence = evidence;
        Checksum = checksum;
    }

    public long Cursor { get; }
    public EvidenceRecord Evidence { get; }
    public string Checksum { get; }
}

internal sealed record StoredEvidenceCursorRecord(
    long Cursor,
    StoredEvidenceRecord Evidence,
    string Checksum);

public sealed record ProtectedEvidenceContent(
    ProtectedEvidenceIdentity Identity,
    JsonElement Arguments,
    JsonElement Result,
    JsonElement NormalizedTarget,
    JsonElement NormalizedEffectResult,
    JsonElement PermissionReceipt,
    JsonElement Provenance);

internal sealed record ProtectedEvidencePayload(
    ProtectedEvidenceIdentity Identity,
    JsonElement Arguments,
    JsonElement Result,
    JsonElement NormalizedTarget,
    JsonElement NormalizedEffectResult,
    JsonElement PermissionReceipt,
    JsonElement Provenance);
