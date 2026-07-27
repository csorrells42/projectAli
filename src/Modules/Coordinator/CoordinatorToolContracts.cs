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

internal sealed class CoordinatorTurnContext(
    string conversationId,
    string userMessageId,
    string assistantMessageId,
    string originalUserText,
    Action<AssistantStreamChunk> publish)
{
    private readonly long _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    public string ConversationId { get; } = conversationId;

    public string UserMessageId { get; } = userMessageId;

    public string AssistantMessageId { get; } = assistantMessageId;

    public string OriginalUserText { get; } = originalUserText;

    public bool UsedEvidenceTool { get; set; }

    public List<CoordinatorSourceItem> WebSources { get; } = [];

    public void Report(
        AgentActivityKind kind,
        string title,
        string? detail = null,
        double? elapsedMilliseconds = null,
        AgentToolApprovalPrompt? approvalPrompt = null) =>
        publish(new AssistantStreamChunk(
            ConversationId,
            UserMessageId,
            AssistantMessageId,
            title,
            Ali.Modules.Evidence.EvidenceStatus.Unknown,
            IsActivity: true,
            ActivityKind: kind,
            ActivityDetail: detail,
            ElapsedMilliseconds: elapsedMilliseconds ??
                System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
            ApprovalPrompt: approvalPrompt));
}
