namespace Ali.Core.Memory;

public enum MemorySource
{
    ExplicitUserRequest,
    UserConfirmed,
    SystemImport
}

public enum MemorySensitivity
{
    Normal,
    PotentiallySensitive
}

public sealed record MemoryEntry(
    string MemoryId,
    string Text,
    string Category,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    MemorySource Source,
    MemorySensitivity Sensitivity,
    bool Active,
    string? ConversationId = null,
    string? MessageId = null,
    string? Note = null);

public sealed record MemoryListResult(
    IReadOnlyList<MemoryEntry> Memories,
    IReadOnlyList<string> Warnings);

public interface IMemoryStore
{
    MemoryListResult List();

    MemoryEntry Save(MemoryEntry memory);

    bool Delete(string memoryId);

    int DeleteMatching(string text);

    int Clear();
}
