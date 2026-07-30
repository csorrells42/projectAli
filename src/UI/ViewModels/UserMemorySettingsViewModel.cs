using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Ali.Modules.UserMemory;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace Ali.UI.ViewModels;

public sealed class UserMemorySettingsViewModel : ObservableObject
{
    private readonly AliServices _services;
    private bool _enabled;
    private ActiveUser? _selectedUser;
    private UserMemoryItemViewModel? _selectedMemory;
    private string _searchText = string.Empty;
    private string _categoryFilter = string.Empty;
    private string _correctionText = string.Empty;
    private string _runtimeStatus = "Mem0 has not been checked.";
    private string _collectionStatus = "Memory count has not been loaded.";
    private string _statusText = "Per-user memory settings loaded.";
    private bool _isBusy;

    public UserMemorySettingsViewModel(AliServices services)
    {
        _services = services;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy, HandleError);
        TestCommand = new AsyncRelayCommand(TestAsync, () => !IsBusy, HandleError);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, HandleError);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy, HandleError);
        CorrectCommand = new AsyncRelayCommand(CorrectAsync, () => !IsBusy && SelectedMemory is not null, HandleError);
        ForgetCommand = new AsyncRelayCommand(ForgetAsync, () => !IsBusy && SelectedMemory is not null, HandleError);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy, HandleError);
        ClearTestProfileCommand = new AsyncRelayCommand(ClearTestProfileAsync, () => !IsBusy && SelectedUser?.IsTestProfile == true, HandleError);
        _services.ActiveUsers.Changed += ActiveUsersOnChanged;
        Reload();
    }

    public ObservableCollection<ActiveUser> Users { get; } = new();
    public ObservableCollection<UserMemoryItemViewModel> Memories { get; } = new();
    public IReadOnlyList<string> CategoryChoices { get; } =
        ["", "people_relationships", "preferences", "dates_places", "taught_facts", "procedures", "stories_experiences", "events", "corrections", "accessibility_communication", "general"];

    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public ActiveUser? SelectedUser
    {
        get => _selectedUser;
        private set
        {
            if (!SetProperty(ref _selectedUser, value) || value is null) return;
            OnPropertyChanged(nameof(ActiveUserText));
            OnPropertyChanged(nameof(IsTestProfile));
            RaiseCommandStates();
        }
    }
    public UserMemoryItemViewModel? SelectedMemory
    {
        get => _selectedMemory;
        set
        {
            if (!SetProperty(ref _selectedMemory, value)) return;
            CorrectionText = value?.Text ?? string.Empty;
            RaiseCommandStates();
        }
    }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string CategoryFilter { get => _categoryFilter; set => SetProperty(ref _categoryFilter, value); }
    public string CorrectionText { get => _correctionText; set => SetProperty(ref _correctionText, value); }
    public string RuntimeStatus { get => _runtimeStatus; private set => SetProperty(ref _runtimeStatus, value); }
    public string CollectionStatus { get => _collectionStatus; private set => SetProperty(ref _collectionStatus, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); } }
    public bool IsTestProfile => SelectedUser?.IsTestProfile == true;
    public string ActiveUserText => SelectedUser is null
        ? "Select a configured identity profile before personal memory is available."
        : SelectedUser.IsTestProfile
            ? $"{SelectedUser.DisplayName} — test profile ({SelectedUser.StableId})"
            : $"{SelectedUser.DisplayName} ({SelectedUser.StableId})";
    public string SettingsPath => _services.UserMemorySettingsPath;

    public ICommand SaveCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand CorrectCommand { get; }
    public ICommand ForgetCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ClearTestProfileCommand { get; }

    public void Reload()
    {
        var settings = _services.LoadUserMemorySettings();
        Enabled = settings.Enabled;
        _services.ActiveUsers.Refresh();
        LoadUsers(_services.ActiveUsers.Current);
        if (SelectedUser is not null)
        {
            RefreshCommand.Execute(null);
        }
    }

    private Task SaveAsync()
    {
        var current = _services.LoadUserMemorySettings();
        _services.SaveUserMemorySettings(current with
        {
            Enabled = Enabled
        });
        StatusText = $"Per-user memory settings saved to {SettingsPath}.";
        return Task.CompletedTask;
    }

    private async Task TestAsync()
    {
        if (SelectedUser is null) return;
        await WithBusyAsync(async () =>
        {
            var status = await _services.UserMemories.TestAsync(SelectedUser, CancellationToken.None);
            RuntimeStatus = $"{status.State}: {status.Message}";
            CollectionStatus = $"{status.CurrentUserMemoryCount} memories for the active user in ali_user_memories.";
            StatusText = status.RuntimeAvailable ? "Local memory connection passed." : "Memory is unavailable; chat remains operational.";
        });
    }

    private async Task RefreshAsync()
    {
        if (SelectedUser is null) return;
        await WithBusyAsync(async () =>
        {
            var values = await _services.UserMemories.ListAsync(SelectedUser, CategoryFilter, CancellationToken.None);
            SetMemories(values);
            CollectionStatus = $"{values.Count} current-user memories loaded.";
            StatusText = $"Memory review refreshed for {SelectedUser.DisplayName} with strict stable-ID filtering.";
        });
    }

    private async Task SearchAsync()
    {
        if (SelectedUser is null || string.IsNullOrWhiteSpace(SearchText)) return;
        await WithBusyAsync(async () =>
        {
            var values = await _services.UserMemories.RecallAsync(SelectedUser, SearchText, 8, CancellationToken.None);
            SetMemories(values.Where(value => string.IsNullOrWhiteSpace(CategoryFilter) || value.Category.Equals(CategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList());
            StatusText = $"Found {Memories.Count} matching current-user memories.";
        });
    }

    private async Task CorrectAsync()
    {
        if (SelectedUser is null || SelectedMemory is null || string.IsNullOrWhiteSpace(CorrectionText)) return;
        if (WpfMessageBox.Show("Replace the selected memory for the active user with this correction?", "Correct memory", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var result = await _services.UserMemories.CorrectAsync(SelectedUser, SelectedMemory.Id, CorrectionText, CancellationToken.None);
            StatusText = result.Message;
            await RefreshCoreAsync();
        });
    }

    private async Task ForgetAsync()
    {
        if (SelectedUser is null || SelectedMemory is null) return;
        if (WpfMessageBox.Show($"Permanently forget this memory for {SelectedUser.DisplayName}?\n\n{SelectedMemory.Text}", "Forget memory", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var result = await _services.UserMemories.DeleteAsync(SelectedUser, SelectedMemory.Id, CancellationToken.None);
            StatusText = result.Message;
            await RefreshCoreAsync();
        });
    }

    private async Task ExportAsync()
    {
        if (SelectedUser is null) return;
        if (WpfMessageBox.Show($"Export all private memories for {SelectedUser.DisplayName} to a local JSON file?", "Export memories", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var values = await _services.UserMemories.ListAsync(SelectedUser, null, CancellationToken.None);
            var folder = Path.Combine(_services.ProfileDataRoot, "Exports", "Memory");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{SelectedUser.StableId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(values, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            StatusText = $"Exported {values.Count} current-user memories to {path}.";
        });
    }

    private async Task ClearTestProfileAsync()
    {
        if (SelectedUser?.IsTestProfile != true) return;
        if (WpfMessageBox.Show("Permanently clear only the John Doe test profile's memories?", "Clear test memories", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var values = await _services.UserMemories.ListAsync(SelectedUser, null, CancellationToken.None);
            foreach (var memory in values)
            {
                await _services.UserMemories.DeleteAsync(SelectedUser, memory.MemoryId, CancellationToken.None);
            }
            StatusText = $"Cleared {values.Count} John Doe test-profile memories.";
            SetMemories([]);
        });
    }

    private async Task RefreshCoreAsync()
    {
        var values = await _services.UserMemories.ListAsync(SelectedUser!, CategoryFilter, CancellationToken.None);
        SetMemories(values);
        CollectionStatus = $"{values.Count} current-user memories loaded.";
    }

    private void SetMemories(IReadOnlyList<UserMemory> values)
    {
        Memories.Clear();
        foreach (var value in values) Memories.Add(new UserMemoryItemViewModel(value));
        SelectedMemory = Memories.FirstOrDefault();
    }

    private void LoadUsers(ActiveUser selected)
    {
        Users.Clear();
        foreach (var user in _services.ActiveUsers.AvailableUsers) Users.Add(user);
        if (_services.ActiveUsers.RequiresSelection)
        {
            SelectedUser = null;
            StatusText = "Choose the active user profile. Ali will not access personal memory until you select one.";
            return;
        }
        SelectedUser = Users.FirstOrDefault(user => user.StableId.Equals(selected.StableId, StringComparison.OrdinalIgnoreCase)) ?? Users.FirstOrDefault();
    }

    private void ActiveUsersOnChanged(object? sender, ActiveUser user)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            Memories.Clear();
            LoadUsers(user);
            StatusText = "Active identity changed; recalled-memory context was cleared immediately.";
        });
    }

    private async Task WithBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try { await action(); }
        finally { IsBusy = false; }
    }

    private void HandleError(Exception exception)
    {
        IsBusy = false;
        StatusText = $"Memory failed safely: {exception.Message}";
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { SaveCommand, TestCommand, RefreshCommand, SearchCommand, CorrectCommand, ForgetCommand, ExportCommand, ClearTestProfileCommand })
        {
            if (command is AsyncRelayCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
        }
    }
}

public sealed class UserMemoryItemViewModel(UserMemory memory)
{
    public string Id => memory.MemoryId;
    public string Text => memory.Text;
    public string Category => memory.Category;
    public string Source => memory.Source;
    public string Updated => (memory.UpdatedUtc ?? memory.CreatedUtc)?.ToLocalTime().ToString("g") ?? "Unknown";
    public string Explicit => memory.ExplicitlyTaught ? "Explicit" : "Learned";
}
