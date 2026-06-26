using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ali.App.Wpf;
using Ali.Core.Conversations;
using Ali.Core.Coding;
using Ali.Core.Evidence;
using Ali.Core.Feedback;
using Ali.Core.Memory;
using Ali.Core.Reminders;
using Ali.Core.Runtime;
using Ali.Core.Voice;
using Ali.Infrastructure.Bootstrap;
using Ali.Infrastructure.Coding;
using Ali.Infrastructure.Runtime;
using Ali.Infrastructure.Voice;
using MediaBrushes = System.Windows.Media.Brushes;

namespace Ali.App.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const double SpectrumRenderWidth = 720d;
    private const double SpectrumRenderHeight = 130d;
    private const double SpectrumRenderInset = 12d;
    private const string RuntimeTopPModelDefault = "Model default";
    private static readonly TimeSpan ModelStatusPingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OllamaStartRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly string[] RuntimeTemperatureChoiceValues = ["0", "0.1", "0.2", "0.3", "0.5", "0.7", "1", "1.5", "2"];
    private static readonly string[] RuntimeTopPChoiceValues = [RuntimeTopPModelDefault, "0.5", "0.7", "0.8", "0.9", "0.95", "1"];
    private static readonly string[] CodingWorkspaceAccessModeChoiceValues = [CodingPermissionModes.Allowed];
    private static readonly string[] CodingExplicitOutsideFileOpenModeChoiceValues = [CodingPermissionModes.Allowed, CodingPermissionModes.Disabled];
    private static readonly string[] CodingSearchOutsideWorkspaceModeChoiceValues = [CodingPermissionModes.AskFirst, CodingPermissionModes.Disabled];
    private static readonly string[] CodingConfirmOrDisabledModeChoiceValues = [CodingPermissionModes.ConfirmEachTime, CodingPermissionModes.Disabled];
    private static readonly string[] CodingDestructiveActionModeChoiceValues = [CodingPermissionModes.ExtraConfirmation, CodingPermissionModes.Disabled];
    private static readonly string[] CodingBlockedModeChoiceValues = [CodingPermissionModes.Blocked];
    private static readonly string[] CodingGitReadModeChoiceValues = [CodingPermissionModes.Allowed, CodingPermissionModes.Disabled];
    private static readonly string[] CodingGitNetworkModeChoiceValues = [CodingPermissionModes.Blocked, CodingPermissionModes.ConfirmEachTime];
    private static readonly string[] CodingPdfReadCreateModeChoiceValues = [CodingPermissionModes.Allowed, CodingPermissionModes.Disabled];
    private static readonly string[] CodingPdfModifyModeChoiceValues = [CodingPermissionModes.ConfirmEachTime, CodingPermissionModes.Disabled];
    private readonly AliServices _services;
    private readonly NAudioInputLevelMonitor _inputLevelMonitor = new();
    private readonly SystemResourceMonitor _resourceMonitor = new();
    private readonly DispatcherTimer _resourceMeterTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _modelStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _shownDueReminderIds = new(StringComparer.OrdinalIgnoreCase);
    private string _conversationId = ConversationSessionFactory.StartFresh().ConversationId;
    private ConversationHistoryItemViewModel? _activeConversationHistoryItem;
    private ConversationHistoryItemViewModel? _selectedConversationHistoryItem;
    private string _conversationSearchText = string.Empty;
    private bool _loadingConversationHistorySelection;
    private bool _checkingModelConnectionStatus;
    private bool _ollamaWasRunningAtStartup;
    private bool _ollamaStartInProgress;
    private DateTimeOffset _nextOllamaStartAttemptAt = DateTimeOffset.MinValue;
    private readonly HashSet<int> _ollamaProcessIdsStartedByAli = new();
    private readonly Dictionary<string, PiperVoiceChoice> _piperVoiceChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeModelChoice> _runtimeModelChoices = new(StringComparer.OrdinalIgnoreCase);
    private VoiceRuntimeSettings _voiceSettings;
    private bool _loadingVoiceSettings;
    private bool _loadingSpeechToolSettings;
    private CancellationTokenSource? _activeResponse;
    private CancellationTokenSource? _activeVoiceInput;
    private CancellationTokenSource? _activeSpeech;
    private SettingsWindow? _settingsWindow;
    private LocalLibraryWindow? _localLibraryWindow;
    private bool _voiceMonitorRequested;
    private bool _suppressInputMonitorRestart;
    private VoiceCaptureDiagnostics? _lastCaptureDiagnostics;
    private double[] _lastSpectrumMagnitudes = new double[SpectrumAnalyzer.BarCount];
    private double[] _renderedSpectrumMagnitudes = new double[SpectrumAnalyzer.BarCount];
    private double _spectrumVisualCeiling = 0.25d;
    private double _lastSpectrumPeakLevel;
    private string _composerText = string.Empty;
    private bool _isBusy;
    private bool _isRecording;
    private bool _isTranscribing;
    private bool _isSpeaking;
    private string _statusText = "Ready. Local runtime is not configured yet.";
    private string _runtimeDisplay;
    private string _runtimeEndpointText = string.Empty;
    private string _runtimeModelText = string.Empty;
    private string _runtimeContextText = "2048";
    private string _runtimeOutputLimitText = "256";
    private string _runtimeTemperatureText = "0.2";
    private string _runtimeTopPText = RuntimeTopPModelDefault;
    private string _runtimeQuantizationText = "Installed package default";
    private string _selectedRuntimeModelChoice = string.Empty;
    private string _runtimeSelectionStatusText = "Runtime model list has not been refreshed yet.";
    private bool _runtimeEnabled;
    private bool _runtimeStreamingEnabled = true;
    private bool _runtimeVisionEnabled;
    private bool _canActivateRuntime;
    private bool _canRevertToLastKnownGood;
    private string _runtimeHealthResult = "No runtime health check has been run.";
    private string _activeRuntimeStatus = "Using safe deterministic stub.";
    private string _modelConnectionStatusText = "model offline";
    private System.Windows.Media.Brush _modelConnectionStatusBrush = MediaBrushes.Red;
    private string _attachmentStatus = "Screenshots are temporary by default.";
    private string _voiceStatus = "Voice idle.";
    private string _sttStatus = "STT status loading.";
    private string _ttsStatus = "TTS status loading.";
    private string _lastTranscript = string.Empty;
    private string _editableTranscript = string.Empty;
    private string _selectedVoiceInputDevice = "Default microphone";
    private string _selectedVoiceOutputDevice = "Default speaker";
    private string _selectedVoiceInputPreset = VoiceInputPreset.HeadsetMic;
    private string _selectedVoiceInputChannelMode = InputChannelModeCatalog.MonoSumLabel;
    private double _voiceInputLevelPercent;
    private string _voiceInputMeterText = "Input meter starting.";
    private string _voiceDiagnosticsText = "No voice capture yet.";
    private string _lastSttDebugText = "No STT debug invocation yet.";
    private PointCollection _spectrumLivePoints = CreateFlatSpectrumPoints();
    private string _spectrumPeakText = "Peak 0%";
    private double _spectrumDisplayGain = 6d;
    private string _whisperExecutableText = string.Empty;
    private string _whisperModelText = string.Empty;
    private string _whisperArgumentsText = string.Empty;
    private string _piperExecutableText = string.Empty;
    private string _piperModelText = string.Empty;
    private string _piperVoiceText = "default";
    private string _selectedPiperVoiceChoice = string.Empty;
    private string _piperArgumentsText = string.Empty;
    private string _voiceSettingsStatusText = "Voice settings loaded.";
    private double _extraInputGainDb;
    private bool _normalizeBeforeStt;
    private bool _retainDebugAudio;
    private bool _autoSendVoiceTranscripts;
    private string _pushToTalkKeyText = "NumPad0";
    private bool _isAssigningPushToTalkKey;
    private bool _pushToTalkKeyDown;
    private bool _currentVoiceInputShouldAutoSend;
    private VoiceTurnMetadata? _lastVoiceMetadata;
    private CorrectionReviewItemViewModel? _selectedCorrectionReviewItem;
    private string _correctionReviewStatusText = "Correction queue not loaded yet.";
    private MemoryEntryViewModel? _selectedMemoryEntry;
    private ReminderEntryViewModel? _selectedReminderEntry;
    private string _memoryReminderStatusText = "Memory and reminder stores not loaded yet.";
    private string _codingWorkspaceRootText = string.Empty;
    private bool _codingAllowExplicitOutsideFileOpen = true;
    private string _codingWorkspaceAccessMode = CodingPermissionModes.Allowed;
    private string _codingExplicitOutsideFileOpenMode = CodingPermissionModes.Allowed;
    private string _codingSearchOutsideWorkspaceMode = CodingPermissionModes.AskFirst;
    private string _codingEditInsideWorkspaceMode = CodingPermissionModes.ConfirmEachTime;
    private string _codingBuildTestRunInsideWorkspaceMode = CodingPermissionModes.ConfirmEachTime;
    private string _codingDestructiveActionMode = CodingPermissionModes.ExtraConfirmation;
    private string _codingOutsideEditRunMode = CodingPermissionModes.Blocked;
    private string _codingSystemAdminActionMode = CodingPermissionModes.Blocked;
    private string _codingGitReadMode = CodingPermissionModes.Allowed;
    private string _codingGitWriteMode = CodingPermissionModes.ConfirmEachTime;
    private string _codingGitMergeMode = CodingPermissionModes.ExtraConfirmation;
    private string _codingGitNetworkMode = CodingPermissionModes.Blocked;
    private string _codingPdfWorkspaceRootText = string.Empty;
    private string _codingPdfReadMode = CodingPermissionModes.Allowed;
    private string _codingPdfCreateMode = CodingPermissionModes.Allowed;
    private string _codingPdfModifyMode = CodingPermissionModes.ConfirmEachTime;
    private string _codingNotepadPlusPlusPathText = string.Empty;
    private string _codingVisualStudioPathText = string.Empty;
    private string _codingPermissionsStatusText = "Coding permissions not loaded yet.";

    public MainWindowViewModel(AliServices services)
    {
        _services = services;
        ResourceMeters.Add(CpuMeter);
        ResourceMeters.Add(RamMeter);
        ResourceMeters.Add(GpuMeter);
        ResourceMeters.Add(VramMeter);

        SendCommand = CreateAsyncCommand(SendAsync, () => IsBusy || IsSpeaking || !string.IsNullOrWhiteSpace(ComposerText));
        StopCommand = CreateCommand(_ => Stop(), _ => IsBusy);
        NewChatCommand = CreateCommand(_ => StartNewChat());
        EraseHistoryCommand = CreateCommand(_ => EraseHistory());
        EraseConversationCommand = CreateCommand(EraseConversation);
        RenameConversationCommand = CreateCommand(RenameConversation);
        CommitConversationRenameCommand = CreateCommand(CommitConversationRename);
        FlagIncorrectCommand = CreateCommand(FlagIncorrect);
        SaveRuntimeSettingsCommand = CreateCommand(_ => SaveRuntimeSettings());
        CheckRuntimeCommand = CreateAsyncCommand(CheckRuntimeAsync, () => !IsBusy);
        RefreshRuntimeModelsCommand = CreateAsyncCommand(RefreshRuntimeModelsAsync, () => !IsBusy);
        ActivateRuntimeCommand = CreateCommand(_ => ActivateRuntime(), _ => CanActivateRuntime && !IsBusy);
        RevertToStubCommand = CreateCommand(_ => RevertToStub(), _ => !IsBusy);
        RevertToLastKnownGoodCommand = CreateCommand(_ => RevertToLastKnownGood(), _ => CanRevertToLastKnownGood && !IsBusy);
        PasteImageCommand = CreateAsyncCommand(AddClipboardImageAsync);
        RemoveAttachmentCommand = CreateCommand(RemoveAttachment);
        ToggleVoiceRecordingCommand = CreateAsyncCommand(ToggleVoiceRecordingAsync, () => !IsBusy || IsRecording || IsTranscribing);
        ToggleVoiceModeCommand = CreateCommand(_ => AutoSendVoiceTranscripts = !AutoSendVoiceTranscripts);
        BeginAssignPushToTalkKeyCommand = CreateCommand(_ => BeginAssignPushToTalkKey());
        SendTranscriptCommand = CreateAsyncCommand(SendTranscriptAsync, () => !IsBusy && !IsRecording && !IsTranscribing && !string.IsNullOrWhiteSpace(EditableTranscript));
        StopSpeakingCommand = CreateCommand(_ => StopSpeaking(), _ => IsSpeaking);
        OpenSettingsCommand = CreateAsyncCommand(OpenSettingsAsync);
        OpenLocalLibraryCommand = CreateCommand(_ => OpenLocalLibrary());
        PlayPiperSampleCommand = CreateAsyncCommand(PlayPiperSampleAsync, () => !IsSpeaking);
        RefreshCorrectionsCommand = CreateAsyncCommand(RefreshCorrectionsAsync);
        MarkCorrectionReviewedCommand = CreateAsyncCommand(MarkSelectedCorrectionReviewedAsync, () => SelectedCorrectionReviewItem is not null);
        MarkCorrectionUnresolvedCommand = CreateAsyncCommand(MarkSelectedCorrectionUnresolvedAsync, () => SelectedCorrectionReviewItem is not null);
        ExportSelectedCorrectionCommand = CreateAsyncCommand(ExportSelectedCorrectionAsync, () => SelectedCorrectionReviewItem is not null);
        ExportAllCorrectionsCommand = CreateAsyncCommand(ExportAllCorrectionsAsync);
        RefreshMemoryRemindersCommand = CreateCommand(_ => RefreshMemoryReminders());
        DeleteSelectedMemoryCommand = CreateCommand(_ => DeleteSelectedMemory(), _ => SelectedMemoryEntry is not null);
        ClearMemoriesCommand = CreateCommand(_ => ClearMemories());
        CancelSelectedReminderCommand = CreateCommand(_ => SetSelectedReminderStatus(ReminderStatus.Cancelled), _ => SelectedReminderEntry is not null);
        CompleteSelectedReminderCommand = CreateCommand(_ => SetSelectedReminderStatus(ReminderStatus.Completed), _ => SelectedReminderEntry is not null);
        ClearRemindersCommand = CreateCommand(_ => ClearReminders());
        SaveCodingPermissionsCommand = CreateCommand(_ => SaveCodingPermissions());
        ResetCodingPermissionsCommand = CreateCommand(_ => ResetCodingPermissionsToDefault());
        BrowseCodingWorkspaceRootCommand = CreateCommand(_ => BrowseCodingWorkspaceRoot());
        BrowseCodingPdfWorkspaceRootCommand = CreateCommand(_ => BrowseCodingPdfWorkspaceRoot());
        BrowseNotepadPlusPlusPathCommand = CreateCommand(_ => BrowseCodingToolPath("Choose notepad++.exe", "Notepad++ (notepad++.exe)|notepad++.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*", path => CodingNotepadPlusPlusPathText = path));
        BrowseVisualStudioPathCommand = CreateCommand(_ => BrowseCodingToolPath("Choose Visual Studio devenv.exe", "Visual Studio (devenv.exe)|devenv.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*", path => CodingVisualStudioPathText = path));

        _voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(_services.DataRoot);
        _extraInputGainDb = _voiceSettings.ExtraInputGainDb;
        _normalizeBeforeStt = _voiceSettings.NormalizeBeforeStt;
        _retainDebugAudio = _voiceSettings.RetainDebugAudio;
        _autoSendVoiceTranscripts = _voiceSettings.AutoSendVoiceTranscripts;
        _pushToTalkKeyText = NormalizePushToTalkKey(_voiceSettings.PushToTalkKey);
        LoadSpeechToolSettings();
        ApplyVoiceToolSettings(saveSettings: false, reportStatus: false);
        foreach (var preset in VoiceInputPreset.All)
        {
            VoiceInputPresets.Add(preset);
        }

        _selectedVoiceInputPreset = VoiceInputPreset.Normalize(_voiceSettings.SelectedInputPreset);
        _selectedVoiceInputChannelMode = InputChannelModeCatalog.ToLabel(
            InputChannelModeCatalog.FromStorageValue(_voiceSettings.SelectedInputChannelMode));
        _inputLevelMonitor.LevelAvailable += InputLevelAvailable;
        _inputLevelMonitor.SpectrumAvailable += SpectrumAvailable;

        _loadingVoiceSettings = true;
        LoadVoiceDevices();
        _loadingVoiceSettings = false;
        ApplyVoiceInputPreset(SelectedVoiceInputPreset);
        VoiceInputLevelPercent = 0;
        VoiceInputMeterText = "Input meter paused.";
        VoiceDiagnosticsText = "Open Settings or start a voice action to monitor the microphone.";

        RefreshSpeechToolStatuses();

        ReplaceChoices(RuntimeTemperatureChoices, RuntimeTemperatureChoiceValues);
        ReplaceChoices(RuntimeTopPChoices, RuntimeTopPChoiceValues);
        ReplaceChoices(CodingWorkspaceAccessModeChoices, CodingWorkspaceAccessModeChoiceValues);
        ReplaceChoices(CodingExplicitOutsideFileOpenModeChoices, CodingExplicitOutsideFileOpenModeChoiceValues);
        ReplaceChoices(CodingSearchOutsideWorkspaceModeChoices, CodingSearchOutsideWorkspaceModeChoiceValues);
        ReplaceChoices(CodingEditInsideWorkspaceModeChoices, CodingConfirmOrDisabledModeChoiceValues);
        ReplaceChoices(CodingBuildTestRunInsideWorkspaceModeChoices, CodingConfirmOrDisabledModeChoiceValues);
        ReplaceChoices(CodingDestructiveActionModeChoices, CodingDestructiveActionModeChoiceValues);
        ReplaceChoices(CodingOutsideEditRunModeChoices, CodingBlockedModeChoiceValues);
        ReplaceChoices(CodingSystemAdminActionModeChoices, CodingBlockedModeChoiceValues);
        ReplaceChoices(CodingGitReadModeChoices, CodingGitReadModeChoiceValues);
        ReplaceChoices(CodingGitWriteModeChoices, CodingConfirmOrDisabledModeChoiceValues);
        ReplaceChoices(CodingGitMergeModeChoices, CodingDestructiveActionModeChoiceValues);
        ReplaceChoices(CodingGitNetworkModeChoices, CodingGitNetworkModeChoiceValues);
        ReplaceChoices(CodingPdfReadModeChoices, CodingPdfReadCreateModeChoiceValues);
        ReplaceChoices(CodingPdfCreateModeChoices, CodingPdfReadCreateModeChoiceValues);
        ReplaceChoices(CodingPdfModifyModeChoices, CodingPdfModifyModeChoiceValues);
        _runtimeDisplay = FormatRuntimeDisplay();
        LoadRuntimeSettings();
        _resourceMeterTimer.Tick += (_, _) => RefreshResourceMeters();
        RefreshResourceMeters();
        _resourceMeterTimer.Start();
        _modelStatusTimer.Tick += async (_, _) => await RefreshModelConnectionStatusAsync(showWaiting: false).ConfigureAwait(true);
        _modelStatusTimer.Start();
        _reminderTimer.Tick += (_, _) => CheckDueReminders();
        _reminderTimer.Start();
        RefreshConversationHistory();
        RefreshMemoryReminders();
        LoadCodingPermissions();
        StatusText = "New chat ready. Saved chats are available in the sidebar.";
    }

    private AsyncRelayCommand CreateAsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(execute, canExecute, HandleCommandException);

    private RelayCommand CreateCommand(Action<object?> execute, Predicate<object?>? canExecute = null) =>
        new(execute, canExecute, HandleCommandException);

    private void HandleCommandException(Exception ex)
    {
        ReportApplicationFailure("Command", ex);
    }

    public void ReportApplicationFailure(string context, Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        StatusText = $"{context} failed safely: {message}";
        if (_settingsWindow is not null)
        {
            VoiceSettingsStatusText = $"{context} failed safely: {message}";
        }
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = new();

    public ObservableCollection<ConversationHistoryItemViewModel> ConversationHistory { get; } = new();

    public ObservableCollection<ResourceMeterViewModel> ResourceMeters { get; } = new();

    public ObservableCollection<CorrectionReviewItemViewModel> CorrectionReviewItems { get; } = new();

    public ObservableCollection<MemoryEntryViewModel> MemoryEntries { get; } = new();

    public ObservableCollection<ReminderEntryViewModel> ReminderEntries { get; } = new();

    public ConversationHistoryItemViewModel? SelectedConversationHistoryItem
    {
        get => _selectedConversationHistoryItem;
        set
        {
            if (!SetProperty(ref _selectedConversationHistoryItem, value)
                || value is null
                || _loadingConversationHistorySelection)
            {
                return;
            }

            LoadConversation(value);
        }
    }

    public string ConversationSearchText
    {
        get => _conversationSearchText;
        set
        {
            if (SetProperty(ref _conversationSearchText, value))
            {
                RefreshConversationHistory();
            }
        }
    }

    public CorrectionReviewItemViewModel? SelectedCorrectionReviewItem
    {
        get => _selectedCorrectionReviewItem;
        set
        {
            if (SetProperty(ref _selectedCorrectionReviewItem, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string CorrectionReviewStatusText
    {
        get => _correctionReviewStatusText;
        private set => SetProperty(ref _correctionReviewStatusText, value);
    }

    public MemoryEntryViewModel? SelectedMemoryEntry
    {
        get => _selectedMemoryEntry;
        set
        {
            if (SetProperty(ref _selectedMemoryEntry, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ReminderEntryViewModel? SelectedReminderEntry
    {
        get => _selectedReminderEntry;
        set
        {
            if (SetProperty(ref _selectedReminderEntry, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string MemoryReminderStatusText
    {
        get => _memoryReminderStatusText;
        private set => SetProperty(ref _memoryReminderStatusText, value);
    }

    public ResourceMeterViewModel CpuMeter { get; } = new("CPU");

    public ResourceMeterViewModel RamMeter { get; } = new("RAM");

    public ResourceMeterViewModel GpuMeter { get; } = new("GPU");

    public ResourceMeterViewModel VramMeter { get; } = new("VRAM");

    public ObservableCollection<string> VoiceInputDevices { get; } = new();

    public ObservableCollection<string> VoiceOutputDevices { get; } = new();

    public ObservableCollection<string> VoiceInputPresets { get; } = new();

    public ObservableCollection<string> VoiceInputChannelModes { get; } = new();

    public ObservableCollection<string> PiperVoiceChoices { get; } = new();

    public ObservableCollection<string> RuntimeModelChoices { get; } = new();

    public ObservableCollection<string> RuntimeQuantizationChoices { get; } = new();

    public ObservableCollection<string> RuntimeContextChoices { get; } = new();

    public ObservableCollection<string> RuntimeOutputLimitChoices { get; } = new();

    public ObservableCollection<string> RuntimeTemperatureChoices { get; } = new();

    public ObservableCollection<string> RuntimeTopPChoices { get; } = new();

    public ObservableCollection<string> CodingWorkspaceAccessModeChoices { get; } = new();

    public ObservableCollection<string> CodingExplicitOutsideFileOpenModeChoices { get; } = new();

    public ObservableCollection<string> CodingSearchOutsideWorkspaceModeChoices { get; } = new();

    public ObservableCollection<string> CodingEditInsideWorkspaceModeChoices { get; } = new();

    public ObservableCollection<string> CodingBuildTestRunInsideWorkspaceModeChoices { get; } = new();

    public ObservableCollection<string> CodingDestructiveActionModeChoices { get; } = new();

    public ObservableCollection<string> CodingOutsideEditRunModeChoices { get; } = new();

    public ObservableCollection<string> CodingSystemAdminActionModeChoices { get; } = new();

    public ObservableCollection<string> CodingGitReadModeChoices { get; } = new();

    public ObservableCollection<string> CodingGitWriteModeChoices { get; } = new();

    public ObservableCollection<string> CodingGitMergeModeChoices { get; } = new();

    public ObservableCollection<string> CodingGitNetworkModeChoices { get; } = new();

    public ObservableCollection<string> CodingPdfReadModeChoices { get; } = new();

    public ObservableCollection<string> CodingPdfCreateModeChoices { get; } = new();

    public ObservableCollection<string> CodingPdfModifyModeChoices { get; } = new();

    public ICommand SendCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand NewChatCommand { get; }

    public ICommand EraseHistoryCommand { get; }

    public ICommand EraseConversationCommand { get; }

    public ICommand RenameConversationCommand { get; }

    public ICommand CommitConversationRenameCommand { get; }

    public ICommand FlagIncorrectCommand { get; }

    public ICommand SaveRuntimeSettingsCommand { get; }

    public ICommand CheckRuntimeCommand { get; }

    public ICommand RefreshRuntimeModelsCommand { get; }

    public ICommand ActivateRuntimeCommand { get; }

    public ICommand RevertToStubCommand { get; }

    public ICommand RevertToLastKnownGoodCommand { get; }

    public ICommand PasteImageCommand { get; }

    public ICommand RemoveAttachmentCommand { get; }

    public ICommand ToggleVoiceRecordingCommand { get; }

    public ICommand ToggleVoiceModeCommand { get; }

    public ICommand BeginAssignPushToTalkKeyCommand { get; }

    public ICommand SendTranscriptCommand { get; }

    public ICommand StopSpeakingCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenLocalLibraryCommand { get; }

    public ICommand PlayPiperSampleCommand { get; }

    public ICommand RefreshCorrectionsCommand { get; }

    public ICommand MarkCorrectionReviewedCommand { get; }

    public ICommand MarkCorrectionUnresolvedCommand { get; }

    public ICommand ExportSelectedCorrectionCommand { get; }

    public ICommand ExportAllCorrectionsCommand { get; }

    public ICommand RefreshMemoryRemindersCommand { get; }

    public ICommand DeleteSelectedMemoryCommand { get; }

    public ICommand ClearMemoriesCommand { get; }

    public ICommand CancelSelectedReminderCommand { get; }

    public ICommand CompleteSelectedReminderCommand { get; }

    public ICommand ClearRemindersCommand { get; }

    public ICommand SaveCodingPermissionsCommand { get; }

    public ICommand ResetCodingPermissionsCommand { get; }

    public ICommand BrowseCodingWorkspaceRootCommand { get; }

    public ICommand BrowseCodingPdfWorkspaceRootCommand { get; }

    public ICommand BrowseNotepadPlusPlusPathCommand { get; }

    public ICommand BrowseVisualStudioPathCommand { get; }

    public string RuntimeSettingsPath => _services.RuntimeSettingsPath;

    public string CodingToolSettingsPath => _services.CodingToolSettingsPath;

    public string CodingWorkspaceRootText
    {
        get => _codingWorkspaceRootText;
        set
        {
            if (SetProperty(ref _codingWorkspaceRootText, value))
            {
                OnPropertyChanged(nameof(CodingPermissionSummaryText));
            }
        }
    }

    public string CodingPdfWorkspaceRootText
    {
        get => _codingPdfWorkspaceRootText;
        set
        {
            if (SetProperty(ref _codingPdfWorkspaceRootText, value))
            {
                OnPropertyChanged(nameof(CodingPermissionSummaryText));
            }
        }
    }

    public bool CodingAllowExplicitOutsideFileOpen
    {
        get => _codingAllowExplicitOutsideFileOpen;
        set
        {
            if (SetProperty(ref _codingAllowExplicitOutsideFileOpen, value))
            {
                var mode = value ? CodingPermissionModes.Allowed : CodingPermissionModes.Disabled;
                if (!_codingExplicitOutsideFileOpenMode.Equals(mode, StringComparison.OrdinalIgnoreCase))
                {
                    _codingExplicitOutsideFileOpenMode = mode;
                    OnPropertyChanged(nameof(CodingExplicitOutsideFileOpenMode));
                }

                OnPropertyChanged(nameof(CodingPermissionSummaryText));
            }
        }
    }

    public string CodingWorkspaceAccessMode
    {
        get => _codingWorkspaceAccessMode;
        set => SetCodingPermissionMode(ref _codingWorkspaceAccessMode, value);
    }

    public string CodingExplicitOutsideFileOpenMode
    {
        get => _codingExplicitOutsideFileOpenMode;
        set
        {
            if (SetCodingPermissionMode(ref _codingExplicitOutsideFileOpenMode, value))
            {
                var allowOutsideFileOpen = !CodingPermissionModes.IsDisabled(_codingExplicitOutsideFileOpenMode);
                if (_codingAllowExplicitOutsideFileOpen != allowOutsideFileOpen)
                {
                    _codingAllowExplicitOutsideFileOpen = allowOutsideFileOpen;
                    OnPropertyChanged(nameof(CodingAllowExplicitOutsideFileOpen));
                }
            }
        }
    }

    public string CodingSearchOutsideWorkspaceMode
    {
        get => _codingSearchOutsideWorkspaceMode;
        set => SetCodingPermissionMode(ref _codingSearchOutsideWorkspaceMode, value);
    }

    public string CodingEditInsideWorkspaceMode
    {
        get => _codingEditInsideWorkspaceMode;
        set => SetCodingPermissionMode(ref _codingEditInsideWorkspaceMode, value);
    }

    public string CodingBuildTestRunInsideWorkspaceMode
    {
        get => _codingBuildTestRunInsideWorkspaceMode;
        set => SetCodingPermissionMode(ref _codingBuildTestRunInsideWorkspaceMode, value);
    }

    public string CodingDestructiveActionMode
    {
        get => _codingDestructiveActionMode;
        set => SetCodingPermissionMode(ref _codingDestructiveActionMode, value);
    }

    public string CodingOutsideEditRunMode
    {
        get => _codingOutsideEditRunMode;
        set => SetCodingPermissionMode(ref _codingOutsideEditRunMode, value);
    }

    public string CodingSystemAdminActionMode
    {
        get => _codingSystemAdminActionMode;
        set => SetCodingPermissionMode(ref _codingSystemAdminActionMode, value);
    }

    public string CodingGitReadMode
    {
        get => _codingGitReadMode;
        set => SetCodingPermissionMode(ref _codingGitReadMode, value);
    }

    public string CodingGitWriteMode
    {
        get => _codingGitWriteMode;
        set => SetCodingPermissionMode(ref _codingGitWriteMode, value);
    }

    public string CodingGitMergeMode
    {
        get => _codingGitMergeMode;
        set => SetCodingPermissionMode(ref _codingGitMergeMode, value);
    }

    public string CodingGitNetworkMode
    {
        get => _codingGitNetworkMode;
        set => SetCodingPermissionMode(ref _codingGitNetworkMode, value);
    }

    public string CodingPdfReadMode
    {
        get => _codingPdfReadMode;
        set => SetCodingPermissionMode(ref _codingPdfReadMode, value);
    }

    public string CodingPdfCreateMode
    {
        get => _codingPdfCreateMode;
        set => SetCodingPermissionMode(ref _codingPdfCreateMode, value);
    }

    public string CodingPdfModifyMode
    {
        get => _codingPdfModifyMode;
        set => SetCodingPermissionMode(ref _codingPdfModifyMode, value);
    }

    public string CodingNotepadPlusPlusPathText
    {
        get => _codingNotepadPlusPlusPathText;
        set
        {
            if (SetProperty(ref _codingNotepadPlusPlusPathText, value))
            {
                OnPropertyChanged(nameof(CodingPermissionSummaryText));
            }
        }
    }

    public string CodingVisualStudioPathText
    {
        get => _codingVisualStudioPathText;
        set
        {
            if (SetProperty(ref _codingVisualStudioPathText, value))
            {
                OnPropertyChanged(nameof(CodingPermissionSummaryText));
            }
        }
    }

    public string CodingPermissionsStatusText
    {
        get => _codingPermissionsStatusText;
        private set => SetProperty(ref _codingPermissionsStatusText, value);
    }

    public string CodingPermissionSummaryText =>
        string.Join(
            Environment.NewLine,
            [
                $"Workspace domain: {CodingWorkspaceRootText}",
                $"Notepad++: {DescribeConfiguredToolPath(CodingNotepadPlusPlusPathText)}",
                $"Visual Studio: {DescribeConfiguredToolPath(CodingVisualStudioPathText)}",
                $"Read/open/search inside workspace: {CodingWorkspaceAccessMode}.",
                $"Explicit outside file open/read: {CodingExplicitOutsideFileOpenMode}.",
                $"Search outside workspace: {CodingSearchOutsideWorkspaceMode}.",
                $"Edit/write inside workspace: {CodingEditInsideWorkspaceMode}.",
                $"Build/test/run inside workspace: {CodingBuildTestRunInsideWorkspaceMode}.",
                $"Delete/move/overwrite: {CodingDestructiveActionMode}.",
                $"Edit/run outside workspace: {CodingOutsideEditRunMode}.",
                $"System folders, registry, services, drivers, security settings: {CodingSystemAdminActionMode}.",
                $"Git status/diff/log: {CodingGitReadMode}.",
                $"Git add/commit: {CodingGitWriteMode}.",
                $"Git merge: {CodingGitMergeMode}.",
                $"Git pull/push: {CodingGitNetworkMode}.",
                $"PDF workspace: {CodingPdfWorkspaceRootText}.",
                $"PDF inspect/extract: {CodingPdfReadMode}.",
                $"PDF create/export: {CodingPdfCreateMode}.",
                $"PDF combine/split/modify: {CodingPdfModifyMode}."
            ]);

    public string MicButtonText => IsRecording ? "Stop Mic" : IsTranscribing ? "Transcribing" : "Mic";

    public string VoiceModeButtonText => AutoSendVoiceTranscripts
        ? $"PTT On ({PushToTalkKeyLabel})"
        : $"PTT Off ({PushToTalkKeyLabel})";

    public string PushToTalkHintText => $"Hold {PushToTalkKeyLabel} to record. Release to transcribe and send.";

    public string RuntimeDisplay
    {
        get => _runtimeDisplay;
        private set => SetProperty(ref _runtimeDisplay, value);
    }

    public string RuntimeEndpointText
    {
        get => _runtimeEndpointText;
        set => SetProperty(ref _runtimeEndpointText, value);
    }

    public string RuntimeModelText
    {
        get => _runtimeModelText;
        set => SetProperty(ref _runtimeModelText, value);
    }

    public string SelectedRuntimeModelChoice
    {
        get => _selectedRuntimeModelChoice;
        set
        {
            if (SetProperty(ref _selectedRuntimeModelChoice, value))
            {
                ApplySelectedRuntimeModelChoice(value, resetToSmallest: true);
            }
        }
    }

    public string RuntimeQuantizationText
    {
        get => _runtimeQuantizationText;
        set => SetProperty(ref _runtimeQuantizationText, value);
    }

    public string RuntimeContextText
    {
        get => _runtimeContextText;
        set => SetProperty(ref _runtimeContextText, value);
    }

    public string RuntimeOutputLimitText
    {
        get => _runtimeOutputLimitText;
        set => SetProperty(ref _runtimeOutputLimitText, value);
    }

    public string RuntimeTemperatureText
    {
        get => _runtimeTemperatureText;
        set => SetProperty(ref _runtimeTemperatureText, value);
    }

    public string RuntimeTopPText
    {
        get => _runtimeTopPText;
        set => SetProperty(ref _runtimeTopPText, value);
    }

    public string RuntimeSelectionStatusText
    {
        get => _runtimeSelectionStatusText;
        private set => SetProperty(ref _runtimeSelectionStatusText, value);
    }

    public bool RuntimeEnabled
    {
        get => _runtimeEnabled;
        set => SetProperty(ref _runtimeEnabled, value);
    }

    public bool RuntimeStreamingEnabled
    {
        get => _runtimeStreamingEnabled;
        set => SetProperty(ref _runtimeStreamingEnabled, value);
    }

    public bool RuntimeVisionEnabled
    {
        get => _runtimeVisionEnabled;
        set => SetProperty(ref _runtimeVisionEnabled, value);
    }

    public bool CanActivateRuntime
    {
        get => _canActivateRuntime;
        private set
        {
            if (SetProperty(ref _canActivateRuntime, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool CanRevertToLastKnownGood
    {
        get => _canRevertToLastKnownGood;
        private set
        {
            if (SetProperty(ref _canRevertToLastKnownGood, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string RuntimeHealthResult
    {
        get => _runtimeHealthResult;
        private set => SetProperty(ref _runtimeHealthResult, value);
    }

    public string ActiveRuntimeStatus
    {
        get => _activeRuntimeStatus;
        private set => SetProperty(ref _activeRuntimeStatus, value);
    }

    public string ModelConnectionStatusText
    {
        get => _modelConnectionStatusText;
        private set => SetProperty(ref _modelConnectionStatusText, value);
    }

    public System.Windows.Media.Brush ModelConnectionStatusBrush
    {
        get => _modelConnectionStatusBrush;
        private set => SetProperty(ref _modelConnectionStatusBrush, value);
    }

    public string AttachmentStatus
    {
        get => _attachmentStatus;
        private set => SetProperty(ref _attachmentStatus, value);
    }

    public string VoiceStatus
    {
        get => _voiceStatus;
        private set => SetProperty(ref _voiceStatus, value);
    }

    public string SttStatus
    {
        get => _sttStatus;
        private set => SetProperty(ref _sttStatus, value);
    }

    public string TtsStatus
    {
        get => _ttsStatus;
        private set => SetProperty(ref _ttsStatus, value);
    }

    public string LastTranscript
    {
        get => _lastTranscript;
        private set => SetProperty(ref _lastTranscript, value);
    }

    public string EditableTranscript
    {
        get => _editableTranscript;
        set
        {
            if (SetProperty(ref _editableTranscript, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string SelectedVoiceInputDevice
    {
        get => _selectedVoiceInputDevice;
        set
        {
            if (SetProperty(ref _selectedVoiceInputDevice, value))
            {
                ApplyVoiceInputDevice(value);
            }
        }
    }

    public string SelectedVoiceOutputDevice
    {
        get => _selectedVoiceOutputDevice;
        set
        {
            if (SetProperty(ref _selectedVoiceOutputDevice, value))
            {
                ApplyVoiceOutputDevice(value);
            }
        }
    }

    public string SelectedVoiceInputPreset
    {
        get => _selectedVoiceInputPreset;
        set
        {
            var normalized = VoiceInputPreset.Normalize(value);
            if (SetProperty(ref _selectedVoiceInputPreset, normalized))
            {
                ApplyVoiceInputPreset(normalized);
            }
        }
    }

    public string SelectedVoiceInputChannelMode
    {
        get => _selectedVoiceInputChannelMode;
        set
        {
            if (SetProperty(ref _selectedVoiceInputChannelMode, value))
            {
                ApplyVoiceInputChannelMode(value);
            }
        }
    }

    public double VoiceInputLevelPercent
    {
        get => _voiceInputLevelPercent;
        private set => SetProperty(ref _voiceInputLevelPercent, value);
    }

    public string VoiceInputMeterText
    {
        get => _voiceInputMeterText;
        private set => SetProperty(ref _voiceInputMeterText, value);
    }

    public string VoiceDiagnosticsText
    {
        get => _voiceDiagnosticsText;
        private set => SetProperty(ref _voiceDiagnosticsText, value);
    }

    public string LastSttDebugText
    {
        get => _lastSttDebugText;
        private set => SetProperty(ref _lastSttDebugText, value);
    }

    public double ExtraInputGainDb
    {
        get => _extraInputGainDb;
        set
        {
            var clamped = Math.Clamp(value, -12d, 24d);
            if (SetProperty(ref _extraInputGainDb, clamped))
            {
                ApplyVoiceInputPreset(SelectedVoiceInputPreset);
                SaveVoiceSettings(extraInputGainDb: clamped);
            }
        }
    }

    public bool NormalizeBeforeStt
    {
        get => _normalizeBeforeStt;
        set
        {
            if (SetProperty(ref _normalizeBeforeStt, value))
            {
                SaveVoiceSettings(normalizeBeforeStt: value);
            }
        }
    }

    public bool RetainDebugAudio
    {
        get => _retainDebugAudio;
        set
        {
            if (SetProperty(ref _retainDebugAudio, value))
            {
                SaveVoiceSettings(retainDebugAudio: value);
            }
        }
    }

    public bool AutoSendVoiceTranscripts
    {
        get => _autoSendVoiceTranscripts;
        set
        {
            if (SetProperty(ref _autoSendVoiceTranscripts, value))
            {
                OnPropertyChanged(nameof(VoiceModeButtonText));
                OnPropertyChanged(nameof(PushToTalkHintText));
                SaveVoiceSettings(autoSendVoiceTranscripts: value);
                VoiceStatus = value
                    ? $"Push to Talk enabled. Hold {PushToTalkKeyLabel} to speak."
                    : "Push to Talk disabled. Mic button still transcribes into the chat bar.";
                RaiseCommandStates();
            }
        }
    }

    public string PushToTalkKeyText
    {
        get => _pushToTalkKeyText;
        private set
        {
            var normalized = NormalizePushToTalkKey(value);
            if (SetProperty(ref _pushToTalkKeyText, normalized))
            {
                OnPropertyChanged(nameof(PushToTalkKeyLabel));
                OnPropertyChanged(nameof(VoiceModeButtonText));
                OnPropertyChanged(nameof(PushToTalkHintText));
                SaveVoiceSettings(pushToTalkKey: normalized);
            }
        }
    }

    public string PushToTalkKeyLabel => FormatPushToTalkKeyLabel(_pushToTalkKeyText);

    public bool IsAssigningPushToTalkKey
    {
        get => _isAssigningPushToTalkKey;
        private set
        {
            if (SetProperty(ref _isAssigningPushToTalkKey, value))
            {
                OnPropertyChanged(nameof(AssignPushToTalkKeyButtonText));
            }
        }
    }

    public string AssignPushToTalkKeyButtonText => IsAssigningPushToTalkKey ? "Press Key..." : "Set PTT Key";

    public bool ManualTranscriptReviewEnabled => true;

    public double ManualTranscriptReviewOpacity => 1d;

    public PointCollection SpectrumLivePoints
    {
        get => _spectrumLivePoints;
        private set => SetProperty(ref _spectrumLivePoints, value);
    }

    public string SpectrumPeakText
    {
        get => _spectrumPeakText;
        private set => SetProperty(ref _spectrumPeakText, value);
    }

    public double SpectrumDisplayGain
    {
        get => _spectrumDisplayGain;
        set
        {
            if (SetProperty(ref _spectrumDisplayGain, Math.Clamp(value, 1d, 24d)))
            {
                RefreshSpectrumPoints();
            }
        }
    }

    public string WhisperExecutableText
    {
        get => _whisperExecutableText;
        set => SetProperty(ref _whisperExecutableText, value);
    }

    public string WhisperModelText
    {
        get => _whisperModelText;
        set => SetProperty(ref _whisperModelText, value);
    }

    public string WhisperArgumentsText
    {
        get => _whisperArgumentsText;
        set => SetProperty(ref _whisperArgumentsText, value);
    }

    public string PiperExecutableText
    {
        get => _piperExecutableText;
        set => SetProperty(ref _piperExecutableText, value);
    }

    public string PiperModelText
    {
        get => _piperModelText;
        set => SetProperty(ref _piperModelText, value);
    }

    public string PiperVoiceText
    {
        get => _piperVoiceText;
        set => SetProperty(ref _piperVoiceText, value);
    }

    public string SelectedPiperVoiceChoice
    {
        get => _selectedPiperVoiceChoice;
        set
        {
            if (SetProperty(ref _selectedPiperVoiceChoice, value))
            {
                ApplySelectedPiperVoiceChoice(value, applySettings: !_loadingSpeechToolSettings);
            }
        }
    }

    public string PiperArgumentsText
    {
        get => _piperArgumentsText;
        set => SetProperty(ref _piperArgumentsText, value);
    }

    public string VoiceSettingsStatusText
    {
        get => _voiceSettingsStatusText;
        private set => SetProperty(ref _voiceSettingsStatusText, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(MicButtonText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsTranscribing
    {
        get => _isTranscribing;
        private set
        {
            if (SetProperty(ref _isTranscribing, value))
            {
                OnPropertyChanged(nameof(MicButtonText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        private set
        {
            if (SetProperty(ref _isSpeaking, value))
            {
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(SendButtonToolTip));
                OnPropertyChanged(nameof(SendButtonBackground));
                OnPropertyChanged(nameof(SendButtonBorderBrush));
                RaiseCommandStates();
            }
        }
    }

    public string ComposerText
    {
        get => _composerText;
        set
        {
            if (SetProperty(ref _composerText, value))
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
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(SendButtonToolTip));
                OnPropertyChanged(nameof(SendButtonBackground));
                OnPropertyChanged(nameof(SendButtonBorderBrush));
                RaiseCommandStates();
            }
        }
    }

    public string SendButtonText => IsBusy || IsSpeaking ? "Stop" : "Send";

    public string SendButtonToolTip => IsBusy
        ? "Stop the current response"
        : IsSpeaking
            ? "Stop speaking"
            : "Send chat";

    public System.Windows.Media.Brush SendButtonBackground => IsBusy || IsSpeaking ? MediaBrushes.DarkRed : MediaBrushes.DarkGreen;

    public System.Windows.Media.Brush SendButtonBorderBrush => IsBusy || IsSpeaking ? MediaBrushes.IndianRed : MediaBrushes.LimeGreen;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task StartLocalRuntimeAsync()
    {
        var options = _services.LoadRuntimeSettings();
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Model))
        {
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = "Local model is not configured yet.";
            return;
        }

        SetModelConnectionStatus("command sent, waiting on model to load", MediaBrushes.Gold);
        StatusText = "Loading local model...";
        await Task.Yield();

        try
        {
            await EnsureLocalOllamaStartedAsync(options).ConfigureAwait(true);
            _services.ConfigureRuntimeCandidate(options);
            var health = await _services.RuntimeController.CheckCandidateAsync(CancellationToken.None).ConfigureAwait(true);
            RuntimeHealthResult = FormatHealthResult(health);
            CanActivateRuntime = _services.RuntimeController.CanActivateCandidate;
            if (health.Succeeded && _services.RuntimeController.ActivateLastHealthChecked())
            {
                CanActivateRuntime = false;
                UpdateRuntimeStatus();
                SetModelConnectionStatus("connected to model", MediaBrushes.LimeGreen);
                StatusText = $"Connected to local model: {options.Model}";
                return;
            }

            UpdateRuntimeStatus();
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = $"Local model failed to load: {health.Summary}";
        }
        catch (Exception ex)
        {
            RuntimeHealthResult = ex.Message;
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = $"Local model failed to load: {ex.Message}";
        }
    }

    public async Task ShutdownLocalRuntimeAsync()
    {
        _modelStatusTimer.Stop();
        SetModelConnectionStatus("command sent, waiting on model to shut down", MediaBrushes.Gold);
        StatusText = "Shutting down local model...";
        await Task.Yield();

        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _services.RuntimeController.ShutdownAsync(shutdown.Token).ConfigureAwait(true);
            StopOllamaProcessesStartedByAli();
            _services.RuntimeController.RevertToFallback();
            UpdateRuntimeStatus();
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = "Local model shut down.";
        }
        catch (Exception ex)
        {
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = $"Local model shutdown did not complete cleanly: {ex.Message}";
        }
    }

    private async Task EnsureLocalOllamaStartedAsync(OpenAiCompatibleRuntimeOptions options)
    {
        if (!IsLocalOllamaEndpoint(options.Endpoint))
        {
            return;
        }

        if (_ollamaStartInProgress || _ollamaProcessIdsStartedByAli.Count > 0)
        {
            return;
        }

        var before = GetOllamaProcesses();
        if (before.Count > 0)
        {
            _ollamaWasRunningAtStartup = true;
            _nextOllamaStartAttemptAt = DateTimeOffset.MaxValue;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextOllamaStartAttemptAt)
        {
            return;
        }

        _ollamaStartInProgress = true;
        _nextOllamaStartAttemptAt = now + OllamaStartRetryInterval;

        var appPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama app.exe");
        var serverPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama.exe");

        try
        {
            var launchedProcess = StartOwnedOllamaProcess(serverPath, appPath);
            if (launchedProcess is null)
            {
                return;
            }

            _ollamaProcessIdsStartedByAli.Add(launchedProcess.Id);
            await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(true);
            var beforeIds = before.Select(process => process.Id).ToHashSet();
            foreach (var process in GetOllamaProcesses())
            {
                if (!beforeIds.Contains(process.Id))
                {
                    _ollamaProcessIdsStartedByAli.Add(process.Id);
                }
            }
        }
        finally
        {
            _ollamaStartInProgress = false;
        }
    }

    private static Process? StartOwnedOllamaProcess(string serverPath, string appPath)
    {
        if (File.Exists(serverPath))
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        if (File.Exists(appPath))
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        return null;
    }

    private void StopOllamaProcessesStartedByAli()
    {
        if (_ollamaWasRunningAtStartup || _ollamaProcessIdsStartedByAli.Count == 0)
        {
            return;
        }

        foreach (var process in GetOllamaProcesses())
        {
            if (!_ollamaProcessIdsStartedByAli.Contains(process.Id))
            {
                continue;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup of the Ollama instance Ali launched.
            }
        }

        _ollamaProcessIdsStartedByAli.Clear();
    }

    private static IReadOnlyList<Process> GetOllamaProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Where(process => IsOllamaProcess(process.ProcessName))
                .ToList();
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private static bool IsOllamaProcess(string processName) =>
        processName.Equals("ollama", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("ollama app", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalOllamaEndpoint(Uri endpoint) =>
        endpoint.Port == 11434
        && (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));

    public async Task RefreshModelConnectionStatusAsync(bool showWaiting)
    {
        if (_checkingModelConnectionStatus)
        {
            return;
        }

        if (IsBusy || IsRecording || IsTranscribing || IsSpeaking)
        {
            return;
        }

        if (_services.RuntimeController.IsUsingFallback)
        {
            if (!ModelConnectionStatusText.Contains("waiting", StringComparison.OrdinalIgnoreCase))
            {
                SetModelConnectionStatus("model offline", MediaBrushes.Red);
            }

            return;
        }

        _checkingModelConnectionStatus = true;
        if (showWaiting)
        {
            SetModelConnectionStatus("command sent, waiting on model to load", MediaBrushes.Gold);
            await Task.Yield();
        }

        try
        {
            using var statusCheck = new CancellationTokenSource(ModelStatusPingTimeout);
            var health = await CheckRuntimeEndpointStatusAsync(
                _services.LoadRuntimeSettings(),
                statusCheck.Token).ConfigureAwait(true);
            RuntimeHealthResult = FormatHealthResult(health);
            if (health.Succeeded)
            {
                SetModelConnectionStatus("connected to model", MediaBrushes.LimeGreen);
            }
            else
            {
                SetModelConnectionStatus("model offline", MediaBrushes.Red);
                StatusText = $"Local model communication failed: {health.Summary}";
            }
        }
        catch (Exception ex)
        {
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = $"Local model communication failed: {ex.Message}";
        }
        finally
        {
            _checkingModelConnectionStatus = false;
        }
    }

    public async Task SendAsync()
    {
        if (IsBusy)
        {
            Stop();
            return;
        }

        if (IsSpeaking)
        {
            StopSpeaking();
            return;
        }

        var text = ComposerText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ComposerText = string.Empty;
        await SendTextAsync(text, VoiceInputOrigin.Typed, voiceMetadata: null).ConfigureAwait(true);
    }

    private async Task SendTextAsync(
        string text,
        VoiceInputOrigin inputOrigin,
        VoiceTurnMetadata? voiceMetadata)
    {
        if (string.IsNullOrWhiteSpace(text) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Streaming local response...";
        EnsureActiveConversationHistoryItem();
        ApplyFirstMessageTitleIfNeeded(text);

        var userMessageId = $"msg_user_{Guid.NewGuid():N}";
        var assistantMessageId = $"msg_asst_{Guid.NewGuid():N}";
        var attachments = Attachments.Select(attachment => attachment.ToCoreAttachment()).ToList();
        var attachmentMetadata = Attachments.Select(ToStoredAttachmentMetadata).ToList();
        var localFoundationStatus = ApplyLocalMemoryAndReminderRequests(text, userMessageId);
        var userMessage = new ChatMessageViewModel(
            userMessageId,
            ChatRole.User,
            text,
            DateTimeOffset.UtcNow,
            EvidenceStatus.Verified,
            attachmentMetadata: attachmentMetadata.Count == 0 ? null : attachmentMetadata);

        var assistantMessage = new ChatMessageViewModel(
            assistantMessageId,
            ChatRole.Assistant,
            string.Empty,
            DateTimeOffset.UtcNow,
            EvidenceStatus.Unknown,
            sourceAttachmentCount: attachments.Count,
            sourceInputOrigin: inputOrigin,
            sourceVoiceMetadata: voiceMetadata,
            sourceUserMessageId: userMessageId,
            sourceQuestion: text);

        var history = Messages.Select(message => message.ToCoreMessage()).ToList();
        Messages.Add(userMessage);
        Messages.Add(assistantMessage);

        _activeResponse = new CancellationTokenSource();
        var streamingSpeech = StartStreamingSpeechIfNeeded(inputOrigin);
        var completed = false;

        try
        {
            await foreach (var chunk in _services.Orchestrator.StreamAnswerAsync(
                               _conversationId,
                               userMessageId,
                               assistantMessageId,
                               text,
                               history,
                               attachments,
                               _activeResponse.Token))
            {
                assistantMessage.Text += chunk.Text;
                assistantMessage.EvidenceStatus = chunk.EvidenceStatus;
                QueueStreamingSpeech(streamingSpeech, chunk.Text);
            }

            CompleteStreamingSpeechInput(streamingSpeech);

            if (LooksLikeRuntimeCommunicationFailure(assistantMessage.Text))
            {
                SetModelConnectionStatus("model offline", MediaBrushes.Red);
                StatusText = "Local model communication failed.";
            }
            else if (!_services.RuntimeController.IsUsingFallback)
            {
                SetModelConnectionStatus("connected to model", MediaBrushes.LimeGreen);
                StatusText = "Response complete.";
            }
            else
            {
                SetModelConnectionStatus("model offline", MediaBrushes.Red);
                StatusText = "Response complete on deterministic stub.";
            }

            completed = true;
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Text += "\n\nStopped by user.";
            CancelStreamingSpeech(streamingSpeech);
            StatusText = "Response stopped.";
        }
        catch (HttpRequestException ex)
        {
            assistantMessage.Text += $"\n\nUnknown: local model communication failed. {ex.Message}";
            CancelStreamingSpeech(streamingSpeech);
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = $"Local model communication failed: {ex.Message}";
        }
        finally
        {
            if (!completed)
            {
                CancelStreamingSpeech(streamingSpeech);
            }

            _activeResponse.Dispose();
            _activeResponse = null;
            IsBusy = false;
            ClearTemporaryAttachments();
            SaveActiveConversation();
            UpdateRuntimeStatus();
            if (!string.IsNullOrWhiteSpace(localFoundationStatus))
            {
                StatusText = $"{StatusText} {localFoundationStatus}";
            }
        }
    }

    private void Stop()
    {
        _activeResponse?.Cancel();
        StopSpeaking();
    }

    private void StartNewChat()
    {
        ResetToFreshConversation("New chat ready.");
    }

    private void EnsureActiveConversationHistoryItem()
    {
        if (_activeConversationHistoryItem is not null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ConversationSearchText))
        {
            ConversationSearchText = string.Empty;
        }

        var firstUserText = Messages
            .Where(message => message.Role == ChatRole.User)
            .Select(message => message.Text)
            .FirstOrDefault();
        var title = ConversationTitleFactory.CreateFromFirstMessage(firstUserText ?? ComposerText);
        _activeConversationHistoryItem = new ConversationHistoryItemViewModel(_conversationId, title);
        ConversationHistory.Insert(0, _activeConversationHistoryItem);
        SelectHistoryItemWithoutLoading(_activeConversationHistoryItem);
    }

    private void ResetToFreshConversation(string statusText)
    {
        Stop();
        StopSpeaking();
        _activeVoiceInput?.Cancel();
        ClearTemporaryAttachments();
        Attachments.Clear();
        Messages.Clear();
        _conversationId = ConversationSessionFactory.StartFresh().ConversationId;
        _activeConversationHistoryItem = null;
        SelectHistoryItemWithoutLoading(null);
        ComposerText = string.Empty;
        EditableTranscript = string.Empty;
        LastTranscript = string.Empty;
        StatusText = statusText;
        VoiceStatus = "Voice idle.";
        AttachmentStatus = "Screenshots are temporary by default.";
    }

    private void RefreshConversationHistory()
    {
        var activeId = _activeConversationHistoryItem?.Id;
        var result = string.IsNullOrWhiteSpace(ConversationSearchText)
            ? _services.Conversations.ListSummaries()
            : _services.Conversations.Search(ConversationSearchText);

        _loadingConversationHistorySelection = true;
        try
        {
            ConversationHistory.Clear();
            ConversationHistoryItemViewModel? activeItem = null;
            foreach (var summary in result.Conversations)
            {
                var item = new ConversationHistoryItemViewModel(
                    summary.ConversationId,
                    summary.Title,
                    summary.UpdatedAt,
                    summary.Preview,
                    summary.MessageCount);
                ConversationHistory.Add(item);
                if (activeId is not null && item.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase))
                {
                    activeItem = item;
                }
            }

            if (activeItem is not null || string.IsNullOrWhiteSpace(ConversationSearchText))
            {
                _activeConversationHistoryItem = activeItem;
            }

            _selectedConversationHistoryItem = activeItem;
            UpdateActiveHistoryVisuals();
            OnPropertyChanged(nameof(SelectedConversationHistoryItem));
        }
        finally
        {
            _loadingConversationHistorySelection = false;
        }

        if (result.Warnings.Count > 0)
        {
            StatusText = $"History loaded with {result.Warnings.Count} warning(s). Corrupt history files were skipped.";
        }
    }

    private void LoadConversation(ConversationHistoryItemViewModel item)
    {
        if (IsBusy)
        {
            StatusText = "Stop the current response before switching chats.";
            SelectHistoryItemWithoutLoading(_activeConversationHistoryItem);
            return;
        }

        var conversation = _services.Conversations.Load(item.Id);
        if (conversation is null)
        {
            StatusText = $"Could not load saved chat: {item.Title}";
            RefreshConversationHistory();
            return;
        }

        StopSpeaking();
        ClearTemporaryAttachments();
        Attachments.Clear();
        Messages.Clear();
        var session = ConversationSessionFactory.Reopen(conversation);
        foreach (var message in session.Messages)
        {
            Messages.Add(ChatMessageViewModel.FromStoredMessage(message));
        }

        _conversationId = session.ConversationId;
        _activeConversationHistoryItem = item;
        UpdateActiveHistoryVisuals();
        ComposerText = string.Empty;
        EditableTranscript = string.Empty;
        LastTranscript = string.Empty;
        VoiceStatus = "Voice idle.";
        AttachmentStatus = "Screenshots are temporary by default.";
        StatusText = $"Loaded saved chat: {conversation.Title}";
    }

    private void SaveActiveConversation()
    {
        if (_activeConversationHistoryItem is null || !Messages.Any(message => message.Role == ChatRole.User))
        {
            return;
        }

        var conversation = BuildStoredConversation(_activeConversationHistoryItem);
        _services.Conversations.Save(conversation);
        RefreshConversationHistory();
    }

    private string ApplyLocalMemoryAndReminderRequests(string text, string userMessageId)
    {
        var statuses = new List<string>();
        var memoryDecision = MemoryRequestParser.Evaluate(text);
        if (memoryDecision.Kind == MemoryRequestKind.Save)
        {
            if (memoryDecision.Sensitivity == MemorySensitivity.PotentiallySensitive)
            {
                statuses.Add(memoryDecision.Message);
            }
            else if (!string.IsNullOrWhiteSpace(memoryDecision.Text))
            {
                var now = DateTimeOffset.UtcNow;
                _services.Memories.Save(new MemoryEntry(
                    $"mem_{Guid.NewGuid():N}",
                    memoryDecision.Text,
                    "general",
                    now,
                    now,
                    MemorySource.ExplicitUserRequest,
                    memoryDecision.Sensitivity,
                    Active: true,
                    _conversationId,
                    userMessageId,
                    "Saved from explicit chat request."));
                statuses.Add(memoryDecision.Message);
            }
        }
        else if (memoryDecision.Kind == MemoryRequestKind.Forget && !string.IsNullOrWhiteSpace(memoryDecision.Text))
        {
            var removed = _services.Memories.DeleteMatching(memoryDecision.Text);
            statuses.Add($"Removed {removed} matching local memory item(s).");
        }
        else if (memoryDecision.Kind == MemoryRequestKind.Ambiguous)
        {
            statuses.Add(memoryDecision.Message);
        }

        var reminderDecision = ReminderRequestParser.Evaluate(text, DateTimeOffset.Now);
        if (reminderDecision.Accepted && reminderDecision.Title is not null && reminderDecision.DueAt is not null)
        {
            var now = DateTimeOffset.UtcNow;
            _services.Reminders.Save(new ReminderEntry(
                $"rem_{Guid.NewGuid():N}",
                reminderDecision.Title,
                reminderDecision.Title,
                reminderDecision.DueAt.Value,
                now,
                ReminderStatus.Scheduled,
                ConversationId: _conversationId,
                MessageId: userMessageId));
            statuses.Add(reminderDecision.Message);
        }
        else if (!string.IsNullOrWhiteSpace(reminderDecision.Message))
        {
            statuses.Add(reminderDecision.Message);
        }

        if (statuses.Count > 0)
        {
            RefreshMemoryReminders();
        }

        return string.Join(" ", statuses);
    }

    private void RefreshMemoryReminders()
    {
        var selectedMemoryId = SelectedMemoryEntry?.Id;
        var selectedReminderId = SelectedReminderEntry?.Id;
        var memoryResult = _services.Memories.List();
        var reminderResult = _services.Reminders.List();

        MemoryEntries.Clear();
        foreach (var memory in memoryResult.Memories)
        {
            MemoryEntries.Add(new MemoryEntryViewModel(memory));
        }

        ReminderEntries.Clear();
        foreach (var reminder in reminderResult.Reminders)
        {
            ReminderEntries.Add(new ReminderEntryViewModel(reminder));
        }

        SelectedMemoryEntry = MemoryEntries.FirstOrDefault(memory => memory.Id == selectedMemoryId)
            ?? MemoryEntries.FirstOrDefault();
        SelectedReminderEntry = ReminderEntries.FirstOrDefault(reminder => reminder.Id == selectedReminderId)
            ?? ReminderEntries.FirstOrDefault();

        var warningCount = memoryResult.Warnings.Count + reminderResult.Warnings.Count;
        MemoryReminderStatusText = warningCount == 0
            ? $"Loaded {MemoryEntries.Count} memory item(s) and {ReminderEntries.Count} reminder(s)."
            : $"Loaded memory/reminders with {warningCount} warning(s).";
    }

    private void DeleteSelectedMemory()
    {
        if (SelectedMemoryEntry is null)
        {
            return;
        }

        _services.Memories.Delete(SelectedMemoryEntry.Id);
        RefreshMemoryReminders();
        MemoryReminderStatusText = "Deleted selected memory item only. Conversations and reminders were not erased.";
    }

    private void ClearMemories()
    {
        var result = System.Windows.MessageBox.Show(
            "Clear local memories on this computer? This removes saved memory items only. It does not remove conversations, reminders, settings, local models, voice resources, correction reports, or the app itself.",
            "Clear local memories",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _services.Memories.Clear();
        RefreshMemoryReminders();
        MemoryReminderStatusText = $"Cleared {removed} memory item(s). Conversations and reminders were not erased.";
    }

    private void SetSelectedReminderStatus(ReminderStatus status)
    {
        if (SelectedReminderEntry is null)
        {
            return;
        }

        _services.Reminders.SetStatus(SelectedReminderEntry.Id, status);
        RefreshMemoryReminders();
        MemoryReminderStatusText = $"Marked selected reminder {status}.";
    }

    private void ClearReminders()
    {
        var result = System.Windows.MessageBox.Show(
            "Clear local reminders on this computer? This removes saved reminders only. It does not remove conversations, memories, settings, local models, voice resources, correction reports, or the app itself.",
            "Clear local reminders",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _services.Reminders.Clear();
        _shownDueReminderIds.Clear();
        RefreshMemoryReminders();
        MemoryReminderStatusText = $"Cleared {removed} reminder(s). Conversations and memories were not erased.";
    }

    private void LoadCodingPermissions()
    {
        var settings = _services.LoadCodingToolSettings();
        ApplyCodingToolSettings(settings);
        CodingPermissionsStatusText = $"Coding permissions loaded from {CodingToolSettingsPath}.";
    }

    private void SaveCodingPermissions()
    {
        if (!CodingWorkspacePolicy.TryNormalizePath(CodingWorkspaceRootText, out var workspaceRoot))
        {
            CodingPermissionsStatusText = "Coding workspace must be a fully-qualified local path.";
            return;
        }

        if (!CodingWorkspacePolicy.TryNormalizePath(CodingPdfWorkspaceRootText, out var pdfWorkspaceRoot))
        {
            CodingPermissionsStatusText = "PDF workspace must be a fully-qualified local folder path.";
            return;
        }

        var explicitOutsideFileOpenMode = PickChoice(
            CodingExplicitOutsideFileOpenModeChoices,
            CodingExplicitOutsideFileOpenMode,
            CodingPermissionModes.Allowed,
            resetToSmallest: false);
        var settings = new CodingToolSettings
        {
            WorkspaceRoot = workspaceRoot,
            AllowExplicitOutsideFileOpen = !CodingPermissionModes.IsDisabled(explicitOutsideFileOpenMode),
            WorkspaceAccessMode = PickChoice(CodingWorkspaceAccessModeChoices, CodingWorkspaceAccessMode, CodingPermissionModes.Allowed, resetToSmallest: false),
            ExplicitOutsideFileOpenMode = explicitOutsideFileOpenMode,
            SearchOutsideWorkspaceMode = PickChoice(CodingSearchOutsideWorkspaceModeChoices, CodingSearchOutsideWorkspaceMode, CodingPermissionModes.AskFirst, resetToSmallest: false),
            EditInsideWorkspaceMode = PickChoice(CodingEditInsideWorkspaceModeChoices, CodingEditInsideWorkspaceMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false),
            BuildTestRunInsideWorkspaceMode = PickChoice(CodingBuildTestRunInsideWorkspaceModeChoices, CodingBuildTestRunInsideWorkspaceMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false),
            DestructiveActionMode = PickChoice(CodingDestructiveActionModeChoices, CodingDestructiveActionMode, CodingPermissionModes.ExtraConfirmation, resetToSmallest: false),
            OutsideEditRunMode = PickChoice(CodingOutsideEditRunModeChoices, CodingOutsideEditRunMode, CodingPermissionModes.Blocked, resetToSmallest: false),
            SystemAdminActionMode = PickChoice(CodingSystemAdminActionModeChoices, CodingSystemAdminActionMode, CodingPermissionModes.Blocked, resetToSmallest: false),
            GitReadMode = PickChoice(CodingGitReadModeChoices, CodingGitReadMode, CodingPermissionModes.Allowed, resetToSmallest: false),
            GitWriteMode = PickChoice(CodingGitWriteModeChoices, CodingGitWriteMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false),
            GitMergeMode = PickChoice(CodingGitMergeModeChoices, CodingGitMergeMode, CodingPermissionModes.ExtraConfirmation, resetToSmallest: false),
            GitNetworkMode = PickChoice(CodingGitNetworkModeChoices, CodingGitNetworkMode, CodingPermissionModes.Blocked, resetToSmallest: false),
            PdfWorkspaceRoot = pdfWorkspaceRoot,
            PdfReadMode = PickChoice(CodingPdfReadModeChoices, CodingPdfReadMode, CodingPermissionModes.Allowed, resetToSmallest: false),
            PdfCreateMode = PickChoice(CodingPdfCreateModeChoices, CodingPdfCreateMode, CodingPermissionModes.Allowed, resetToSmallest: false),
            PdfModifyMode = PickChoice(CodingPdfModifyModeChoices, CodingPdfModifyMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false),
            NotepadPlusPlusPath = NormalizeOptionalCodingToolPath(CodingNotepadPlusPlusPathText),
            VisualStudioPath = NormalizeOptionalCodingToolPath(CodingVisualStudioPathText)
        };
        _services.SaveCodingToolSettings(settings);
        ApplyCodingToolSettings(settings);
        CodingPermissionsStatusText = $"Saved coding permissions. Workspace: {workspaceRoot}. PDF workspace: {pdfWorkspaceRoot}";
    }

    private void ResetCodingPermissionsToDefault()
    {
        var settings = new CodingToolSettings();
        _services.SaveCodingToolSettings(settings);
        ApplyCodingToolSettings(settings);
        CodingPermissionsStatusText = "Coding permissions reset to default.";
    }

    private void ApplyCodingToolSettings(CodingToolSettings settings)
    {
        CodingWorkspaceRootText = settings.WorkspaceRoot;
        CodingWorkspaceAccessMode = PickChoice(CodingWorkspaceAccessModeChoices, settings.WorkspaceAccessMode, CodingPermissionModes.Allowed, resetToSmallest: false);
        CodingExplicitOutsideFileOpenMode = settings.AllowExplicitOutsideFileOpen
            ? PickChoice(CodingExplicitOutsideFileOpenModeChoices, settings.ExplicitOutsideFileOpenMode, CodingPermissionModes.Allowed, resetToSmallest: false)
            : CodingPermissionModes.Disabled;
        CodingSearchOutsideWorkspaceMode = PickChoice(CodingSearchOutsideWorkspaceModeChoices, settings.SearchOutsideWorkspaceMode, CodingPermissionModes.AskFirst, resetToSmallest: false);
        CodingEditInsideWorkspaceMode = PickChoice(CodingEditInsideWorkspaceModeChoices, settings.EditInsideWorkspaceMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false);
        CodingBuildTestRunInsideWorkspaceMode = PickChoice(CodingBuildTestRunInsideWorkspaceModeChoices, settings.BuildTestRunInsideWorkspaceMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false);
        CodingDestructiveActionMode = PickChoice(CodingDestructiveActionModeChoices, settings.DestructiveActionMode, CodingPermissionModes.ExtraConfirmation, resetToSmallest: false);
        CodingOutsideEditRunMode = PickChoice(CodingOutsideEditRunModeChoices, settings.OutsideEditRunMode, CodingPermissionModes.Blocked, resetToSmallest: false);
        CodingSystemAdminActionMode = PickChoice(CodingSystemAdminActionModeChoices, settings.SystemAdminActionMode, CodingPermissionModes.Blocked, resetToSmallest: false);
        CodingGitReadMode = PickChoice(CodingGitReadModeChoices, settings.GitReadMode, CodingPermissionModes.Allowed, resetToSmallest: false);
        CodingGitWriteMode = PickChoice(CodingGitWriteModeChoices, settings.GitWriteMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false);
        CodingGitMergeMode = PickChoice(CodingGitMergeModeChoices, settings.GitMergeMode, CodingPermissionModes.ExtraConfirmation, resetToSmallest: false);
        CodingGitNetworkMode = PickChoice(CodingGitNetworkModeChoices, settings.GitNetworkMode, CodingPermissionModes.Blocked, resetToSmallest: false);
        CodingPdfWorkspaceRootText = settings.ResolvePdfWorkspaceRoot(_services.DataRoot);
        CodingPdfReadMode = PickChoice(CodingPdfReadModeChoices, settings.PdfReadMode, CodingPermissionModes.Allowed, resetToSmallest: false);
        CodingPdfCreateMode = PickChoice(CodingPdfCreateModeChoices, settings.PdfCreateMode, CodingPermissionModes.Allowed, resetToSmallest: false);
        CodingPdfModifyMode = PickChoice(CodingPdfModifyModeChoices, settings.PdfModifyMode, CodingPermissionModes.ConfirmEachTime, resetToSmallest: false);
        CodingAllowExplicitOutsideFileOpen = !CodingPermissionModes.IsDisabled(CodingExplicitOutsideFileOpenMode);
        CodingNotepadPlusPlusPathText = settings.NotepadPlusPlusPath;
        CodingVisualStudioPathText = settings.VisualStudioPath;
        OnPropertyChanged(nameof(CodingPermissionSummaryText));
    }

    private bool SetCodingPermissionMode(ref string field, string value)
    {
        if (SetProperty(ref field, value?.Trim() ?? string.Empty))
        {
            OnPropertyChanged(nameof(CodingPermissionSummaryText));
            return true;
        }

        return false;
    }

    private void BrowseCodingWorkspaceRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var selectedPath = Directory.Exists(CodingWorkspaceRootText)
            ? CodingWorkspaceRootText
            : Path.Combine(documents, "Programming Projects");

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose Ali's coding workspace folder",
            SelectedPath = selectedPath,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            CodingWorkspaceRootText = dialog.SelectedPath;
        }
    }

    private void BrowseCodingPdfWorkspaceRoot()
    {
        var selectedPath = Directory.Exists(CodingPdfWorkspaceRootText)
            ? CodingPdfWorkspaceRootText
            : Path.Combine(_services.DataRoot, "GeneratedDocuments");

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose Ali's PDF workspace folder",
            SelectedPath = selectedPath,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            CodingPdfWorkspaceRootText = dialog.SelectedPath;
        }
    }

    private void BrowseCodingToolPath(string title, string filter, Action<string> applyPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        var owner = System.Windows.Application.Current?.Windows
            .OfType<SettingsWindow>()
            .FirstOrDefault(window => window.DataContext == this)
            ?? System.Windows.Application.Current?.MainWindow;
        if (dialog.ShowDialog(owner) == true)
        {
            applyPath(dialog.FileName);
        }
    }

    private static string NormalizeOptionalCodingToolPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }

    private static string DescribeConfiguredToolPath(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? "auto-detect"
            : path;

    private void CheckDueReminders()
    {
        var due = _services.Reminders
            .ListDue(DateTimeOffset.Now)
            .Where(reminder => _shownDueReminderIds.Add(reminder.ReminderId))
            .ToList();
        if (due.Count == 0)
        {
            return;
        }

        var first = due[0];
        StatusText = due.Count == 1
            ? $"Reminder due: {first.Title}"
            : $"{due.Count} reminders are due. First: {first.Title}";
        RefreshMemoryReminders();
    }

    private void ApplyFirstMessageTitleIfNeeded(string text)
    {
        if (_activeConversationHistoryItem is null || Messages.Any(message => message.Role == ChatRole.User))
        {
            return;
        }

        _activeConversationHistoryItem.SetTitle(ConversationTitleFactory.CreateFromFirstMessage(text));
    }

    private StoredConversation BuildStoredConversation(ConversationHistoryItemViewModel historyItem)
    {
        var messages = Messages
            .Where(ShouldPersistMessage)
            .Select(message => message.ToStoredMessage(_conversationId))
            .ToList();
        var now = DateTimeOffset.UtcNow;
        var firstUserText = messages
            .Where(message => message.Role == ChatRole.User)
            .Select(message => message.Text)
            .FirstOrDefault();
        var title = historyItem.Title.Equals("Current chat", StringComparison.OrdinalIgnoreCase)
            ? ConversationTitleFactory.CreateFromFirstMessage(firstUserText ?? historyItem.Title)
            : historyItem.Title;
        historyItem.SetTitle(title);

        return new StoredConversation(
            _conversationId,
            title,
            messages.FirstOrDefault()?.CreatedAt ?? now,
            now,
            messages);
    }

    private static bool ShouldPersistMessage(ChatMessageViewModel message) =>
        !string.IsNullOrWhiteSpace(message.Text)
        && !message.Text.StartsWith("Ali bootstrap ready.", StringComparison.Ordinal);

    private static StoredAttachmentMetadata ToStoredAttachmentMetadata(ImageAttachmentViewModel attachment) =>
        new(
            attachment.Id,
            AttachmentKind.Image,
            attachment.FileName,
            attachment.ContentType,
            attachment.RetainAfterSession,
            attachment.CreatedAt);

    private void SelectHistoryItemWithoutLoading(ConversationHistoryItemViewModel? item)
    {
        _loadingConversationHistorySelection = true;
        try
        {
            _selectedConversationHistoryItem = item;
            UpdateActiveHistoryVisuals();
            OnPropertyChanged(nameof(SelectedConversationHistoryItem));
        }
        finally
        {
            _loadingConversationHistorySelection = false;
        }
    }

    private void UpdateActiveHistoryVisuals()
    {
        var activeId = _activeConversationHistoryItem?.Id;
        foreach (var item in ConversationHistory)
        {
            item.SetActive(activeId is not null && item.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshResourceMeters()
    {
        var snapshot = _resourceMonitor.Sample();
        CpuMeter.Update(snapshot.CpuPercent, "CPU counter unavailable");
        RamMeter.Update(snapshot.RamPercent, "RAM counter unavailable");
        GpuMeter.Update(snapshot.GpuPercent, "GPU counter unavailable");
        VramMeter.Update(snapshot.VramPercent, "VRAM counter unavailable");
    }

    private void EraseHistory()
    {
        var result = System.Windows.MessageBox.Show(
            "Erase saved chat history on this computer? This removes saved conversations and recent chat entries. It does not remove local models, settings, voice resources, correction reports, memories, reminders, or the app itself.",
            "Erase saved chat history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Stop();
        StopSpeaking();
        _activeVoiceInput?.Cancel();
        ClearTemporaryAttachments();
        var erase = _services.Conversations.EraseAll();
        ConversationHistory.Clear();
        ResetToFreshConversation(
            erase.Warnings.Count == 0
                ? $"Erased {erase.DeletedConversationCount} saved chat(s). Corrections, settings, local models, and voice resources were not erased."
                : $"Erased {erase.DeletedConversationCount} saved chat(s) with warnings. Corrections, settings, local models, and voice resources were not erased.");
    }

    private void EraseConversation(object? parameter)
    {
        if (parameter is not ConversationHistoryItemViewModel item)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Erase saved chat \"{item.Title}\" from this computer? This does not remove settings, local models, voice resources, correction reports, memories, reminders, or the app itself.",
            "Erase saved chat",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _services.Conversations.Delete(item.Id);
        if (_activeConversationHistoryItem?.Id == item.Id)
        {
            ResetToFreshConversation($"Erased current chat: {item.Title}");
        }

        RefreshConversationHistory();
        StatusText = $"Erased chat: {item.Title}";
    }

    private static void RenameConversation(object? parameter)
    {
        if (parameter is ConversationHistoryItemViewModel item)
        {
            item.BeginRename();
        }
    }

    private void CommitConversationRename(object? parameter)
    {
        if (parameter is not ConversationHistoryItemViewModel item)
        {
            return;
        }

        item.CommitRename();
        _services.Conversations.Rename(item.Id, item.Title);
        if (_activeConversationHistoryItem?.Id == item.Id)
        {
            _activeConversationHistoryItem.SetTitle(item.Title);
        }

        RefreshConversationHistory();
        StatusText = $"Renamed chat: {item.Title}";
    }

    private async Task ToggleVoiceRecordingAsync()
    {
        if (IsRecording || IsTranscribing)
        {
            await StopVoiceRecordingOrTranscriptionAsync().ConfigureAwait(true);
            return;
        }

        _currentVoiceInputShouldAutoSend = false;
        await StartVoiceRecordingAsync().ConfigureAwait(true);
    }

    public bool IsPushToTalkKey(Key key) =>
        AutoSendVoiceTranscripts
        && TryParsePushToTalkKey(_pushToTalkKeyText, out var configuredKey)
        && key == configuredKey;

    public void BeginAssignPushToTalkKey()
    {
        IsAssigningPushToTalkKey = true;
        VoiceSettingsStatusText = "Press the key to use for Push to Talk. Ali will save the next keypress.";
    }

    public void AssignPushToTalkKey(Key key)
    {
        if (key is Key.None or Key.System)
        {
            VoiceSettingsStatusText = "That key cannot be used for Push to Talk.";
            return;
        }

        PushToTalkKeyText = NormalizePushToTalkKey(key.ToString());
        IsAssigningPushToTalkKey = false;
        VoiceSettingsStatusText = $"Push to Talk key set to {PushToTalkKeyLabel}.";
    }

    public async Task StartPushToTalkAsync()
    {
        if (!AutoSendVoiceTranscripts || _pushToTalkKeyDown || IsRecording || IsTranscribing || IsBusy)
        {
            return;
        }

        _pushToTalkKeyDown = true;
        _currentVoiceInputShouldAutoSend = true;
        try
        {
            await StartVoiceRecordingAsync().ConfigureAwait(true);
        }
        catch
        {
            _pushToTalkKeyDown = false;
            _currentVoiceInputShouldAutoSend = false;
            throw;
        }
    }

    public async Task StopPushToTalkAsync()
    {
        if (!_pushToTalkKeyDown)
        {
            return;
        }

        _pushToTalkKeyDown = false;
        if (IsRecording || IsTranscribing)
        {
            await StopVoiceRecordingOrTranscriptionAsync().ConfigureAwait(true);
        }
    }

    private void LoadRuntimeSettings()
    {
        var options = _services.LoadRuntimeSettings();
        ApplyRuntimeOptions(options);
        _services.ConfigureRuntimeCandidate(options);
        CanActivateRuntime = false;
        RuntimeHealthResult = $"Loaded settings from {RuntimeSettingsPath}";
        StatusText = "Runtime settings loaded.";
        UpdateRuntimeStatus();
    }

    private void SaveRuntimeSettings()
    {
        try
        {
            var options = BuildRuntimeOptionsFromUi();
            _services.SaveRuntimeSettings(options);
            _services.ConfigureRuntimeCandidate(options);
            CanActivateRuntime = false;
            RuntimeHealthResult = $"Saved settings to {RuntimeSettingsPath}";
            StatusText = "Runtime settings saved.";
            UpdateRuntimeStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"Runtime settings were not saved: {ex.Message}";
        }
    }

    private async Task RefreshRuntimeModelsAsync()
    {
        if (!Uri.TryCreate(RuntimeEndpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            RuntimeSelectionStatusText = "Runtime endpoint must be an absolute URL before refreshing models.";
            return;
        }

        var endpointPolicy = LocalEndpointPolicy.Validate(endpoint, allowPrivateLan: false);
        if (!endpointPolicy.IsAllowed)
        {
            RuntimeSelectionStatusText = endpointPolicy.Reason;
            return;
        }

        IsBusy = true;
        StatusText = "Refreshing installed local models...";
        try
        {
            var installedChoices = await FetchInstalledRuntimeModelChoicesAsync(endpoint, CancellationToken.None).ConfigureAwait(true);
            if (installedChoices.Count == 0)
            {
                RuntimeSelectionStatusText = "No installed models were listed by the local runtime endpoint.";
                StatusText = RuntimeSelectionStatusText;
                return;
            }

            var currentModel = RuntimeModelText;
            var currentQuantization = RuntimeQuantizationText;
            var currentContext = RuntimeContextText;
            var currentOutputLimit = RuntimeOutputLimitText;
            LoadRuntimeModelChoices(installedChoices, currentModel);

            var selectedLabel = FindRuntimeModelLabel(currentModel) ?? RuntimeModelChoices.FirstOrDefault() ?? string.Empty;
            SelectRuntimeModelChoice(
                selectedLabel,
                preferredQuantization: currentQuantization,
                preferredContext: currentContext,
                preferredOutputLimit: currentOutputLimit,
                resetToSmallest: string.IsNullOrWhiteSpace(currentModel));

            RuntimeSelectionStatusText = $"Found {installedChoices.Count} installed local model(s).";
            StatusText = RuntimeSelectionStatusText;
        }
        catch (Exception ex)
        {
            RuntimeSelectionStatusText = $"Installed model refresh failed: {ex.Message}";
            StatusText = RuntimeSelectionStatusText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckRuntimeAsync()
    {
        IsBusy = true;
        CanActivateRuntime = false;
        StatusText = "Checking local runtime...";
        SetModelConnectionStatus("command sent, waiting on model to load", MediaBrushes.Gold);
        await Task.Yield();

        try
        {
            var options = BuildRuntimeOptionsFromUi();
            _services.ConfigureRuntimeCandidate(options);
            var health = await _services.RuntimeController.CheckCandidateAsync(CancellationToken.None).ConfigureAwait(true);
            RuntimeHealthResult = FormatHealthResult(health);
            CanActivateRuntime = _services.RuntimeController.CanActivateCandidate;
            StatusText = CanActivateRuntime
                ? "Runtime check passed. Activate Runtime is now available."
                : health.Succeeded
                    ? "No candidate runtime is active. Stub remains active."
                    : $"Runtime check failed: {health.Summary}";
            if (!health.Succeeded)
            {
                SetModelConnectionStatus("model offline", MediaBrushes.Red);
            }

            UpdateRuntimeStatus();
        }
        catch (Exception ex)
        {
            RuntimeHealthResult = ex.Message;
            StatusText = $"Runtime check failed: {ex.Message}";
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ActivateRuntime()
    {
        if (!_services.RuntimeController.ActivateLastHealthChecked())
        {
            StatusText = "Runtime was not activated because no successful health check is available.";
            return;
        }

        CanActivateRuntime = false;
        UpdateRuntimeStatus();
        SetModelConnectionStatus("connected to model", MediaBrushes.LimeGreen);
        StatusText = "Verified runtime activated.";
    }

    private void RevertToStub()
    {
        _services.RuntimeController.RevertToFallback();
        CanActivateRuntime = _services.RuntimeController.CanActivateCandidate;
        UpdateRuntimeStatus();
        SetModelConnectionStatus("model offline", MediaBrushes.Red);
        StatusText = "Reverted to deterministic stub.";
    }

    private void RevertToLastKnownGood()
    {
        if (!_services.RuntimeController.RevertToLastKnownGood())
        {
            StatusText = "No last-known-good runtime is available yet.";
            return;
        }

        UpdateRuntimeStatus();
        SetModelConnectionStatus("connected to model", MediaBrushes.LimeGreen);
        StatusText = "Reverted to last-known-good runtime.";
    }

    private async void FlagIncorrect(object? parameter)
    {
        if (parameter is not ChatMessageViewModel message || !message.CanFlagAsIncorrect || message.IsFlaggedForCorrection)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message.SourceUserMessageId) || string.IsNullOrWhiteSpace(message.SourceQuestion))
        {
            StatusText = "Cannot flag bootstrap message: no source question is attached.";
            return;
        }

        try
        {
            var report = await _services.Orchestrator.Corrections.FlagIncorrectAsync(
                _conversationId,
                message.SourceUserMessageId,
                message.Id,
                message.SourceQuestion,
                message.Text,
                _services.Orchestrator.Runtime.ActiveProfile,
                message.EvidenceStatus,
                message.SourceAttachmentCount > 0 ? CorrectionCategory.MisreadScreenshot : CorrectionCategory.Other,
                userNote: "Flagged from WPF bootstrap chat.",
                voiceMetadata: message.SourceVoiceMetadata,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);

            message.MarkCorrection(report.Id);
            SaveActiveConversation();
            StatusText = $"Flagged for correction: {report.Id}";
        }
        catch (Exception ex)
        {
            StatusText = $"Correction queue write failed: {ex.Message}";
        }
    }

    private async Task RefreshCorrectionsAsync()
    {
        try
        {
            var selectedId = SelectedCorrectionReviewItem?.Id;
            var reports = await _services.Orchestrator.Corrections.ListAsync(CancellationToken.None).ConfigureAwait(true);

            CorrectionReviewItems.Clear();
            CorrectionReviewItemViewModel? selected = null;
            foreach (var report in reports)
            {
                var item = new CorrectionReviewItemViewModel(report);
                CorrectionReviewItems.Add(item);
                if (selectedId is not null && item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    selected = item;
                }
            }

            SelectedCorrectionReviewItem = selected ?? CorrectionReviewItems.FirstOrDefault();
            CorrectionReviewStatusText = reports.Count == 0
                ? "Correction queue is empty."
                : $"Loaded {reports.Count} local correction report(s).";
        }
        catch (Exception ex)
        {
            CorrectionReviewStatusText = $"Correction queue load failed: {ex.Message}";
        }
    }

    private async Task MarkSelectedCorrectionReviewedAsync()
    {
        await MarkSelectedCorrectionAsync(CorrectionStatus.Reviewed, "reviewed").ConfigureAwait(true);
    }

    private async Task MarkSelectedCorrectionUnresolvedAsync()
    {
        await MarkSelectedCorrectionAsync(CorrectionStatus.New, "unresolved").ConfigureAwait(true);
    }

    private async Task MarkSelectedCorrectionAsync(CorrectionStatus status, string displayStatus)
    {
        if (SelectedCorrectionReviewItem is null)
        {
            return;
        }

        var updated = await _services.Orchestrator.Corrections.SetStatusAsync(
                SelectedCorrectionReviewItem.Id,
                status,
                CancellationToken.None)
            .ConfigureAwait(true);

        if (updated is null)
        {
            CorrectionReviewStatusText = "Selected correction no longer exists.";
            await RefreshCorrectionsAsync().ConfigureAwait(true);
            return;
        }

        SelectedCorrectionReviewItem.Update(updated);
        CorrectionReviewStatusText = $"Marked correction {displayStatus}: {updated.Id}";
    }

    private async Task ExportSelectedCorrectionAsync()
    {
        if (SelectedCorrectionReviewItem is null)
        {
            return;
        }

        var path = await _services.Orchestrator.Corrections.ExportOneMarkdownAsync(
                SelectedCorrectionReviewItem.Id,
                CorrectionExportDirectory(),
                CancellationToken.None)
            .ConfigureAwait(true);

        if (path is null)
        {
            CorrectionReviewStatusText = "Selected correction no longer exists.";
            await RefreshCorrectionsAsync().ConfigureAwait(true);
            return;
        }

        CorrectionReviewStatusText = $"Exported correction: {path}";
        await RefreshCorrectionsAsync().ConfigureAwait(true);
    }

    private async Task ExportAllCorrectionsAsync()
    {
        var path = await _services.Orchestrator.Corrections.ExportAllMarkdownAsync(
                CorrectionExportDirectory(),
                CancellationToken.None)
            .ConfigureAwait(true);

        CorrectionReviewStatusText = $"Exported correction queue: {path}";
    }

    private string CorrectionExportDirectory() =>
        Path.Combine(_services.DataRoot, "correction-exports");

    private OpenAiCompatibleRuntimeOptions BuildRuntimeOptionsFromUi()
    {
        if (!Uri.TryCreate(RuntimeEndpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("Runtime endpoint must be an absolute URL.");
        }

        if (!int.TryParse(RuntimeContextText.Trim(), out var contextTokens) || contextTokens < 512)
        {
            throw new InvalidOperationException("Context size must be at least 512 tokens.");
        }

        if (!int.TryParse(RuntimeOutputLimitText.Trim(), out var outputLimit) || outputLimit < 1)
        {
            throw new InvalidOperationException("Max output tokens must be at least 1.");
        }

        if (!double.TryParse(RuntimeTemperatureText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
            || temperature < 0
            || temperature > 2)
        {
            throw new InvalidOperationException("Temperature must be a number from 0 to 2.");
        }

        double? topP = null;
        var topPText = RuntimeTopPText.Trim();
        if (topPText.Equals(RuntimeTopPModelDefault, StringComparison.OrdinalIgnoreCase))
        {
            topPText = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(topPText))
        {
            if (!double.TryParse(topPText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTopP)
                || parsedTopP <= 0
                || parsedTopP > 1)
            {
                throw new InvalidOperationException("Top-p must be blank or a number greater than 0 and no more than 1.");
            }

            topP = parsedTopP;
        }

        var model = RuntimeModelText.Trim();
        var selectedModel = CurrentRuntimeModelChoice();
        var quantization = PreferConfigured(RuntimeQuantizationText, selectedModel?.DefaultQuantization ?? "Installed package default");

        return new OpenAiCompatibleRuntimeOptions(
            Enabled: !string.IsNullOrWhiteSpace(model),
            Endpoint: endpoint,
            Model: model,
            DisplayName: selectedModel?.DisplayName ?? (string.IsNullOrWhiteSpace(model) ? "Local OpenAI-compatible runtime" : $"Local {model}"),
            Family: selectedModel?.Family ?? "local",
            Size: selectedModel?.Size ?? "unknown",
            Quantization: quantization,
            ContextTokens: contextTokens,
            OutputTokenLimit: outputLimit,
            Temperature: temperature,
            TopP: topP,
            StreamingEnabled: RuntimeStreamingEnabled,
            SupportsVision: RuntimeVisionEnabled,
            SupportsToolCalls: false,
            AllowPrivateLanEndpoint: false);
    }

    public async Task AddClipboardImageAsync()
    {
        if (!System.Windows.Clipboard.ContainsImage())
        {
            AttachmentStatus = "Clipboard does not contain an image.";
            return;
        }

        var image = System.Windows.Clipboard.GetImage();
        if (image is null)
        {
            AttachmentStatus = "Clipboard image could not be read.";
            return;
        }

        await AddBitmapSourceAsync(image, "clipboard").ConfigureAwait(true);
    }

    private async Task AddBitmapSourceAsync(BitmapSource bitmapSource, string sourceName)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

        await using var stream = new MemoryStream();
        encoder.Save(stream);
        await AddPngBytesAsync(stream.ToArray(), sourceName).ConfigureAwait(true);
    }

    private async Task AddPngBytesAsync(byte[] pngBytes, string sourceName)
    {
        var directory = Path.Combine(
            _services.DataRoot,
            "SessionImages",
            DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        var id = $"img_{Guid.NewGuid():N}";
        var fileName = $"{sourceName}-{id}.png";
        var filePath = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(filePath, pngBytes).ConfigureAwait(true);

        Attachments.Add(new ImageAttachmentViewModel(
            id,
            fileName,
            filePath,
            "image/png",
            Convert.ToBase64String(pngBytes),
            DateTimeOffset.UtcNow));

        AttachmentStatus = $"{Attachments.Count} image attachment(s). Temporary by default.";
    }

    private void RemoveAttachment(object? parameter)
    {
        if (parameter is not ImageAttachmentViewModel attachment)
        {
            return;
        }

        Attachments.Remove(attachment);
        DeleteAttachmentIfTemporary(attachment);
        AttachmentStatus = Attachments.Count == 0
            ? "Screenshots are temporary by default."
            : $"{Attachments.Count} image attachment(s). Temporary by default.";
    }

    private void ClearTemporaryAttachments()
    {
        foreach (var attachment in Attachments.ToList())
        {
            if (!attachment.RetainAfterSession)
            {
                DeleteAttachmentIfTemporary(attachment);
                Attachments.Remove(attachment);
            }
        }

        AttachmentStatus = Attachments.Count == 0
            ? "Screenshots are temporary by default."
            : $"{Attachments.Count} retained image attachment(s).";
    }

    private static void DeleteAttachmentIfTemporary(ImageAttachmentViewModel attachment)
    {
        if (attachment.RetainAfterSession)
        {
            return;
        }

        try
        {
            if (File.Exists(attachment.FilePath))
            {
                File.Delete(attachment.FilePath);
            }
        }
        catch
        {
            // Temporary cleanup should not hide the verified answer or correction flow.
        }
    }

    private async Task StartVoiceRecordingAsync()
    {
        if (IsRecording || IsTranscribing)
        {
            return;
        }

        StopSpeaking();
        _activeVoiceInput?.Dispose();
        _activeVoiceInput = new CancellationTokenSource();

        var directory = Path.Combine(
            _services.DataRoot,
            "SessionAudio",
            DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        try
        {
            StopInputLevelMonitor();
            SubscribeRecorderLevels();
            ApplyVoiceInputPreset(SelectedVoiceInputPreset);
            await _services.VoiceRecorder.StartAsync(directory, _activeVoiceInput.Token).ConfigureAwait(true);
            IsRecording = true;
            VoiceStatus = $"Recording from {SelectedVoiceInputDevice}...";
            SttStatus = _services.SpeechToText.IsConfigured
                ? "Waiting for recording to stop."
                : "Recording works, but local STT is not configured.";
        }
        catch (Exception ex)
        {
            UnsubscribeRecorderLevels();
            _services.VoiceRecorder.Cancel();
            IsRecording = false;
            VoiceStatus = $"Recording could not start: {ex.Message}";
            StartInputLevelMonitor();
        }
    }

    private async Task StopVoiceRecordingOrTranscriptionAsync()
    {
        if (IsTranscribing)
        {
            _activeVoiceInput?.Cancel();
            SttStatus = "Transcription cancellation requested.";
            return;
        }

        if (!IsRecording)
        {
            return;
        }

        VoiceAudioInput? audioInput = null;
        try
        {
            audioInput = await _services.VoiceRecorder.StopAsync(_activeVoiceInput?.Token ?? CancellationToken.None).ConfigureAwait(true);
            if (RetainDebugAudio)
            {
                audioInput = audioInput with { RetainAudio = true };
            }

            IsRecording = false;
            VoiceStatus = "Recording stopped.";
            UnsubscribeRecorderLevels();
            UpdateCaptureDiagnostics(audioInput);
        }
        catch (OperationCanceledException)
        {
            UnsubscribeRecorderLevels();
            VoiceStatus = "Voice input canceled.";
            SttStatus = "Recording canceled.";
            IsRecording = false;
            if (audioInput is not null)
            {
                DeleteVoiceAudioIfTemporary(audioInput);
            }

            _activeVoiceInput?.Dispose();
            _activeVoiceInput = null;
            StartInputLevelMonitor();
            return;
        }
        catch (Exception ex)
        {
            UnsubscribeRecorderLevels();
            VoiceStatus = "Voice recording did not stop cleanly.";
            SttStatus = ex.Message;
            IsRecording = false;
            if (audioInput is not null)
            {
                DeleteVoiceAudioIfTemporary(audioInput);
            }

            _activeVoiceInput?.Dispose();
            _activeVoiceInput = null;
            StartInputLevelMonitor();
            return;
        }

        _ = TranscribeAudioAsync(audioInput);
    }

    private async Task TranscribeAudioAsync(VoiceAudioInput audioInput)
    {
        var shouldAutoSend = false;
        var routeAsPushToTalk = _currentVoiceInputShouldAutoSend;
        try
        {
            IsTranscribing = true;
            SttStatus = "Transcribing locally...";
            var captureDiagnostics = UpdateCaptureDiagnostics(audioInput) ?? _lastCaptureDiagnostics;
            if (captureDiagnostics is not null)
            {
                var captureGate = VoiceCaptureSafetyGate.Evaluate(captureDiagnostics);
                if (!captureGate.Accepted)
                {
                    _lastVoiceMetadata = CreateVoiceMetadata(
                        transcript: null,
                        audioInput,
                        suspiciousOrNoSpeech: true,
                        rejectionReason: captureGate.Reason);
                    VoiceStatus = captureGate.Message;
                    SttStatus = $"Voice capture rejected: {captureGate.Reason}. No transcript was sent.";
                    return;
                }
            }

            if (NormalizeBeforeStt)
            {
                var normalization = VoiceAudioNormalizer.NormalizePcm16WaveInPlace(audioInput.FilePath);
                SttStatus = normalization.Applied
                    ? $"Normalized voice audio before STT ({normalization.GainMultiplier:0.00}x)."
                    : "Voice audio normalization checked; no change needed.";
                UpdateCaptureDiagnostics(audioInput);
            }

            var transcript = await _services.SpeechToText.TranscribeAsync(
                audioInput,
                _activeVoiceInput?.Token ?? CancellationToken.None).ConfigureAwait(true);
            UpdateLastSttDebugText();
            var normalizedTranscript = SpeechTranscriptGuard.NormalizeAssistantName(transcript.Text);

            var transcriptGuard = SpeechTranscriptGuard.Evaluate(normalizedTranscript);
            if (!transcriptGuard.Accepted)
            {
                _lastVoiceMetadata = CreateVoiceMetadata(
                    normalizedTranscript,
                    audioInput,
                    suspiciousOrNoSpeech: true,
                    rejectionReason: transcriptGuard.Reason);
                VoiceStatus = transcriptGuard.Message;
                SttStatus = $"Transcript rejected: {transcriptGuard.Reason}. No transcript was sent.";
                return;
            }

            SaveLastSuccessfulSttDevice();
            LastTranscript = normalizedTranscript;
            EditableTranscript = normalizedTranscript;
            var routing = VoiceTranscriptRouting.Decide(routeAsPushToTalk);
            if (routing.PlaceTranscriptInComposer)
            {
                ComposerText = normalizedTranscript;
            }

            _lastVoiceMetadata = CreateVoiceMetadata(
                normalizedTranscript,
                audioInput,
                suspiciousOrNoSpeech: false,
                rejectionReason: null);

            shouldAutoSend = routing.SendAutomatically;
            VoiceStatus = shouldAutoSend
                ? "Transcript accepted; sending to Ali."
                : "Transcript placed in the chat bar.";
            SttStatus = $"Transcript created by {transcript.ProviderName}.";
        }
        catch (OperationCanceledException)
        {
            VoiceStatus = "Voice input canceled.";
            SttStatus = "Transcription canceled.";
        }
        catch (Exception ex)
        {
            UpdateLastSttDebugText();
            _lastVoiceMetadata = CreateVoiceMetadata(
                transcript: null,
                audioInput,
                suspiciousOrNoSpeech: true,
                rejectionReason: "STT failure");
            VoiceStatus = "I couldn't hear that clearly. Try again or check the microphone. I did not run a command.";
            SttStatus = ex.Message;
        }
        finally
        {
            IsTranscribing = false;
            _currentVoiceInputShouldAutoSend = false;
            DeleteVoiceAudioIfTemporary(audioInput);
            _activeVoiceInput?.Dispose();
            _activeVoiceInput = null;
            StartInputLevelMonitor();
        }

        if (shouldAutoSend)
        {
            await SendTranscriptAsync().ConfigureAwait(true);
        }
    }

    private async Task SendTranscriptAsync()
    {
        var transcript = SpeechTranscriptGuard.NormalizeAssistantName(EditableTranscript).Trim();
        if (string.IsNullOrWhiteSpace(transcript) || IsBusy)
        {
            return;
        }

        var transcriptGuard = SpeechTranscriptGuard.Evaluate(transcript);
        if (!transcriptGuard.Accepted)
        {
            _lastVoiceMetadata = CreateVoiceMetadata(
                transcript,
                audioInput: null,
                suspiciousOrNoSpeech: true,
                rejectionReason: transcriptGuard.Reason);
            VoiceStatus = transcriptGuard.Message;
            StatusText = VoiceStatus;
            return;
        }

        if (VoiceCommandSafety.RequiresVisibleConfirmation(transcript))
        {
            _lastVoiceMetadata = CreateVoiceMetadata(
                transcript,
                audioInput: null,
                suspiciousOrNoSpeech: true,
                rejectionReason: "risky command");
            VoiceStatus = VoiceCommandSafety.BlockedPhaseOneCMessage();
            StatusText = VoiceStatus;
            return;
        }

        var baseVoiceMetadata = _lastVoiceMetadata is { SuspiciousOrNoSpeech: false }
            ? _lastVoiceMetadata
            : CreateVoiceMetadata(
                transcript,
                audioInput: null,
                suspiciousOrNoSpeech: false,
                rejectionReason: null);
        var voiceMetadata = baseVoiceMetadata with
        {
            Transcript = baseVoiceMetadata.Transcript ?? transcript,
            TextToSpeechProvider = _services.TextToSpeech.ProviderName,
            TextToSpeechVoice = _services.TextToSpeech.VoiceId,
            InputDeviceNumber = CurrentInputDeviceNumber(),
            InputDeviceName = CurrentInputDeviceName(),
            InputChannelMode = InputChannelModeCatalog.ToLabel(CurrentInputChannelMode()),
            InputPreset = SelectedVoiceInputPreset,
            ExtraInputGainDb = ExtraInputGainDb,
            NormalizeBeforeStt = NormalizeBeforeStt,
            SpeechToTextModel = CurrentSpeechToTextModel(),
            TextToSpeechModel = CurrentTextToSpeechModel(),
            RawAudioRetained = baseVoiceMetadata.RawAudioRetained,
            SuspiciousOrNoSpeech = false,
            RejectionReason = null
        };

        VoiceStatus = "Voice transcript sent to Ali.";
        try
        {
            await SendTextAsync(transcript, VoiceInputOrigin.Voice, voiceMetadata).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Transcript was accepted, but sending failed: {ex.Message}";
            StatusText = VoiceStatus;
        }
    }

    private StreamingSpeechState? StartStreamingSpeechIfNeeded(VoiceInputOrigin inputOrigin)
    {
        if (inputOrigin != VoiceInputOrigin.Voice)
        {
            return null;
        }

        if (!_services.TextToSpeech.IsConfigured)
        {
            TtsStatus = "Local TTS is not configured. Text answer is available.";
            VoiceStatus = "Speech skipped because local TTS is not configured.";
            return null;
        }

        _activeSpeech?.Cancel();
        _services.SpeechPlayer.Stop();
        _activeSpeech?.Dispose();
        _activeSpeech = new CancellationTokenSource();

        var state = new StreamingSpeechState(_activeSpeech);
        IsSpeaking = true;
        TtsStatus = "Waiting for streamed response...";
        VoiceStatus = "Voice response streaming...";
        state.ConsumerTask = ConsumeStreamingSpeechAsync(state);
        return state;
    }

    private static void QueueStreamingSpeech(StreamingSpeechState? state, string chunkText)
    {
        if (state is null)
        {
            return;
        }

        foreach (var segment in state.Buffer.Append(chunkText))
        {
            state.Queue.Writer.TryWrite(segment);
        }
    }

    private void CompleteStreamingSpeechInput(StreamingSpeechState? state)
    {
        if (state is null)
        {
            return;
        }

        foreach (var segment in state.Buffer.Complete())
        {
            state.Queue.Writer.TryWrite(segment);
        }

        state.Queue.Writer.TryComplete();
        TtsStatus = "Finishing streamed speech...";
    }

    private void CancelStreamingSpeech(StreamingSpeechState? state)
    {
        if (state is null)
        {
            return;
        }

        state.Cancellation.Cancel();
        state.Queue.Writer.TryComplete();
        _services.SpeechPlayer.Stop();
    }

    private async Task ConsumeStreamingSpeechAsync(StreamingSpeechState state)
    {
        try
        {
            await foreach (var segment in state.Queue.Reader.ReadAllAsync(state.Cancellation.Token).ConfigureAwait(true))
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                SpeechSynthesisResult? speech = null;
                try
                {
                    TtsStatus = "Synthesizing streamed speech...";
                    var settings = new VoiceSettings(
                        _services.TextToSpeech.VoiceId,
                        Rate: 1.0,
                        RetainAudio: false);

                    speech = await _services.TextToSpeech.SynthesizeAsync(
                        segment,
                        settings,
                        state.Cancellation.Token).ConfigureAwait(true);

                    TtsStatus = "Speaking streamed response...";
                    await _services.SpeechPlayer.PlayAsync(speech.AudioPath, state.Cancellation.Token).ConfigureAwait(true);
                    SaveLastSuccessfulTtsDevice();
                }
                finally
                {
                    if (speech is not null && !speech.RetainAudio && File.Exists(speech.AudioPath))
                    {
                        TryDeleteFile(speech.AudioPath);
                    }
                }
            }

            TtsStatus = "Speech complete.";
            VoiceStatus = "Voice loop complete.";
        }
        catch (OperationCanceledException)
        {
            TtsStatus = "Speech stopped.";
        }
        catch (Exception ex)
        {
            TtsStatus = $"Speech failed: {ex.Message}";
        }
        finally
        {
            IsSpeaking = false;
            if (ReferenceEquals(_activeSpeech, state.Cancellation))
            {
                _activeSpeech.Dispose();
                _activeSpeech = null;
            }
        }
    }

    private void StopSpeaking()
    {
        _activeSpeech?.Cancel();
        _services.SpeechPlayer.Stop();
        IsSpeaking = false;
        if (!string.IsNullOrWhiteSpace(TtsStatus))
        {
            TtsStatus = "Speech stopped.";
        }
    }

    private async Task PlayPiperSampleAsync()
    {
        if (IsSpeaking)
        {
            return;
        }

        ApplyVoiceToolSettings(saveSettings: true, reportStatus: false);
        if (!_services.TextToSpeech.IsConfigured)
        {
            TtsStatus = "Piper is not configured yet.";
            return;
        }

        _activeSpeech?.Cancel();
        _services.SpeechPlayer.Stop();
        _activeSpeech?.Dispose();
        _activeSpeech = new CancellationTokenSource();

        SpeechSynthesisResult? speech = null;
        IsSpeaking = true;
        try
        {
            TtsStatus = $"Testing {PiperVoiceText}...";
            speech = await _services.TextToSpeech.SynthesizeAsync(
                "Hello, I am Ali. This is what my selected voice sounds like.",
                new VoiceSettings(PiperVoiceText, Rate: 1.0, RetainAudio: false),
                _activeSpeech.Token).ConfigureAwait(true);

            await _services.SpeechPlayer.PlayAsync(speech.AudioPath, _activeSpeech.Token).ConfigureAwait(true);
            TtsStatus = $"Voice sample complete: {PiperVoiceText}.";
            SaveLastSuccessfulTtsDevice();
        }
        catch (OperationCanceledException)
        {
            TtsStatus = "Voice sample stopped.";
        }
        catch (Exception ex)
        {
            TtsStatus = $"Voice sample failed: {ex.Message}";
        }
        finally
        {
            IsSpeaking = false;
            if (speech is not null && !speech.RetainAudio && File.Exists(speech.AudioPath))
            {
                TryDeleteFile(speech.AudioPath);
            }

            _activeSpeech?.Dispose();
            _activeSpeech = null;
        }
    }

    private async Task OpenSettingsAsync()
    {
        try
        {
            if (!OpenSettingsWindow())
            {
                return;
            }

            _voiceMonitorRequested = true;
            RefreshVoiceSettingsChoices();
            StartInputLevelMonitor();
            await RefreshCorrectionsAsync().ConfigureAwait(true);
            RefreshMemoryReminders();
            await RefreshRuntimeModelChoicesForSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _voiceMonitorRequested = false;
            StopInputLevelMonitor();
            HandleCommandException(ex);
        }
    }

    private bool OpenSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
            return false;
        }

        var owner = System.Windows.Application.Current?.MainWindow;
        _settingsWindow = new SettingsWindow
        {
            DataContext = this,
            Owner = owner
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _voiceMonitorRequested = false;
            StopInputLevelMonitor();
            VoiceInputLevelPercent = 0;
            VoiceInputMeterText = "Input meter paused.";
            VoiceDiagnosticsText = "Microphone monitoring is off.";
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
        return true;
    }

    private void OpenLocalLibrary()
    {
        if (_localLibraryWindow is not null)
        {
            if (!_localLibraryWindow.IsVisible)
            {
                _localLibraryWindow.Show();
            }

            _localLibraryWindow.Activate();
            return;
        }

        var owner = System.Windows.Application.Current?.MainWindow;
        _localLibraryWindow = new LocalLibraryWindow(_services)
        {
            Owner = owner
        };
        _localLibraryWindow.Closed += (_, _) => _localLibraryWindow = null;
        _localLibraryWindow.Show();
        _localLibraryWindow.Activate();
    }

    private void RefreshVoiceSettingsChoices()
    {
        _suppressInputMonitorRestart = true;
        _loadingVoiceSettings = true;
        try
        {
            try
            {
                LoadVoiceDevices();
                RefreshVoiceInputChannelModes();
            }
            catch (Exception ex)
            {
                VoiceInputDevices.Clear();
                VoiceInputDevices.Add("0: Default microphone");
                SelectedVoiceInputDevice = VoiceInputDevices[0];
                VoiceOutputDevices.Clear();
                VoiceOutputDevices.Add("-1: Default playback device");
                SelectedVoiceOutputDevice = VoiceOutputDevices[0];
                VoiceInputChannelModes.Clear();
                VoiceInputChannelModes.Add(InputChannelModeCatalog.MonoSumLabel);
                SelectedVoiceInputChannelMode = InputChannelModeCatalog.MonoSumLabel;
                VoiceSettingsStatusText = $"Voice devices unavailable: {ex.Message}";
            }
        }
        finally
        {
            _loadingVoiceSettings = false;
            _suppressInputMonitorRestart = false;
        }

        try
        {
            LoadPiperVoiceChoices();
            var selectedPiperVoice = FindPiperVoiceLabelForModel(PiperModelText)
                ?? PiperVoiceChoices.FirstOrDefault()
                ?? string.Empty;
            if (!string.Equals(SelectedPiperVoiceChoice, selectedPiperVoice, StringComparison.Ordinal))
            {
                SelectedPiperVoiceChoice = selectedPiperVoice;
            }
            else
            {
                OnPropertyChanged(nameof(SelectedPiperVoiceChoice));
            }
        }
        catch (Exception ex)
        {
            _piperVoiceChoices.Clear();
            PiperVoiceChoices.Clear();
            SelectedPiperVoiceChoice = string.Empty;
            VoiceSettingsStatusText = $"Piper voice list unavailable: {ex.Message}";
        }

        RefreshSpeechToolStatuses();
    }

    private async Task RefreshRuntimeModelChoicesForSettingsAsync()
    {
        var currentModel = RuntimeModelText;
        var currentQuantization = RuntimeQuantizationText;
        var currentContext = RuntimeContextText;
        var currentOutputLimit = RuntimeOutputLimitText;

        if (!Uri.TryCreate(RuntimeEndpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            EnsureRuntimeModelChoicesAvailable(currentModel);
            RuntimeSelectionStatusText = "Runtime endpoint must be an absolute URL before refreshing models.";
            return;
        }

        var endpointPolicy = LocalEndpointPolicy.Validate(endpoint, allowPrivateLan: false);
        if (!endpointPolicy.IsAllowed)
        {
            EnsureRuntimeModelChoicesAvailable(currentModel);
            RuntimeSelectionStatusText = endpointPolicy.Reason;
            return;
        }

        try
        {
            var installedChoices = await FetchInstalledRuntimeModelChoicesAsync(endpoint, CancellationToken.None).ConfigureAwait(true);
            if (installedChoices.Count == 0)
            {
                EnsureRuntimeModelChoicesAvailable(currentModel);
                RuntimeSelectionStatusText = "No installed models were listed by the local runtime endpoint.";
                return;
            }

            LoadRuntimeModelChoices(installedChoices, currentModel);
            var selectedLabel = FindRuntimeModelLabel(currentModel)
                ?? RuntimeModelChoices.FirstOrDefault()
                ?? string.Empty;
            SelectRuntimeModelChoice(
                selectedLabel,
                preferredQuantization: currentQuantization,
                preferredContext: currentContext,
                preferredOutputLimit: currentOutputLimit,
                resetToSmallest: string.IsNullOrWhiteSpace(currentModel));
            RuntimeSelectionStatusText = $"Found {installedChoices.Count} installed local model(s).";
        }
        catch (Exception ex)
        {
            EnsureRuntimeModelChoicesAvailable(currentModel);
            RuntimeSelectionStatusText = $"Installed model refresh failed: {ex.Message}";
        }
    }

    private void EnsureRuntimeModelChoicesAvailable(string? selectedModel)
    {
        if (RuntimeModelChoices.Count > 0)
        {
            return;
        }

        LoadRuntimeModelChoices(CreateKnownRuntimeModelChoices(), selectedModel);
        var selectedLabel = FindRuntimeModelLabel(selectedModel)
            ?? RuntimeModelChoices.FirstOrDefault()
            ?? string.Empty;
        SelectRuntimeModelChoice(
            selectedLabel,
            preferredQuantization: RuntimeQuantizationText,
            preferredContext: RuntimeContextText,
            preferredOutputLimit: RuntimeOutputLimitText,
            resetToSmallest: false);
    }

    private void LoadSpeechToolSettings()
    {
        _loadingSpeechToolSettings = true;
        var whisperDefaults = WhisperCliSpeechToTextOptions.FromEnvironment();
        var piperDefaults = PiperCliTextToSpeechOptions.FromEnvironment(_services.DataRoot);
        LoadPiperVoiceChoices();

        WhisperExecutableText = ToPortablePath(PreferValidConfiguredPath(
            _voiceSettings.WhisperExecutablePath,
            PreferConfigured(FindLocalWhisperPythonExecutable(), whisperDefaults.ExecutablePath))) ?? string.Empty;
        WhisperModelText = ToPortablePath(PreferValidConfiguredPath(
            _voiceSettings.WhisperModelPath,
            PreferConfigured(FindLocalWhisperModelRoot(), whisperDefaults.ModelPath))) ?? string.Empty;
        var localWhisperArguments = BuildLocalWhisperArgumentsTemplate();
        WhisperArgumentsText = PreferWhisperArgumentsTemplate(
            _voiceSettings.WhisperArgumentsTemplate,
            localWhisperArguments,
            whisperDefaults.ArgumentsTemplate);
        PiperExecutableText = ToPortablePath(PreferPiperExecutablePath(
            _voiceSettings.PiperExecutablePath,
            PreferConfigured(FindLocalPiperExecutable(), piperDefaults.ExecutablePath))) ?? string.Empty;
        PiperModelText = ToPortablePath(PreferValidConfiguredPath(
            _voiceSettings.PiperModelPath,
            PreferConfigured(PreferredPiperModelPath(), piperDefaults.ModelPath))) ?? string.Empty;
        PiperVoiceText = PreferConfigured(_voiceSettings.PiperVoiceId, piperDefaults.VoiceId);
        PiperArgumentsText = PreferPiperArgumentsTemplate(
            _voiceSettings.PiperArgumentsTemplate,
            BuildLocalPiperArgumentsTemplate(),
            piperDefaults.ArgumentsTemplate);
        SelectedPiperVoiceChoice = FindPiperVoiceLabelForModel(PiperModelText) ?? PiperVoiceChoices.FirstOrDefault() ?? string.Empty;
        ApplySelectedPiperVoiceChoice(SelectedPiperVoiceChoice, applySettings: false);
        _loadingSpeechToolSettings = false;
    }

    private void ApplyVoiceToolSettings() => ApplyVoiceToolSettings(saveSettings: true, reportStatus: true);

    private void ApplyVoiceToolSettings(bool saveSettings, bool reportStatus)
    {
        try
        {
            var sttOptions = BuildWhisperOptionsFromUi();
            var ttsOptions = BuildPiperOptionsFromUi();

            LocalSpeechToolPolicy.EnsureLocalOnly(
                "Speech-to-text",
                sttOptions.ExecutablePath,
                sttOptions.ModelPath,
                sttOptions.ArgumentsTemplate);
            LocalSpeechToolPolicy.EnsureLocalOnly(
                "Text-to-speech",
                ttsOptions.ExecutablePath,
                ttsOptions.ModelPath,
                ttsOptions.ArgumentsTemplate);

            SetProcessEnvironment("ALI_WHISPER_EXE", sttOptions.ExecutablePath);
            SetProcessEnvironment("ALI_WHISPER_MODEL", sttOptions.ModelPath);
            SetProcessEnvironment("ALI_WHISPER_ARGS", sttOptions.ArgumentsTemplate);
            SetProcessEnvironment("ALI_PIPER_EXE", ttsOptions.ExecutablePath);
            SetProcessEnvironment("ALI_PIPER_MODEL", ttsOptions.ModelPath);
            SetProcessEnvironment("ALI_PIPER_VOICE", ttsOptions.VoiceId);
            SetProcessEnvironment("ALI_PIPER_ARGS", ttsOptions.ArgumentsTemplate);

            _services.ConfigureSpeechTools(sttOptions, ttsOptions);
            if (saveSettings)
            {
                SaveVoiceToolSettings();
            }

            RefreshSpeechToolStatuses();
            if (reportStatus)
            {
                VoiceSettingsStatusText = "Voice tool settings applied for this Ali session.";
            }
        }
        catch (Exception ex)
        {
            RefreshSpeechToolStatuses();
            VoiceSettingsStatusText = reportStatus
                ? $"Voice settings were not applied: {ex.Message}"
                : $"Saved voice settings were not applied: {ex.Message}";
        }
    }

    private WhisperCliSpeechToTextOptions BuildWhisperOptionsFromUi()
    {
        var defaults = WhisperCliSpeechToTextOptions.FromEnvironment();
        return new WhisperCliSpeechToTextOptions(
            ResolvePortablePath(WhisperExecutableText),
            ResolvePortablePath(WhisperModelText),
            PreferConfigured(WhisperArgumentsText, defaults.ArgumentsTemplate),
            defaults.OutputTextSuffix);
    }

    private static string? FindLocalWhisperPythonExecutable()
    {
        var candidate = LocalVoiceResourceLocator.FindWhisperPythonExecutable(AppBaseDirectory);
        return File.Exists(candidate) ? ToPortablePath(candidate) : null;
    }

    private static string? FindLocalWhisperModelRoot()
    {
        var candidate = LocalVoiceResourceLocator.FindWhisperModelRoot(AppBaseDirectory);
        return Directory.Exists(candidate) ? ToPortablePath(candidate) : null;
    }

    private static string? BuildLocalWhisperArgumentsTemplate()
    {
        var script = FindLocalWhisperScript();
        var portableScript = File.Exists(script) ? ToPortablePath(script) : null;
        return string.IsNullOrWhiteSpace(portableScript)
            ? null
            : $"\"{portableScript}\" --audio \"{{audio}}\" --model-root \"{{model}}\" --model-id small.en --output-base \"{{outputBase}}\" --vad-filter";
    }

    private static string BuildLocalPiperArgumentsTemplate() =>
        "-m piper --model \"{model}\" --output_file \"{output}\"";

    private static string? FindLocalWhisperScript()
    {
        return LocalVoiceResourceLocator.FindWhisperScript(AppBaseDirectory);
    }

    private PiperCliTextToSpeechOptions BuildPiperOptionsFromUi()
    {
        var defaults = PiperCliTextToSpeechOptions.FromEnvironment(_services.DataRoot);
        return new PiperCliTextToSpeechOptions(
            ResolvePortablePath(PiperExecutableText),
            ResolvePortablePath(PiperModelText),
            PreferConfigured(PiperVoiceText, defaults.VoiceId),
            PreferConfigured(PiperArgumentsText, defaults.ArgumentsTemplate),
            defaults.OutputDirectory);
    }

    private void LoadPiperVoiceChoices()
    {
        _piperVoiceChoices.Clear();
        PiperVoiceChoices.Clear();

        var voiceDirectory = FindLocalPiperVoiceDirectory();
        if (voiceDirectory is null)
        {
            return;
        }

        foreach (var modelPath in Directory.EnumerateFiles(voiceDirectory, "en_US-*.onnx").OrderBy(Path.GetFileName))
        {
            var voiceId = Path.GetFileNameWithoutExtension(modelPath);
            var label = FormatPiperVoiceLabel(voiceId);
            _piperVoiceChoices[label] = new PiperVoiceChoice(label, voiceId, ToPortablePath(modelPath) ?? modelPath);
            PiperVoiceChoices.Add(label);
        }
    }

    private void ApplySelectedPiperVoiceChoice(string label, bool applySettings)
    {
        if (!_piperVoiceChoices.TryGetValue(label, out var choice))
        {
            return;
        }

        PiperVoiceText = choice.VoiceId;
        PiperModelText = choice.ModelPath;
        if (applySettings)
        {
            ApplyVoiceToolSettings(saveSettings: true, reportStatus: false);
            VoiceSettingsStatusText = $"Ali voice set to {choice.Label}.";
        }
    }

    private string? FindPiperVoiceLabelForModel(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var normalized = ResolvePortablePath(modelPath);
        if (normalized is null)
        {
            return null;
        }

        return _piperVoiceChoices.Values.FirstOrDefault(choice =>
            string.Equals(ResolvePortablePath(choice.ModelPath), normalized, StringComparison.OrdinalIgnoreCase))?.Label;
    }

    private string? PreferredPiperModelPath()
    {
        var preferred = _piperVoiceChoices.Values.FirstOrDefault(choice =>
            choice.VoiceId.Equals("en_US-hfc_female-medium", StringComparison.OrdinalIgnoreCase));
        preferred ??= _piperVoiceChoices.Values.FirstOrDefault();
        return preferred?.ModelPath;
    }

    private void SaveVoiceToolSettings()
    {
        _voiceSettings = _voiceSettings with
        {
            WhisperExecutablePath = ToPortablePath(WhisperExecutableText),
            WhisperModelPath = ToPortablePath(WhisperModelText),
            WhisperArgumentsTemplate = NullIfWhiteSpace(WhisperArgumentsText),
            PiperExecutablePath = ToPortablePath(PiperExecutableText),
            PiperModelPath = ToPortablePath(PiperModelText),
            PiperVoiceId = NullIfWhiteSpace(PiperVoiceText),
            PiperArgumentsTemplate = NullIfWhiteSpace(PiperArgumentsText)
        };

        VoiceRuntimeSettingsStore.Save(_services.DataRoot, _voiceSettings);
    }

    private void RefreshSpeechToolStatuses()
    {
        SttStatus = _services.SpeechToText.IsConfigured
            ? $"STT ready: {_services.SpeechToText.ProviderName}"
            : "STT not configured. Set the local Whisper executable.";
        TtsStatus = _services.TextToSpeech.IsConfigured
            ? $"TTS ready: {_services.TextToSpeech.ProviderName} ({_services.TextToSpeech.VoiceId})"
            : "TTS not configured. Set the local Piper executable and voice model.";
    }

    private static void DeleteVoiceAudioIfTemporary(VoiceAudioInput audioInput)
    {
        if (!audioInput.RetainAudio)
        {
            TryDeleteFile(audioInput.FilePath);
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Temporary audio cleanup is best-effort.
        }
    }

    private void ApplyRuntimeOptions(OpenAiCompatibleRuntimeOptions options)
    {
        var selectedModel = RuntimeModelChoice.FromOptions(options);
        LoadRuntimeModelChoices(CreateKnownRuntimeModelChoices().Append(selectedModel), options.Model);

        RuntimeEnabled = options.Enabled;
        RuntimeEndpointText = options.Endpoint.ToString();
        var temperatureText = options.Temperature.ToString(CultureInfo.InvariantCulture);
        var topPText = options.TopP?.ToString(CultureInfo.InvariantCulture) ?? RuntimeTopPModelDefault;
        EnsureChoice(RuntimeTemperatureChoices, temperatureText);
        EnsureChoice(RuntimeTopPChoices, topPText);
        RuntimeTemperatureText = temperatureText;
        RuntimeTopPText = topPText;
        RuntimeStreamingEnabled = options.StreamingEnabled;
        RuntimeVisionEnabled = options.SupportsVision;

        var selectedLabel = FindRuntimeModelLabel(options.Model);
        if (selectedLabel is null)
        {
            RuntimeModelText = options.Model;
            RuntimeQuantizationText = options.Quantization;
            RuntimeContextText = options.ContextTokens.ToString(CultureInfo.InvariantCulture);
            RuntimeOutputLimitText = options.OutputTokenLimit.ToString(CultureInfo.InvariantCulture);
            RuntimeSelectionStatusText = string.IsNullOrWhiteSpace(options.Model)
                ? "Refresh installed models or choose a known model option."
                : "Saved model is not in the local model list yet.";
            return;
        }

        SelectRuntimeModelChoice(
            selectedLabel,
            preferredQuantization: options.Quantization,
            preferredContext: options.ContextTokens.ToString(CultureInfo.InvariantCulture),
            preferredOutputLimit: options.OutputTokenLimit.ToString(CultureInfo.InvariantCulture),
            resetToSmallest: false);
    }

    private RuntimeModelChoice? CurrentRuntimeModelChoice()
    {
        if (_runtimeModelChoices.TryGetValue(SelectedRuntimeModelChoice, out var selected))
        {
            return selected;
        }

        return _runtimeModelChoices.Values.FirstOrDefault(choice =>
            choice.Model.Equals(RuntimeModelText, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplySelectedRuntimeModelChoice(string label, bool resetToSmallest)
    {
        SelectRuntimeModelChoice(
            label,
            preferredQuantization: resetToSmallest ? null : RuntimeQuantizationText,
            preferredContext: resetToSmallest ? null : RuntimeContextText,
            preferredOutputLimit: resetToSmallest ? null : RuntimeOutputLimitText,
            resetToSmallest);
    }

    private void SelectRuntimeModelChoice(
        string label,
        string? preferredQuantization,
        string? preferredContext,
        string? preferredOutputLimit,
        bool resetToSmallest)
    {
        if (!_runtimeModelChoices.TryGetValue(label, out var choice))
        {
            return;
        }

        if (!string.Equals(_selectedRuntimeModelChoice, label, StringComparison.Ordinal))
        {
            _selectedRuntimeModelChoice = label;
            OnPropertyChanged(nameof(SelectedRuntimeModelChoice));
        }
        else
        {
            OnPropertyChanged(nameof(SelectedRuntimeModelChoice));
        }

        RuntimeModelText = choice.Model;
        RuntimeEnabled = !string.IsNullOrWhiteSpace(choice.Model);
        RuntimeStreamingEnabled = choice.StreamingEnabled;
        RuntimeVisionEnabled = choice.SupportsVision;

        var quantizationChoices = string.IsNullOrWhiteSpace(preferredQuantization)
            ? choice.Quantizations
            : choice.Quantizations.Append(preferredQuantization.Trim());
        ReplaceChoices(RuntimeQuantizationChoices, quantizationChoices);
        ReplaceChoices(RuntimeContextChoices, choice.ContextTokens.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        ReplaceChoices(RuntimeOutputLimitChoices, choice.OutputTokenLimits.Select(value => value.ToString(CultureInfo.InvariantCulture)));

        RuntimeQuantizationText = PickChoice(RuntimeQuantizationChoices, preferredQuantization, choice.DefaultQuantization, resetToSmallest);
        RuntimeContextText = PickChoice(RuntimeContextChoices, preferredContext, choice.ContextTokens.FirstOrDefault().ToString(CultureInfo.InvariantCulture), resetToSmallest);
        RuntimeOutputLimitText = PickChoice(RuntimeOutputLimitChoices, preferredOutputLimit, choice.OutputTokenLimits.FirstOrDefault().ToString(CultureInfo.InvariantCulture), resetToSmallest);
        RuntimeSelectionStatusText = $"{choice.Source}. Vision: {(choice.SupportsVision ? "yes" : "no")}. Streaming: {(choice.StreamingEnabled ? "yes" : "unknown until health check")}.";
    }

    private void LoadRuntimeModelChoices(IEnumerable<RuntimeModelChoice> choices, string? selectedModel)
    {
        _runtimeModelChoices.Clear();
        RuntimeModelChoices.Clear();

        foreach (var choice in choices.Where(choice => !string.IsNullOrWhiteSpace(choice.Model)))
        {
            AddRuntimeModelChoice(choice);
        }

        if (!string.IsNullOrWhiteSpace(selectedModel) && FindRuntimeModelLabel(selectedModel) is null)
        {
            AddRuntimeModelChoice(RuntimeModelChoice.FromModelId(selectedModel, "Saved runtime setting"));
        }
    }

    private void AddRuntimeModelChoice(RuntimeModelChoice choice)
    {
        var label = choice.Label;
        if (_runtimeModelChoices.ContainsKey(label))
        {
            return;
        }

        _runtimeModelChoices[label] = choice;
        RuntimeModelChoices.Add(label);
    }

    private string? FindRuntimeModelLabel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return _runtimeModelChoices.Values.FirstOrDefault(choice =>
            choice.Model.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase))?.Label;
    }

    private static void ReplaceChoices(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }
    }

    private static void EnsureChoice(ObservableCollection<string> target, string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || target.Any(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        target.Add(value);
    }

    private static string PickChoice(
        ObservableCollection<string> choices,
        string? preferred,
        string fallback,
        bool resetToSmallest)
    {
        if (!resetToSmallest && !string.IsNullOrWhiteSpace(preferred))
        {
            var match = choices.FirstOrDefault(choice => choice.Equals(preferred.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var fallbackMatch = choices.FirstOrDefault(choice => choice.Equals(fallback.Trim(), StringComparison.OrdinalIgnoreCase));
            if (fallbackMatch is not null)
            {
                return fallbackMatch;
            }
        }

        return choices.FirstOrDefault() ?? string.Empty;
    }

    private static async Task<IReadOnlyList<RuntimeModelChoice>> FetchInstalledRuntimeModelChoicesAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "models"));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseRuntimeModelChoices(body);
    }

    private static async Task<RuntimeHealthCheck> CheckRuntimeEndpointStatusAsync(
        OpenAiCompatibleRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        if (!options.Enabled)
        {
            return CreateRuntimeEndpointStatus(
                started,
                succeeded: false,
                options,
                "Local model runtime is disabled.",
                "Local model runtime is disabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return CreateRuntimeEndpointStatus(
                started,
                succeeded: false,
                options,
                "Model/package ID is required before checking a runtime.",
                "Model/package ID is required before checking a runtime.");
        }

        try
        {
            var choices = await FetchInstalledRuntimeModelChoicesAsync(
                options.Endpoint,
                cancellationToken).ConfigureAwait(false);

            if (choices.Any(choice => choice.Model.Equals(options.Model, StringComparison.OrdinalIgnoreCase)))
            {
                return CreateRuntimeEndpointStatus(
                    started,
                    succeeded: true,
                    options,
                    $"Local runtime endpoint responded and listed model '{options.Model}'.");
            }

            var summary = choices.Count == 0
                ? "Local runtime endpoint responded, but no installed models were listed."
                : $"Local runtime endpoint responded, but model '{options.Model}' was not listed.";
            return CreateRuntimeEndpointStatus(started, succeeded: false, options, summary, summary);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            var summary = $"Local runtime endpoint ping failed: {ex.Message}";
            return CreateRuntimeEndpointStatus(started, succeeded: false, options, summary, summary);
        }
    }

    private static RuntimeHealthCheck CreateRuntimeEndpointStatus(
        DateTimeOffset started,
        bool succeeded,
        OpenAiCompatibleRuntimeOptions options,
        string summary,
        string? errorText = null) =>
        new(
            Succeeded: succeeded,
            Summary: summary,
            CheckedAt: DateTimeOffset.UtcNow,
            Elapsed: DateTimeOffset.UtcNow - started,
            Endpoint: options.Endpoint.ToString(),
            ModelPackageId: options.Model,
            ContextTokens: options.ContextTokens,
            OutputTokenLimit: options.OutputTokenLimit,
            Temperature: options.Temperature,
            ErrorText: errorText);

    private static IReadOnlyList<RuntimeModelChoice> ParseRuntimeModelChoices(string json)
    {
        using var document = JsonDocument.Parse(json);
        var choices = new List<RuntimeModelChoice>();

        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var choice = RuntimeModelChoice.FromJsonModel(item);
                if (choice is not null)
                {
                    choices.Add(choice);
                }
            }
        }

        if (document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                var choice = RuntimeModelChoice.FromJsonModel(item);
                if (choice is not null)
                {
                    choices.Add(choice);
                }
            }
        }

        return choices
            .GroupBy(choice => choice.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<RuntimeModelChoice> CreateKnownRuntimeModelChoices() =>
    [
        RuntimeModelChoice.FromModelId("qwen3:1.7b", "Known Qwen option"),
        RuntimeModelChoice.FromModelId("qwen3:4b", "Known Qwen option"),
        RuntimeModelChoice.FromModelId("qwen3:8b", "Known Qwen option"),
        RuntimeModelChoice.FromModelId("qwen3:14b", "Known Qwen option"),
        RuntimeModelChoice.FromModelId("qwen3:32b", "Known Qwen option"),
        RuntimeModelChoice.FromModelId("qwen3-vl:8b", "Known vision option")
    ];

    private static bool LooksLikeVisionModel(string model) =>
        model.Contains("vl", StringComparison.OrdinalIgnoreCase)
        || model.Contains("vision", StringComparison.OrdinalIgnoreCase)
        || model.Contains("visual", StringComparison.OrdinalIgnoreCase);

    private static string FormatHealthResult(RuntimeHealthCheck health)
    {
        var streaming = health.StreamingSupported is null
            ? "not checked"
            : health.StreamingSupported.Value ? "yes" : "no";

        return $"{health.Summary}\nEndpoint: {health.Endpoint ?? "n/a"}\nModel: {health.ModelPackageId ?? "n/a"}\nElapsed: {health.Elapsed.TotalMilliseconds:N0} ms\nStreaming supported: {streaming}"
            + (string.IsNullOrWhiteSpace(health.ErrorText) ? string.Empty : $"\nFailure detail: {health.ErrorText}");
    }

    private void UpdateLastSttDebugText()
    {
        if (_services.SpeechToText is not WhisperCliSpeechToTextProvider { LastDebugInfo: { } debug })
        {
            return;
        }

        LastSttDebugText =
            $"Whisper debug: exit {debug.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | elapsed {debug.Elapsed.TotalMilliseconds:N0} ms | exe {debug.ExecutablePath ?? "n/a"} | model {debug.ModelPath ?? "n/a"} | wav {debug.InputAudioPath} | transcript \"{debug.Transcript}\" | stderr {debug.StandardError}";
    }

    private void UpdateRuntimeStatus()
    {
        RuntimeDisplay = FormatRuntimeDisplay();
        CanRevertToLastKnownGood = _services.RuntimeController.CanRevertToLastKnownGood;
        ActiveRuntimeStatus = _services.RuntimeController.IsUsingFallback
            ? "Active runtime: deterministic stub"
            : $"Active runtime: {_services.RuntimeController.ActiveProfile.PackageId}";
        if (_services.RuntimeController.IsUsingFallback
            && !ModelConnectionStatusText.Contains("waiting", StringComparison.OrdinalIgnoreCase))
        {
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
        }
    }

    private void SetModelConnectionStatus(string text, System.Windows.Media.Brush brush)
    {
        ModelConnectionStatusText = text;
        ModelConnectionStatusBrush = brush;
    }

    private bool IsModelConnectedStatus() =>
        string.Equals(ModelConnectionStatusText, "connected to model", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRuntimeCommunicationFailure(string text) =>
        text.Contains("local model runtime returned HTTP", StringComparison.OrdinalIgnoreCase)
        || text.Contains("local model communication failed", StringComparison.OrdinalIgnoreCase);

    private void RaiseCommandStates()
    {
        if (SendCommand is AsyncRelayCommand send)
        {
            send.RaiseCanExecuteChanged();
        }

        if (StopCommand is RelayCommand stop)
        {
            stop.RaiseCanExecuteChanged();
        }

        if (CheckRuntimeCommand is AsyncRelayCommand checkRuntime)
        {
            checkRuntime.RaiseCanExecuteChanged();
        }

        if (RefreshRuntimeModelsCommand is AsyncRelayCommand refreshModels)
        {
            refreshModels.RaiseCanExecuteChanged();
        }

        if (ActivateRuntimeCommand is RelayCommand activate)
        {
            activate.RaiseCanExecuteChanged();
        }

        if (RevertToStubCommand is RelayCommand revertStub)
        {
            revertStub.RaiseCanExecuteChanged();
        }

        if (RevertToLastKnownGoodCommand is RelayCommand revertLastKnownGood)
        {
            revertLastKnownGood.RaiseCanExecuteChanged();
        }

        if (SendTranscriptCommand is AsyncRelayCommand sendTranscript)
        {
            sendTranscript.RaiseCanExecuteChanged();
        }

        if (StopSpeakingCommand is RelayCommand stopSpeaking)
        {
            stopSpeaking.RaiseCanExecuteChanged();
        }

        if (PlayPiperSampleCommand is AsyncRelayCommand playPiperSample)
        {
            playPiperSample.RaiseCanExecuteChanged();
        }

        if (MarkCorrectionReviewedCommand is AsyncRelayCommand markCorrectionReviewed)
        {
            markCorrectionReviewed.RaiseCanExecuteChanged();
        }

        if (MarkCorrectionUnresolvedCommand is AsyncRelayCommand markCorrectionUnresolved)
        {
            markCorrectionUnresolved.RaiseCanExecuteChanged();
        }

        if (ExportSelectedCorrectionCommand is AsyncRelayCommand exportSelectedCorrection)
        {
            exportSelectedCorrection.RaiseCanExecuteChanged();
        }

        if (DeleteSelectedMemoryCommand is RelayCommand deleteMemory)
        {
            deleteMemory.RaiseCanExecuteChanged();
        }

        if (CancelSelectedReminderCommand is RelayCommand cancelReminder)
        {
            cancelReminder.RaiseCanExecuteChanged();
        }

        if (CompleteSelectedReminderCommand is RelayCommand completeReminder)
        {
            completeReminder.RaiseCanExecuteChanged();
        }
    }

    private string FormatRuntimeDisplay()
    {
        var profile = _services.Orchestrator.Runtime.ActiveProfile;
        return $"{profile.DisplayName} | {profile.Quantization} | {profile.ContextTokens:N0} ctx";
    }

    private void LoadVoiceDevices()
    {
        VoiceInputDevices.Clear();
        IReadOnlyList<AudioInputDevice> inputDevices;
        try
        {
            inputDevices = NAudioVoiceRecorder.GetInputDevices();
        }
        catch (Exception ex)
        {
            inputDevices = Array.Empty<AudioInputDevice>();
            VoiceStatus = $"Input device list unavailable: {ex.Message}";
        }

        if (inputDevices.Count == 0)
        {
            VoiceInputDevices.Add("0: Default microphone");
        }
        else
        {
            foreach (var device in inputDevices)
            {
                VoiceInputDevices.Add($"{device.DeviceNumber}: {device.Name}");
            }
        }

        var inputSelection = VoiceDeviceSelection.ResolveInput(_voiceSettings, inputDevices);
        SelectedVoiceInputDevice = VoiceInputDevices.FirstOrDefault(
            device => device.StartsWith($"{inputSelection.DeviceNumber}:", StringComparison.Ordinal))
            ?? VoiceInputDevices[0];
        if (!string.IsNullOrWhiteSpace(inputSelection.Warning))
        {
            VoiceStatus = inputSelection.Warning;
        }

        VoiceOutputDevices.Clear();
        IReadOnlyList<AudioOutputDevice> outputDevices;
        try
        {
            outputDevices = NAudioWaveSpeechPlayer.GetOutputDevices();
        }
        catch (Exception ex)
        {
            outputDevices = new[] { new AudioOutputDevice(-1, "Default playback device") };
            VoiceStatus = $"Output device list unavailable: {ex.Message}";
        }

        foreach (var device in outputDevices)
        {
            VoiceOutputDevices.Add($"{device.DeviceNumber}: {device.Name}");
        }

        if (VoiceOutputDevices.Count == 0)
        {
            VoiceOutputDevices.Add("-1: Default playback device");
        }

        var outputSelection = VoiceDeviceSelection.ResolveOutput(_voiceSettings, outputDevices);
        SelectedVoiceOutputDevice = VoiceOutputDevices.FirstOrDefault(
            device => device.StartsWith($"{outputSelection.DeviceNumber}:", StringComparison.Ordinal))
            ?? VoiceOutputDevices[0];
        if (!string.IsNullOrWhiteSpace(outputSelection.Warning))
        {
            VoiceStatus = outputSelection.Warning;
        }
    }

    private void ApplyVoiceInputDevice(string selectedDevice)
    {
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder
            && TryReadDeviceNumber(selectedDevice, out var deviceNumber))
        {
            recorder.InputDeviceNumber = deviceNumber;
            RefreshVoiceInputChannelModes();
            SaveVoiceSettings(selectedInputDeviceNumber: deviceNumber, selectedInputDeviceName: CurrentInputDeviceName());
            StartInputLevelMonitor();
        }
    }

    private void ApplyVoiceOutputDevice(string selectedDevice)
    {
        if (_services.SpeechPlayer is NAudioWaveSpeechPlayer player
            && TryReadDeviceNumber(selectedDevice, out var deviceNumber))
        {
            player.OutputDeviceNumber = deviceNumber;
            SaveVoiceSettings(selectedOutputDeviceNumber: deviceNumber, selectedOutputDeviceName: CurrentOutputDeviceName());
        }
    }

    private void ApplyVoiceInputPreset(string presetName)
    {
        var settings = BuildCurrentProcessorSettings(presetName);
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.ProcessorSettings = settings;
        }

        _inputLevelMonitor.ProcessorSettings = settings;
        SaveVoiceSettings(selectedInputPreset: presetName);
    }

    private VoiceProcessorSettings BuildCurrentProcessorSettings(string presetName)
    {
        var settings = VoiceInputPreset.CreateSettings(presetName);
        return settings with { MakeupGainDb = Math.Clamp(settings.MakeupGainDb + ExtraInputGainDb, -12d, 30d) };
    }

    private void ApplyVoiceInputChannelMode(string selectedMode)
    {
        var channelMode = InputChannelModeCatalog.FromLabel(selectedMode);
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.ChannelMode = channelMode;
        }

        _inputLevelMonitor.ChannelMode = channelMode;
        SaveVoiceSettings(selectedInputChannelMode: channelMode.ToString());
        StartInputLevelMonitor();
    }

    private void RefreshVoiceInputChannelModes()
    {
        var preferredMode = _loadingVoiceSettings
            ? InputChannelModeCatalog.FromStorageValue(_voiceSettings.SelectedInputChannelMode)
            : InputChannelModeCatalog.FromLabel(SelectedVoiceInputChannelMode);
        var labels = InputChannelModeCatalog.CreateLabels(CurrentInputDeviceChannelCount());

        VoiceInputChannelModes.Clear();
        foreach (var label in labels)
        {
            VoiceInputChannelModes.Add(label);
        }

        var preferredLabel = InputChannelModeCatalog.ToLabel(preferredMode);
        var selectedLabel = VoiceInputChannelModes.Contains(preferredLabel)
            ? preferredLabel
            : InputChannelModeCatalog.MonoSumLabel;
        if (SelectedVoiceInputChannelMode == selectedLabel)
        {
            ApplyVoiceInputChannelMode(selectedLabel);
            return;
        }

        SelectedVoiceInputChannelMode = selectedLabel;
    }

    private void InputLevelAvailable(object? sender, VoiceInputLevelSnapshot snapshot)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyInputLevelSnapshot(snapshot);
            return;
        }

        _ = dispatcher.BeginInvoke(() => ApplyInputLevelSnapshot(snapshot));
    }

    private void ApplyInputLevelSnapshot(VoiceInputLevelSnapshot snapshot)
    {
        VoiceInputLevelPercent = snapshot.LevelPercent;
        VoiceInputMeterText = $"{snapshot.Summary} Peak {snapshot.Peak:P0}, RMS {snapshot.Rms:P1}.";
        VoiceDiagnosticsText = $"{snapshot.DeviceName} | {snapshot.SampleRate} Hz | {snapshot.Channels} ch | {snapshot.State}";
    }

    private void SpectrumAvailable(object? sender, SpectrumFrame frame)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplySpectrumFrame(frame);
            return;
        }

        _ = dispatcher.BeginInvoke(() => ApplySpectrumFrame(frame));
    }

    private void ApplySpectrumFrame(SpectrumFrame frame)
    {
        if (frame.Magnitudes.Length == 0)
        {
            return;
        }

        _lastSpectrumMagnitudes = frame.Magnitudes.ToArray();
        EnsureSpectrumRenderBuffers(frame.Magnitudes.Length);
        var frameCeiling = Math.Max(0.08d, frame.Magnitudes.Select(ShapeSpectrumMagnitude).DefaultIfEmpty(0.08d).Max());
        _spectrumVisualCeiling = frameCeiling > _spectrumVisualCeiling
            ? Ease(_spectrumVisualCeiling, frameCeiling, 0.10d)
            : Ease(_spectrumVisualCeiling, frameCeiling, 0.025d);

        for (var index = 0; index < frame.Magnitudes.Length; index++)
        {
            var visualMagnitude = NormalizeSpectrumForDisplay(ShapeSpectrumMagnitude(frame.Magnitudes[index]));
            _renderedSpectrumMagnitudes[index] = Ease(_renderedSpectrumMagnitudes[index], visualMagnitude, 0.14d);
        }

        _lastSpectrumPeakLevel = frame.PeakLevel;
        SpectrumPeakText = $"Peak {frame.PeakLevel:P0}";
        RefreshSpectrumPoints();
    }

    private void RefreshSpectrumPoints()
    {
        SpectrumLivePoints = SmoothSpectrumPoints(CreateSpectrumPoints(_renderedSpectrumMagnitudes));
        SpectrumPeakText = $"Peak {_lastSpectrumPeakLevel:P0}";
    }

    private void StartInputLevelMonitor()
    {
        if (!_voiceMonitorRequested || _suppressInputMonitorRestart)
        {
            return;
        }

        if (IsRecording || !TryReadDeviceNumber(SelectedVoiceInputDevice, out var deviceNumber))
        {
            return;
        }

        try
        {
            _inputLevelMonitor.Start(deviceNumber, CurrentInputDeviceName());
        }
        catch (Exception ex)
        {
            VoiceInputLevelPercent = 0;
            VoiceInputMeterText = $"Input meter unavailable: {ex.Message}";
        }
    }

    private void StopInputLevelMonitor() => _inputLevelMonitor.Stop();

    private void SubscribeRecorderLevels()
    {
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.LevelAvailable += InputLevelAvailable;
            recorder.SpectrumAvailable += SpectrumAvailable;
        }
    }

    private void UnsubscribeRecorderLevels()
    {
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.LevelAvailable -= InputLevelAvailable;
            recorder.SpectrumAvailable -= SpectrumAvailable;
        }
    }

    private VoiceCaptureDiagnostics? UpdateCaptureDiagnostics(VoiceAudioInput audioInput)
    {
        try
        {
            var diagnostics = VoiceAudioFileAnalyzer.AnalyzeWaveAudio(
                audioInput.FilePath,
                CurrentInputDeviceNumber(),
                CurrentInputDeviceName());
            _lastCaptureDiagnostics = diagnostics;
            VoiceDiagnosticsText = diagnostics.Summary;
            ApplyInputLevelSnapshot(diagnostics.Level);
            return diagnostics;
        }
        catch (Exception ex)
        {
            VoiceDiagnosticsText = $"Capture diagnostics unavailable: {ex.Message}";
            return null;
        }
    }

    private void SaveLastSuccessfulSttDevice() =>
        SaveVoiceSettings(
            lastSuccessfulSttDeviceNumber: CurrentInputDeviceNumber(),
            lastSuccessfulSttDeviceName: CurrentInputDeviceName());

    private void SaveLastSuccessfulTtsDevice() =>
        SaveVoiceSettings(
            lastSuccessfulTtsDeviceNumber: CurrentOutputDeviceNumber(),
            lastSuccessfulTtsDeviceName: CurrentOutputDeviceName());

    private void SaveVoiceSettings(
        int? selectedInputDeviceNumber = null,
        string? selectedInputDeviceName = null,
        int? selectedOutputDeviceNumber = null,
        string? selectedOutputDeviceName = null,
        int? lastSuccessfulSttDeviceNumber = null,
        string? lastSuccessfulSttDeviceName = null,
        int? lastSuccessfulTtsDeviceNumber = null,
        string? lastSuccessfulTtsDeviceName = null,
        string? selectedInputPreset = null,
        string? selectedInputChannelMode = null,
        double? extraInputGainDb = null,
        bool? normalizeBeforeStt = null,
        bool? retainDebugAudio = null,
        bool? autoSendVoiceTranscripts = null,
        string? pushToTalkKey = null)
    {
        if (_loadingVoiceSettings)
        {
            return;
        }

        _voiceSettings = _voiceSettings with
        {
            SelectedInputDeviceNumber = selectedInputDeviceNumber ?? _voiceSettings.SelectedInputDeviceNumber,
            SelectedInputDeviceName = selectedInputDeviceName ?? _voiceSettings.SelectedInputDeviceName,
            SelectedOutputDeviceNumber = selectedOutputDeviceNumber ?? _voiceSettings.SelectedOutputDeviceNumber,
            SelectedOutputDeviceName = selectedOutputDeviceName ?? _voiceSettings.SelectedOutputDeviceName,
            LastSuccessfulSttDeviceNumber = lastSuccessfulSttDeviceNumber ?? _voiceSettings.LastSuccessfulSttDeviceNumber,
            LastSuccessfulSttDeviceName = lastSuccessfulSttDeviceName ?? _voiceSettings.LastSuccessfulSttDeviceName,
            LastSuccessfulTtsDeviceNumber = lastSuccessfulTtsDeviceNumber ?? _voiceSettings.LastSuccessfulTtsDeviceNumber,
            LastSuccessfulTtsDeviceName = lastSuccessfulTtsDeviceName ?? _voiceSettings.LastSuccessfulTtsDeviceName,
            SelectedInputPreset = VoiceInputPreset.Normalize(selectedInputPreset ?? _voiceSettings.SelectedInputPreset),
            SelectedInputChannelMode = selectedInputChannelMode ?? _voiceSettings.SelectedInputChannelMode,
            ExtraInputGainDb = extraInputGainDb ?? _voiceSettings.ExtraInputGainDb,
            NormalizeBeforeStt = normalizeBeforeStt ?? _voiceSettings.NormalizeBeforeStt,
            RetainDebugAudio = retainDebugAudio ?? _voiceSettings.RetainDebugAudio,
            AutoSendVoiceTranscripts = autoSendVoiceTranscripts ?? _voiceSettings.AutoSendVoiceTranscripts,
            PushToTalkKey = NormalizePushToTalkKey(pushToTalkKey ?? _voiceSettings.PushToTalkKey)
        };

        VoiceRuntimeSettingsStore.Save(_services.DataRoot, _voiceSettings);
    }

    private static bool TryParsePushToTalkKey(string? value, out Key key)
    {
        var normalized = NormalizePushToTalkKey(value);
        return Enum.TryParse(normalized, ignoreCase: true, out key);
    }

    private static string NormalizePushToTalkKey(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "NumPad0" : value.Trim();
        return text.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant() switch
        {
            "KEYPAD0" or "NUMPAD0" or "NUM0" => nameof(Key.NumPad0),
            "KEYPAD1" or "NUMPAD1" or "NUM1" => nameof(Key.NumPad1),
            "KEYPAD2" or "NUMPAD2" or "NUM2" => nameof(Key.NumPad2),
            "KEYPAD3" or "NUMPAD3" or "NUM3" => nameof(Key.NumPad3),
            "KEYPAD4" or "NUMPAD4" or "NUM4" => nameof(Key.NumPad4),
            "KEYPAD5" or "NUMPAD5" or "NUM5" => nameof(Key.NumPad5),
            "KEYPAD6" or "NUMPAD6" or "NUM6" => nameof(Key.NumPad6),
            "KEYPAD7" or "NUMPAD7" or "NUM7" => nameof(Key.NumPad7),
            "KEYPAD8" or "NUMPAD8" or "NUM8" => nameof(Key.NumPad8),
            "KEYPAD9" or "NUMPAD9" or "NUM9" => nameof(Key.NumPad9),
            _ when Enum.TryParse<Key>(text, ignoreCase: true, out var key) => key.ToString(),
            _ => nameof(Key.NumPad0)
        };
    }

    private static string FormatPushToTalkKeyLabel(string value) =>
        value switch
        {
            nameof(Key.NumPad0) => "Keypad 0",
            nameof(Key.NumPad1) => "Keypad 1",
            nameof(Key.NumPad2) => "Keypad 2",
            nameof(Key.NumPad3) => "Keypad 3",
            nameof(Key.NumPad4) => "Keypad 4",
            nameof(Key.NumPad5) => "Keypad 5",
            nameof(Key.NumPad6) => "Keypad 6",
            nameof(Key.NumPad7) => "Keypad 7",
            nameof(Key.NumPad8) => "Keypad 8",
            nameof(Key.NumPad9) => "Keypad 9",
            _ => value
        };

    private int CurrentInputDeviceNumber() =>
        TryReadDeviceNumber(SelectedVoiceInputDevice, out var deviceNumber) ? deviceNumber : 0;

    private int CurrentOutputDeviceNumber() =>
        TryReadDeviceNumber(SelectedVoiceOutputDevice, out var deviceNumber) ? deviceNumber : -1;

    private int CurrentInputDeviceChannelCount() =>
        TryReadDeviceNumber(SelectedVoiceInputDevice, out var deviceNumber)
            ? NAudioVoiceRecorder.GetInputDeviceChannelCount(deviceNumber)
            : 1;

    private InputChannelMode CurrentInputChannelMode() =>
        InputChannelModeCatalog.FromLabel(SelectedVoiceInputChannelMode);

    private string CurrentInputDeviceName() => ReadDeviceName(SelectedVoiceInputDevice);

    private string CurrentOutputDeviceName() => ReadDeviceName(SelectedVoiceOutputDevice);

    private string CurrentSpeechToTextModel() =>
        _services.SpeechToText is WhisperCliSpeechToTextProvider whisper ? whisper.ModelPath : string.Empty;

    private string CurrentTextToSpeechModel() =>
        _services.TextToSpeech is PiperCliTextToSpeechProvider piper ? piper.ModelPath : string.Empty;

    private VoiceTurnMetadata CreateVoiceMetadata(
        string? transcript,
        VoiceAudioInput? audioInput,
        bool suspiciousOrNoSpeech,
        string? rejectionReason)
    {
        var level = _lastCaptureDiagnostics?.Level;
        return new VoiceTurnMetadata(
            VoiceInputOrigin.Voice,
            transcript,
            _services.SpeechToText.ProviderName,
            _services.SpeechToText.Mode,
            _services.TextToSpeech.ProviderName,
            _services.TextToSpeech.VoiceId,
            audioInput?.RetainAudio ?? false,
            CurrentInputDeviceNumber(),
            CurrentInputDeviceName(),
            InputChannelModeCatalog.ToLabel(CurrentInputChannelMode()),
            SelectedVoiceInputPreset,
            ExtraInputGainDb,
            NormalizeBeforeStt,
            CurrentSpeechToTextModel(),
            CurrentTextToSpeechModel(),
            suspiciousOrNoSpeech,
            rejectionReason,
            level?.Peak,
            level?.Rms,
            level?.State.ToString());
    }

    private static PointCollection CreateFlatSpectrumPoints() =>
        CreateSpectrumPoints(new double[SpectrumAnalyzer.BarCount]);

    private static PointCollection CreateSpectrumPoints(IReadOnlyList<double> magnitudes)
    {
        var points = new PointCollection();
        if (magnitudes.Count == 0)
        {
            return points;
        }

        var denominator = Math.Max(1, magnitudes.Count - 1);
        for (var index = 0; index < magnitudes.Count; index++)
        {
            var x = index * (SpectrumRenderWidth / denominator);
            var level = Math.Clamp(magnitudes[index], 0d, 1d);
            var graphBottom = Math.Max(SpectrumRenderInset + 1d, SpectrumRenderHeight - SpectrumRenderInset);
            var usableHeight = graphBottom - SpectrumRenderInset;
            var y = graphBottom - (usableHeight * level);
            points.Add(new System.Windows.Point(x, y));
        }

        return points;
    }

    private void EnsureSpectrumRenderBuffers(int length)
    {
        if (_renderedSpectrumMagnitudes.Length == length)
        {
            return;
        }

        _renderedSpectrumMagnitudes = new double[length];
    }

    private static double ShapeSpectrumMagnitude(double magnitude)
    {
        var lifted = Math.Max(0d, magnitude - 0.01d);
        return Math.Clamp(Math.Pow(lifted, 0.9d), 0d, 1d);
    }

    private double NormalizeSpectrumForDisplay(double magnitude) =>
        Math.Clamp(
            magnitude / Math.Max(0.08d, _spectrumVisualCeiling) * 0.78d * SpectrumDisplayGain / 6d,
            0d,
            0.92d);

    private static double Ease(double current, double target, double amount) =>
        current + ((target - current) * amount);

    private static PointCollection SmoothSpectrumPoints(PointCollection source)
    {
        if (source.Count < 4)
        {
            return source;
        }

        var smoothed = new PointCollection(source.Count * 2);
        smoothed.Add(source[0]);

        for (var index = 1; index < source.Count - 2; index++)
        {
            var p0 = source[index - 1];
            var p1 = source[index];
            var p2 = source[index + 1];
            var p3 = source[index + 2];

            smoothed.Add(p1);
            smoothed.Add(CatmullRom(p0, p1, p2, p3, 0.5d));
        }

        smoothed.Add(source[^2]);
        smoothed.Add(source[^1]);
        return smoothed;
    }

    private static System.Windows.Point CatmullRom(
        System.Windows.Point p0,
        System.Windows.Point p1,
        System.Windows.Point p2,
        System.Windows.Point p3,
        double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        var x = 0.5d * ((2d * p1.X) + ((-p0.X + p2.X) * t) + ((2d * p0.X - 5d * p1.X + 4d * p2.X - p3.X) * t2) + ((-p0.X + 3d * p1.X - 3d * p2.X + p3.X) * t3));
        var y = 0.5d * ((2d * p1.Y) + ((-p0.Y + p2.Y) * t) + ((2d * p0.Y - 5d * p1.Y + 4d * p2.Y - p3.Y) * t2) + ((-p0.Y + 3d * p1.Y - 3d * p2.Y + p3.Y) * t3));
        return new System.Windows.Point(x, y);
    }

    private static string PreferConfigured(string? configured, string? fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback ?? string.Empty : configured.Trim();

    private static string PreferValidConfiguredPath(string? configured, string? fallback)
    {
        var resolved = ResolvePortablePath(configured);
        return !string.IsNullOrWhiteSpace(configured) && LocalPathExists(resolved)
            ? configured.Trim()
            : fallback ?? string.Empty;
    }

    private static string PreferPiperExecutablePath(string? configured, string? fallback)
    {
        var resolved = ResolvePortablePath(configured);
        if (!string.IsNullOrWhiteSpace(configured)
            && LocalPathExists(resolved)
            && !IsGeneratedPiperShimPath(resolved))
        {
            return configured.Trim();
        }

        return fallback ?? string.Empty;
    }

    private static string PreferWhisperArgumentsTemplate(
        string? configured,
        string? localWhisperArguments,
        string fallback)
    {
        var configuredTrim = NullIfWhiteSpace(configured);
        if (configuredTrim is null)
        {
            return PreferConfigured(localWhisperArguments, fallback);
        }

        return configuredTrim.Contains("local_whisper_stt.py", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(localWhisperArguments)
                ? localWhisperArguments
                : configuredTrim;
    }

    private static string PreferPiperArgumentsTemplate(
        string? configured,
        string localPiperArguments,
        string fallback)
    {
        var configuredTrim = NullIfWhiteSpace(configured);
        if (configuredTrim is null)
        {
            return PreferConfigured(localPiperArguments, fallback);
        }

        return IsGeneratedPiperShimArguments(configuredTrim)
            ? localPiperArguments
            : configuredTrim;
    }

    private static bool IsGeneratedPiperShimPath(string? value)
    {
        var resolved = ResolvePortablePath(value);
        return !string.IsNullOrWhiteSpace(resolved)
            && Path.GetFileName(resolved).Equals("piper.exe", StringComparison.OrdinalIgnoreCase)
            && resolved.Contains($"{Path.DirectorySeparatorChar}python-venv{Path.DirectorySeparatorChar}Scripts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedPiperShimArguments(string value) =>
        !value.Contains("-m piper", StringComparison.OrdinalIgnoreCase)
        && value.Contains("--model", StringComparison.OrdinalIgnoreCase)
        && value.Contains("--output_file", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetProcessEnvironment(string variableName, string? value) =>
        Environment.SetEnvironmentVariable(variableName, NullIfWhiteSpace(value), EnvironmentVariableTarget.Process);

    private static string AppBaseDirectory => Path.GetFullPath(AppContext.BaseDirectory);

    private static string? ResolvePortablePath(string? value) =>
        LocalVoiceResourceLocator.ResolvePath(AppBaseDirectory, value);

    private static string? ToPortablePath(string? value) =>
        LocalVoiceResourceLocator.ToPortablePath(AppBaseDirectory, value);

    private static string? FindLocalPiperExecutable()
    {
        var candidate = LocalVoiceResourceLocator.FindPythonExecutable(AppBaseDirectory);
        return File.Exists(candidate) ? ToPortablePath(candidate) : null;
    }

    private static string? FindLocalPiperVoiceDirectory()
    {
        var candidate = LocalVoiceResourceLocator.FindPiperVoiceDirectory(AppBaseDirectory);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? FindLocalVoiceResourceDirectory()
    {
        return LocalVoiceResourceLocator.FindVoiceRoot(AppBaseDirectory);
    }

    private static bool LocalPathExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    private static string FormatPiperVoiceLabel(string voiceId)
    {
        var name = voiceId.StartsWith("en_US-", StringComparison.OrdinalIgnoreCase)
            ? voiceId["en_US-".Length..]
            : voiceId;
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return voiceId;
        }

        var quality = parts[^1];
        var baseName = string.Join(" ", parts[..^1]).Replace('_', ' ');
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = quality;
            quality = string.Empty;
        }

        var label = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(baseName);
        return string.IsNullOrWhiteSpace(quality)
            ? label
            : $"{label} ({quality})";
    }

    private static string ReadDeviceName(string selectedDevice)
    {
        var separatorIndex = selectedDevice.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex >= 0 && separatorIndex + 1 < selectedDevice.Length
            ? selectedDevice[(separatorIndex + 1)..].Trim()
            : selectedDevice.Trim();
    }

    private static bool TryReadDeviceNumber(string selectedDevice, out int deviceNumber)
    {
        deviceNumber = 0;
        var separatorIndex = selectedDevice.IndexOf(':', StringComparison.Ordinal);
        var numberText = separatorIndex >= 0
            ? selectedDevice[..separatorIndex]
            : selectedDevice;

        return int.TryParse(numberText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out deviceNumber);
    }
}

internal sealed record PiperVoiceChoice(string Label, string VoiceId, string ModelPath);

internal sealed record RuntimeModelChoice(
    string Model,
    string DisplayName,
    string Family,
    string Size,
    IReadOnlyList<string> Quantizations,
    IReadOnlyList<int> ContextTokens,
    IReadOnlyList<int> OutputTokenLimits,
    bool StreamingEnabled,
    bool SupportsVision,
    string Source)
{
    public string Label => $"{DisplayName} ({Model})";

    public string DefaultQuantization => Quantizations.FirstOrDefault() ?? "Installed package default";

    public static RuntimeModelChoice FromOptions(OpenAiCompatibleRuntimeOptions options) =>
        FromModelId(
            options.Model,
            "Saved runtime setting",
            displayName: options.DisplayName,
            family: options.Family,
            size: options.Size,
            quantization: options.Quantization,
            streamingEnabled: options.StreamingEnabled,
            supportsVision: options.SupportsVision,
            contextTokens: options.ContextTokens,
            outputTokenLimit: options.OutputTokenLimit);

    public static RuntimeModelChoice? FromJsonModel(JsonElement item)
    {
        var model = ReadStringProperty(item, "id", "name", "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        JsonElement? details = TryGetProperty(item, "details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.Object
            ? detailsElement
            : null;

        var family = details is { } modelDetails
            ? ReadStringProperty(modelDetails, "family", "families")
            : null;
        var size = details is { } sizeDetails
            ? ReadStringProperty(sizeDetails, "parameter_size", "size")
            : null;
        var quantization = details is { } quantDetails
            ? ReadStringProperty(quantDetails, "quantization_level", "quantization")
            : null;

        return FromModelId(
            model,
            "Installed local runtime model",
            family: family,
            size: size,
            quantization: quantization);
    }

    public static RuntimeModelChoice FromModelId(
        string model,
        string source,
        string? displayName = null,
        string? family = null,
        string? size = null,
        string? quantization = null,
        bool streamingEnabled = true,
        bool? supportsVision = null,
        int? contextTokens = null,
        int? outputTokenLimit = null)
    {
        var normalizedModel = model.Trim();
        var inferredSize = string.IsNullOrWhiteSpace(size) ? InferSize(normalizedModel) : size.Trim();
        var inferredFamily = string.IsNullOrWhiteSpace(family) ? InferFamily(normalizedModel) : family.Trim();
        var inferredVision = supportsVision
            ?? (normalizedModel.Contains("vl", StringComparison.OrdinalIgnoreCase)
                || normalizedModel.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || normalizedModel.Contains("visual", StringComparison.OrdinalIgnoreCase));
        var contextChoices = BuildContextChoices(normalizedModel, contextTokens);
        var outputChoices = BuildOutputChoices(outputTokenLimit);
        var quantizationChoices = new[]
        {
            string.IsNullOrWhiteSpace(quantization) ? "Installed package default" : quantization.Trim()
        };

        return new RuntimeModelChoice(
            normalizedModel,
            string.IsNullOrWhiteSpace(displayName) ? InferDisplayName(normalizedModel, inferredSize) : displayName.Trim(),
            inferredFamily,
            inferredSize,
            quantizationChoices,
            contextChoices,
            outputChoices,
            streamingEnabled,
            inferredVision,
            source);
    }

    private static IReadOnlyList<int> BuildContextChoices(string model, int? preferred)
    {
        var lower = model.ToLowerInvariant();
        var values = lower.Contains("32b", StringComparison.Ordinal)
            ? new[] { 2048, 4096 }
            : lower.Contains("1.7b", StringComparison.Ordinal) || lower.Contains("4b", StringComparison.Ordinal)
                ? new[] { 1024, 2048, 4096, 8192 }
                : new[] { 2048, 4096, 8192 };

        return AddPreferred(values, preferred, minimum: 512);
    }

    private static IReadOnlyList<int> BuildOutputChoices(int? preferred) =>
        AddPreferred([128, 256, 512], preferred, minimum: 1);

    private static IReadOnlyList<int> AddPreferred(IReadOnlyList<int> values, int? preferred, int minimum)
    {
        var set = new SortedSet<int>(values);
        if (preferred.HasValue && preferred.Value >= minimum)
        {
            set.Add(preferred.Value);
        }

        return set.ToList();
    }

    private static string InferDisplayName(string model, string size)
    {
        var lower = model.ToLowerInvariant();
        if (lower.Contains("qwen3-vl", StringComparison.Ordinal))
        {
            return $"Qwen3 VL {size}";
        }

        if (lower.Contains("qwen3", StringComparison.Ordinal))
        {
            return $"Qwen3 {size}";
        }

        return model;
    }

    private static string InferFamily(string model)
    {
        var lower = model.ToLowerInvariant();
        if (lower.Contains("qwen", StringComparison.Ordinal))
        {
            return "Qwen";
        }

        return "local";
    }

    private static string InferSize(string model)
    {
        foreach (var size in new[] { "1.7B", "4B", "8B", "14B", "16B", "32B" })
        {
            if (model.Contains(size, StringComparison.OrdinalIgnoreCase))
            {
                return size;
            }
        }

        return "unknown";
    }

    private static string? ReadStringProperty(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(item, name, out var property))
            {
                if (property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }

                if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    return property.ToString();
                }

                if (property.ValueKind == JsonValueKind.Array)
                {
                    var first = property.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                    {
                        return first.GetString();
                    }
                }
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement item, string name, out JsonElement property)
    {
        foreach (var candidate in item.EnumerateObject())
        {
            if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

}
