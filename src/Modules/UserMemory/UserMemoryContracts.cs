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
    string? ErrorCode = null)
{
    public static MemoryOperationResult Failed(string message, string code) =>
        new(false, message, [], code);
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

    event EventHandler<ActiveUser>? Changed;

    ActiveUser Select(string stableId);

    void Refresh();
}
