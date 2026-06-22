using Ali.Core.Evidence;

namespace Ali.Core.Feedback;

public enum CorrectionCategory
{
    FactuallyIncorrect,
    WrongOrUnsupportedSource,
    MisreadScreenshot,
    IncorrectCode,
    PowerShellOrCommandPromptError,
    ClaimedActionSucceededWhenItDidNot,
    BadToolResult,
    MemoryError,
    CalendarOrReminderError,
    Other
}

public enum CorrectionStatus
{
    New,
    Triaged,
    Reproduced,
    FixInProgress,
    Fixed,
    VerificationPending,
    Verified,
    Closed,
    NotReproducible,
    Rejected
}

public sealed record CorrectionReport(
    string Id,
    string ConversationId,
    string UserMessageId,
    string AssistantMessageId,
    string Question,
    string Answer,
    CorrectionCategory Category,
    CorrectionStatus Status,
    DateTimeOffset CreatedAt,
    string RuntimeKind,
    string RuntimeLocation,
    string RuntimeEndpoint,
    string ModelPackage,
    string Quantization,
    int ContextTokens,
    EvidenceStatus AnswerEvidenceStatus,
    string? UserNote = null,
    string? ExpectedAnswer = null,
    string? CorrectedAnswer = null);

public interface ICorrectionQueueStore
{
    Task SaveAsync(CorrectionReport report, CancellationToken cancellationToken);

    Task<IReadOnlyList<CorrectionReport>> ListAsync(CancellationToken cancellationToken);
}
