using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.UserMemory;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Model-visible participant-memory adapter for the one Agent Framework loop. The
/// model supplies typed semantic fields; this adapter supplies admitted roster,
/// provenance, permission, and consent from trusted non-model state.
/// </summary>
internal sealed class AliParticipantMemoryTools
{
    private const int MaximumResults = 8;
    private readonly IParticipantMemoryService _memory;
    private readonly IParticipantRosterAuthority _rosters;
    private readonly ParticipantMemoryReceiptAuthority _receipts;
    private readonly Func<CoordinatorTurnContext?> _turnAccessor;
    private readonly ParticipantMemoryConsentCoordinator _consents = new();

    public AliParticipantMemoryTools(
        IParticipantMemoryService memory,
        IParticipantRosterAuthority rosters,
        ParticipantMemoryReceiptAuthority receipts,
        Func<CoordinatorTurnContext?> turnAccessor)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _turnAccessor = turnAccessor ?? throw new ArgumentNullException(nameof(turnAccessor));
    }

    public async Task<CoordinatorMemoryResult> RecallAsync(
        [Description("The exact semantic retrieval query chosen by the model, bounded to 4096 characters.")] string query,
        [Description("Set true only when the user asks to include sensitive memories and is present to complete independent Windows credential verification.")] bool includeSensitive,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)
            || query.Trim().Length > ParticipantMemoryLimits.MaximumRecallQueryLength)
        {
            return FailedRead(
                $"Participant-memory recall requires a non-empty query of at most {ParticipantMemoryLimits.MaximumRecallQueryLength} characters.");
        }
        if (!TryCreateReadContext(
                AliCapabilityCatalog.RecallUserMemoryName,
                out var turn,
                out var roster,
                out var authority,
                out var failure))
        {
            return FailedRead(failure);
        }

        turn.UsedEvidenceTool = true;
        var health = await _memory.CheckParticipantHealthAsync(roster, authority, cancellationToken)
            .ConfigureAwait(false);
        if (!health.Enabled || !health.EmbeddingAvailable || !health.Mem0Available || !health.QdrantAvailable)
        {
            return FailedRead(health.Failure?.SafeMessage
                ?? "Participant memory is not ready at its verified provider boundary.", roster);
        }

        var result = await _memory.RecallParticipantsAsync(
            new ParticipantMemoryRecallRequest(
                StableRequestId(turn, AliCapabilityCatalog.RecallUserMemoryName),
                roster,
                authority,
                query.Trim(),
                MaximumResults,
                health.EmbeddingSpaceId,
                includeSensitive),
            cancellationToken).ConfigureAwait(false);
        return ToCoordinatorResult(result, roster, "recalled");
    }

    public async Task<CoordinatorMemoryResult> ListAsync(
        [Description("Set true only when the user asks to include sensitive memories and is present to complete independent Windows credential verification.")] bool includeSensitive,
        CancellationToken cancellationToken)
    {
        if (!TryCreateReadContext(
                AliCapabilityCatalog.ListCurrentUserMemoriesName,
                out var turn,
                out var roster,
                out var authority,
                out var failure))
        {
            return FailedRead(failure);
        }

        turn.UsedEvidenceTool = true;
        var health = await _memory.CheckParticipantHealthAsync(roster, authority, cancellationToken)
            .ConfigureAwait(false);
        if (!health.Enabled || !health.EmbeddingAvailable || !health.Mem0Available || !health.QdrantAvailable)
        {
            return FailedRead(health.Failure?.SafeMessage
                ?? "Participant memory is not ready at its verified provider boundary.", roster);
        }

        var result = await _memory.ListParticipantsAsync(
            new ParticipantMemoryListRequest(
                StableRequestId(turn, AliCapabilityCatalog.ListCurrentUserMemoriesName),
                roster,
                authority,
                MaximumResults,
                health.EmbeddingSpaceId,
                includeSensitive),
            cancellationToken).ConfigureAwait(false);
        return ToCoordinatorResult(result, roster, "listed");
    }

    public async Task<CoordinatorMemoryWriteResult> MutateAsync(
        [Description("Typed participant-memory proposal. The model owns semantic roles and meaning; it cannot supply consent, permission, authentication, provenance, or roster state.")]
        ParticipantMemoryProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var turn = _turnAccessor();
        var roster = turn?.ParticipantRoster;
        if (turn is null || roster is null || !roster.Normalize().Participants.Any())
        {
            return new(false, "No immutable admitted participant roster is available.");
        }
        var requestId = StableRequestId(
            turn,
            AliCapabilityCatalog.MutateParticipantMemoryName,
            proposal.Operation.ToString());
        CoordinatorMemoryWriteResult ConfirmedNoEffect(string message) => new(
            false,
            message,
            null,
            requestId,
            DurableCompletionConfirmed: true);
        if (!TryGetTrustedPermission(
                turn,
                AliCapabilityCatalog.MutateParticipantMemoryName,
                proposal.Operation.ToString(),
                requireInteractiveOnce: true,
                roster,
                out var authority,
                out var permissionFailure))
        {
            return ConfirmedNoEffect(permissionFailure);
        }

        var freshness = _rosters.CheckCurrent(roster);
        if (!freshness.IsCurrent)
        {
            return ConfirmedNoEffect(
                "The admitted participant roster changed before the memory operation.");
        }

        var health = await _memory.CheckParticipantHealthAsync(roster, authority, cancellationToken)
            .ConfigureAwait(false);
        if (!health.Enabled || !health.EmbeddingAvailable || !health.Mem0Available || !health.QdrantAvailable)
        {
            return ConfirmedNoEffect(health.Failure?.SafeMessage
                ?? "Participant memory is not ready at its verified provider boundary.");
        }
        if (!_consents.TryIssue(
                proposal,
                roster,
                _receipts,
                DateTimeOffset.UtcNow,
                out var consents,
                out var pending,
                out _))
        {
            return ConfirmedNoEffect(
                $"The exact proposal still needs consent from: {string.Join(", ", pending)}. Call {AliCapabilityCatalog.ConsentParticipantMemoryProposalName} with the unchanged proposal after each participant is explicitly selected and approves.");
        }
        var provenance = new ParticipantMemoryProvenance(
            roster.TurnId,
            turn.UserMessageId,
            "agent-framework-tool",
            DateTimeOffset.UtcNow,
            proposal.ReportedByParticipantReference);

        var result = await _memory.MutateParticipantsAsync(
            new ParticipantMemoryMutationRequest(
                requestId,
                roster,
                authority,
                proposal,
                health.EmbeddingSpaceId,
                provenance,
                consents),
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? new(
                true,
                "Participant-memory mutation completed with a typed attributable receipt.",
                result.Records.FirstOrDefault()?.MemoryId,
                requestId)
            : new(
                false,
                result.Failure?.SafeMessage ?? "Participant-memory mutation failed safely.",
                null,
                requestId,
                DurableCompletionConfirmed: result.NoEffectConfirmed);
    }

    public Task<CoordinatorParticipantMemoryConsentResult> ConsentAsync(
        [Description("The exact typed participant-memory proposal this explicitly selected participant approves. Every field must remain unchanged for final mutation.")]
        ParticipantMemoryProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var turn = _turnAccessor();
        var roster = turn?.ParticipantRoster;
        if (turn is null || roster is null || !roster.Normalize().Participants.Any())
        {
            return Task.FromResult(new CoordinatorParticipantMemoryConsentResult(
                false,
                false,
                "No immutable admitted participant roster is available.",
                string.Empty,
                []));
        }
        if (!TryGetTrustedPermission(
                turn,
                AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
                proposal.Operation.ToString(),
                requireInteractiveOnce: true,
                roster,
                out var authority,
                out var permissionFailure))
        {
            return Task.FromResult(new CoordinatorParticipantMemoryConsentResult(
                false,
                false,
                permissionFailure,
                string.Empty,
                []));
        }
        if (!_rosters.CheckCurrent(roster).IsCurrent)
        {
            return Task.FromResult(new CoordinatorParticipantMemoryConsentResult(
                false,
                false,
                "The admitted participant roster changed before consent was recorded.",
                string.Empty,
                []));
        }

        var validationProbe = ParticipantMemoryPolicy.ValidateMutation(
            new ParticipantMemoryMutationRequest(
                $"participant-consent:{Guid.NewGuid():N}",
                roster,
                authority,
                proposal,
                "consent-validation-only",
                new ParticipantMemoryProvenance(
                    roster.TurnId,
                    turn.UserMessageId,
                    "agent-framework-consent",
                    DateTimeOffset.UtcNow,
                    proposal.ReportedByParticipantReference),
                []),
            DateTimeOffset.UtcNow,
            _receipts);
        var consentRequired = validationProbe.Failure?.Code
            == ParticipantMemoryFailureCode.ConsentRequired;
        var destructiveWithoutConsent = (proposal.Operation is ParticipantMemoryMutationKind.Delete
            or ParticipantMemoryMutationKind.Revoke
            or ParticipantMemoryMutationKind.Archive)
            && validationProbe.Failure?.Code == ParticipantMemoryFailureCode.AuthenticationRequired;
        if (!consentRequired && !destructiveWithoutConsent)
        {
            return Task.FromResult(new CoordinatorParticipantMemoryConsentResult(
                false,
                false,
                validationProbe.Failure?.SafeMessage
                    ?? "The typed proposal was not admitted for scoped consent.",
                string.Empty,
                []));
        }
        if (destructiveWithoutConsent)
        {
            return Task.FromResult(new CoordinatorParticipantMemoryConsentResult(
                false,
                true,
                "This exact operation does not require participant-consent staging; final mutation still requires interactive approval and independent authentication.",
                ParticipantMemoryConsentCoordinator.Fingerprint(proposal, roster.TenantId),
                []));
        }

        var capture = _consents.Capture(
            proposal,
            roster,
            authority.Permission!,
            _receipts,
            DateTimeOffset.UtcNow);
        return Task.FromResult(new CoordinatorParticipantMemoryConsentResult(
            capture.Recorded,
            capture.Recorded && capture.PendingParticipantReferences.Count == 0,
            !capture.Recorded
                ? "That exact consent call was already consumed; collect fresh explicit approvals for a new mutation attempt."
                : capture.PendingParticipantReferences.Count == 0
                ? "Every required participant has approved the exact proposal; mutate_participant_memory may now execute it unchanged."
                : $"Consent recorded for the selected participant. Still pending: {string.Join(", ", capture.PendingParticipantReferences)}.",
            capture.ProposalFingerprint,
            capture.PendingParticipantReferences));
    }

    public async Task<CoordinatorMemoryReconciliationResult> ReconcileAsync(
        [Description("The exact durable mutation request ID previously returned by mutate_participant_memory.")]
        string mutationRequestId,
        CancellationToken cancellationToken)
    {
        var exactMutationRequestId = mutationRequestId?.Trim() ?? string.Empty;
        CoordinatorMemoryReconciliationResult Failed(string message) => new(
            false,
            message,
            exactMutationRequestId,
            null,
            null);
        if (exactMutationRequestId.Length is 0 or > 128
            || exactMutationRequestId.Any(character => char.IsControl(character)))
        {
            return Failed("An exact bounded participant-memory mutation request ID is required.");
        }

        var turn = _turnAccessor();
        var roster = turn?.ParticipantRoster;
        if (turn is null || roster is null || !roster.Normalize().Participants.Any())
        {
            return Failed("No immutable admitted participant roster is available.");
        }
        if (!TryGetTrustedPermission(
                turn,
                AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
                "Reconcile",
                requireInteractiveOnce: true,
                roster,
                out var authority,
                out var permissionFailure))
        {
            return Failed(permissionFailure);
        }
        if (!_rosters.CheckCurrent(roster).IsCurrent)
        {
            return Failed("The admitted participant roster changed before reconciliation.");
        }

        var health = await _memory.CheckParticipantHealthAsync(roster, authority, cancellationToken)
            .ConfigureAwait(false);
        if (!health.Enabled || !health.EmbeddingAvailable || !health.Mem0Available || !health.QdrantAvailable)
        {
            return Failed(health.Failure?.SafeMessage
                ?? "Participant memory is not ready at its verified provider boundary.");
        }

        var result = await _memory.ReconcileParticipantMutationAsync(
            new ParticipantMemoryReconciliationRequest(
                StableRequestId(turn, AliCapabilityCatalog.ReconcileParticipantMemoryMutationName),
                roster,
                authority,
                exactMutationRequestId,
                health.EmbeddingSpaceId),
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? new(
                true,
                $"Participant-memory mutation is {result.MutationStatus} by exact durable receipt.",
                result.MutationRequestId,
                result.MutationOperation,
                result.MutationStatus,
                result.Records.FirstOrDefault()?.MemoryId)
            : new(
                false,
                result.Failure?.SafeMessage ?? "Participant-memory reconciliation failed safely.",
                result.MutationRequestId,
                result.MutationOperation,
                result.MutationStatus);
    }

    private bool TryCreateReadContext(
        string toolName,
        out CoordinatorTurnContext turn,
        out ParticipantRosterSnapshot roster,
        out ParticipantMemoryAuthorityContext authority,
        out string failure)
    {
        turn = _turnAccessor()!;
        roster = turn?.ParticipantRoster!;
        authority = ParticipantMemoryAuthorityContext.Anonymous;
        if (turn is null || roster is null)
        {
            failure = "No immutable admitted participant roster is available.";
            return false;
        }
        if (!_rosters.CheckCurrent(roster).IsCurrent)
        {
            failure = "The admitted participant roster no longer matches live state.";
            return false;
        }
        return TryGetTrustedPermission(
            turn,
            toolName,
            "Read",
            requireInteractiveOnce: false,
            roster,
            out authority,
            out failure);
    }

    private bool TryGetTrustedPermission(
        CoordinatorTurnContext turn,
        string toolName,
        string operation,
        bool requireInteractiveOnce,
        ParticipantRosterSnapshot roster,
        out ParticipantMemoryAuthorityContext authority,
        out string failure)
    {
        authority = ParticipantMemoryAuthorityContext.Anonymous;
        var principal = roster.SelectedParticipantReference;
        if (string.IsNullOrWhiteSpace(principal))
        {
            failure = "Select a registered participant before accessing participant memory.";
            return false;
        }
        if (!turn.TryGetActiveToolCallId(toolName, out var callId)
            || string.IsNullOrWhiteSpace(callId)
            || !turn.TryGetPermissionReceipt(callId, out var coordinatorReceipt)
            || coordinatorReceipt is null
            || coordinatorReceipt.Permission.Decision.StartsWith("denied", StringComparison.Ordinal))
        {
            failure = "Participant memory requires an admitted exact tool permission receipt.";
            return false;
        }
        if (requireInteractiveOnce
            && (!string.Equals(coordinatorReceipt.Source, "interactive-user", StringComparison.Ordinal)
                || !string.Equals(
                    coordinatorReceipt.Permission.Decision,
                    "approved-once",
                    StringComparison.Ordinal)))
        {
            failure = "A durable participant-memory mutation requires one interactive exact-call approval.";
            return false;
        }

        var permission = _receipts.IssuePermission(
            principal,
            [operation],
            callId,
            coordinatorReceipt.Source,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        authority = new ParticipantMemoryAuthorityContext(principal, null, [])
        {
            Permission = permission
        };
        failure = string.Empty;
        return true;
    }

    private static CoordinatorMemoryResult ToCoordinatorResult(
        ParticipantMemoryRecallResult result,
        ParticipantRosterSnapshot roster,
        string verb)
    {
        if (!result.Success)
        {
            return FailedRead(result.Failure?.SafeMessage ?? "Participant memory failed safely.");
        }
        var selected = roster.SelectedParticipantReference ?? "none";
        return new(
            $"Participant memory {verb} {result.Records.Count} item(s) for admitted roster {roster.Revision}; selected participant: {selected}.",
            result.Records.Select(record => new CoordinatorMemoryItem(
                record.MemoryId,
                record.Text,
                record.Category,
                record.CorrectedUtc ?? record.ConfirmedUtc ?? record.CreatedUtc)
            {
                SpeakerParticipantReference = record.SpeakerParticipantReference,
                SubjectParticipantReferences = record.SubjectParticipantReferences,
                WitnessParticipantReferences = record.WitnessParticipantReferences,
                SharedEventReference = record.SharedEventReference,
                ClaimKind = record.ClaimKind,
                EvidenceKind = record.EvidenceKind,
                Visibility = record.Visibility,
                AudienceParticipantReferences = record.AudienceParticipantReferences,
                Sensitivity = record.Sensitivity,
                ReportedByParticipantReference =
                    record.Provenance.ReportedByParticipantReference
            }).ToArray(),
            [])
        {
            ParticipantRoster = ToCoordinatorRoster(roster)
        };
    }

    private static CoordinatorMemoryResult FailedRead(
        string message,
        ParticipantRosterSnapshot? roster = null) => new(message, [], [message])
        {
            ParticipantRoster = roster is null ? null : ToCoordinatorRoster(roster)
        };

    private static CoordinatorParticipantRosterResult ToCoordinatorRoster(
        ParticipantRosterSnapshot roster) => new(
        roster.Revision,
        roster.SelectedParticipantReference,
        roster.Participants.Select(participant => new CoordinatorParticipantRosterItem(
            participant.ReferenceId,
            participant.DisplayLabel,
            participant.Kind,
            participant.Presence,
            participant.ObservationSource,
            participant.ObservationConfidence)).ToArray());

    private static string StableRequestId(
        CoordinatorTurnContext turn,
        string toolName,
        string? operation = null)
    {
        if ((string.Equals(
                    toolName,
                    AliCapabilityCatalog.MutateParticipantMemoryName,
                    StringComparison.Ordinal)
                || string.Equals(
                    toolName,
                    AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
                    StringComparison.Ordinal))
            && turn.TryGetActiveDurableOperationId(toolName, out var durableOperationId)
            && !string.IsNullOrWhiteSpace(durableOperationId))
        {
            return durableOperationId;
        }

        var canonical = string.Join(
            "\n",
            turn.ConversationId,
            turn.UserMessageId,
            turn.AssistantMessageId,
            toolName,
            operation ?? "Read",
            turn.TryGetActiveToolCallId(toolName, out var callId) ? callId : "missing-call");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var issuedUnixSeconds = turn.ParticipantRoster?.CapturedUtc.ToUnixTimeSeconds()
            ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"participant-request:{issuedUnixSeconds}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

internal sealed record ParticipantMemoryConsentCapture(
    bool Recorded,
    string ProposalFingerprint,
    IReadOnlyList<string> PendingParticipantReferences);

/// <summary>
/// Bounded process-local collection of exact-proposal approvals. Each approval comes
/// from a separately selected participant and an interactive exact-call permission.
/// It stores no inferred identity and issues fresh turn-bound receipts only when the
/// unchanged proposal has every required approval.
/// </summary>
internal sealed class ParticipantMemoryConsentCoordinator
{
    private const int MaximumProposals = 256;
    private const int MaximumConsumedApprovalUses = 4_096;
    private readonly Dictionary<string, ConsentApprovalSet> _grants = new(StringComparer.Ordinal);
    private readonly LinkedList<(string ApprovalKey, string SessionId)> _oldest = new();
    private readonly HashSet<string> _consumedApprovalUses = new(StringComparer.Ordinal);
    private readonly Queue<string> _oldestConsumedApprovalUses = new();
    private readonly object _sync = new();

    internal ParticipantMemoryConsentCapture Capture(
        ParticipantMemoryProposal proposal,
        ParticipantRosterSnapshot roster,
        ParticipantMemoryPermissionReceipt permission,
        ParticipantMemoryReceiptAuthority receipts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(receipts);
        var principal = roster.SelectedParticipantReference
            ?? throw new InvalidOperationException(
                "Scoped consent requires one explicitly selected participant.");
        if (!receipts.IsIssued(permission)
            || !permission.IsCurrent(now)
            || !permission.Grants(proposal.Operation.ToString())
            || !string.Equals(
                permission.PrincipalParticipantReference,
                principal,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Scoped consent requires an issued exact-operation permission for the selected participant.");
        }

        var fingerprint = Fingerprint(proposal, roster.TenantId);
        var approvalKey = ApprovalKey(fingerprint, roster.ConversationId);
        var approvalUseKey = ApprovalUseKey(
            approvalKey,
            principal,
            permission.SourceCallId);
        var required = RequiredParticipants(proposal, principal);
        lock (_sync)
        {
            if (_consumedApprovalUses.Contains(approvalUseKey))
            {
                return new(false, fingerprint, required);
            }
            if (!_grants.TryGetValue(approvalKey, out var approvalSet))
            {
                while (_grants.Count >= MaximumProposals)
                {
                    var oldest = _oldest.First?.Value
                        ?? throw new InvalidOperationException(
                            "The bounded consent approval index is inconsistent.");
                    _oldest.RemoveFirst();
                    _grants.Remove(oldest.ApprovalKey);
                }
                approvalSet = new ConsentApprovalSet(
                    $"participant-consent-session:{Guid.NewGuid():N}",
                    roster.ConversationId,
                    new Dictionary<string, ParticipantMemoryPermissionReceipt>(StringComparer.Ordinal));
                approvalSet.OrderNode = _oldest.AddLast((approvalKey, approvalSet.SessionId));
                _grants.Add(approvalKey, approvalSet);
            }
            var proposalGrants = approvalSet.Grants;
            proposalGrants[principal] = permission;
            RemoveExpired(proposalGrants, receipts, proposal.Operation.ToString(), now);
            return new(
                true,
                fingerprint,
                required
                    .Where(reference => !proposalGrants.ContainsKey(reference))
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        }
    }

    internal bool TryIssue(
        ParticipantMemoryProposal proposal,
        ParticipantRosterSnapshot roster,
        ParticipantMemoryReceiptAuthority receipts,
        DateTimeOffset now,
        out IReadOnlyList<ParticipantMemoryConsentReceipt> consentReceipts,
        out IReadOnlyList<string> pendingParticipantReferences,
        out string proposalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(receipts);
        var fingerprint = Fingerprint(proposal, roster.TenantId);
        proposalFingerprint = fingerprint;
        var approvalKey = ApprovalKey(fingerprint, roster.ConversationId);
        if (proposal.Operation is ParticipantMemoryMutationKind.Delete
            or ParticipantMemoryMutationKind.Revoke
            or ParticipantMemoryMutationKind.Archive)
        {
            consentReceipts = [];
            pendingParticipantReferences = [];
            return true;
        }

        var selected = roster.SelectedParticipantReference;
        if (string.IsNullOrWhiteSpace(selected))
        {
            consentReceipts = [];
            pendingParticipantReferences = ["explicit-selected-participant"];
            return false;
        }
        var required = RequiredParticipants(proposal, selected);
        ParticipantMemoryPermissionReceipt[] grants;
        string consentSessionId;
        lock (_sync)
        {
            if (!_grants.TryGetValue(approvalKey, out var approvalSet)
                || !string.Equals(
                    approvalSet.ConversationId,
                    roster.ConversationId,
                    StringComparison.Ordinal))
            {
                consentReceipts = [];
                pendingParticipantReferences = required;
                return false;
            }
            var proposalGrants = approvalSet.Grants;
            RemoveExpired(proposalGrants, receipts, proposal.Operation.ToString(), now);
            pendingParticipantReferences = required
                .Where(reference => roster.Find(reference) is null
                    || !proposalGrants.ContainsKey(reference))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (pendingParticipantReferences.Count != 0)
            {
                consentReceipts = [];
                return false;
            }
            grants = required.Select(reference => proposalGrants[reference]).ToArray();
            consentSessionId = approvalSet.SessionId;
            foreach (var grant in grants)
            {
                RememberConsumedApprovalUse(ApprovalUseKey(
                    approvalKey,
                    grant.PrincipalParticipantReference,
                    grant.SourceCallId));
            }
            _grants.Remove(approvalKey);
            if (approvalSet.OrderNode is not null)
            {
                _oldest.Remove(approvalSet.OrderNode);
                approvalSet.OrderNode = null;
            }
        }

        try
        {
            consentReceipts = grants.Select(permission =>
            {
                var remaining = permission.ExpiresUtc - now;
                var lifetime = remaining < TimeSpan.FromMinutes(2)
                    ? remaining
                    : TimeSpan.FromMinutes(2);
                return receipts.IssueConsent(
                    permission,
                    proposal.Operation.ToString(),
                    fingerprint,
                    consentSessionId,
                    proposal.Visibility,
                    proposal.AudienceParticipantReferences ?? [],
                    roster.TurnId,
                    now,
                    lifetime);
            }).ToArray();
            pendingParticipantReferences = [];
            return true;
        }
        catch (InvalidOperationException)
        {
            consentReceipts = [];
            pendingParticipantReferences = required;
            return false;
        }
    }

    internal static string Fingerprint(ParticipantMemoryProposal proposal, string tenantId)
        => ParticipantMemoryProposalFingerprint.Create(proposal, tenantId);

    private static string ApprovalKey(string proposalFingerprint, string conversationId)
    {
        var bytes = Encoding.UTF8.GetBytes(
            proposalFingerprint + "\0" + conversationId.Trim());
        var hash = SHA256.HashData(bytes);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ApprovalUseKey(
        string approvalKey,
        string principalParticipantReference,
        string sourceCallId) =>
        string.Join(
            "\0",
            approvalKey,
            principalParticipantReference.Trim(),
            sourceCallId.Trim());

    private void RememberConsumedApprovalUse(string approvalUseKey)
    {
        if (!_consumedApprovalUses.Add(approvalUseKey))
        {
            return;
        }
        _oldestConsumedApprovalUses.Enqueue(approvalUseKey);
        while (_consumedApprovalUses.Count > MaximumConsumedApprovalUses)
        {
            _consumedApprovalUses.Remove(_oldestConsumedApprovalUses.Dequeue());
        }
    }

    private static IReadOnlyList<string> RequiredParticipants(
        ParticipantMemoryProposal proposal,
        string selectedParticipantReference) =>
        new[] { proposal.SpeakerParticipantReference, proposal.ReportedByParticipantReference }
            .Concat(proposal.SubjectParticipantReferences ?? [])
            .Concat(proposal.WitnessParticipantReferences ?? [])
            .Concat(proposal.AudienceParticipantReferences ?? [])
            .Append(selectedParticipantReference)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void RemoveExpired(
        Dictionary<string, ParticipantMemoryPermissionReceipt> grants,
        ParticipantMemoryReceiptAuthority receipts,
        string operation,
        DateTimeOffset now)
    {
        foreach (var key in grants
                     .Where(pair => !receipts.IsIssued(pair.Value)
                         || !pair.Value.IsCurrent(now)
                         || !pair.Value.Grants(operation))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            grants.Remove(key);
        }
    }

    private sealed class ConsentApprovalSet(
        string sessionId,
        string conversationId,
        Dictionary<string, ParticipantMemoryPermissionReceipt> grants)
    {
        internal string SessionId { get; } = sessionId;

        internal string ConversationId { get; } = conversationId;

        internal Dictionary<string, ParticipantMemoryPermissionReceipt> Grants { get; } = grants;

        internal LinkedListNode<(string ApprovalKey, string SessionId)>? OrderNode { get; set; }
    }
}
