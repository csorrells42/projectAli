namespace Ali.Modules.UserMemory;

public sealed class Mem0UserMemoryService : IUserMemoryService, IAsyncDisposable
{
    // FastEmbed's first CPU model load can legitimately take well over eight
    // seconds on a cold machine. Killing and restarting the private worker at
    // that point discards all loading work and can turn one cold start into a
    // series of guaranteed foreground recall misses.
    internal static readonly TimeSpan WarmupAttemptTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan WarmupOverallTimeout = TimeSpan.FromSeconds(75);

    private readonly Mem0ProcessClient _client;
    private readonly Func<UserMemorySettings> _settings;
    private readonly object _warmupSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _warmupTask;

    internal Mem0UserMemoryService(Mem0ProcessClient client, Func<UserMemorySettings> settings)
    {
        _client = client;
        _settings = settings;
    }

    internal void BeginWarmup(ActiveUser user) =>
        _ = EnsureWarmupStarted(user);

    public async Task<IReadOnlyList<UserMemory>> RecallAsync(
        ActiveUser user,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var settings = _settings().Normalize();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(query)) return [];
        await EnsureWarmupStarted(user).WaitAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(new
        {
            operation = "recall",
            user = ToUser(user),
            query = query.Trim(),
            maximumResults = Math.Clamp(maximumResults, 1, 8)
        }, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message);
        }
        return FilterRecallMatches(response.Memories ?? [], settings, maximumResults);
    }

    public Task<MemoryOperationResult> RememberAsync(
        ActiveUser user,
        string conversation,
        string source,
        string? category,
        CancellationToken cancellationToken) =>
        OperateAsync(new { operation = "remember", user = ToUser(user), conversation, source, category }, cancellationToken);

    public Task<MemoryOperationResult> CorrectAsync(ActiveUser user, string correction, CancellationToken cancellationToken) =>
        OperateAsync(new { operation = "correct", user = ToUser(user), correction }, cancellationToken);

    public Task<MemoryOperationResult> ForgetAsync(ActiveUser user, string request, CancellationToken cancellationToken) =>
        OperateAsync(new { operation = "forget", user = ToUser(user), request }, cancellationToken);

    public async Task<IReadOnlyList<UserMemory>> ListAsync(ActiveUser user, string? category, CancellationToken cancellationToken)
    {
        if (!_settings().Enabled) return [];
        await EnsureWarmupStarted(user).WaitAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(new { operation = "list", user = ToUser(user), category }, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message);
        }
        return response.Memories ?? [];
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

    private Task EnsureWarmupStarted(ActiveUser user)
    {
        lock (_warmupSync)
        {
            return _warmupTask ??= WarmupAsync(user.Normalize());
        }
    }

    private async Task WarmupAsync(ActiveUser user)
    {
        if (!_settings().Normalize().Enabled) return;
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        overallTimeout.CancelAfter(WarmupOverallTimeout);
        for (var attempt = 1; attempt <= 3 && !overallTimeout.IsCancellationRequested; attempt++)
        {
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
            attemptTimeout.CancelAfter(WarmupAttemptTimeout);
            try
            {
                // Exercise the same embedding and Qdrant path used by foreground recall.
                // A health-only probe leaves the CPU embedding model cold and merely moves
                // the first-turn stall from process startup to the first actual question.
                var response = await SendAsync(new
                {
                    operation = "recall",
                    user = ToUser(user),
                    query = "personal memory retrieval readiness check",
                    maximumResults = 1
                }, attemptTimeout.Token).ConfigureAwait(false);
                if (!response.Success)
                {
                    throw new InvalidOperationException(response.Message);
                }
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException
                or IOException
                or InvalidOperationException
                or TimeoutException)
            {
                if (_lifetime.IsCancellationRequested || overallTimeout.IsCancellationRequested || attempt == 3)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), overallTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    internal static IReadOnlyList<UserMemory> FilterRecallMatches(
        IReadOnlyList<UserMemory> memories,
        UserMemorySettings settings,
        int maximumResults)
    {
        var normalized = settings.Normalize();
        var scored = memories
            .Where(memory => memory.Score.HasValue)
            .OrderByDescending(memory => memory.Score)
            .ToList();
        if (scored.Count == 0)
        {
            return memories.Take(Math.Clamp(maximumResults, 1, 8)).ToList();
        }

        var topScore = scored[0].Score!.Value;
        if (topScore < normalized.RecallMinimumScore)
        {
            return [];
        }

        var threshold = Math.Max(
            normalized.RecallMinimumScore,
            topScore - normalized.RecallScoreWindow);
        return scored
            .Where(memory => memory.Score!.Value >= threshold)
            .Take(Math.Clamp(maximumResults, 1, 8))
            .ToList();
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

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        Task? warmup;
        lock (_warmupSync) warmup = _warmupTask;
        if (warmup is not null)
        {
            try { await warmup.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
