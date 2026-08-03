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
    private IReadOnlyList<string> _repairPointIds = [];
    private string _reconcileRequestId = string.Empty;

    public UserMemorySettingsViewModel(AliServices services)
    {
        _services = services;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy, HandleError);
        TestCommand = new AsyncRelayCommand(TestAsync, () => !IsBusy, HandleError);
        RepairCommand = new AsyncRelayCommand(
            RepairAsync,
            () => !IsBusy && SelectedUser is not null && _repairPointIds.Count != 0,
            HandleError);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, HandleError);
        LoadSensitiveCommand = new AsyncRelayCommand(
            LoadSensitiveAsync,
            () => !IsBusy && SelectedUser is not null,
            HandleError);
        ReconcileCommand = new AsyncRelayCommand(
            ReconcileAsync,
            () => !IsBusy
                && SelectedUser is not null
                && !string.IsNullOrWhiteSpace(ReconcileRequestId),
            HandleError);
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
    public string ReconcileRequestId
    {
        get => _reconcileRequestId;
        set
        {
            if (SetProperty(ref _reconcileRequestId, value))
            {
                RaiseCommandStates();
            }
        }
    }
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
    public string RepairButtonText => _repairPointIds.Count == 0
        ? "No Repair Needed"
        : $"Repair {_repairPointIds.Count} Failed Point(s)";

    public ICommand SaveCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand LoadSensitiveCommand { get; }
    public ICommand ReconcileCommand { get; }
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
            var health = await _services.UserMemories.CheckDesktopParticipantHealthAsync(
                SelectedUser,
                CancellationToken.None);
            SetRepairPointIds(health.FailedPointIds);
            RuntimeStatus = $"{status.State}: {status.Message}";
            CollectionStatus = $"{status.CurrentUserMemoryCount} authorized participant memories loaded; {health.DegradedPointCount} point(s) degraded.";
            StatusText = status.RuntimeAvailable ? "Local memory connection passed." : "Memory is unavailable; chat remains operational.";
        });
    }

    private async Task RepairAsync()
    {
        if (SelectedUser is null || _repairPointIds.Count == 0) return;
        if (WpfMessageBox.Show(
                $"Repair exactly {_repairPointIds.Count} failed participant-memory vector point(s)? Windows will ask for the current account credential.",
                "Repair participant memory",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await WithBusyAsync(async () =>
        {
            var result = await _services.UserMemories.RepairDesktopParticipantPointsAsync(
                SelectedUser,
                _repairPointIds,
                CancellationToken.None);
            SetRepairPointIds(result.FailedPointIds);
            StatusText = result.Success
                ? $"Repaired {result.UpdatedPointCount} exact participant-memory point(s); {result.FailedPointCount} remain failed."
                : result.Failure?.SafeMessage ?? "Participant-memory repair failed safely.";
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

    private async Task LoadSensitiveAsync()
    {
        if (SelectedUser is null
            || _services.UserMemories is not IParticipantMemoryDesktopReviewService review)
        {
            return;
        }
        if (WpfMessageBox.Show(
                "Load sensitive memories for the selected profile? Windows will request the current account credential. This is available only when that profile is the sole registered local owner.",
                "Load sensitive participant memory",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await WithBusyAsync(async () =>
        {
            var result = await review.ReviewDesktopParticipantsAsync(
                SelectedUser,
                CategoryFilter,
                includeSensitive: true,
                CancellationToken.None);
            if (!result.Success)
            {
                CollectionStatus = "Sensitive memories were not loaded.";
                StatusText = result.Message;
                return;
            }
            SetMemories(result.Memories);
            CollectionStatus = $"{result.Memories.Count} authorized memories loaded after explicit sensitive review.";
            StatusText = result.Message;
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
            ApplyMutationResult(result);
            await RefreshCoreAsync();
        });
    }

    private async Task ForgetAsync()
    {
        if (SelectedUser is null || SelectedMemory is null) return;
        if (WpfMessageBox.Show(
                $"Remove this memory from active participant recall for {SelectedUser.DisplayName}?\n\n{SelectedMemory.Text}\n\nThe active point and participant journal copies are removed, but physical storage history or backups are not claimed erased.",
                "Remove active memory",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var result = await _services.UserMemories.DeleteAsync(SelectedUser, SelectedMemory.Id, CancellationToken.None);
            ApplyMutationResult(result);
            await RefreshCoreAsync();
        });
    }

    private async Task ReconcileAsync()
    {
        if (SelectedUser is null
            || string.IsNullOrWhiteSpace(ReconcileRequestId)
            || _services.UserMemories is not IParticipantMemoryDesktopReviewService review)
        {
            return;
        }
        var exactRequestId = ReconcileRequestId.Trim();
        if (WpfMessageBox.Show(
                $"Reconcile exactly this participant-memory request without reapplying it?\n\n{exactRequestId}\n\nWindows will request the current account credential.",
                "Reconcile participant memory",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await WithBusyAsync(async () =>
        {
            var result = await review.ReconcileDesktopParticipantMutationAsync(
                SelectedUser,
                exactRequestId,
                CancellationToken.None);
            StatusText = result.Success
                ? $"Request {result.MutationRequestId} is {result.MutationStatus} by exact bounded recovery; the original mutation was not reapplied."
                : $"Request {result.MutationRequestId} remains unresolved: {result.Failure?.SafeMessage ?? "reconciliation failed safely"}";
            if (result.Success)
            {
                ReconcileRequestId = string.Empty;
                await RefreshCoreAsync();
            }
        });
    }

    private async Task ExportAsync()
    {
        if (SelectedUser is null) return;
        if (WpfMessageBox.Show($"Export the currently authorized low-sensitivity memories for {SelectedUser.DisplayName} to a local JSON file?", "Export memories", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var values = await _services.UserMemories.ListAsync(SelectedUser, null, CancellationToken.None);
            var folder = Path.Combine(_services.ProfileDataRoot, "Exports", "Memory");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{SelectedUser.StableId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(values, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            StatusText = $"Exported {values.Count} authorized low-sensitivity current-user memories to {path}.";
        });
    }

    private async Task ClearTestProfileAsync()
    {
        if (SelectedUser?.IsTestProfile != true) return;
        if (WpfMessageBox.Show(
                "Remove the John Doe test profile's current memories from active participant recall? Participant journal copies are redacted; physical storage history or backups are not claimed erased.",
                "Clear active test memories",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var values = await _services.UserMemories.ListAsync(SelectedUser, null, CancellationToken.None);
            var deletedIds = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<string>();
            foreach (var memory in values)
            {
                var result = await _services.UserMemories.DeleteAsync(
                    SelectedUser,
                    memory.MemoryId,
                    CancellationToken.None);
                if (result.Success)
                {
                    deletedIds.Add(memory.MemoryId);
                }
                else
                {
                    failures.Add(result.Message);
                    if (string.Equals(result.MutationStatus, "in_doubt", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(result.RequestId))
                    {
                        ReconcileRequestId = result.RequestId;
                    }
                }
            }
            SetMemories(values
                .Where(memory => !deletedIds.Contains(memory.MemoryId))
                .ToArray());
            CollectionStatus = failures.Count == 0
                ? "No John Doe test-profile memories remain in the authorized review set."
                : $"{failures.Count} John Doe test-profile memory deletion(s) did not complete.";
            StatusText = failures.Count == 0
                ? $"Cleared {deletedIds.Count} John Doe test-profile memories."
                : $"Cleared {deletedIds.Count}; {failures.Count} were not deleted. {failures[0]}";
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

    private void ApplyMutationResult(MemoryOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success
            && string.Equals(result.MutationStatus, "in_doubt", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(result.RequestId))
        {
            ReconcileRequestId = result.RequestId;
        }
        else if (result.Success)
        {
            ReconcileRequestId = string.Empty;
        }
        StatusText = string.IsNullOrWhiteSpace(result.RequestId)
            ? result.Message
            : $"{result.Message} Request ID: {result.RequestId}. Durable status: {result.MutationStatus ?? "unknown"}.";
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

    private void SetRepairPointIds(IReadOnlyList<string>? pointIds)
    {
        _repairPointIds = (pointIds ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(ParticipantMemoryLimits.MaximumRepairPointIds)
            .ToArray();
        OnPropertyChanged(nameof(RepairButtonText));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { SaveCommand, TestCommand, RepairCommand, RefreshCommand, LoadSensitiveCommand, ReconcileCommand, SearchCommand, CorrectCommand, ForgetCommand, ExportCommand, ClearTestProfileCommand })
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
