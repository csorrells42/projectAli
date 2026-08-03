using Ali.Modules.Evidence;
using Ali.Modules.Runtime;
using Ali.Modules.Voice;

namespace Ali.Modules.Conversation;

public enum ChatMessageOrigin
{
    Typed,
    Voice,
    Image,
    System,
    ToolResult
}

public sealed record ConversationListResult(
    IReadOnlyList<StoredConversationSummary> Conversations,
    IReadOnlyList<string> Warnings);

public sealed record ConversationEraseResult(
    int DeletedConversationCount,
    IReadOnlyList<string> Warnings);

public sealed record StoredConversationSummary(
    string ConversationId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    bool HasAttachments,
    bool HasVoiceOriginMessages,
    bool RetainsRawAudio,
    bool RetainsRawImageData,
    string Preview);

public sealed record StoredConversation(
    string ConversationId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StoredChatMessage> Messages,
    bool RetainsRawAudio = false,
    bool RetainsRawImageData = false);

public sealed record StoredChatMessage(
    string MessageId,
    string ConversationId,
    ChatRole Role,
    string Text,
    DateTimeOffset CreatedAt,
    ChatMessageOrigin Origin,
    EvidenceStatus EvidenceStatus,
    IReadOnlyList<StoredAttachmentMetadata>? Attachments = null,
    string? CorrectionId = null,
    string? RuntimeSnapshotId = null,
    string? SourceUserMessageId = null,
    string? SourceQuestion = null,
    int SourceAttachmentCount = 0,
    VoiceInputOrigin SourceInputOrigin = VoiceInputOrigin.Typed,
    VoiceTurnMetadata? SourceVoiceMetadata = null);

public sealed record StoredAttachmentMetadata(
    string AttachmentId,
    AttachmentKind Kind,
    string FileName,
    string ContentType,
    bool RetainAfterSession,
    DateTimeOffset CreatedAt);

public enum ConversationPublicationProbeStatus
{
    Present,
    Absent,
    Mismatch,
    Unavailable
}

public sealed record ConversationPublicationProbeResult(
    ConversationPublicationProbeStatus Status,
    string OutcomeCode);

public interface IConversationPublicationProbe
{
    ConversationPublicationProbeResult ProbeAssistantPublication(
        string conversationId,
        string assistantMessageId,
        string answerDigest);
}

public interface IConversationStore
{
    ConversationListResult ListSummaries();

    ConversationListResult Search(string query);

    StoredConversation? Load(string conversationId);

    StoredConversation Save(StoredConversation conversation);

    StoredConversationSummary? Rename(string conversationId, string title);

    bool Delete(string conversationId);

    ConversationEraseResult EraseAll();
}
