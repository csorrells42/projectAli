using Ali.Core.Evidence;
using Ali.Core.Models;

namespace Ali.Core.Feedback;

public sealed class CorrectionQueueService(ICorrectionQueueStore store)
{
    public async Task<CorrectionReport> FlagIncorrectAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string question,
        string answer,
        ModelProfile modelProfile,
        EvidenceStatus answerEvidenceStatus,
        CorrectionCategory category,
        string? userNote,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        var report = new CorrectionReport(
            Id: $"corr_{Guid.NewGuid():N}",
            ConversationId: conversationId,
            UserMessageId: userMessageId,
            AssistantMessageId: assistantMessageId,
            Question: question,
            Answer: answer,
            Category: category,
            Status: CorrectionStatus.New,
            CreatedAt: DateTimeOffset.UtcNow,
            RuntimeKind: modelProfile.RuntimeKind,
            RuntimeLocation: modelProfile.RuntimeLocation,
            RuntimeEndpoint: modelProfile.RuntimeEndpoint,
            ModelPackage: modelProfile.PackageId,
            Quantization: modelProfile.Quantization,
            ContextTokens: modelProfile.ContextTokens,
            AnswerEvidenceStatus: answerEvidenceStatus,
            UserNote: userNote);

        await store.SaveAsync(report, cancellationToken).ConfigureAwait(false);
        return report;
    }
}
