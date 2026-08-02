using System.Collections.ObjectModel;
using System.Windows.Input;
using Ali.Modules.Capabilities;

namespace Ali.UI.ViewModels;

public sealed class CapabilitySettingsViewModel : ObservableObject
{
    private readonly CapabilitySettingsSnapshotOwner _owner;
    private readonly string _settingsFileName;
    private readonly Func<Task<bool>>? _refreshPublishedCapabilities;
    private readonly Dictionary<string, bool> _appliedSelections = new(StringComparer.Ordinal);
    private CapabilitySettingsStamp _stamp;
    private CapabilitySettingsPresetViewModel? _selectedPreset;
    private bool _isBusy;
    private bool _isDirty;
    private bool _needsInitialSave;
    private bool _requiresReload;
    private bool _isFailedClosed;
    private string _loadStatus = string.Empty;
    private string _statusText = string.Empty;
    private string _summaryText = string.Empty;
    private int _knownGroupCount;
    private int _enabledGroupCount;
    private int _disabledGroupCount;
    private int _declaredTaskToolCount;
    private int _callableTaskToolCount;
    private int _unavailableTaskToolCount;
    private int _callableProtocolToolCount;
    private int _unavailableProtocolToolCount;
    private int _quarantinedRuntimeToolCount;
    private int _unknownSelectionCount;

    public CapabilitySettingsViewModel(
        CapabilitySettingsSnapshotOwner owner,
        string settingsPath,
        Func<Task<bool>>? refreshPublishedCapabilities = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _refreshPublishedCapabilities = refreshPublishedCapabilities;
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsFileName = Path.GetFileName(settingsPath.Trim());
        if (string.IsNullOrWhiteSpace(_settingsFileName))
        {
            throw new ArgumentException(
                "The capability settings path must include a file name.",
                nameof(settingsPath));
        }

        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            CanSave,
            _ => HandleUnexpectedMutationFailure("save"));
        ReloadCommand = new AsyncRelayCommand(
            ReloadAsync,
            () => !IsBusy,
            _ => HandleUnexpectedReloadFailure());
        ApplyPresetCommand = new AsyncRelayCommand(
            ApplySelectedPresetAsync,
            CanApplyPreset,
            _ => HandleUnexpectedMutationFailure("apply a preset to"));

        ApplyEnvelope(_owner.CaptureSettings());
    }

    public ObservableCollection<CapabilitySettingsRowViewModel> Rows { get; } = [];

    public ObservableCollection<CapabilitySettingsPresetViewModel> Presets { get; } = [];

    public string SettingsFileName => _settingsFileName;

    public CapabilitySettingsPresetViewModel? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEdit));
            UpdateRowEditability();
            RaiseCommandStates();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool NeedsInitialSave
    {
        get => _needsInitialSave;
        private set
        {
            if (SetProperty(ref _needsInitialSave, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool RequiresReload
    {
        get => _requiresReload;
        private set
        {
            if (!SetProperty(ref _requiresReload, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEdit));
            UpdateRowEditability();
            RaiseCommandStates();
        }
    }

    public bool IsFailedClosed
    {
        get => _isFailedClosed;
        private set
        {
            if (!SetProperty(ref _isFailedClosed, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEdit));
            UpdateRowEditability();
            RaiseCommandStates();
        }
    }

    public bool CanEdit => !IsBusy && !RequiresReload && !IsFailedClosed;

    public string LoadStatus
    {
        get => _loadStatus;
        private set => SetProperty(ref _loadStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public int KnownGroupCount
    {
        get => _knownGroupCount;
        private set => SetProperty(ref _knownGroupCount, value);
    }

    public int EnabledGroupCount
    {
        get => _enabledGroupCount;
        private set => SetProperty(ref _enabledGroupCount, value);
    }

    public int DisabledGroupCount
    {
        get => _disabledGroupCount;
        private set => SetProperty(ref _disabledGroupCount, value);
    }

    public int DeclaredTaskToolCount
    {
        get => _declaredTaskToolCount;
        private set => SetProperty(ref _declaredTaskToolCount, value);
    }

    public int CallableTaskToolCount
    {
        get => _callableTaskToolCount;
        private set => SetProperty(ref _callableTaskToolCount, value);
    }

    public int UnavailableTaskToolCount
    {
        get => _unavailableTaskToolCount;
        private set => SetProperty(ref _unavailableTaskToolCount, value);
    }

    public int CallableProtocolToolCount
    {
        get => _callableProtocolToolCount;
        private set => SetProperty(ref _callableProtocolToolCount, value);
    }

    public int UnavailableProtocolToolCount
    {
        get => _unavailableProtocolToolCount;
        private set => SetProperty(ref _unavailableProtocolToolCount, value);
    }

    public int QuarantinedRuntimeToolCount
    {
        get => _quarantinedRuntimeToolCount;
        private set => SetProperty(ref _quarantinedRuntimeToolCount, value);
    }

    public int UnknownSelectionCount
    {
        get => _unknownSelectionCount;
        private set => SetProperty(ref _unknownSelectionCount, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand ReloadCommand { get; }

    public ICommand ApplyPresetCommand { get; }

    internal async Task SaveAsync()
    {
        var expected = _stamp;
        var draft = CaptureDraft();
        IsBusy = true;
        try
        {
            var result = await Task.Run(
                    () => _owner.TrySaveRows(expected, draft))
                .ConfigureAwait(true);
            ApplyMutationResult(result);
            await RefreshPublishedCapabilitiesAfterSaveAsync(result).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var current = await Task.Run(_owner.Reload).ConfigureAwait(true);
            ApplyEnvelope(current);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ApplySelectedPresetAsync()
    {
        var selected = SelectedPreset;
        if (selected is null)
        {
            return;
        }

        var expected = _stamp;
        var presetId = selected.Id;
        var draft = CaptureDraft();
        IsBusy = true;
        try
        {
            var result = await Task.Run(
                    () => _owner.TryApplyPreset(expected, presetId, draft))
                .ConfigureAwait(true);
            ApplyMutationResult(result);
            await RefreshPublishedCapabilitiesAfterSaveAsync(result).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() =>
        CanEdit && (IsDirty || NeedsInitialSave);

    private bool CanApplyPreset() =>
        CanEdit && SelectedPreset is not null;

    private async Task RefreshPublishedCapabilitiesAfterSaveAsync(
        CapabilitySettingsMutationResult result)
    {
        if (result.Status != CapabilitySettingsMutationStatus.Saved
            || _refreshPublishedCapabilities is null)
        {
            return;
        }

        try
        {
            if (await _refreshPublishedCapabilities().ConfigureAwait(true))
            {
                StatusText += " The running MCP server now publishes the saved capability set.";
            }
        }
        catch
        {
            StatusText += " The settings are saved, but the MCP publication refresh failed safely; use Restart on the MCP Server tab.";
        }
    }

    private Dictionary<string, bool> CaptureDraft() =>
        Rows.ToDictionary(row => row.GroupId, row => row.IsEnabled, StringComparer.Ordinal);

    private void ApplyMutationResult(CapabilitySettingsMutationResult result)
    {
        switch (result.Status)
        {
            case CapabilitySettingsMutationStatus.Saved:
                ApplyEnvelope(result.Current);
                StatusText = $"Saved capability settings to {_settingsFileName}.";
                break;
            case CapabilitySettingsMutationStatus.NoChange:
                ApplyEnvelope(result.Current);
                StatusText = $"{_settingsFileName} is already up to date.";
                break;
            case CapabilitySettingsMutationStatus.Conflict:
                RequiresReload = true;
                StatusText = $"{_settingsFileName} changed elsewhere. Your draft is preserved; reload before making another change.";
                break;
            case CapabilitySettingsMutationStatus.Busy:
                StatusText = $"{_settingsFileName} is busy. Your draft is preserved; try saving again.";
                break;
            case CapabilitySettingsMutationStatus.WriteFailed:
                StatusText = $"Could not write {_settingsFileName}. Your draft is preserved; try saving again.";
                break;
            case CapabilitySettingsMutationStatus.InvalidRequest:
                RequiresReload = true;
                StatusText = $"{_settingsFileName} no longer matches the displayed capabilities. Your draft is preserved; reload before making another change.";
                break;
            case CapabilitySettingsMutationStatus.FailedClosed:
                ApplyEnvelope(result.Current);
                RequiresReload = true;
                IsFailedClosed = true;
                StatusText = $"{_settingsFileName} could not be saved safely. Capabilities are fail-closed; reload is required.";
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown capability settings mutation status '{result.Status}'.");
        }
    }

    private void ApplyEnvelope(CapabilitySettingsEnvelope envelope)
    {
        _stamp = envelope.Stamp;
        LoadStatus = envelope.LoadStatus.ToString();
        KnownGroupCount = envelope.KnownGroupCount;
        EnabledGroupCount = envelope.EnabledGroupCount;
        DisabledGroupCount = envelope.DisabledGroupCount;
        DeclaredTaskToolCount = envelope.DeclaredTaskToolCount;
        CallableTaskToolCount = envelope.CallableTaskToolCount;
        UnavailableTaskToolCount = envelope.UnavailableTaskToolCount;
        CallableProtocolToolCount = envelope.CallableProtocolToolCount;
        UnavailableProtocolToolCount = envelope.UnavailableProtocolToolCount;
        QuarantinedRuntimeToolCount = envelope.QuarantinedRuntimeToolCount;
        UnknownSelectionCount = envelope.UnknownSelectionCount;

        foreach (var row in Rows)
        {
            row.SelectionChanged -= OnRowSelectionChanged;
        }
        Rows.Clear();
        _appliedSelections.Clear();
        foreach (var source in envelope.Rows)
        {
            var row = new CapabilitySettingsRowViewModel(source);
            row.SelectionChanged += OnRowSelectionChanged;
            Rows.Add(row);
            _appliedSelections.Add(row.GroupId, row.IsEnabled);
        }

        var selectedId = SelectedPreset?.Id;
        Presets.Clear();
        foreach (var source in envelope.Presets)
        {
            Presets.Add(new CapabilitySettingsPresetViewModel(source));
        }
        SelectedPreset = Presets.FirstOrDefault(
                preset => string.Equals(preset.Id, selectedId, StringComparison.Ordinal))
            ?? Presets.FirstOrDefault();

        IsDirty = false;
        NeedsInitialSave = envelope.LoadStatus == CapabilityAvailabilityLoadStatus.MissingFileDefaults;
        IsFailedClosed = envelope.LoadStatus == CapabilityAvailabilityLoadStatus.FailedClosed;
        RequiresReload = IsFailedClosed;
        UpdatePresetCounts();
        UpdateRowEditability();
        SummaryText = $"{envelope.CallableTaskToolCount} of {envelope.DeclaredTaskToolCount} task tools are ready across {envelope.EnabledGroupCount} enabled capability groups.";
        StatusText = envelope.LoadStatus switch
        {
            CapabilityAvailabilityLoadStatus.Loaded =>
                $"Loaded capability settings from {_settingsFileName}.",
            CapabilityAvailabilityLoadStatus.MissingFileDefaults =>
                $"{_settingsFileName} does not exist yet. Defaults are active and can be saved without making an edit.",
            CapabilityAvailabilityLoadStatus.FailedClosed =>
                $"{_settingsFileName} could not be loaded safely. Capabilities are fail-closed; reload is required.",
            _ => throw new InvalidOperationException(
                $"Unknown capability settings load status '{envelope.LoadStatus}'.")
        };
        RaiseCommandStates();
    }

    private void OnRowSelectionChanged(object? sender, EventArgs e)
    {
        IsDirty = Rows.Any(
            row => !_appliedSelections.TryGetValue(row.GroupId, out var applied)
                   || row.IsEnabled != applied);
        UpdatePresetCounts();
        StatusText = IsDirty
            ? $"Unsaved capability changes for {_settingsFileName}."
            : NeedsInitialSave
                ? $"{_settingsFileName} does not exist yet. Defaults are active and can be saved without making an edit."
                : $"No unsaved capability changes for {_settingsFileName}.";
    }

    private void UpdatePresetCounts()
    {
        var enabledById = Rows.ToDictionary(
            row => row.GroupId,
            row => row.IsEnabled,
            StringComparer.Ordinal);
        foreach (var preset in Presets)
        {
            preset.SetWouldEnableGroupCount(
                preset.GroupIds.Count(
                    groupId => !enabledById.TryGetValue(groupId, out var enabled) || !enabled));
        }
    }

    private void UpdateRowEditability()
    {
        foreach (var row in Rows)
        {
            row.SetIsEditable(CanEdit);
        }
    }

    private void RaiseCommandStates()
    {
        (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyPresetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void HandleUnexpectedMutationFailure(string operation)
    {
        RequiresReload = true;
        StatusText = $"Could not safely {operation} {_settingsFileName}. Your draft is preserved; reload before making another change.";
    }

    private void HandleUnexpectedReloadFailure()
    {
        RequiresReload = true;
        StatusText = $"Could not reload {_settingsFileName} safely. The current display is preserved; try reloading again.";
    }
}

public sealed class CapabilitySettingsRowViewModel : ObservableObject
{
    private bool _isEnabled;
    private bool _isEditable;

    internal CapabilitySettingsRowViewModel(CapabilitySettingsRow source)
    {
        GroupId = source.GroupId;
        Capability = source.Capability;
        Description = source.Description;
        _isEnabled = source.Enabled;
        Status = source.Status.ToString();
        DeclaredToolCount = source.DeclaredToolCount;
        CallableToolCount = source.CallableToolCount;
        UnavailableToolCount = source.UnavailableToolCount;
        Reasons = Array.AsReadOnly(
            source.Reasons
                .Select(reason => new CapabilitySettingsReasonViewModel(reason))
                .ToArray());
    }

    internal event EventHandler? SelectionChanged;

    public string GroupId { get; }

    public string Capability { get; }

    public string Description { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!IsEditable || !SetProperty(ref _isEnabled, value))
            {
                return;
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsEditable
    {
        get => _isEditable;
        private set => SetProperty(ref _isEditable, value);
    }

    public string Status { get; }

    public int DeclaredToolCount { get; }

    public int CallableToolCount { get; }

    public int UnavailableToolCount { get; }

    public IReadOnlyList<CapabilitySettingsReasonViewModel> Reasons { get; }

    internal void SetIsEditable(bool value) => IsEditable = value;
}

public sealed class CapabilitySettingsReasonViewModel
{
    internal CapabilitySettingsReasonViewModel(CapabilitySettingsReason source)
    {
        CapabilityId = source.CapabilityId;
        ToolName = source.ToolName;
        Code = source.Code.ToString();
        DependencyId = source.DependencyId;
        Message = source.Message;
    }

    public string CapabilityId { get; }

    public string ToolName { get; }

    public string Code { get; }

    public string DependencyId { get; }

    public string Message { get; }
}

public sealed class CapabilitySettingsPresetViewModel : ObservableObject
{
    private int _wouldEnableGroupCount;

    internal CapabilitySettingsPresetViewModel(CapabilitySettingsPreset source)
    {
        Id = source.Id;
        DisplayName = source.DisplayName;
        Description = source.Description;
        GroupIds = Array.AsReadOnly(source.GroupIds.ToArray());
        _wouldEnableGroupCount = source.WouldEnableGroupCount;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public IReadOnlyList<string> GroupIds { get; }

    public int WouldEnableGroupCount
    {
        get => _wouldEnableGroupCount;
        private set
        {
            if (SetProperty(ref _wouldEnableGroupCount, value))
            {
                OnPropertyChanged(nameof(IsFullyApplied));
            }
        }
    }

    public bool IsFullyApplied => WouldEnableGroupCount == 0;

    internal void SetWouldEnableGroupCount(int value) => WouldEnableGroupCount = value;
}
