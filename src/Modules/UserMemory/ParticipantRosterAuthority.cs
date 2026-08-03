namespace Ali.Modules.UserMemory;

public sealed record ParticipantRosterFreshness(
    bool IsCurrent,
    string CurrentSelectionGeneration,
    string CurrentPresenceGeneration);

public interface IParticipantRosterAuthority
{
    ParticipantRosterSnapshot CaptureAtAdmission(
        string turnId,
        string conversationId,
        ActiveUserSelectionSnapshot selection,
        string selectionGeneration,
        DateTimeOffset capturedUtc);

    ParticipantRosterFreshness CheckCurrent(ParticipantRosterSnapshot admittedRoster);
}

/// <summary>
/// Production adapter for Ali's explicit active-profile boundary. Camera and voice
/// observations are admitted only as advisory presence; selection remains the durable
/// participant authority and recognition is never authentication or consent.
/// </summary>
public sealed class SelectedParticipantRosterAuthority : IParticipantRosterAuthority
{
    internal const string NoPresenceGeneration = "participant-presence-none-v1";
    private const int MaximumConversationScopes = 4_096;
    private readonly IActiveUserSession _activeUsers;
    private readonly IParticipantPresenceSnapshotSource _presence;
    private readonly string _tenantId;
    private readonly Dictionary<string, ParticipantRosterSessionScope> _sessions =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _oldestSessions = new();
    private readonly object _sessionSync = new();

    public SelectedParticipantRosterAuthority(
        IActiveUserSession activeUsers,
        string tenantId,
        IParticipantPresenceSnapshotSource? presence = null)
    {
        _activeUsers = activeUsers ?? throw new ArgumentNullException(nameof(activeUsers));
        _presence = presence ?? new EmptyParticipantPresenceSnapshotSource();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        _tenantId = tenantId.Trim();
    }

    public ParticipantRosterSnapshot CaptureAtAdmission(
        string turnId,
        string conversationId,
        ActiveUserSelectionSnapshot selection,
        string selectionGeneration,
        DateTimeOffset capturedUtc)
    {
        var session = ResolveSession(conversationId);
        var registered = _activeUsers.AvailableUsers
            .Select(user => user.Normalize().StableId)
            .ToHashSet(StringComparer.Ordinal);
        var presence = _presence.Capture().Normalize();
        return ParticipantRosterFactory.Capture(
            _tenantId,
            turnId,
            conversationId,
            selection,
            selectionGeneration,
            presence.Generation,
            registered,
            presence.Observations,
            capturedUtc,
            session);
    }

    public ParticipantRosterFreshness CheckCurrent(ParticipantRosterSnapshot admittedRoster)
    {
        ArgumentNullException.ThrowIfNull(admittedRoster);
        var selectionGeneration = _activeUsers.CaptureSelectionRevision();
        var selection = _activeUsers.CaptureSelectionSnapshot();
        var presence = _presence.Capture().Normalize();
        var selectedReference = selection.IsResolved
            ? selection.SelectedUser!.Normalize().StableId
            : null;
        var current = string.Equals(
                admittedRoster.SelectionGeneration,
                selectionGeneration,
                StringComparison.Ordinal)
            && string.Equals(
                admittedRoster.PresenceGeneration,
                presence.Generation,
                StringComparison.Ordinal)
            && string.Equals(
                admittedRoster.SelectedParticipantReference,
                selectedReference,
                StringComparison.Ordinal);
        return new(current, selectionGeneration, presence.Generation);
    }

    private ParticipantRosterSessionScope ResolveSession(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_sessionSync)
        {
            if (_sessions.TryGetValue(conversationId, out var existing))
            {
                return existing;
            }
            while (_sessions.Count >= MaximumConversationScopes)
            {
                _sessions.Remove(_oldestSessions.Dequeue());
            }
            var created = new ParticipantRosterSessionScope(conversationId);
            _sessions.Add(conversationId, created);
            _oldestSessions.Enqueue(conversationId);
            return created;
        }
    }

    private sealed class EmptyParticipantPresenceSnapshotSource :
        IParticipantPresenceSnapshotSource
    {
        public ParticipantPresenceSnapshot Capture() => ParticipantPresenceSnapshot.None;
    }
}

/// <summary>
/// Opaque conversation-session mapping for unregistered observations. Caller-supplied
/// camera/voice IDs are never used as durable participant references.
/// </summary>
public sealed class ParticipantRosterSessionScope
{
    internal const int MaximumReferences = 256;
    private readonly Dictionary<string, string> _references = new(StringComparer.Ordinal);
    private readonly Queue<string> _oldestReferences = new();
    private readonly object _sync = new();

    public ParticipantRosterSessionScope(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ConversationId = conversationId.Trim();
        SessionId = Guid.NewGuid().ToString("N");
    }

    public string ConversationId { get; }

    public string SessionId { get; }

    internal string Resolve(ParticipantReferenceKind kind, string sourceReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);
        var prefix = kind == ParticipantReferenceKind.Guest ? "guest" : "unknown";
        var key = $"{prefix}:{sourceReference.Trim()}";
        lock (_sync)
        {
            if (!_references.TryGetValue(key, out var reference))
            {
                while (_references.Count >= MaximumReferences)
                {
                    _references.Remove(_oldestReferences.Dequeue());
                }
                reference = $"{prefix}:{Guid.NewGuid():N}";
                _references.Add(key, reference);
                _oldestReferences.Enqueue(key);
            }
            return reference;
        }
    }
}
