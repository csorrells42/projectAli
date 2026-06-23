namespace Ali.App.Wpf.ViewModels;

public sealed class ConversationHistoryItemViewModel(
    string id,
    string title,
    DateTimeOffset? updatedAt = null,
    string preview = "",
    int messageCount = 0) : ObservableObject
{
    private string _title = string.IsNullOrWhiteSpace(title) ? "Untitled chat" : title.Trim();
    private string _draftTitle = string.IsNullOrWhiteSpace(title) ? "Untitled chat" : title.Trim();
    private bool _isRenaming;
    private DateTimeOffset _updatedAt = updatedAt ?? DateTimeOffset.UtcNow;
    private string _preview = preview;
    private int _messageCount = messageCount;

    public string Id { get; } = id;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, string.IsNullOrWhiteSpace(value) ? "Untitled chat" : value.Trim());
    }

    public string DraftTitle
    {
        get => _draftTitle;
        set => SetProperty(ref _draftTitle, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (SetProperty(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(IsViewing));
            }
        }
    }

    public bool IsViewing => !IsRenaming;

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        private set => SetProperty(ref _updatedAt, value);
    }

    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public int MessageCount
    {
        get => _messageCount;
        private set => SetProperty(ref _messageCount, value);
    }

    public void BeginRename()
    {
        DraftTitle = Title;
        IsRenaming = true;
    }

    public void CommitRename()
    {
        Title = DraftTitle;
        DraftTitle = Title;
        IsRenaming = false;
    }

    public void CancelRename()
    {
        DraftTitle = Title;
        IsRenaming = false;
    }

    public void SetTitle(string title)
    {
        Title = title;
        DraftTitle = Title;
    }

    public void UpdateMetadata(DateTimeOffset updatedAt, string preview, int messageCount)
    {
        UpdatedAt = updatedAt;
        Preview = preview;
        MessageCount = messageCount;
    }
}
