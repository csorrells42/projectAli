namespace Ali.Modules.UserMemory;

public enum ParticipantReferenceKind
{
    Registered,
    Guest,
    Unknown
}

public enum ParticipantPresenceState
{
    Unknown,
    Present,
    NotPresent
}

public sealed record ParticipantReference(
    string ReferenceId,
    string DisplayLabel,
    ParticipantReferenceKind Kind,
    ParticipantPresenceState Presence,
    string ObservationSource,
    double? ObservationConfidence = null)
{
    public ParticipantReference Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ReferenceId);
        var confidence = ObservationConfidence;
        if (confidence.HasValue
            && (!double.IsFinite(confidence.Value) || confidence is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ObservationConfidence),
                "Participant observation confidence must be between zero and one.");
        }

        return this with
        {
            ReferenceId = ReferenceId.Trim(),
            DisplayLabel = string.IsNullOrWhiteSpace(DisplayLabel)
                ? "Unknown participant"
                : DisplayLabel.Trim(),
            ObservationSource = string.IsNullOrWhiteSpace(ObservationSource)
                ? "not-observed"
                : ObservationSource.Trim()
        };
    }
}

/// <summary>
/// Immutable, mechanically sourced context captured at turn admission. Presence and
/// recognition are advisory context only; this record never identifies the speaker,
/// grants consent, authenticates a principal, or conveys capability authority.
/// </summary>
public sealed record ParticipantRosterSnapshot(
    string TenantId,
    string TurnId,
    string ConversationId,
    DateTimeOffset CapturedUtc,
    string SelectionGeneration,
    string PresenceGeneration,
    string? SelectedParticipantReference,
    IReadOnlyList<ParticipantReference> Participants)
{
    public ParticipantRosterSnapshot Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SelectionGeneration);
        ArgumentException.ThrowIfNullOrWhiteSpace(PresenceGeneration);
        ArgumentNullException.ThrowIfNull(Participants);
        var tenantId = TenantId.Trim();
        if (tenantId.Length > 128 || tenantId.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TenantId),
                "A participant-memory tenant ID must be at most 128 non-control characters.");
        }
        var selectionGeneration = SelectionGeneration.Trim();
        var presenceGeneration = PresenceGeneration.Trim();
        if (selectionGeneration.Any(char.IsControl)
            || presenceGeneration.Any(char.IsControl)
            || $"{selectionGeneration}:{presenceGeneration}".Length > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SelectionGeneration),
                "Participant-memory generations must form a bounded non-control revision.");
        }

        var normalized = Participants.Select(participant => participant.Normalize()).ToArray();
        if (normalized.Length > ParticipantMemoryLimits.MaximumParticipantsPerTurn)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Participants),
                $"A participant roster may contain at most {ParticipantMemoryLimits.MaximumParticipantsPerTurn} entries.");
        }

        var duplicate = normalized
            .GroupBy(participant => participant.ReferenceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"The participant roster contains duplicate reference '{duplicate.Key}'.",
                nameof(Participants));
        }

        var selected = NormalizeOptional(SelectedParticipantReference);
        if (selected is not null
            && !normalized.Any(participant => string.Equals(
                participant.ReferenceId,
                selected,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The selected participant reference is not present in the captured roster.",
                nameof(SelectedParticipantReference));
        }

        return this with
        {
            TenantId = tenantId,
            TurnId = TurnId.Trim(),
            ConversationId = ConversationId.Trim(),
            SelectionGeneration = selectionGeneration,
            PresenceGeneration = presenceGeneration,
            SelectedParticipantReference = selected,
            Participants = normalized
        };
    }

    public ParticipantReference? Find(string? referenceId) =>
        string.IsNullOrWhiteSpace(referenceId)
            ? null
            : Participants.FirstOrDefault(participant => string.Equals(
                participant.ReferenceId,
                referenceId.Trim(),
                StringComparison.Ordinal));

    public string Revision => $"{SelectionGeneration}:{PresenceGeneration}";

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum ParticipantMemoryClaimKind
{
    DirectStatement,
    Hearsay,
    DirectObservation,
    Preference,
    SharedExperience,
    Directive,
    Other
}

public enum ParticipantMemoryEvidenceKind
{
    StatedDirectly,
    ReportedByParticipant,
    ObservedDirectly,
    Unknown
}

public enum ParticipantMemoryVisibility
{
    Private,
    Shared,
    TeamProject,
    General
}

public enum ParticipantMemorySensitivity
{
    Low,
    Sensitive
}

public enum ParticipantMemoryState
{
    Candidate,
    Confirmed,
    Disputed,
    Superseded,
    Revoked,
    Archived
}

public enum ParticipantMemoryMutationKind
{
    Add,
    Correct,
    Dispute,
    Revoke,
    Archive,
    Delete
}

public enum ParticipantMemoryAuthenticationKind
{
    WindowsHello,
    Passkey,
    LocalCredential,
    TrustedTestFactor,
    FaceRecognition,
    SpeakerRecognition,
    PassivePresence
}

public enum ParticipantMemoryFailureCode
{
    Disabled,
    Unavailable,
    Cancelled,
    TimedOut,
    InvalidProposal,
    UnknownParticipant,
    AmbiguousIdentity,
    ConsentRequired,
    AuthenticationRequired,
    PermissionDenied,
    StaleRoster,
    StaleResult,
    EmbeddingSpaceMismatch,
    NotFound,
    Conflict,
    ProtocolFailure
}

public sealed record ParticipantMemoryProvenance(
    string SourceTurnId,
    string SourceMessageId,
    string SourceChannel,
    DateTimeOffset CapturedUtc,
    string? ReportedByParticipantReference = null)
{
    public ParticipantMemoryProvenance Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceTurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceMessageId);
        return this with
        {
            SourceTurnId = SourceTurnId.Trim(),
            SourceMessageId = SourceMessageId.Trim(),
            SourceChannel = string.IsNullOrWhiteSpace(SourceChannel)
                ? "unknown"
                : SourceChannel.Trim(),
            ReportedByParticipantReference = NormalizeOptional(ReportedByParticipantReference)
        };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ParticipantMemoryConsentReceipt(
    string ReceiptId,
    string GrantedByParticipantReference,
    string Operation,
    string ProposalFingerprint,
    string ConsentSessionId,
    ParticipantMemoryVisibility Visibility,
    IReadOnlyList<string> AudienceParticipantReferences,
    DateTimeOffset GrantedUtc,
    DateTimeOffset? ExpiresUtc,
    string SourceTurnId)
{
    public bool IsCurrent(DateTimeOffset now) =>
        GrantedUtc <= now
        && (ExpiresUtc is null || (ExpiresUtc > now && ExpiresUtc > GrantedUtc));
}

public sealed record ParticipantMemoryAuthenticationReceipt(
    string ReceiptId,
    string PrincipalParticipantReference,
    ParticipantMemoryAuthenticationKind Kind,
    DateTimeOffset AuthenticatedUtc,
    DateTimeOffset ExpiresUtc,
    IReadOnlyList<string> GrantedOperations)
{
    public bool IsCurrent(DateTimeOffset now) =>
        AuthenticatedUtc <= now
        && ExpiresUtc > now
        && ExpiresUtc > AuthenticatedUtc;

    public bool UsesIndependentTrustedFactor => Kind is
        ParticipantMemoryAuthenticationKind.WindowsHello
        or ParticipantMemoryAuthenticationKind.Passkey
        or ParticipantMemoryAuthenticationKind.LocalCredential
        or ParticipantMemoryAuthenticationKind.TrustedTestFactor;
}

/// <summary>
/// Trusted permission issued by the coordinator after its action boundary admits the
/// exact tool call. This is never a model argument and is distinct from authentication
/// and participant consent.
/// </summary>
public sealed record ParticipantMemoryPermissionReceipt(
    string ReceiptId,
    string PrincipalParticipantReference,
    IReadOnlyList<string> GrantedOperations,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    string SourceCallId,
    string Source)
{
    public bool IsCurrent(DateTimeOffset now) =>
        IssuedUtc <= now
        && ExpiresUtc > now
        && ExpiresUtc > IssuedUtc;

    public bool Grants(string operation) => GrantedOperations.Any(value =>
        string.Equals(value, operation, StringComparison.OrdinalIgnoreCase));
}

public sealed record ParticipantMemoryAuthorityContext(
    string? RequestingParticipantReference,
    ParticipantMemoryAuthenticationReceipt? Authentication,
    IReadOnlyList<string> TeamProjectAudienceKeys)
{
    public ParticipantMemoryPermissionReceipt? Permission { get; init; }

    public static ParticipantMemoryAuthorityContext Anonymous { get; } = new(null, null, []);
}

/// <summary>
/// Typed semantic proposal produced by Ali's configured model. Mechanical code may
/// validate these fields, but must never fill them by parsing the user's English.
/// </summary>
public sealed record ParticipantMemoryProposal(
    ParticipantMemoryMutationKind Operation,
    string? TargetMemoryId,
    string Text,
    string Category,
    string? SpeakerParticipantReference,
    IReadOnlyList<string> SubjectParticipantReferences,
    IReadOnlyList<string> WitnessParticipantReferences,
    string? SharedEventReference,
    ParticipantMemoryClaimKind ClaimKind,
    ParticipantMemoryEvidenceKind EvidenceKind,
    ParticipantMemoryVisibility Visibility,
    IReadOnlyList<string> AudienceParticipantReferences,
    ParticipantMemorySensitivity Sensitivity,
    double AttributionConfidence,
    string? ReportedByParticipantReference);

public sealed record ParticipantMemoryRecord(
    string MemoryId,
    string TenantId,
    string Text,
    string Category,
    string? SpeakerParticipantReference,
    IReadOnlyList<string> SubjectParticipantReferences,
    IReadOnlyList<string> WitnessParticipantReferences,
    string? SharedEventReference,
    ParticipantMemoryClaimKind ClaimKind,
    ParticipantMemoryEvidenceKind EvidenceKind,
    ParticipantMemoryVisibility Visibility,
    IReadOnlyList<string> AudienceParticipantReferences,
    ParticipantMemorySensitivity Sensitivity,
    double AttributionConfidence,
    ParticipantMemoryState State,
    ParticipantMemoryProvenance Provenance,
    IReadOnlyList<ParticipantMemoryConsentReceipt> ConsentReceipts,
    string? CorrectsMemoryId,
    string? SupersedesMemoryId,
    string? DisputesMemoryId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ConfirmedUtc,
    DateTimeOffset? CorrectedUtc,
    DateTimeOffset? RevokedUtc,
    DateTimeOffset? ArchivedUtc,
    string EmbeddingSpaceId,
    double? Score = null,
    double? SemanticScore = null,
    double? KeywordScore = null);

public sealed record ParticipantMemoryFailureReceipt(
    ParticipantMemoryFailureCode Code,
    string Operation,
    string RequestId,
    string SafeMessage,
    bool Retryable,
    DateTimeOffset OccurredUtc);

public sealed record ParticipantMemoryMutationRequest(
    string RequestId,
    ParticipantRosterSnapshot Roster,
    ParticipantMemoryAuthorityContext Authority,
    ParticipantMemoryProposal Proposal,
    string ExpectedEmbeddingSpaceId,
    ParticipantMemoryProvenance Provenance,
    IReadOnlyList<ParticipantMemoryConsentReceipt> ConsentReceipts);

public sealed record ParticipantMemoryRecallRequest(
    string RequestId,
    ParticipantRosterSnapshot Roster,
    ParticipantMemoryAuthorityContext Authority,
    string Query,
    int MaximumResults,
    string ExpectedEmbeddingSpaceId,
    bool IncludeSensitive = false);

public sealed record ParticipantMemoryListRequest(
    string RequestId,
    ParticipantRosterSnapshot Roster,
    ParticipantMemoryAuthorityContext Authority,
    int MaximumResults,
    string ExpectedEmbeddingSpaceId,
    bool IncludeSensitive = false);

public sealed record ParticipantMemoryMutationResult(
    bool Success,
    IReadOnlyList<ParticipantMemoryRecord> Records,
    ParticipantMemoryFailureReceipt? Failure,
    bool NoEffectConfirmed = false,
    string? MutationStatus = null)
{
    public static ParticipantMemoryMutationResult Failed(
        ParticipantMemoryFailureReceipt failure,
        bool noEffectConfirmed = false,
        string? mutationStatus = null) =>
        new(false, [], failure, noEffectConfirmed, mutationStatus);
}

public sealed record ParticipantMemoryRecallResult(
    bool Success,
    IReadOnlyList<ParticipantMemoryRecord> Records,
    string RosterRevision,
    string EmbeddingSpaceId,
    ParticipantMemoryFailureReceipt? Failure)
{
    public static ParticipantMemoryRecallResult Failed(
        string rosterRevision,
        string embeddingSpaceId,
        ParticipantMemoryFailureReceipt failure) =>
        new(false, [], rosterRevision, embeddingSpaceId, failure);
}

public sealed record ParticipantMemoryHealthResult(
    bool Enabled,
    bool Mem0Available,
    bool QdrantAvailable,
    string EmbeddingSpaceId,
    string CollectionName,
    ParticipantMemoryFailureReceipt? Failure)
{
    public bool EmbeddingAvailable { get; init; }

    public int DegradedPointCount { get; init; }

    public IReadOnlyList<string> FailedPointIds { get; init; } = [];

    public bool DeliberateRepairAvailable { get; init; }
}

public sealed record ParticipantMemoryRepairRequest(
    string RequestId,
    ParticipantRosterSnapshot Roster,
    ParticipantMemoryAuthorityContext Authority,
    string ExpectedEmbeddingSpaceId,
    IReadOnlyList<string> PointIds);

public sealed record ParticipantMemoryRepairResult(
    bool Success,
    int UpdatedPointCount,
    int FailedPointCount,
    IReadOnlyList<string> FailedPointIds,
    ParticipantMemoryFailureReceipt? Failure);

public sealed record ParticipantMemoryReconciliationRequest(
    string RequestId,
    ParticipantRosterSnapshot Roster,
    ParticipantMemoryAuthorityContext Authority,
    string MutationRequestId,
    string ExpectedEmbeddingSpaceId);

public sealed record ParticipantMemoryReconciliationResult(
    bool Success,
    string MutationRequestId,
    string? MutationOperation,
    string? MutationStatus,
    IReadOnlyList<ParticipantMemoryRecord> Records,
    ParticipantMemoryFailureReceipt? Failure)
{
    public static ParticipantMemoryReconciliationResult Failed(
        string mutationRequestId,
        ParticipantMemoryFailureReceipt failure) =>
        new(false, mutationRequestId, null, null, [], failure);
}

public interface IParticipantMemoryService
{
    Task<ParticipantMemoryRecallResult> RecallParticipantsAsync(
        ParticipantMemoryRecallRequest request,
        CancellationToken cancellationToken);

    Task<ParticipantMemoryRecallResult> ListParticipantsAsync(
        ParticipantMemoryListRequest request,
        CancellationToken cancellationToken);

    Task<ParticipantMemoryMutationResult> MutateParticipantsAsync(
        ParticipantMemoryMutationRequest request,
        CancellationToken cancellationToken);

    Task<ParticipantMemoryHealthResult> CheckParticipantHealthAsync(
        ParticipantRosterSnapshot roster,
        ParticipantMemoryAuthorityContext authority,
        CancellationToken cancellationToken);

    Task<ParticipantMemoryRepairResult> RepairParticipantEmbeddingSpaceAsync(
        ParticipantMemoryRepairRequest request,
        CancellationToken cancellationToken);

    Task<ParticipantMemoryReconciliationResult> ReconcileParticipantMutationAsync(
        ParticipantMemoryReconciliationRequest request,
        CancellationToken cancellationToken);
}

internal static class ParticipantMemoryLimits
{
    public const int MaximumParticipantsPerTurn = 16;
    public const int MaximumReferencesPerRole = 16;
    public const int MaximumAudienceKeys = 16;
    public const int MaximumRecallResults = 8;
    public const int MaximumRecallQueryLength = 4_096;
    public const int MaximumMemoryTextLength = 4096;
    public const int MaximumCategoryLength = 128;
    public const int MaximumRepairPointIds = 32;
}

internal static class ParticipantMemoryProposalFingerprint
{
    internal static string Create(ParticipantMemoryProposal proposal, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenantId = tenantId.Trim(),
            proposal
        });
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        try
        {
            return "participant-proposal:"
                + Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(hash);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
