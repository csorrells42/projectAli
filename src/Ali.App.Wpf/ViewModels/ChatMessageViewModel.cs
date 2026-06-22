using Ali.Core.Evidence;
using Ali.Core.Runtime;
using Ali.Core.Voice;

namespace Ali.App.Wpf.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    private string _text;
    private EvidenceStatus _evidenceStatus;
    private bool _isFlaggedForCorrection;

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
        string? sourceQuestion = null)
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
    }

    public string Id { get; }

    public ChatRole Role { get; }

    public string RoleName => Role.ToString();

    public DateTimeOffset CreatedAt { get; }

    public string? SourceUserMessageId { get; }

    public string? SourceQuestion { get; }

    public int SourceAttachmentCount { get; }

    public VoiceInputOrigin SourceInputOrigin { get; }

    public VoiceTurnMetadata? SourceVoiceMetadata { get; }

    public bool CanFlagAsIncorrect => Role == ChatRole.Assistant;

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

    public ChatMessage ToCoreMessage() =>
        new(Id, Role, Text, CreatedAt, EvidenceStatus);
}
