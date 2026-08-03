namespace Ali.Modules.UserMemory;

public sealed record ActiveUser(
    string StableId,
    string DisplayName,
    bool IsTestProfile,
    string ResolutionMethod,
    string? Address = null,
    string? Email = null,
    string? PhoneNumber = null)
{
    public ActiveUser Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StableId);
        return this with
        {
            StableId = StableId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "Current user" : DisplayName.Trim(),
            ResolutionMethod = string.IsNullOrWhiteSpace(ResolutionMethod) ? "explicit-selection" : ResolutionMethod.Trim(),
            Address = NormalizeOptional(Address),
            Email = NormalizeOptional(Email),
            PhoneNumber = NormalizeOptional(PhoneNumber)
        };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// An immutable, point-in-time view of the explicit active-user selection boundary.
/// A selection-required snapshot deliberately contains no provisional user.
/// </summary>
public sealed record ActiveUserSelectionSnapshot
{
    private ActiveUserSelectionSnapshot(bool requiresSelection, ActiveUser? selectedUser)
    {
        RequiresSelection = requiresSelection;
        SelectedUser = selectedUser;
    }

    public bool RequiresSelection { get; }

    public bool IsResolved => !RequiresSelection && SelectedUser is not null;

    public ActiveUser? SelectedUser { get; }

    public static ActiveUserSelectionSnapshot SelectionRequired { get; } =
        new(requiresSelection: true, selectedUser: null);

    public static ActiveUserSelectionSnapshot Resolved(ActiveUser selectedUser)
    {
        ArgumentNullException.ThrowIfNull(selectedUser);
        return new ActiveUserSelectionSnapshot(
            requiresSelection: false,
            selectedUser.Normalize());
    }
}

public sealed record UserMemory(
    string MemoryId,
    string Text,
    string Category,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? UpdatedUtc,
    double? Score,
    bool ExplicitlyTaught,
    string Source,
    double? SemanticScore = null,
    double? KeywordScore = null,
    double? EntityBoost = null);

public sealed record MemoryOperationResult(
    bool Success,
    string Message,
    IReadOnlyList<UserMemory> Memories,
    string? ErrorCode = null,
    string? RequestId = null,
    string? MutationStatus = null)
{
    public static MemoryOperationResult Failed(
        string message,
        string code,
        string? requestId = null,
        string? mutationStatus = null) =>
        new(false, message, [], code, requestId, mutationStatus);
}

public sealed record UserMemoryStatus(
    bool Enabled,
    bool RuntimeAvailable,
    bool QdrantAvailable,
    string State,
    string Message,
    int CurrentUserMemoryCount = 0);

public interface IUserMemoryService
{
    Task<IReadOnlyList<UserMemory>> RecallAsync(
        ActiveUser user,
        string query,
        int maximumResults,
        CancellationToken cancellationToken);

    Task<MemoryOperationResult> RememberAsync(
        ActiveUser user,
        string conversation,
        string source,
        string? category,
        CancellationToken cancellationToken);

    Task<MemoryOperationResult> CorrectAsync(
        ActiveUser user,
        string memoryId,
        string correction,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserMemory>> ListAsync(
        ActiveUser user,
        string? category,
        CancellationToken cancellationToken);

    Task<MemoryOperationResult> DeleteAsync(
        ActiveUser user,
        string memoryId,
        CancellationToken cancellationToken);

    Task<UserMemoryStatus> TestAsync(ActiveUser user, CancellationToken cancellationToken);
}

public interface IParticipantMemoryDesktopReviewService
{
    Task<IReadOnlyList<UserMemory>> RecallDesktopParticipantsAsync(
        ActiveUser user,
        string query,
        int maximumResults,
        bool includeSensitive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserMemory>> ListDesktopParticipantsAsync(
        ActiveUser user,
        string? category,
        bool includeSensitive,
        CancellationToken cancellationToken);

    Task<ParticipantMemoryDesktopReviewResult> ReviewDesktopParticipantsAsync(
        ActiveUser user,
        string? category,
        bool includeSensitive,
        CancellationToken cancellationToken);

    Task<ParticipantMemoryReconciliationResult> ReconcileDesktopParticipantMutationAsync(
        ActiveUser user,
        string mutationRequestId,
        CancellationToken cancellationToken);
}

public sealed record ParticipantMemoryDesktopReviewResult(
    bool Success,
    IReadOnlyList<UserMemory> Memories,
    string Message,
    ParticipantMemoryFailureCode? FailureCode = null)
{
    public static ParticipantMemoryDesktopReviewResult Failed(
        string message,
        ParticipantMemoryFailureCode code) =>
        new(false, [], message, code);
}

public interface IActiveUserSession
{
    ActiveUser Current { get; }

    IReadOnlyList<ActiveUser> AvailableUsers { get; }

    bool RequiresSelection { get; }

    /// <summary>
    /// Captures the selection boundary once. Implementations should override this method and
    /// read selection state atomically; the default preserves compatibility for legacy sessions.
    /// </summary>
    ActiveUserSelectionSnapshot CaptureSelectionSnapshot() =>
        RequiresSelection
            ? ActiveUserSelectionSnapshot.SelectionRequired
            : ActiveUserSelectionSnapshot.Resolved(Current);

    /// <summary>
    /// Returns an in-process selection generation. Production sessions override this
    /// so selecting A, then B, then A cannot revive a lease captured for the first A.
    /// </summary>
    string CaptureSelectionRevision()
    {
        var snapshot = CaptureSelectionSnapshot();
        return snapshot.IsResolved
            ? $"legacy-active-user:{snapshot.SelectedUser!.StableId}"
            : "legacy-active-user:selection-required";
    }

    event EventHandler<ActiveUser>? Changed;

    ActiveUser Select(string stableId);

    void Refresh();
}
