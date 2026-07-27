namespace Ali.Modules.Coordinator;

public sealed record CoordinatorMemoryResult(
    string Status,
    IReadOnlyList<CoordinatorMemoryItem> Memories,
    IReadOnlyList<string> Warnings);

public sealed record CoordinatorMemoryItem(
    string MemoryId,
    string Text,
    string Category,
    DateTimeOffset UpdatedAt);

public sealed record CoordinatorMemoryWriteResult(
    bool Saved,
    string Message,
    string? MemoryId = null);

public sealed record CoordinatorSourceResult(
    string Status,
    IReadOnlyList<CoordinatorSourceItem> Sources,
    IReadOnlyList<string> Warnings);

public sealed record CoordinatorSourceItem(
    string Name,
    string Topic,
    string Url,
    DateTimeOffset RetrievedAt,
    string Excerpt);

public sealed record CoordinatorReminderResult(
    bool Saved,
    string Message,
    string? ReminderId = null,
    DateTimeOffset? DueAt = null);

public sealed record CoordinatorIdentityResult(
    string AssistantName,
    string ProfileId,
    string Description);

public sealed record CoordinatorCapability(
    string Name,
    string Description);

public sealed record CoordinatorCapabilityResult(
    string Status,
    IReadOnlyList<CoordinatorCapability> Tools);

public sealed record CoordinatorRoutingPlan(
    bool AnswerDirectly,
    string? MemoryQuery,
    string? CurrentWebQuery,
    string? CurrentWebTopic,
    string? LocalLibraryQuery,
    string? FactToRemember,
    string? MemoryCategory,
    string? ReminderTitle,
    string? ReminderDueAtLocal,
    bool NeedAssistantIdentity,
    bool NeedCurrentLocalTime,
    bool NeedToolCatalog);

internal sealed class CoordinatorTurnContext(
    string conversationId,
    string userMessageId,
    string originalUserText)
{
    public string ConversationId { get; } = conversationId;

    public string UserMessageId { get; } = userMessageId;

    public string OriginalUserText { get; } = originalUserText;

    public bool UsedEvidenceTool { get; set; }

    public List<CoordinatorSourceItem> WebSources { get; } = [];
}
