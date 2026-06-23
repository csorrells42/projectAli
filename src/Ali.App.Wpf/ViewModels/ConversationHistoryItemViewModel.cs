namespace Ali.App.Wpf.ViewModels;

public sealed class ConversationHistoryItemViewModel(string id, string title) : ObservableObject
{
    private string _title = string.IsNullOrWhiteSpace(title) ? "Untitled chat" : title.Trim();
    private string _draftTitle = string.IsNullOrWhiteSpace(title) ? "Untitled chat" : title.Trim();
    private bool _isRenaming;

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
}
