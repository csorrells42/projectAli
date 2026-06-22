using System.Runtime.CompilerServices;
using Ali.Core.Evidence;
using Ali.Core.Feedback;
using Ali.Core.Permissions;
using Ali.Core.Runtime;

namespace Ali.Core.Orchestration;

public sealed record AssistantStreamChunk(
    string ConversationId,
    string UserMessageId,
    string AssistantMessageId,
    string Text,
    EvidenceStatus EvidenceStatus);

public sealed class ConversationOrchestrator(
    ILocalModelRuntime runtime,
    PermissionService permissionService,
    CorrectionQueueService correctionQueue)
{
    public ILocalModelRuntime Runtime { get; } = runtime;

    public PermissionService Permissions { get; } = permissionService;

    public CorrectionQueueService Corrections { get; } = correctionQueue;

    public async IAsyncEnumerable<AssistantStreamChunk> StreamAnswerAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        var request = new ChatRequest(conversationId, userMessageId, userText, history)
        {
            Attachments = attachments
        };

        await foreach (var token in Runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                token.Text,
                token.EvidenceStatus);
        }
    }
}
