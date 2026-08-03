using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Ali.Modules.Evidence;
using Ali.Modules.Feedback;
using Ali.Modules.Runtime;

namespace Ali;

public sealed record AssistantStreamChunk(
    string ConversationId,
    string UserMessageId,
    string AssistantMessageId,
    string Text,
    EvidenceStatus EvidenceStatus,
    string? FinishReason = null,
    bool IsActivity = false,
    bool IsReasoning = false,
    AgentActivityKind? ActivityKind = null,
    string? ActivityDetail = null,
    double? ElapsedMilliseconds = null,
    AgentToolApprovalPrompt? ApprovalPrompt = null,
    string? ActivityKey = null,
    AgentToolExecutionReceipt? ExecutionReceipt = null,
    AgentRecoveryPrompt? RecoveryPrompt = null)
{
    public bool ReachedOutputLimit =>
        string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);

    internal FinalAnswerPublicationDelivery? FinalPublicationDelivery { get; init; }

    internal bool IsInterimPause { get; init; }
}

/// <summary>
/// Thin desktop boundary for the model-controlled Extensions.AI coordinator.
/// English interpretation and tool selection happen after the model receives the request.
/// </summary>
public sealed class ConversationOrchestrator(
    ILocalModelRuntime runtime,
    CorrectionQueueService correctionQueue,
    AliToolCoordinator coordinator) : IDisposable
{
    private int _disposed;

    public event Action<AssistantStreamChunk>? BackgroundActivity
    {
        add => coordinator.BackgroundActivity += value;
        remove => coordinator.BackgroundActivity -= value;
    }

    public ILocalModelRuntime Runtime { get; } = runtime;

    public CorrectionQueueService Corrections { get; } = correctionQueue;

    public bool ResolveToolApproval(AgentToolApprovalDecision decision) =>
        coordinator.ResolveToolApproval(decision);

    public async IAsyncEnumerable<AssistantStreamChunk> StreamRecoveryDecisionAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        AgentRecoveryDecision decision,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in coordinator.StreamRecoveryDecisionAsync(
                           conversationId,
                           userMessageId,
                           assistantMessageId,
                           decision,
                           history,
                           attachments,
                           cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public Task CancelRecoveryDecisionAsync(
        string conversationId,
        AgentRecoveryPrompt prompt,
        CancellationToken cancellationToken) =>
        coordinator.CancelRecoveryDecisionAsync(
            conversationId,
            prompt,
            cancellationToken);

    public async IAsyncEnumerable<AssistantStreamChunk> StreamAnswerAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in coordinator.StreamAnswerAsync(
                           conversationId,
                           userMessageId,
                           assistantMessageId,
                           userText,
                           history,
                           attachments,
                           cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            coordinator.Dispose();
        }
    }
}
