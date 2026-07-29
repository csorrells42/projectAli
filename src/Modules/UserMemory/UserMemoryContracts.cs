namespace Ali.Modules.UserMemory;

public sealed record ActiveUser(
    string StableId,
    string DisplayName,
    bool IsTestProfile,
    string ResolutionMethod)
{
    public ActiveUser Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StableId);
        return this with
        {
            StableId = StableId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "Current user" : DisplayName.Trim(),
            ResolutionMethod = string.IsNullOrWhiteSpace(ResolutionMethod) ? "explicit-selection" : ResolutionMethod.Trim()
        };
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
    string Source);

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
        string correction,
        CancellationToken cancellationToken);

    Task<MemoryOperationResult> ForgetAsync(
        ActiveUser user,
        string request,
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

    event EventHandler<ActiveUser>? Changed;

    ActiveUser Select(string stableId);

    void Refresh();
}
