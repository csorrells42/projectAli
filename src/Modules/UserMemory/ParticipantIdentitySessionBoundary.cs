namespace Ali.Modules.UserMemory;

/// <summary>
/// Read-only participant-security view over the frozen active-user session. The
/// boundary consumes two matching immutable snapshots before publishing state and
/// advances its own generation when either selection or registry state changes.
/// </summary>
internal sealed class ParticipantIdentitySessionBoundary : IActiveUserSession
{
    private readonly IActiveUserSession _source;
    private readonly object _sync = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private SourceState? _published;
    private long _generation;

    public ParticipantIdentitySessionBoundary(IActiveUserSession source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public ActiveUser Current
    {
        get
        {
            var snapshot = CaptureState().Selection;
            return snapshot.IsResolved
                ? snapshot.SelectedUser!
                : throw new InvalidOperationException(
                    "Participant identity selection is unresolved.");
        }
    }

    public IReadOnlyList<ActiveUser> AvailableUsers =>
        CaptureState().AvailableUsers.ToArray();

    public bool RequiresSelection => CaptureState().Selection.RequiresSelection;

    public ActiveUserSelectionSnapshot CaptureSelectionSnapshot() =>
        CaptureState().Selection;

    public string CaptureSelectionRevision() => CaptureState().Revision;

    public event EventHandler<ActiveUser>? Changed
    {
        add => _source.Changed += value;
        remove => _source.Changed -= value;
    }

    public ActiveUser Select(string stableId) => throw new NotSupportedException(
        "The participant identity boundary is read-only; select profiles through the active-user session.");

    public void Refresh() => throw new NotSupportedException(
        "The participant identity boundary is read-only; refresh profiles through the active-user session.");

    private PublishedState CaptureState()
    {
        lock (_sync)
        {
            var first = CaptureSourceState();
            var second = CaptureSourceState();
            if (!SameSourceState(first, second))
            {
                throw new InvalidOperationException(
                    "Participant identity state changed while it was being captured.");
            }

            if (_published is null || !SameSourceState(_published, second))
            {
                _published = second;
                _generation++;
            }

            return new PublishedState(
                second.Selection,
                second.AvailableUsers,
                $"participant-identity-boundary-v1:{_instanceId}:{_generation}");
        }
    }

    private SourceState CaptureSourceState()
    {
        var revisionBefore = NormalizeRevision(_source.CaptureSelectionRevision());
        var selection = NormalizeSelection(_source.CaptureSelectionSnapshot());
        var available = (_source.AvailableUsers
                ?? throw new InvalidOperationException(
                    "The active-user registry returned no immutable snapshot."))
            .Select(user => user.Normalize())
            .OrderBy(user => user.StableId, StringComparer.Ordinal)
            .ToArray();
        var revisionAfter = NormalizeRevision(_source.CaptureSelectionRevision());
        if (!string.Equals(revisionBefore, revisionAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Participant identity selection changed while registry state was being captured.");
        }
        if (available
            .Select(user => user.StableId)
            .Distinct(StringComparer.Ordinal)
            .Count() != available.Length)
        {
            throw new InvalidOperationException(
                "The active-user registry contains duplicate stable identifiers.");
        }
        if (selection.IsResolved
            && !available.Contains(selection.SelectedUser!))
        {
            throw new InvalidOperationException(
                "The selected participant does not exactly match the active-user registry snapshot.");
        }

        return new SourceState(selection, available, revisionAfter);
    }

    private static ActiveUserSelectionSnapshot NormalizeSelection(
        ActiveUserSelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.IsResolved
            ? ActiveUserSelectionSnapshot.Resolved(selection.SelectedUser!.Normalize())
            : selection.RequiresSelection
                ? ActiveUserSelectionSnapshot.SelectionRequired
                : throw new InvalidOperationException(
                    "The active-user selection snapshot is neither resolved nor selection-required.");
    }

    private static string NormalizeRevision(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        var normalized = revision.Trim();
        if (normalized.Length > 512 || normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "The active-user selection revision is invalid.");
        }
        return normalized;
    }

    private static bool SameSourceState(SourceState left, SourceState right) =>
        string.Equals(left.SourceRevision, right.SourceRevision, StringComparison.Ordinal)
        && left.Selection == right.Selection
        && left.AvailableUsers.SequenceEqual(right.AvailableUsers);

    private sealed record SourceState(
        ActiveUserSelectionSnapshot Selection,
        ActiveUser[] AvailableUsers,
        string SourceRevision);

    private sealed record PublishedState(
        ActiveUserSelectionSnapshot Selection,
        ActiveUser[] AvailableUsers,
        string Revision);
}
