using Ali.Modules.Evidence;
using Ali.Modules.Runtime.Models;

namespace Ali.Modules.Runtime;

public sealed record ChatRequest(
    string ConversationId,
    string UserMessageId,
    string UserText,
    IReadOnlyList<ChatMessage> History)
{
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = Array.Empty<ChatAttachment>();
}

public sealed record ChatAttachment(
    string Id,
    AttachmentKind Kind,
    string FileName,
    string ContentType,
    string Base64Data,
    bool RetainAfterSession,
    DateTimeOffset CreatedAt);

public enum AttachmentKind
{
    Image
}

public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    string Text,
    DateTimeOffset CreatedAt,
    EvidenceStatus EvidenceStatus = EvidenceStatus.Unverified);

public enum ChatRole
{
    User,
    Assistant,
    System
}

public sealed record ModelToken(
    string Text,
    EvidenceStatus EvidenceStatus,
    string? FinishReason = null,
    bool IsThinking = false)
{
    public bool ReachedOutputLimit =>
        string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

public sealed record RuntimeHealthCheck(
    bool Succeeded,
    string Summary,
    DateTimeOffset CheckedAt,
    TimeSpan Elapsed,
    string? Endpoint = null,
    string? ModelPackageId = null,
    int? ContextTokens = null,
    int? OutputTokenLimit = null,
    double? Temperature = null,
    bool? StreamingSupported = null,
    string? ErrorText = null);

public interface ILocalModelRuntime
{
    ModelProfile ActiveProfile { get; }

    IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IModelSwitchAwareRuntime
{
    string ModelId { get; }

    Task UnloadForModelSwitchAsync(CancellationToken cancellationToken);
}

public interface IReasoningEffortRuntime
{
    string ReasoningEffort { get; }

    void SetReasoningEffort(string effort);
}
