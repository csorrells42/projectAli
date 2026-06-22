using Ali.Core.Evidence;
using Ali.Core.Voice;

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
    VoiceTranscriptionError,
    SpokenResponseError,
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
    int OutputTokenLimit,
    double? Temperature,
    bool? StreamingEnabled,
    EvidenceStatus AnswerEvidenceStatus,
    string? UserNote = null,
    string? ExpectedAnswer = null,
    string? CorrectedAnswer = null,
    VoiceInputOrigin InputOrigin = VoiceInputOrigin.Typed,
    string? VoiceTranscript = null,
    string? SpeechToTextProvider = null,
    string? SpeechToTextMode = null,
    string? TextToSpeechProvider = null,
    string? TextToSpeechVoice = null,
    bool RawAudioRetained = false,
    int? VoiceInputDeviceNumber = null,
    string? VoiceInputDeviceName = null,
    string? VoiceInputPreset = null,
    string? SpeechToTextModel = null,
    string? TextToSpeechModel = null,
    bool SuspiciousOrNoSpeech = false);

public interface ICorrectionQueueStore
{
    Task SaveAsync(CorrectionReport report, CancellationToken cancellationToken);

    Task<IReadOnlyList<CorrectionReport>> ListAsync(CancellationToken cancellationToken);
}
