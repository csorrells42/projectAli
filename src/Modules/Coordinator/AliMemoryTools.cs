using System.ComponentModel;
using System.Text;
using Ali.Modules.Memory;
using Ali.Modules.UserMemory;

namespace Ali.Modules.Coordinator;

internal sealed class AliMemoryTools
{
    private const int MaximumResults = 8;
    private readonly IMemoryStore? _legacyMemories;
    private readonly IUserMemoryService? _userMemories;
    private readonly IActiveUserSession? _activeUsers;
    private readonly Func<UserMemorySettings>? _settings;
    private readonly Func<CoordinatorTurnContext?> _turnAccessor;

    public AliMemoryTools(IMemoryStore memories, Func<CoordinatorTurnContext?> turnAccessor)
    {
        _legacyMemories = memories;
        _turnAccessor = turnAccessor;
    }

    public AliMemoryTools(
        IUserMemoryService memories,
        IActiveUserSession activeUsers,
        Func<UserMemorySettings> settings,
        Func<CoordinatorTurnContext?> turnAccessor)
    {
        _userMemories = memories;
        _activeUsers = activeUsers;
        _settings = settings;
        _turnAccessor = turnAccessor;
    }

    public Task<CoordinatorMemoryResult> SearchAsync(
        [Description("The personal fact or prior detail to recall.")] string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_userMemories is not null && _activeUsers is not null)
        {
            if (_activeUsers.RequiresSelection)
            {
                return Task.FromResult(new CoordinatorMemoryResult(
                    "Select the active user profile before Ali accesses personal memory.",
                    [],
                    ["Personal memory was skipped because more than one identity profile is available and none was explicitly selected."]));
            }
            return SearchPerUserAsync(query, cancellationToken);
        }

        var result = _legacyMemories!.List();
        var queryTerms = Tokenize(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = result.Memories
            .Where(memory => memory.Active)
            .Select(memory => new { Memory = memory, Score = ScoreMemory(memory, query, queryTerms) })
            .Where(item => queryTerms.Count == 0 || item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Memory.UpdatedAt)
            .Take(MaximumResults)
            .Select(item => new CoordinatorMemoryItem(
                item.Memory.MemoryId,
                item.Memory.Text,
                item.Memory.Category,
                item.Memory.UpdatedAt))
            .ToList();

        return Task.FromResult(new CoordinatorMemoryResult(
            matches.Count == 0 ? "No matching saved memory was found." : $"Found {matches.Count} matching saved memories.",
            matches,
            result.Warnings));
    }

    public Task<CoordinatorMemoryWriteResult> RememberAsync(
        [Description("The exact fact the user explicitly asked Ali to remember.")] string fact,
        [Description("A short category such as person, preference, location, project, or general.")] string? category,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(fact))
        {
            return Task.FromResult(new CoordinatorMemoryWriteResult(false, "Nothing was saved because the fact was empty."));
        }

        var sensitivity = MemoryRequestParser.Evaluate($"remember that {fact}").Sensitivity;
        if (sensitivity == MemorySensitivity.PotentiallySensitive)
        {
            return Task.FromResult(new CoordinatorMemoryWriteResult(
                false,
                "Potentially sensitive information requires direct user review and was not saved automatically."));
        }

        var now = DateTimeOffset.UtcNow;
        if (_userMemories is not null && _activeUsers is not null)
        {
            return RememberPerUserAsync(fact, category, cancellationToken);
        }

        var context = _turnAccessor();
        var saved = _legacyMemories!.Save(new MemoryEntry(
            $"mem_{Guid.NewGuid():N}",
            fact.Trim(),
            string.IsNullOrWhiteSpace(category) ? "general" : category.Trim(),
            now,
            now,
            MemorySource.ExplicitUserRequest,
            MemorySensitivity.Normal,
            Active: true,
            context?.ConversationId,
            context?.UserMessageId,
            "Saved by the Agent Framework memory tool after framework approval."));
        return Task.FromResult(new CoordinatorMemoryWriteResult(true, "Memory saved locally.", saved.MemoryId));
    }

    public async Task<CoordinatorMemoryWriteResult> CorrectAsync(
        [Description("The corrected durable fact for the current user.")] string correction,
        CancellationToken cancellationToken)
    {
        if (_userMemories is null || _activeUsers is null)
            return new(false, "Per-user correction is unavailable in the legacy memory store.");
        if (_activeUsers.RequiresSelection)
            return new(false, "Select the active user profile before correcting personal memory.");
        var result = await _userMemories.CorrectAsync(_activeUsers.Current, correction, cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Message, result.Memories.FirstOrDefault()?.MemoryId);
    }

    public async Task<CoordinatorMemoryWriteResult> ForgetAsync(
        [Description("What the current user explicitly asked Ali to forget.")] string request,
        CancellationToken cancellationToken)
    {
        if (_userMemories is null || _activeUsers is null)
            return new(false, "Per-user forgetting is unavailable in the legacy memory store.");
        if (_activeUsers.RequiresSelection)
            return new(false, "Select the active user profile before forgetting personal memory.");
        var result = await _userMemories.ForgetAsync(_activeUsers.Current, request, cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Message, result.Memories.FirstOrDefault()?.MemoryId);
    }

    public async Task<CoordinatorMemoryResult> ListCurrentAsync(CancellationToken cancellationToken)
    {
        if (_userMemories is null || _activeUsers is null)
            return await SearchAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        if (_activeUsers.RequiresSelection)
            return new("Select the active user profile before listing personal memory.", [], ["No personal memory was read."]);
        var values = await _userMemories.ListAsync(_activeUsers.Current, null, cancellationToken).ConfigureAwait(false);
        return ToCoordinatorResult(values, "Loaded current-user memories.");
    }

    private async Task<CoordinatorMemoryResult> SearchPerUserAsync(string query, CancellationToken cancellationToken)
    {
        var settings = (_settings?.Invoke() ?? new UserMemorySettings()).Normalize();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.RecallTimeoutMilliseconds);
        try
        {
            var values = await _userMemories!.RecallAsync(
                _activeUsers!.Current,
                query,
                Math.Min(MaximumResults, settings.RecallMaximumResults),
                timeout.Token).ConfigureAwait(false);
            return ToCoordinatorResult(values, values.Count == 0
                ? "No matching saved memory was found."
                : $"Found {values.Count} matching saved memories.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("Memory recall timed out; Ali continued safely.", [], ["Per-user memory recall exceeded its short timeout."]);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return new("Memory recall failed safely; Ali continued without it.", [], [$"Per-user memory unavailable: {ex.Message}"]);
        }
    }

    private async Task<CoordinatorMemoryWriteResult> RememberPerUserAsync(
        string fact,
        string? category,
        CancellationToken cancellationToken)
    {
        if (_activeUsers!.RequiresSelection)
        {
            return new(false, "Select the active user profile before saving personal memory.");
        }
        var result = await _userMemories!.RememberAsync(
            _activeUsers!.Current,
            fact.Trim(),
            "explicit_user_request",
            cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Message, result.Memories.FirstOrDefault()?.MemoryId);
    }

    private static CoordinatorMemoryResult ToCoordinatorResult(IReadOnlyList<Ali.Modules.UserMemory.UserMemory> values, string message) =>
        new(
            message,
            values.Select(memory => new CoordinatorMemoryItem(
                memory.MemoryId,
                memory.Text,
                memory.Category,
                memory.UpdatedUtc ?? memory.CreatedUtc ?? DateTimeOffset.UtcNow)).ToList(),
            []);

    private static int ScoreMemory(
        MemoryEntry memory,
        string query,
        IReadOnlySet<string> queryTerms)
    {
        var searchable = $"{memory.Text} {memory.Category}";
        var score = queryTerms.Count(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query)
            && searchable.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        return score;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else if (token.Length > 1)
            {
                yield return token.ToString();
                token.Clear();
            }
            else
            {
                token.Clear();
            }
        }

        if (token.Length > 1)
        {
            yield return token.ToString();
        }
    }
}
