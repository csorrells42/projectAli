namespace Ali.Modules.UserMemory;

public sealed class Mem0UserMemoryService : IUserMemoryService, IAsyncDisposable
{
    private readonly Mem0ProcessClient _client;
    private readonly Func<UserMemorySettings> _settings;

    internal Mem0UserMemoryService(Mem0ProcessClient client, Func<UserMemorySettings> settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<IReadOnlyList<UserMemory>> RecallAsync(
        ActiveUser user,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (!_settings().Enabled || string.IsNullOrWhiteSpace(query)) return [];
        var response = await SendAsync(new
        {
            operation = "recall",
            user = ToUser(user),
            query = query.Trim(),
            maximumResults = Math.Clamp(maximumResults, 1, 8)
        }, cancellationToken).ConfigureAwait(false);
        return response.Success ? response.Memories ?? [] : [];
    }

    public Task<MemoryOperationResult> RememberAsync(ActiveUser user, string conversation, string source, CancellationToken cancellationToken) =>
        OperateAsync(new { operation = "remember", user = ToUser(user), conversation, source }, cancellationToken);

    public Task<MemoryOperationResult> CorrectAsync(ActiveUser user, string correction, CancellationToken cancellationToken) =>
        OperateAsync(new { operation = "correct", user = ToUser(user), correction }, cancellationToken);

    public Task<MemoryOperationResult> ForgetAsync(ActiveUser user, string request, CancellationToken cancellationToken) =>
        OperateAsync(new { operation = "forget", user = ToUser(user), request }, cancellationToken);

    public async Task<IReadOnlyList<UserMemory>> ListAsync(ActiveUser user, string? category, CancellationToken cancellationToken)
    {
        if (!_settings().Enabled) return [];
        var response = await SendAsync(new { operation = "list", user = ToUser(user), category }, cancellationToken).ConfigureAwait(false);
        return response.Success ? response.Memories ?? [] : [];
    }

    public Task<MemoryOperationResult> DeleteAsync(ActiveUser user, string memoryId, CancellationToken cancellationToken) =>
        OperateAsync(new { operation = "delete", user = ToUser(user), memoryId }, cancellationToken);

    public async Task<UserMemoryStatus> TestAsync(ActiveUser user, CancellationToken cancellationToken)
    {
        if (!_settings().Enabled) return new(false, false, false, "Disabled", "Per-user memory is disabled.");
        try
        {
            var response = await SendAsync(new { operation = "health", user = ToUser(user) }, cancellationToken).ConfigureAwait(false);
            return new(true, response.Success, response.Success, response.Success ? "Ready" : "Unavailable", response.Message, response.Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return new(true, false, false, "Unavailable", $"Memory failed safely: {ex.Message}");
        }
    }

    private async Task<MemoryOperationResult> OperateAsync(object request, CancellationToken cancellationToken)
    {
        if (!_settings().Enabled) return MemoryOperationResult.Failed("Per-user memory is disabled.", "disabled");
        try
        {
            var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return new(response.Success, response.Message, response.Memories ?? [], response.ErrorCode);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return MemoryOperationResult.Failed($"Memory failed safely: {ex.Message}", "unavailable");
        }
    }

    private async Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static object ToUser(ActiveUser user)
    {
        var normalized = user.Normalize();
        return new
        {
            stableId = normalized.StableId,
            displayName = normalized.DisplayName,
            isTestProfile = normalized.IsTestProfile,
            resolutionMethod = normalized.ResolutionMethod
        };
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
