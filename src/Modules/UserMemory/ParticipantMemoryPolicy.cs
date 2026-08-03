using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ali.Modules.UserMemory;

internal sealed record ParticipantMemoryValidationResult(
    bool Valid,
    ParticipantRosterSnapshot? Roster,
    ParticipantMemoryProposal? Proposal,
    ParticipantMemoryProvenance? Provenance,
    IReadOnlyList<ParticipantMemoryConsentReceipt> ConsentReceipts,
    ParticipantMemoryFailureReceipt? Failure)
{
    public static ParticipantMemoryValidationResult Rejected(
        ParticipantMemoryMutationRequest request,
        ParticipantMemoryFailureCode code,
        string message,
        bool retryable = false) =>
        new(false, null, null, null, [], ParticipantMemoryPolicy.Failure(
            code,
            request.Proposal?.Operation.ToString() ?? "Unknown",
            request.RequestId,
            message,
            retryable));
}

/// <summary>
/// Enforces only typed, mechanical storage and authority boundaries. It deliberately
/// does not inspect English text to choose a speaker, subject, witness, claim kind,
/// visibility, relevance query, answer, or correction target.
/// </summary>
internal static class ParticipantMemoryPolicy
{
    private static readonly StringComparer IdComparer = StringComparer.Ordinal;
    private static readonly JsonSerializerOptions WorkerJsonOptions = CreateWorkerJsonOptions();

    public static ParticipantMemoryValidationResult ValidateMutation(
        ParticipantMemoryMutationRequest request,
        DateTimeOffset now,
        ParticipantMemoryReceiptAuthority receiptAuthority)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(receiptAuthority);
        if (string.IsNullOrWhiteSpace(request.RequestId)
            || request.RequestId.Length > 128
            || request.RequestId.Any(char.IsControl))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "A memory mutation requires a request ID.");
        }
        if (string.IsNullOrWhiteSpace(request.ExpectedEmbeddingSpaceId))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                "The memory mutation did not name its embedding space.");
        }

        ParticipantRosterSnapshot roster;
        try
        {
            roster = request.Roster.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                $"The participant roster is invalid: {ex.Message}");
        }

        var proposal = request.Proposal;
        if (proposal is null)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "The configured model did not supply a typed memory proposal.");
        }
        if (!Enum.IsDefined(proposal.Operation)
            || !Enum.IsDefined(proposal.ClaimKind)
            || !Enum.IsDefined(proposal.EvidenceKind)
            || !Enum.IsDefined(proposal.Visibility)
            || !Enum.IsDefined(proposal.Sensitivity))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "The typed memory proposal contains an unsupported enum value.");
        }

        var authorityFailure = ValidateAuthorityContext(
            roster,
            request.Authority,
            proposal.Operation.ToString(),
            request.RequestId,
            now,
            receiptAuthority);
        if (authorityFailure is not null)
        {
            return new(false, null, null, null, [], authorityFailure);
        }

        var targetId = NormalizeOptional(proposal.TargetMemoryId);
        if (targetId is { Length: > 128 }
            || targetId?.Any(char.IsControl) == true)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "The exact target memory ID is malformed.");
        }
        var text = proposal.Text?.Trim() ?? string.Empty;
        var category = proposal.Category?.Trim() ?? string.Empty;
        if (proposal.Operation is ParticipantMemoryMutationKind.Add
            or ParticipantMemoryMutationKind.Correct
            or ParticipantMemoryMutationKind.Dispute)
        {
            if (text.Length is 0 or > ParticipantMemoryLimits.MaximumMemoryTextLength)
            {
                return ParticipantMemoryValidationResult.Rejected(
                    request,
                    ParticipantMemoryFailureCode.InvalidProposal,
                    $"Memory text must contain between 1 and {ParticipantMemoryLimits.MaximumMemoryTextLength} characters.");
            }
            if (ContainsUnsupportedContentControl(text))
            {
                return ParticipantMemoryValidationResult.Rejected(
                    request,
                    ParticipantMemoryFailureCode.InvalidProposal,
                    "Memory text contains an unsupported control character.");
            }
            if (category.Length is 0 or > ParticipantMemoryLimits.MaximumCategoryLength)
            {
                return ParticipantMemoryValidationResult.Rejected(
                    request,
                    ParticipantMemoryFailureCode.InvalidProposal,
                    $"Memory category must contain between 1 and {ParticipantMemoryLimits.MaximumCategoryLength} characters.");
            }
            if (ContainsUnsupportedContentControl(category))
            {
                return ParticipantMemoryValidationResult.Rejected(
                    request,
                    ParticipantMemoryFailureCode.InvalidProposal,
                    "Memory category contains an unsupported control character.");
            }
        }

        var targetRequired = proposal.Operation is not ParticipantMemoryMutationKind.Add;
        if (targetRequired != (targetId is not null))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                targetRequired
                    ? "This mutation requires one exact memory ID."
                    : "A new memory must not name an existing mutation target.");
        }

        if (!double.IsFinite(proposal.AttributionConfidence)
            || proposal.AttributionConfidence is < 0 or > 1)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "Attribution confidence must be between zero and one.");
        }

        var reportedBy = NormalizeOptional(proposal.ReportedByParticipantReference);
        var sharedEvent = NormalizeOptional(proposal.SharedEventReference);
        var boundedReferences = new[]
            {
                NormalizeOptional(proposal.SpeakerParticipantReference),
                reportedBy,
                sharedEvent
            }
            .Concat(proposal.SubjectParticipantReferences ?? [])
            .Concat(proposal.WitnessParticipantReferences ?? [])
            .Concat(proposal.AudienceParticipantReferences ?? [])
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        if (boundedReferences.Any(reference =>
                reference.Length is 0 or > 128
                || reference.Any(char.IsControl)))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "A participant or shared-event reference is malformed.");
        }
        var roleReferences = NormalizeReferences(
            proposal.SpeakerParticipantReference,
            proposal.SubjectParticipantReferences,
            proposal.WitnessParticipantReferences)
            .Concat(reportedBy is null ? [] : [reportedBy])
            .Distinct(IdComparer)
            .ToArray();
        var requestingPrincipal = NormalizeOptional(
            request.Authority.RequestingParticipantReference);
        var lifecycleActors = new[]
            {
                NormalizeOptional(proposal.SpeakerParticipantReference),
                reportedBy
            }
            .Concat(proposal.SubjectParticipantReferences ?? [])
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(IdComparer)
            .ToArray();
        if (roleReferences.Length > ParticipantMemoryLimits.MaximumReferencesPerRole * 2 + 1)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "The proposal contains too many participant role references.");
        }
        var unknownReference = roleReferences.FirstOrDefault(reference => roster.Find(reference) is null);
        if (unknownReference is not null)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.UnknownParticipant,
                $"Participant reference '{unknownReference}' was not present in the admitted roster.");
        }

        var subjects = NormalizeList(proposal.SubjectParticipantReferences);
        var witnesses = NormalizeList(proposal.WitnessParticipantReferences);
        var audience = NormalizeList(proposal.AudienceParticipantReferences);
        if (subjects.Count > ParticipantMemoryLimits.MaximumReferencesPerRole
            || witnesses.Count > ParticipantMemoryLimits.MaximumReferencesPerRole
            || audience.Count > ParticipantMemoryLimits.MaximumReferencesPerRole)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "A participant role or audience exceeds the bounded reference count.");
        }
        var consentBoundReferences = roleReferences
            .Concat(proposal.Visibility is ParticipantMemoryVisibility.Private
                or ParticipantMemoryVisibility.Shared
                    ? audience
                    : [])
            .Distinct(IdComparer)
            .ToArray();
        var nonRegisteredConsentReference = consentBoundReferences.FirstOrDefault(reference =>
            roster.Find(reference)?.Kind is not ParticipantReferenceKind.Registered);
        if (nonRegisteredConsentReference is not null)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.AmbiguousIdentity,
                "Ambiguous or guest identities cannot be forced into durable attribution; roles and audiences require registered profiles that can complete explicit selection and consent.");
        }
        if (proposal.Operation == ParticipantMemoryMutationKind.Add
            && (requestingPrincipal is null
                || !lifecycleActors.Contains(requestingPrincipal, IdComparer)))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "A new durable memory must name the requesting participant as its speaker, subject, or reporter so its lifecycle remains reachable.");
        }

        var audienceFailure = ValidateAudience(request, roster, audience);
        if (audienceFailure is not null)
        {
            return new(false, null, null, null, [], audienceFailure);
        }

        if (proposal.Visibility == ParticipantMemoryVisibility.General
            && proposal.Sensitivity == ParticipantMemorySensitivity.Sensitive)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.PermissionDenied,
                "Sensitive memory cannot use the installation-wide general audience.");
        }

        ParticipantMemoryProvenance provenance;
        try
        {
            provenance = request.Provenance.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                $"Memory provenance is invalid: {ex.Message}");
        }
        if (provenance.CapturedUtc == default
            || new[]
                {
                    provenance.SourceTurnId,
                    provenance.SourceMessageId,
                    provenance.SourceChannel,
                    provenance.ReportedByParticipantReference
                }
                .Where(value => value is not null)
                .Any(value => value!.Length > 128 || value.Any(char.IsControl))
            || JsonSerializer.Serialize(provenance, WorkerJsonOptions).Length > 4_096
            || JsonSerializer.Serialize(request.ConsentReceipts ?? [], WorkerJsonOptions).Length > 16_384)
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.InvalidProposal,
                "Memory provenance or consent receipts exceed the worker protocol bound.");
        }
        if (!string.Equals(provenance.SourceTurnId, roster.TurnId, StringComparison.Ordinal))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.StaleRoster,
                "Memory provenance does not match the admitted turn.");
        }

        var consentFailure = ValidateConsent(
            request,
            roster,
            audience,
            roleReferences,
            now,
            receiptAuthority);
        if (consentFailure is not null)
        {
            return new(false, null, null, null, [], consentFailure);
        }

        if (RequiresAuthentication(proposal.Operation, proposal.Sensitivity)
            && !HasAuthentication(
                request.Authority,
                proposal.Operation,
                now,
                receiptAuthority))
        {
            return ParticipantMemoryValidationResult.Rejected(
                request,
                ParticipantMemoryFailureCode.AuthenticationRequired,
                "This memory operation requires an independent authenticated principal.");
        }

        return new(
            true,
            roster,
            proposal with
            {
                TargetMemoryId = targetId,
                Text = text,
                Category = category,
                SpeakerParticipantReference = NormalizeOptional(proposal.SpeakerParticipantReference),
                SubjectParticipantReferences = subjects,
                WitnessParticipantReferences = witnesses,
                SharedEventReference = sharedEvent,
                AudienceParticipantReferences = audience,
                ReportedByParticipantReference = reportedBy
            },
            provenance with
            {
                ReportedByParticipantReference = reportedBy
            },
            request.ConsentReceipts?.ToArray() ?? [],
            null);
    }

    private static JsonSerializerOptions CreateWorkerJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static bool ContainsUnsupportedContentControl(string value) =>
        value.Any(character => char.IsControl(character)
            && character is not ('\r' or '\n' or '\t'));

    public static IReadOnlyList<string> BuildRecordAccessKeys(ParticipantMemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return BuildAccessKeys(
            record.Visibility,
            record.Sensitivity,
            record.AudienceParticipantReferences);
    }

    public static IReadOnlyList<string> BuildAccessKeys(
        ParticipantMemoryVisibility visibility,
        ParticipantMemorySensitivity memorySensitivity,
        IReadOnlyList<string> audienceParticipantReferences)
    {
        var sensitivity = memorySensitivity == ParticipantMemorySensitivity.Sensitive
            ? "sensitive"
            : "low";
        var audience = NormalizeList(audienceParticipantReferences);
        return visibility switch
        {
            ParticipantMemoryVisibility.General => [$"scope:general:{sensitivity}"],
            ParticipantMemoryVisibility.Private or ParticipantMemoryVisibility.Shared =>
                audience
                    .Select(reference => $"participant:{reference}:{sensitivity}")
                    .Distinct(IdComparer)
                    .Order(IdComparer)
                    .ToArray(),
            ParticipantMemoryVisibility.TeamProject => audience
                .Select(reference => $"team:{reference}:{sensitivity}")
                .Distinct(IdComparer)
                .Order(IdComparer)
                .ToArray(),
            _ => []
        };
    }

    public static IReadOnlyList<string> BuildAuthorizedRecallKeys(
        ParticipantMemoryAuthorityContext authority,
        DateTimeOffset now,
        ParticipantMemoryReceiptAuthority receiptAuthority,
        string operation = "Read")
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(receiptAuthority);
        var permission = authority.Permission;
        if (permission is null
            || !receiptAuthority.IsIssued(permission)
            || !permission.IsCurrent(now)
            || !permission.Grants(operation))
        {
            return [];
        }

        var keys = new HashSet<string>(IdComparer) { "scope:general:low" };
        var principal = NormalizeOptional(authority.RequestingParticipantReference);
        if (principal is not null
            && string.Equals(
                permission.PrincipalParticipantReference,
                principal,
                StringComparison.Ordinal))
        {
            keys.Add($"participant:{principal}:low");
        }
        var authentication = authority.Authentication;
        if (authentication is not null
            && receiptAuthority.IsIssued(authentication)
            && authentication.IsCurrent(now)
            && authentication.UsesIndependentTrustedFactor
            && principal is not null
            && string.Equals(
                authentication.PrincipalParticipantReference,
                principal,
                StringComparison.Ordinal)
            && authentication.GrantedOperations.Any(value => string.Equals(
                value,
                operation,
                StringComparison.OrdinalIgnoreCase)))
        {
            keys.Add($"participant:{principal}:sensitive");
        }

        return keys.Order(IdComparer).Take(ParticipantMemoryLimits.MaximumAudienceKeys).ToArray();
    }

    public static ParticipantMemoryFailureReceipt? ValidateAuthorityContext(
        ParticipantRosterSnapshot roster,
        ParticipantMemoryAuthorityContext authority,
        string operation,
        string requestId,
        DateTimeOffset now,
        ParticipantMemoryReceiptAuthority receiptAuthority)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(receiptAuthority);
        var principal = NormalizeOptional(authority.RequestingParticipantReference);
        if (principal is null)
        {
            return Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                operation,
                requestId,
                "Anonymous access is not an authority for participant memory.");
        }

        var participant = roster.Find(principal);
        if (participant is null)
        {
            return Failure(
                ParticipantMemoryFailureCode.UnknownParticipant,
                operation,
                requestId,
                "The requesting principal is not present in the admitted roster.");
        }
        if (participant.Kind == ParticipantReferenceKind.Unknown)
        {
            return Failure(
                ParticipantMemoryFailureCode.AmbiguousIdentity,
                operation,
                requestId,
                "An unknown presence cannot become memory authority.");
        }

        var permission = authority.Permission;
        if (permission is null
            || string.IsNullOrWhiteSpace(permission.ReceiptId)
            || !receiptAuthority.IsIssued(permission)
            || !permission.IsCurrent(now)
            || !permission.Grants(operation)
            || !string.Equals(
                permission.PrincipalParticipantReference,
                principal,
                StringComparison.Ordinal))
        {
            return Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                operation,
                requestId,
                "Participant memory requires a current exact-operation permission receipt.");
        }

        var explicitlySelected = string.Equals(
            roster.SelectedParticipantReference,
            principal,
            StringComparison.Ordinal);
        var authentication = authority.Authentication;
        var independentlyAuthenticated = authentication is not null
            && IsExactAuthentication(
                authentication,
                principal,
                operation,
                now,
                receiptAuthority);
        if (authentication is not null && !independentlyAuthenticated)
        {
            return Failure(
                ParticipantMemoryFailureCode.AuthenticationRequired,
                operation,
                requestId,
                "A supplied authentication receipt was not issued for this exact principal and operation.");
        }
        if (NormalizeList(authority.TeamProjectAudienceKeys).Count != 0)
        {
            return Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                operation,
                requestId,
                "Team/project audience keys require a trusted membership authority that is not configured.");
        }
        return explicitlySelected || independentlyAuthenticated
            ? null
            : Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                operation,
                requestId,
                "Presence or recognition does not establish participant-memory authority.");
    }

    public static ParticipantMemoryFailureReceipt Failure(
        ParticipantMemoryFailureCode code,
        string operation,
        string requestId,
        string safeMessage,
        bool retryable = false) =>
        new(
            code,
            string.IsNullOrWhiteSpace(operation) ? "memory" : operation.Trim(),
            string.IsNullOrWhiteSpace(requestId) ? "unknown-request" : requestId.Trim(),
            string.IsNullOrWhiteSpace(safeMessage) ? "Memory failed safely." : safeMessage.Trim(),
            retryable,
            DateTimeOffset.UtcNow);

    private static ParticipantMemoryFailureReceipt? ValidateAudience(
        ParticipantMemoryMutationRequest request,
        ParticipantRosterSnapshot roster,
        IReadOnlyList<string> audience)
    {
        switch (request.Proposal.Visibility)
        {
            case ParticipantMemoryVisibility.General when audience.Count != 0:
                return Failure(
                    ParticipantMemoryFailureCode.InvalidProposal,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "General memory must not carry a hidden explicit participant audience.");
            case ParticipantMemoryVisibility.Private when audience.Count != 1:
                return Failure(
                    ParticipantMemoryFailureCode.InvalidProposal,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "Private memory requires exactly one explicit participant audience member.");
            case ParticipantMemoryVisibility.Shared when audience.Count < 2:
                return Failure(
                    ParticipantMemoryFailureCode.InvalidProposal,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "Shared memory requires at least two explicit participant audience members.");
            case ParticipantMemoryVisibility.TeamProject when audience.Count == 0:
                return Failure(
                    ParticipantMemoryFailureCode.InvalidProposal,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "Team/project memory requires an authoritative configured audience key.");
        }

        if (request.Proposal.Visibility == ParticipantMemoryVisibility.TeamProject)
        {
            return Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "Team/project memory is unavailable until a trusted local membership authority is configured.");
        }
        else
        {
            var unknownAudience = audience.FirstOrDefault(reference => roster.Find(reference) is null);
            if (unknownAudience is not null)
            {
                return Failure(
                    ParticipantMemoryFailureCode.UnknownParticipant,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    $"Audience reference '{unknownAudience}' was not present in the admitted roster.");
            }

            var requestingPrincipal = NormalizeOptional(
                request.Authority.RequestingParticipantReference);
            if (request.Proposal.Operation is ParticipantMemoryMutationKind.Add
                    or ParticipantMemoryMutationKind.Correct
                    or ParticipantMemoryMutationKind.Dispute
                && request.Proposal.Visibility is ParticipantMemoryVisibility.Private
                    or ParticipantMemoryVisibility.Shared
                && (requestingPrincipal is null
                    || !audience.Contains(requestingPrincipal, IdComparer)))
            {
                return Failure(
                    ParticipantMemoryFailureCode.PermissionDenied,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "A newly written participant memory must include the requesting participant in its exact audience.");
            }
        }

        return null;
    }

    private static ParticipantMemoryFailureReceipt? ValidateConsent(
        ParticipantMemoryMutationRequest request,
        ParticipantRosterSnapshot roster,
        IReadOnlyList<string> audience,
        IReadOnlyList<string> roleReferences,
        DateTimeOffset now,
        ParticipantMemoryReceiptAuthority receiptAuthority)
    {
        if (request.Proposal.Operation is ParticipantMemoryMutationKind.Delete
            or ParticipantMemoryMutationKind.Revoke
            or ParticipantMemoryMutationKind.Archive)
        {
            return null;
        }

        var receipts = request.ConsentReceipts ?? [];
        static bool Reference(string? value, int maximum = 128) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximum
            && !value.Any(char.IsControl);
        static bool References(IReadOnlyList<string>? values) =>
            values is not null
            && values.Count <= ParticipantMemoryLimits.MaximumAudienceKeys
            && values.All(value => Reference(value))
            && values.Distinct(IdComparer).Count() == values.Count;
        if (receipts.Any(receipt => receipt is null
                || !Reference(receipt.ReceiptId)
                || !Reference(receipt.GrantedByParticipantReference)
                || !Reference(receipt.Operation)
                || !Reference(receipt.ProposalFingerprint, 256)
                || !Reference(receipt.ConsentSessionId)
                || !Reference(receipt.SourceTurnId)
                || receipt.GrantedUtc == default
                || !Enum.IsDefined(receipt.Visibility)
                || !References(receipt.AudienceParticipantReferences)
                || (receipt.ExpiresUtc is not null
                    && receipt.ExpiresUtc <= receipt.GrantedUtc)))
        {
            return Failure(
                ParticipantMemoryFailureCode.ConsentRequired,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "Consent receipts contain malformed or unbounded authority fields.");
        }
        var required = new HashSet<string>(roleReferences, IdComparer);
        required.UnionWith(audience.Where(reference => roster.Find(reference) is not null));
        var requestingPrincipal = NormalizeOptional(
            request.Authority.RequestingParticipantReference);
        if (requestingPrincipal is not null)
        {
            required.Add(requestingPrincipal);
        }
        if (required.Count == 0)
        {
            return Failure(
                ParticipantMemoryFailureCode.ConsentRequired,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "Durable participant memory requires mechanically issued scoped consent.");
        }

        var proposalFingerprint = ParticipantMemoryProposalFingerprint.Create(
            request.Proposal,
            roster.TenantId);
        var issuedReceipts = receipts
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt.ReceiptId)
                && receiptAuthority.IsIssued(receipt))
            .ToArray();
        if (issuedReceipts.Length != receipts.Count
            || issuedReceipts.Length != required.Count
            || issuedReceipts.Select(receipt => receipt.GrantedByParticipantReference)
                .Distinct(IdComparer).Count() != required.Count
            || issuedReceipts.Select(receipt => receipt.ConsentSessionId)
                .Distinct(IdComparer).Count() != 1
            || issuedReceipts.Any(receipt =>
                string.IsNullOrWhiteSpace(receipt.ConsentSessionId)
                || !string.Equals(
                    receipt.ProposalFingerprint,
                    proposalFingerprint,
                    StringComparison.Ordinal)))
        {
            return Failure(
                ParticipantMemoryFailureCode.ConsentRequired,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "Consent receipts must be one exact, unconsumed proposal session covering every required participant once.");
        }

        foreach (var participantReference in required)
        {
            var participant = roster.Find(participantReference)!;
            if (participant.Kind == ParticipantReferenceKind.Unknown)
            {
                return Failure(
                    ParticipantMemoryFailureCode.AmbiguousIdentity,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "An unknown participant cannot be forced into durable attribution.");
            }

            var receipt = receipts.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.GrantedByParticipantReference,
                    participantReference,
                    StringComparison.Ordinal)
                && string.Equals(
                    candidate.Operation,
                    request.Proposal.Operation.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    candidate.SourceTurnId,
                    roster.TurnId,
                    StringComparison.Ordinal)
                && candidate.Visibility == request.Proposal.Visibility
                && candidate.IsCurrent(now));
            if (receipt is null)
            {
                return Failure(
                    ParticipantMemoryFailureCode.ConsentRequired,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    $"Scoped consent is missing for participant '{participantReference}'.");
            }

            var receiptAudience = new HashSet<string>(
                NormalizeList(receipt.AudienceParticipantReferences),
                IdComparer);
            if (!receiptAudience.SetEquals(audience))
            {
                return Failure(
                    ParticipantMemoryFailureCode.ConsentRequired,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "A consent receipt does not cover the proposed audience.");
            }
        }

        if (!receiptAuthority.TryBindConsentSession(receipts, request.RequestId))
        {
            return Failure(
                ParticipantMemoryFailureCode.ConsentRequired,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "The exact consent session is already bound to another mutation request or the bounded binding ledger is full.");
        }

        return null;
    }

    private static bool RequiresAuthentication(
        ParticipantMemoryMutationKind operation,
        ParticipantMemorySensitivity sensitivity) =>
        sensitivity == ParticipantMemorySensitivity.Sensitive
        || operation is ParticipantMemoryMutationKind.Correct
            or ParticipantMemoryMutationKind.Dispute
            or ParticipantMemoryMutationKind.Revoke
            or ParticipantMemoryMutationKind.Archive
            or ParticipantMemoryMutationKind.Delete;

    private static bool HasAuthentication(
        ParticipantMemoryAuthorityContext authority,
        ParticipantMemoryMutationKind operation,
        DateTimeOffset now,
        ParticipantMemoryReceiptAuthority receiptAuthority)
    {
        var principal = NormalizeOptional(authority.RequestingParticipantReference);
        var receipt = authority.Authentication;
        return principal is not null
            && receipt is not null
            && IsExactAuthentication(
                receipt,
                principal,
                operation.ToString(),
                now,
                receiptAuthority);
    }

    private static bool IsExactAuthentication(
        ParticipantMemoryAuthenticationReceipt receipt,
        string principal,
        string operation,
        DateTimeOffset now,
        ParticipantMemoryReceiptAuthority receiptAuthority) =>
        !string.IsNullOrWhiteSpace(receipt.ReceiptId)
            && receiptAuthority.IsIssued(receipt)
            && receipt.IsCurrent(now)
            && receipt.UsesIndependentTrustedFactor
            && string.Equals(
                receipt.PrincipalParticipantReference,
                principal,
                StringComparison.Ordinal)
            && receipt.GrantedOperations.Any(value => string.Equals(
                value,
                operation,
                StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> NormalizeReferences(
        string? speaker,
        IReadOnlyList<string>? subjects,
        IReadOnlyList<string>? witnesses)
    {
        var values = new List<string>();
        var normalizedSpeaker = NormalizeOptional(speaker);
        if (normalizedSpeaker is not null)
        {
            values.Add(normalizedSpeaker);
        }
        values.AddRange(NormalizeList(subjects));
        values.AddRange(NormalizeList(witnesses));
        return values.Distinct(IdComparer).ToArray();
    }

    private static IReadOnlyList<string> NormalizeList(IReadOnlyList<string>? values) =>
        (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(IdComparer)
        .ToArray();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
