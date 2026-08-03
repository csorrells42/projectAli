namespace Ali.Modules.UserMemory;

public sealed record ParticipantPresenceObservation(
    string SessionReferenceId,
    string? RegisteredProfileId,
    string DisplayLabel,
    ParticipantPresenceState Presence,
    string Source,
    double? Confidence);

/// <summary>
/// Converts immutable selection and advisory presence state into an immutable roster.
/// It never asserts who spoke and never converts recognition into authentication.
/// </summary>
public static class ParticipantRosterFactory
{
    public static ParticipantRosterSnapshot Capture(
        string tenantId,
        string turnId,
        string conversationId,
        ActiveUserSelectionSnapshot selection,
        string selectionGeneration,
        string presenceGeneration,
        IReadOnlySet<string> registeredProfileIds,
        IReadOnlyList<ParticipantPresenceObservation> observations,
        DateTimeOffset capturedUtc,
        ParticipantRosterSessionScope? sessionScope = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(registeredProfileIds);
        ArgumentNullException.ThrowIfNull(observations);
        sessionScope ??= new ParticipantRosterSessionScope(conversationId);
        if (!string.Equals(sessionScope.ConversationId, conversationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The participant session scope belongs to another conversation.",
                nameof(sessionScope));
        }
        var participants = new Dictionary<string, ParticipantReference>(StringComparer.Ordinal);
        string? selectedReference = null;
        if (selection.IsResolved)
        {
            var selected = selection.SelectedUser!.Normalize();
            selectedReference = selected.StableId;
            participants[selected.StableId] = new ParticipantReference(
                selected.StableId,
                selected.DisplayName,
                ParticipantReferenceKind.Registered,
                ParticipantPresenceState.Unknown,
                "explicit-profile-selection");
        }

        foreach (var observation in observations.Take(ParticipantMemoryLimits.MaximumParticipantsPerTurn))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.SessionReferenceId);
            var registeredId = string.IsNullOrWhiteSpace(observation.RegisteredProfileId)
                ? null
                : observation.RegisteredProfileId.Trim();
            string referenceId;
            ParticipantReferenceKind kind;
            if (registeredId is not null && registeredProfileIds.Contains(registeredId))
            {
                referenceId = registeredId;
                kind = ParticipantReferenceKind.Registered;
            }
            else if (registeredId is not null)
            {
                kind = ParticipantReferenceKind.Guest;
                referenceId = sessionScope.Resolve(kind, observation.SessionReferenceId);
            }
            else
            {
                kind = ParticipantReferenceKind.Unknown;
                referenceId = sessionScope.Resolve(kind, observation.SessionReferenceId);
            }

            if (!participants.ContainsKey(referenceId)
                && participants.Count >= ParticipantMemoryLimits.MaximumParticipantsPerTurn)
            {
                continue;
            }
            participants[referenceId] = new ParticipantReference(
                referenceId,
                observation.DisplayLabel,
                kind,
                observation.Presence,
                observation.Source,
                observation.Confidence).Normalize();
        }

        return new ParticipantRosterSnapshot(
            tenantId,
            turnId,
            conversationId,
            capturedUtc,
            selectionGeneration,
            presenceGeneration,
            selectedReference,
            participants.Values.OrderBy(value => value.ReferenceId, StringComparer.Ordinal).ToArray())
            .Normalize();
    }
}
