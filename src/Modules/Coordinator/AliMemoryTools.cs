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
    private readonly Func<CancellationToken, Task>? _waitForPendingReview;
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
        Func<CoordinatorTurnContext?> turnAccessor,
        Func<CancellationToken, Task>? waitForPendingReview = null)
    {
        _userMemories = memories;
        _activeUsers = activeUsers;
        _settings = settings;
        _turnAccessor = turnAccessor;
        _waitForPendingReview = waitForPendingReview;
    }

    public Task<CoordinatorMemoryResult> SearchAsync(
        [Description("The personal fact or prior detail to recall.")] string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_userMemories is not null && _activeUsers is not null)
        {
            if (!TryGetTurnUser(out var activeUser))
            {
                return Task.FromResult(new CoordinatorMemoryResult(
                    "Select the active user profile before Ali accesses personal memory.",
                    [],
                    ["Personal memory was skipped because more than one identity profile is available and none was explicitly selected."]));
            }
            return SearchPerUserAsync(activeUser, query, cancellationToken);
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

    public Task<CoordinatorMemoryResult> SearchAsModelToolAsync(
        [Description("The personal fact or prior detail to recall.")] string query,
        CancellationToken cancellationToken)
    {
        MarkAuthoritativeToolUsed();
        return SearchAsync(query, cancellationToken);
    }

    internal Task<CoordinatorMemoryResult> SearchForSelectionAsync(
        ActiveUserSelectionSnapshot selection,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (_userMemories is null)
        {
            return SearchAsync(query, cancellationToken);
        }

        return selection.IsResolved
            ? SearchPerUserAsync(selection.SelectedUser!, query, cancellationToken)
            : Task.FromResult(new CoordinatorMemoryResult(
                "Select the active user profile before Ali accesses personal memory.",
                [],
                ["Personal memory was skipped because more than one identity profile is available and none was explicitly selected."]));
    }

    public Task<CoordinatorMemoryWriteResult> RememberAsync(
        [Description("The exact fact the user explicitly asked Ali to remember.")] string fact,
        [Description("A short category such as person, preference, location, project, or general.")] string? category,
        CancellationToken cancellationToken)
    {
        MarkAuthoritativeToolUsed();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(fact))
        {
            return Task.FromResult(new CoordinatorMemoryWriteResult(false, "Nothing was saved because the fact was empty."));
        }

        var now = DateTimeOffset.UtcNow;
        if (_userMemories is not null && _activeUsers is not null)
        {
            return TryGetTurnUser(out var activeUser)
                ? RememberPerUserAsync(activeUser, fact, category, cancellationToken)
                : Task.FromResult(new CoordinatorMemoryWriteResult(
                    false,
                    "Select the active user profile before saving personal memory."));
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
        [Description("The exact memory ID returned by relevant memory context, recall_user_memory, or list_current_user_memories.")] string memoryId,
        [Description("The corrected durable fact for the current user.")] string correction,
        CancellationToken cancellationToken)
    {
        MarkAuthoritativeToolUsed();
        if (_userMemories is null || _activeUsers is null)
            return new(false, "Per-user correction is unavailable in the legacy memory store.");
        if (!TryGetTurnUser(out var activeUser))
            return new(false, "Select the active user profile before correcting personal memory.");
        var result = await _userMemories.CorrectAsync(activeUser, memoryId, correction, cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Message, result.Memories.FirstOrDefault()?.MemoryId);
    }

    public async Task<CoordinatorMemoryWriteResult> ForgetAsync(
        [Description("The exact memory ID returned by relevant memory context, recall_user_memory, or list_current_user_memories. Never pass descriptive text or a search query.")] string memoryId,
        CancellationToken cancellationToken)
    {
        MarkAuthoritativeToolUsed();
        if (_userMemories is null || _activeUsers is null)
            return new(false, "Per-user forgetting is unavailable in the legacy memory store.");
        if (!TryGetTurnUser(out var activeUser))
            return new(false, "Select the active user profile before forgetting personal memory.");
        var result = await _userMemories.DeleteAsync(activeUser, memoryId, cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Message, result.Memories.FirstOrDefault()?.MemoryId);
    }

    internal async Task<CoordinatorMemoryWriteResult> ForgetForSelectionAsync(
        ActiveUserSelectionSnapshot selection,
        string memoryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        MarkAuthoritativeToolUsed();
        if (_userMemories is null)
        {
            return await ForgetAsync(memoryId, cancellationToken).ConfigureAwait(false);
        }
        if (!selection.IsResolved)
        {
            return new(false, "Select the active user profile before forgetting personal memory.");
        }

        var result = await _userMemories.DeleteAsync(
            selection.SelectedUser!,
            memoryId,
            cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Message, result.Memories.FirstOrDefault()?.MemoryId);
    }

    public async Task<CoordinatorMemoryResult> ListCurrentAsync(CancellationToken cancellationToken)
    {
        MarkAuthoritativeToolUsed();
        if (_userMemories is null || _activeUsers is null)
            return await SearchAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        if (!TryGetTurnUser(out var activeUser))
            return new("Select the active user profile before listing personal memory.", [], ["No personal memory was read."]);
        var values = await _userMemories.ListAsync(activeUser, null, cancellationToken).ConfigureAwait(false);
        return ToCoordinatorResult(values, "Loaded current-user memories.");
    }

    internal async Task<CoordinatorMemoryResult> ListForSelectionAsync(
        ActiveUserSelectionSnapshot selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        MarkAuthoritativeToolUsed();
        if (_userMemories is null)
        {
            return await ListCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!selection.IsResolved)
        {
            return new(
                "Select the active user profile before listing personal memory.",
                [],
                ["No personal memory was read."]);
        }

        var values = await _userMemories.ListAsync(
            selection.SelectedUser!,
            null,
            cancellationToken).ConfigureAwait(false);
        return ToCoordinatorResult(values, "Loaded current-user memories.");
    }

    private async Task<CoordinatorMemoryResult> SearchPerUserAsync(
        ActiveUser activeUser,
        string query,
        CancellationToken cancellationToken)
    {
        if (_waitForPendingReview is not null)
        {
            await _waitForPendingReview(cancellationToken).ConfigureAwait(false);
        }

        var settings = (_settings?.Invoke() ?? new UserMemorySettings()).Normalize();
        try
        {
            var values = await _userMemories!.RecallAsync(
                activeUser,
                query,
                Math.Min(MaximumResults, settings.RecallMaximumResults),
                cancellationToken).ConfigureAwait(false);
            return ToCoordinatorResult(values, values.Count == 0
                ? "No matching saved memory was found."
                : $"Found {values.Count} matching saved memories.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return new("Memory recall failed safely; Ali continued without it.", [], [$"Per-user memory unavailable: {ex.Message}"]);
        }
    }

    private void MarkAuthoritativeToolUsed()
    {
        var turn = _turnAccessor();
        if (turn is not null)
        {
            turn.UsedEvidenceTool = true;
        }
    }

    private async Task<CoordinatorMemoryWriteResult> RememberPerUserAsync(
        ActiveUser activeUser,
        string fact,
        string? category,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim();
        var result = await _userMemories!.RememberAsync(
            activeUser,
            fact.Trim(),
            "model_selected_user_fact",
            normalizedCategory,
            cancellationToken).ConfigureAwait(false);
        return new(result.Success, result.Message, result.Memories.FirstOrDefault()?.MemoryId);
    }

    private bool TryGetTurnUser(out ActiveUser activeUser)
    {
        var captured = _turnAccessor()?.CapturedUserSelection;
        if (captured is not null)
        {
            if (captured.IsResolved)
            {
                activeUser = captured.SelectedUser!;
                return true;
            }

            activeUser = null!;
            return false;
        }

        var current = _activeUsers?.CaptureSelectionSnapshot();
        if (current?.IsResolved == true)
        {
            activeUser = current.SelectedUser!;
            return true;
        }

        activeUser = null!;
        return false;
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
