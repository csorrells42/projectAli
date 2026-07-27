using System.ComponentModel;
using System.Text;
using Ali.Modules.Memory;
using Ali.Modules.Permissions;

namespace Ali.Modules.Coordinator;

internal sealed class AliMemoryTools(
    IMemoryStore memories,
    PermissionService permissions,
    Func<CoordinatorTurnContext?> turnAccessor)
{
    private const int MaximumResults = 8;

    public Task<CoordinatorMemoryResult> SearchAsync(
        [Description("The personal fact or prior detail to recall.")] string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = memories.List();
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

        var permission = permissions.Evaluate(PermissionRequest.Create(
            "memory.write",
            PermissionRisk.FileWrite,
            "Save an explicitly requested local memory.",
            userConfirmed: true));
        if (permission.Kind != PermissionDecisionKind.Allow)
        {
            return Task.FromResult(new CoordinatorMemoryWriteResult(false, permission.Reason));
        }

        var now = DateTimeOffset.UtcNow;
        var context = turnAccessor();
        var saved = memories.Save(new MemoryEntry(
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
            "Saved by the Extensions.AI memory tool after an explicit user request."));
        return Task.FromResult(new CoordinatorMemoryWriteResult(true, "Memory saved locally.", saved.MemoryId));
    }

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
