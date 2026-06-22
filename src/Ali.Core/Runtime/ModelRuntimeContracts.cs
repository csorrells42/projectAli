using Ali.Core.Evidence;
using Ali.Core.Models;

namespace Ali.Core.Runtime;

public sealed record ChatRequest(
    string ConversationId,
    string UserMessageId,
    string UserText,
    IReadOnlyList<ChatMessage> History);

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
    EvidenceStatus EvidenceStatus);

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
}
