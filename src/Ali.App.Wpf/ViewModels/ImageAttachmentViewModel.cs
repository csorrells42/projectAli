using Ali.Core.Runtime;

namespace Ali.App.Wpf.ViewModels;

public sealed class ImageAttachmentViewModel : ObservableObject
{
    private bool _retainAfterSession;

    public ImageAttachmentViewModel(
        string id,
        string fileName,
        string filePath,
        string contentType,
        string base64Data,
        DateTimeOffset createdAt)
    {
        Id = id;
        FileName = fileName;
        FilePath = filePath;
        ContentType = contentType;
        Base64Data = base64Data;
        CreatedAt = createdAt;
    }

    public string Id { get; }

    public string FileName { get; }

    public string FilePath { get; }

    public string ContentType { get; }

    public string Base64Data { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool RetainAfterSession
    {
        get => _retainAfterSession;
        set => SetProperty(ref _retainAfterSession, value);
    }

    public ChatAttachment ToCoreAttachment() =>
        new(
            Id,
            AttachmentKind.Image,
            FileName,
            ContentType,
            Base64Data,
            RetainAfterSession,
            CreatedAt);
}
