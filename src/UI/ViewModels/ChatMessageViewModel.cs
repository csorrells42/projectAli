using Ali.Modules.Evidence;
using Ali.Modules.Conversation;
using Ali.Modules.Runtime;
using Ali.Modules.Voice;

namespace Ali.UI.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    private string _text;
    private EvidenceStatus _evidenceStatus;
    private bool _isFlaggedForCorrection;
    private bool _isResponseComplete;

    public ChatMessageViewModel(
        string id,
        ChatRole role,
        string text,
        DateTimeOffset createdAt,
        EvidenceStatus evidenceStatus,
        int sourceAttachmentCount = 0,
        VoiceInputOrigin sourceInputOrigin = VoiceInputOrigin.Typed,
        VoiceTurnMetadata? sourceVoiceMetadata = null,
        string? sourceUserMessageId = null,
        string? sourceQuestion = null,
        IReadOnlyList<StoredAttachmentMetadata>? attachmentMetadata = null,
        string? correctionId = null,
        bool isResponseComplete = true)
    {
        Id = id;
        Role = role;
        _text = text;
        CreatedAt = createdAt;
        _evidenceStatus = evidenceStatus;
        SourceAttachmentCount = sourceAttachmentCount;
        SourceInputOrigin = sourceInputOrigin;
        SourceVoiceMetadata = sourceVoiceMetadata;
        SourceUserMessageId = sourceUserMessageId;
        SourceQuestion = sourceQuestion;
        AttachmentMetadata = attachmentMetadata;
        CorrectionId = correctionId;
        _isResponseComplete = isResponseComplete;
    }

    public string Id { get; }

    public ChatRole Role { get; }

    public string RoleName => Role.ToString();

    public System.Windows.HorizontalAlignment MessageAlignment => Role == ChatRole.User
        ? System.Windows.HorizontalAlignment.Right
        : System.Windows.HorizontalAlignment.Left;

    public System.Windows.TextAlignment MessageTextAlignment => Role == ChatRole.User
        ? System.Windows.TextAlignment.Right
        : System.Windows.TextAlignment.Left;

    public DateTimeOffset CreatedAt { get; }

    public string? SourceUserMessageId { get; }

    public string? SourceQuestion { get; }

    public int SourceAttachmentCount { get; }

    public VoiceInputOrigin SourceInputOrigin { get; }

    public VoiceTurnMetadata? SourceVoiceMetadata { get; }

    public IReadOnlyList<StoredAttachmentMetadata>? AttachmentMetadata { get; }

    public string? CorrectionId { get; private set; }

    public bool CanFlagAsIncorrect => Role == ChatRole.Assistant;

    public bool AreActionsVisible => _isResponseComplete;

    public bool IsResponseComplete
    {
        get => _isResponseComplete;
        set
        {
            if (SetProperty(ref _isResponseComplete, value))
            {
                OnPropertyChanged(nameof(AreActionsVisible));
            }
        }
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public EvidenceStatus EvidenceStatus
    {
        get => _evidenceStatus;
        set => SetProperty(ref _evidenceStatus, value);
    }

    public bool IsFlaggedForCorrection
    {
        get => _isFlaggedForCorrection;
        set => SetProperty(ref _isFlaggedForCorrection, value);
    }

    public void MarkCorrection(string correctionId)
    {
        CorrectionId = correctionId;
        IsFlaggedForCorrection = true;
    }

    public ChatMessage ToCoreMessage() =>
        new(Id, Role, Text, CreatedAt, EvidenceStatus);

    public StoredChatMessage ToStoredMessage(string conversationId)
    {
        var origin = SourceInputOrigin switch
        {
            VoiceInputOrigin.Voice => ChatMessageOrigin.Voice,
            _ when AttachmentMetadata?.Count > 0 || SourceAttachmentCount > 0 => ChatMessageOrigin.Image,
            _ => ChatMessageOrigin.Typed
        };

        return new StoredChatMessage(
            Id,
            conversationId,
            Role,
            Text,
            CreatedAt,
            origin,
            EvidenceStatus,
            AttachmentMetadata,
            CorrectionId,
            SourceUserMessageId: SourceUserMessageId,
            SourceQuestion: SourceQuestion,
            SourceAttachmentCount: SourceAttachmentCount,
            SourceInputOrigin: SourceInputOrigin,
            SourceVoiceMetadata: SourceVoiceMetadata);
    }

    public static ChatMessageViewModel FromStoredMessage(StoredChatMessage message) =>
        new(
            message.MessageId,
            message.Role,
            message.Text,
            message.CreatedAt,
            message.EvidenceStatus,
            message.SourceAttachmentCount,
            message.SourceInputOrigin,
            message.SourceVoiceMetadata,
            message.SourceUserMessageId,
            message.SourceQuestion,
            message.Attachments,
            message.CorrectionId)
        {
            IsFlaggedForCorrection = !string.IsNullOrWhiteSpace(message.CorrectionId)
        };
}
