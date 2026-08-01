using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ali.Modules.Conversation;
using Ali.Modules.ConversationBridge;
using Ali.Modules.Coordinator;
using Ali.Modules.Evidence;
using Ali.Modules.Feedback;
using Ali.Modules.Memory;
using Ali.Modules;
using Ali.Modules.Reminders;
using Ali.Modules.Runtime;
using Ali.Modules.Voice;
using Ali.Modules.Internet;
using Ali.Modules.Permissions;
using Ali.Modules.About;
using Ali.Modules.Integrations;
using Ali.Modules.RAG;
using Ali.Modules.Storage;
using Ali.Modules.Interaction;
using Ali.Modules.Identity;
using Ali.Modules.UserMemory;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Webcam.Common;
using Ali.UI;
using Ali;
using MediaBrushes = System.Windows.Media.Brushes;

namespace Ali.UI.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const string RuntimeTopPModelDefault = "Model default";
    private const int StreamingTextFlushCharacters = 32;
    private const int StreamingTextDisplaySliceCharacters = 72;
    private const string PermissionAllowed = "allowed";
    private const string PermissionAskFirst = "ask-first";
    private const string PermissionConfirmEachTime = "confirm-each-time";
    private const string PermissionExtraConfirmation = "extra-confirmation";
    private const string PermissionBlocked = "blocked";
    private const string PermissionDisabled = "disabled";
    private static readonly TimeSpan ModelStatusPingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OllamaStartRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StreamingTextFlushInterval = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan StreamingTextPaceDelay = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan VoicePlaybackEchoCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly JsonSerializerOptions MaintenanceReceiptJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RuntimeTemperatureChoiceValues = ["0", "0.1", "0.2", "0.3", "0.5", "0.7", "1", "1.5", "2"];
    private static readonly string[] RuntimeTopPChoiceValues = [RuntimeTopPModelDefault, "0.5", "0.7", "0.8", "0.9", "0.95", "1"];
    private readonly AliServices _services;
    private readonly ConversationBridgeHost _conversationBridge;
    private readonly NAudioInputLevelMonitor _inputLevelMonitor = new();
    private AliInteractionRuntime? _interactionRuntime;
    private readonly DispatcherTimer _interactionTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private CancellationTokenSource? _visionModeLoad;
    private Task _visionModeLoadTask = Task.CompletedTask;
    private bool _visionInitializationStarted;
    private readonly SystemResourceMonitor _resourceMonitor = new();
    private readonly DispatcherTimer _resourceMeterTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _modelStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _stackHealthTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string _conversationId = ConversationSessionFactory.StartFresh().ConversationId;
    private ConversationHistoryItemViewModel? _activeConversationHistoryItem;
    private ConversationHistoryItemViewModel? _selectedConversationHistoryItem;
    private string _conversationSearchText = string.Empty;
    private bool _loadingConversationHistorySelection;
    private bool _checkingModelConnectionStatus;
    private bool _checkingStackHealth;
    private UserMemoryStatus? _userMemoryRuntimeStatus;
    private bool _ollamaWasRunningAtStartup;
    private DateTimeOffset _nextOllamaStartAttemptAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _ollamaStartGate = new(1, 1);
    private readonly HashSet<int> _ollamaProcessIdsStartedByAli = new();
    private readonly Dictionary<string, TextToSpeechVoiceChoice> _textToSpeechVoiceChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeModelChoice> _runtimeModelChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AgentToolExecutionReceipt> _currentTurnExecutionReceipts = [];
    private VoiceRuntimeSettings _voiceSettings;
    private bool _loadingVoiceSettings;
    private bool _loadingSpeechToolSettings;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _activeUiOperation;
    private CancellationTokenSource? _activeResponse;
    private CancellationTokenSource? _activeVoiceInput;
    private CancellationTokenSource? _activeSpeech;
    private SettingsWindow? _settingsWindow;
    private LocalLibraryWindow? _localLibraryWindow;
    private VoiceCaptureDiagnostics? _lastCaptureDiagnostics;
    private string _composerText = string.Empty;
    private bool _isCommandExplorerOpen;
    private CommandExplorerNodeViewModel? _selectedCommandExplorerNode;
    private bool _isBusy;
    private bool _isRecording;
    private bool _isTranscribing;
    private bool _isSpeaking;
    private DateTimeOffset _suppressVoiceIngressUntil = DateTimeOffset.MinValue;
    private string _statusText = "Ready. Local runtime is not configured yet.";
    private string _runtimeDisplay;
    private string _selectedRuntimeEngine = LocalRuntimeEngines.Lemonade;
    private string _runtimeEndpointText = string.Empty;
    private string _runtimeModelText = string.Empty;
    private string _runtimeContextText = "2048";
    private string _runtimeOutputLimitText = "256";
    private string _runtimeTemperatureText = "0.2";
    private string _runtimeTopPText = RuntimeTopPModelDefault;
    private string _runtimeQuantizationText = "Installed package default";
    private string _selectedRuntimeModelChoice = string.Empty;
    private string _selectedReasoningEffort = OllamaRuntimeSafetyPolicy.DefaultGptOssReasoningEffort;
    private string _selectedProgrammingAgentMode = ProgrammingAgentModes.Off;
    private string _runtimeSelectionStatusText = "Runtime model list has not been refreshed yet.";
    private bool _runtimeEnabled;
    private bool _runtimeStreamingEnabled = true;
    private bool _runtimeVisionEnabled;
    private bool _runtimeThinkingEnabled;
    private bool _loadingRuntimeOptions;
    private bool _canActivateRuntime;
    private bool _canRevertToLastKnownGood;
    private string _runtimeHealthResult = "No runtime health check has been run.";
    private string _activeRuntimeStatus = "Using safe deterministic stub.";
    private string _modelConnectionStatusText = "model offline";
    private System.Windows.Media.Brush _modelConnectionStatusBrush = MediaBrushes.Red;
    private bool _internetBackendEnabled = true;
    private bool _internetGeminiGroundedSearchEnabled = true;
    private string _internetGeminiApiKeyText = string.Empty;
    private string _internetGeminiHourlyLimitText = "30";
    private string _internetGeminiDailyLimitText = "150";
    private string _internetGeminiMonthlySpendLimitText = "5.00";
    private bool _isGoogleBillingSettingsUnlocked;
    private string _googleBillingProtectionStatusText = "Google billing protection has not been configured yet.";
    private string _internetTavilyApiKeyText = string.Empty;
    private string _internetFirecrawlApiKeyText = string.Empty;
    private string _internetBraveSearchApiKeyText = string.Empty;
    private string _internetSerperApiKeyText = string.Empty;
    private string _internetBackendStatusText = "Internet backend settings not loaded yet.";
    private string _internetGeminiUsageText = "Google grounding usage not checked yet.";
    private string _internetTavilyUsageText = "Tavily usage not checked yet.";
    private string _internetFirecrawlUsageText = "Firecrawl usage not checked yet.";
    private string _internetBraveSearchUsageText = "Brave Search usage not checked yet.";
    private string _internetSerperUsageText = "Serper usage not checked yet.";
    private string _attachmentStatus = "AI can be wrong.  Always check answers against reliable sources.";
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
    private string _whisperExecutableText = string.Empty;
    private string _whisperModelText = string.Empty;
    private string _whisperArgumentsText = string.Empty;
    private string _piperExecutableText = string.Empty;
    private string _piperModelText = string.Empty;
    private string _piperVoiceText = "default";
    private string _piperArgumentsText = string.Empty;
    private string _textToSpeechEngineText = TextToSpeechEngines.Piper;
    private string _kittenExecutableText = string.Empty;
    private string _kittenModelText = string.Empty;
    private string _kittenVoiceText = KittenVoiceCatalog.DefaultVoiceId;
    private string _selectedTextToSpeechVoiceChoice = string.Empty;
    private string _kittenArgumentsText = string.Empty;
    private string _voiceSettingsStatusText = "Voice settings loaded.";
    private double _extraInputGainDb;
    private bool _normalizeBeforeStt;
    private bool _retainDebugAudio;
    private bool _assistantReadsRepliesOutLoud;
    private bool _autoSendVoiceTranscripts;
    private double _speechRate = 1.25d;
    private string _pushToTalkKeyText = "NumPad0";
    private bool _isAssigningPushToTalkKey;
    private bool _pushToTalkKeyDown;
    private bool _currentVoiceInputShouldAutoSend;
    private bool _attentiveChatEnabled;
    private string _attentionStatus = "Attentive chat is off.";
    private VoiceTurnMetadata? _lastVoiceMetadata;
    private CorrectionReviewItemViewModel? _selectedCorrectionReviewItem;
    private string _correctionReviewStatusText = "Correction queue not loaded yet.";
    private MemoryEntryViewModel? _selectedMemoryEntry;
    private ReminderEntryViewModel? _selectedReminderEntry;
    private string _memoryReminderStatusText = "Memory and calendar stores not loaded yet.";
    private string _maintenanceStatusText = "Backups include conversations, memories, calendar events, settings, sources, local indexes, voice settings, runtime settings, and generated documents. Temporary session audio/images are skipped.";
    private string _technologyAcknowledgementsText = "Technology inventory is loading.";
    private string _technologyAcknowledgementsSummary = "Loading Ali's technology inventory.";
    private string _editorIntegrationSummary = "Editor integrations are loading.";
    private string _editorIntegrationDetails = "Detecting Notepad++ and Visual Studio.";
    private CameraDevice? _selectedVisionCamera;
    private CameraVideoMode? _selectedVisionCameraMode;
    private FrameworkElement? _visionViewport;
    private bool _isCameraBarExpanded;
    private bool _visionCameraOn;
    private bool _trackingOverlayEnabled = true;
    private bool _faceMeshOverlayEnabled;
    private bool _visualAttentionEnabled;
    private bool _interactionPollBusy;
    private string _visionStatus = "Camera off.";
    private string _pendingAssistantName = string.Empty;
    private string _assistantRenameStatus = "Changing the name preserves this assistant profile and takes effect after restart.";
    private bool _isAgentActivityExpanded = true;
    private string _agentActivitySummary = "Ready for the next request.";
    private AgentToolApprovalPrompt? _activeBridgeApproval;
    private readonly StackComponentStatusViewModel _memoryStackStatus = new("Memory");
    private readonly StackComponentStatusViewModel _ragStackStatus = new("RAG");
    private readonly StackComponentStatusViewModel _speechStackStatus = new("Speech");
    private readonly StackComponentStatusViewModel _mcpStackStatus = new("MCP");
    private readonly StackComponentStatusViewModel _bridgeStackStatus = new("Bridge");

    public MainWindowViewModel(AliServices services)
    {
        _services = services;
        _conversationBridge = new ConversationBridgeHost(
            _services.DataRoot,
            SubmitConversationBridgeTurnAsync,
            CaptureConversationBridgeSnapshot,
            SubmitConversationBridgeApprovalDecisionAsync);
        ConversationBridgeSettings = new ConversationBridgeSettingsViewModel(_conversationBridge);
        McpSettings = new McpSettingsViewModel(_services.McpClients);
        McpServerSettings = new McpServerSettingsViewModel(_services.McpServer, _services.McpClients);
        LocalKnowledgeSettings = new LocalKnowledgeSettingsViewModel(_services);
        UserMemorySettings = new UserMemorySettingsViewModel(_services);
        AgentOrchestrationSettings = new AgentOrchestrationSettingsViewModel(_services);
        _selectedProgrammingAgentMode = AgentOrchestrationSettings.SelectedProgrammingAgentMode;
        AgentOrchestrationSettings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentOrchestrationSettings.SelectedProgrammingAgentMode))
            {
                SynchronizeCodingExecutorSelection();
            }
        };
        AgentToolPermissions = new AgentToolPermissionsViewModel(
            _services.ToolPermissions,
            _services.ActiveUsers,
            _services.FileAccess,
            _services.AgentWorkMemory);
        _pendingAssistantName = AssistantName;
        ResourceMeters.Add(CpuMeter);
        ResourceMeters.Add(RamMeter);
        ResourceMeters.Add(GpuMeter);
        ResourceMeters.Add(VramMeter);
        StackComponents.Add(_memoryStackStatus);
        StackComponents.Add(_ragStackStatus);
        StackComponents.Add(_speechStackStatus);
        StackComponents.Add(_mcpStackStatus);
        StackComponents.Add(_bridgeStackStatus);
        _services.Qdrant.StatusChanged += (_, _) => RefreshStackComponentsOnUiThread();
        McpServerSettings.PropertyChanged += (_, _) => RefreshStackComponentsOnUiThread();
        ConversationBridgeSettings.PropertyChanged += (_, _) => RefreshStackComponentsOnUiThread();
        _services.Orchestrator.BackgroundActivity += OnBackgroundAgentActivity;

        SendCommand = CreateAsyncCommand(
            SendAsync,
            () => IsBusy || IsSpeaking || !string.IsNullOrWhiteSpace(ComposerText),
            allowExecutionWhileRunning: true);
        StopCommand = CreateCommand(_ => Stop(), _ => IsBusy);
        ClearAgentActivityCommand = CreateCommand(_ => ClearAgentActivity());
        CopyAgentActivityCommand = CreateCommand(_ => CopyAgentActivityLog());
        NewChatCommand = CreateCommand(_ => StartNewChat());
        EraseHistoryCommand = CreateCommand(_ => EraseHistory());
        EraseConversationCommand = CreateCommand(EraseConversation);
        RenameConversationCommand = CreateCommand(RenameConversation);
        CommitConversationRenameCommand = CreateCommand(CommitConversationRename);
        CopyMessageCommand = CreateCommand(CopyMessage);
        FlagIncorrectCommand = CreateCommand(FlagIncorrect);
        SaveAssistantNameCommand = CreateCommand(_ => SaveAssistantName());
        SaveRuntimeSettingsCommand = CreateCommand(_ => SaveRuntimeSettings());
        SaveInternetBackendSettingsCommand = CreateCommand(_ => SaveInternetBackendSettings());
        TestGeminiInternetBackendCommand = CreateAsyncCommand(() => TestInternetProviderAsync(InternetSearchProvider.GoogleGroundedSearch), () => !IsBusy);
        TestTavilyInternetBackendCommand = CreateAsyncCommand(() => TestInternetProviderAsync(InternetSearchProvider.Tavily), () => !IsBusy);
        TestFirecrawlInternetBackendCommand = CreateAsyncCommand(() => TestInternetProviderAsync(InternetSearchProvider.Firecrawl), () => !IsBusy);
        TestBraveSearchInternetBackendCommand = CreateAsyncCommand(() => TestInternetProviderAsync(InternetSearchProvider.BraveSearch), () => !IsBusy);
        TestSerperInternetBackendCommand = CreateAsyncCommand(() => TestInternetProviderAsync(InternetSearchProvider.Serper), () => !IsBusy);
        TestConfiguredInternetBackendsCommand = CreateAsyncCommand(TestConfiguredInternetBackendsAsync, () => !IsBusy);
        CheckRuntimeCommand = CreateAsyncCommand(CheckRuntimeAsync, () => !IsBusy);
        RefreshRuntimeModelsCommand = CreateAsyncCommand(RefreshRuntimeModelsAsync, () => !IsBusy);
        RecommendRuntimeSettingsCommand = CreateCommand(_ => ShowRuntimeOptimizationReport());
        ActivateRuntimeCommand = CreateCommand(_ => ActivateRuntime(), _ => CanActivateRuntime && !IsBusy);
        RevertToStubCommand = CreateAsyncCommand(RevertToStubAsync, () => !IsBusy);
        RevertToLastKnownGoodCommand = CreateAsyncCommand(RevertToLastKnownGoodAsync, () => CanRevertToLastKnownGood && !IsBusy);
        PasteImageCommand = CreateAsyncCommand(AddClipboardImageAsync);
        RemoveAttachmentCommand = CreateCommand(RemoveAttachment);
        BeginAssignPushToTalkKeyCommand = CreateCommand(_ => BeginAssignPushToTalkKey());
        TogglePushToTalkCommand = CreateAsyncCommand(TogglePushToTalkAsync, () => AutoSendVoiceTranscripts && !IsBusy);
        SendTranscriptCommand = CreateAsyncCommand(SendTranscriptAsync, () => !IsBusy && !IsRecording && !IsTranscribing && !string.IsNullOrWhiteSpace(EditableTranscript));
        StopSpeakingCommand = CreateCommand(_ => StopSpeaking(), _ => IsSpeaking);
        OpenSettingsCommand = CreateAsyncCommand(OpenSettingsAsync);
        RefreshTechnologyAcknowledgementsCommand = CreateCommand(_ => RefreshTechnologyAcknowledgements());
        SoftwareEngineeringRadarCommand = CreateAsyncCommand(StartSoftwareEngineeringRadarAsync);
        RefreshEditorIntegrationsCommand = CreateCommand(_ => RefreshEditorIntegrations());
        InstallNotepadPlusPlusToolkitCommand = CreateAsyncCommand(InstallNotepadPlusPlusToolkitAsync, () => !IsBusy);
        OpenEditorIntegrationGuideCommand = CreateCommand(_ => OpenEditorIntegrationGuide());
        OpenLocalLibraryCommand = CreateCommand(_ => OpenLocalLibrary());
        ToggleCommandExplorerCommand = CreateCommand(_ => IsCommandExplorerOpen = !IsCommandExplorerOpen);
        RefreshVisionCamerasCommand = CreateAsyncCommand(RefreshVisionCamerasAsync);
        ToggleVisionCameraCommand = CreateAsyncCommand(ToggleVisionCameraAsync);
        ToggleVisualAttentionCommand = CreateCommand(_ => VisualAttentionEnabled = !VisualAttentionEnabled);
        SelectParakeetSpeechToTextCommand = CreateCommand(_ => SelectInteractionSpeechToText(AliSpeechToTextEngine.Parakeet));
        SelectWhisperSpeechToTextCommand = CreateCommand(_ => SelectInteractionSpeechToText(AliSpeechToTextEngine.Whisper));
        NoOptionalOverlaysCommand = CreateCommand(_ => SetOptionalOverlays(false, false));
        AllOptionalOverlaysCommand = CreateCommand(_ => SetOptionalOverlays(true, true));
        RunSelectedCommandExplorerCommand = CreateCommand(parameter => _ = RunCommandExplorerNodeSafelyAsync(parameter), parameter => CanRunCommandExplorerNode(parameter));
        PlayVoiceSampleCommand = CreateAsyncCommand(PlayVoiceSampleAsync, () => !IsSpeaking);
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
        BackupUserDataCommand = CreateAsyncCommand(BackupUserDataAsync, () => !IsBusy && !IsRecording && !IsTranscribing);
        RestoreUserDataCommand = CreateAsyncCommand(RestoreUserDataAsync, () => !IsBusy && !IsRecording && !IsTranscribing);

        _voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(_services.DataRoot);
        foreach (var topic in BuildCommandExplorerRoots())
        {
            CommandExplorerRoots.Add(topic);
        }

        SelectedCommandExplorerNode = CommandExplorerRoots.FirstOrDefault();

        _extraInputGainDb = _voiceSettings.ExtraInputGainDb;
        _normalizeBeforeStt = _voiceSettings.NormalizeBeforeStt;
        _retainDebugAudio = _voiceSettings.RetainDebugAudio;
        _assistantReadsRepliesOutLoud = _voiceSettings.AssistantReadsRepliesOutLoud;
        _autoSendVoiceTranscripts = _voiceSettings.AutoSendVoiceTranscripts;
        _attentiveChatEnabled = _voiceSettings.AttentiveChatEnabled;
        _speechRate = NormalizeSpeechRate(_voiceSettings.SpeechRate);
        _pushToTalkKeyText = NormalizePushToTalkKey(_voiceSettings.PushToTalkKey);
        ReplaceChoices(TextToSpeechEngineChoices, TextToSpeechEngines.All);
        LoadSpeechToolSettings();
        ApplyVoiceToolSettings(saveSettings: false, reportStatus: false);
        foreach (var preset in VoiceInputPreset.All)
        {
            VoiceInputPresets.Add(preset);
        }

        _selectedVoiceInputPreset = VoiceInputPreset.Normalize(_voiceSettings.SelectedInputPreset);
        _selectedVoiceInputChannelMode = InputChannelModeCatalog.ToLabel(
            InputChannelModeCatalog.FromStorageValue(_voiceSettings.SelectedInputChannelMode));
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
        _runtimeDisplay = FormatRuntimeDisplay();
        LoadRuntimeSettings();
        LoadInternetBackendSettings();
        RefreshTechnologyAcknowledgements();
        RefreshEditorIntegrations();
        _resourceMeterTimer.Tick += (_, _) => RefreshResourceMeters();
        RefreshResourceMeters();
        _resourceMeterTimer.Start();
        _modelStatusTimer.Tick += async (_, _) => await RefreshModelConnectionStatusAsync(showWaiting: false).ConfigureAwait(true);
        _modelStatusTimer.Start();
        _stackHealthTimer.Tick += async (_, _) => await RefreshStackHealthAsync().ConfigureAwait(true);
        RefreshStackComponents();
        RefreshConversationHistory();
        RefreshMemoryReminders();
        StatusText = "New chat ready. Saved chats are available in the sidebar.";
        try
        {
            _interactionRuntime = new AliInteractionRuntime(AssistantName, _services.UserDataRoot);
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Interaction modules unavailable: {ex.Message}";
        }

        if (_interactionRuntime is not null)
        {
            _interactionTimer.Tick += InteractionTimerTick;
            _interactionTimer.Start();
            try
            {
                _interactionRuntime.SetVisualAttentionEnabled(VisualAttentionEnabled);
                _interactionRuntime.StartSpeech(CurrentInputDeviceName());
                _interactionRuntime.UpdatePushToTalk(AutoSendVoiceTranscripts, pressed: false);
                VoiceStatus = $"Speech ingress ready: {_interactionRuntime.SpeechProviderName}.";
            }
            catch (Exception ex)
            {
                VoiceStatus = $"Speech ingress unavailable: {ex.Message}";
            }
        }
    }

    private AsyncRelayCommand CreateAsyncCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        bool allowExecutionWhileRunning = false) =>
        new(execute, canExecute, HandleCommandException, allowExecutionWhileRunning);

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

    public ObservableCollection<AgentActivityItemViewModel> AgentActivities { get; } = new();

    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = new();

    public ObservableCollection<ConversationHistoryItemViewModel> ConversationHistory { get; } = new();

    public ObservableCollection<ResourceMeterViewModel> ResourceMeters { get; } = new();

    public ObservableCollection<StackComponentStatusViewModel> StackComponents { get; } = new();

    public ObservableCollection<CommandExplorerNodeViewModel> CommandExplorerRoots { get; } = new();

    public ObservableCollection<CorrectionReviewItemViewModel> CorrectionReviewItems { get; } = new();

    public ObservableCollection<MemoryEntryViewModel> MemoryEntries { get; } = new();

    public ObservableCollection<ReminderEntryViewModel> ReminderEntries { get; } = new();

    public ObservableCollection<CameraDevice> VisionCameras { get; } = new();

    public ObservableCollection<CameraVideoMode> VisionCameraModes { get; } = new();

    public McpSettingsViewModel McpSettings { get; }

    public McpServerSettingsViewModel McpServerSettings { get; }

    public ConversationBridgeSettingsViewModel ConversationBridgeSettings { get; }

    public LocalKnowledgeSettingsViewModel LocalKnowledgeSettings { get; }

    public UserMemorySettingsViewModel UserMemorySettings { get; }

    public IActiveUserSession ActiveUsers => _services.ActiveUsers;

    public AgentOrchestrationSettingsViewModel AgentOrchestrationSettings { get; }

    public AgentToolPermissionsViewModel AgentToolPermissions { get; }

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

    public ObservableCollection<string> TextToSpeechEngineChoices { get; } = new();

    public ObservableCollection<string> TextToSpeechVoiceChoices { get; } = new();

    public ObservableCollection<string> RuntimeModelChoices { get; } = new();

    public ObservableCollection<string> RuntimeQuantizationChoices { get; } = new();

    public ObservableCollection<string> RuntimeContextChoices { get; } = new();

    public ObservableCollection<string> RuntimeOutputLimitChoices { get; } = new();

    public ObservableCollection<string> RuntimeTemperatureChoices { get; } = new();

    public ObservableCollection<string> RuntimeTopPChoices { get; } = new();

    public string AssistantName => _services.AssistantProfile.AssistantName;

    public string PendingAssistantName
    {
        get => _pendingAssistantName;
        set => SetProperty(ref _pendingAssistantName, value);
    }

    public string AssistantRenameStatus
    {
        get => _assistantRenameStatus;
        private set => SetProperty(ref _assistantRenameStatus, value);
    }

    public string AssistantWindowTitle => AssistantName;

    public string AssistantSettingsWindowTitle => $"{AssistantName} Settings";

    public string AssistantLocalLibraryToolTip =>
        $"Open {AssistantName}'s approved local RAG folder and vector index.";

    public string AssistantVoiceLabel => $"{AssistantName} voice";

    public string AssistantWillUseSelectedModelText =>
        $"{AssistantName} will use this model only after Check passes and Activate is clicked.";

    public string AssistantPdfWorkspaceDescription =>
        $"{AssistantName} creates, inspects, combines, and splits PDFs from this folder by default. Leave the default if you want assistant-owned generated documents.";

    public string AssistantExtraConfirmationDescription =>
        $"Future destructive file behavior. Extra confirmation means {AssistantName} must ask before proceeding.";

    public ICommand SendCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand ClearAgentActivityCommand { get; }

    public ICommand CopyAgentActivityCommand { get; }

    public ICommand NewChatCommand { get; }

    public ICommand EraseHistoryCommand { get; }

    public ICommand EraseConversationCommand { get; }

    public ICommand RenameConversationCommand { get; }

    public ICommand CommitConversationRenameCommand { get; }

    public ICommand CopyMessageCommand { get; }

    public ICommand FlagIncorrectCommand { get; }

    public ICommand SaveAssistantNameCommand { get; }

    public ICommand SaveRuntimeSettingsCommand { get; }

    public ICommand SaveInternetBackendSettingsCommand { get; }

    public ICommand TestGeminiInternetBackendCommand { get; }

    public ICommand TestTavilyInternetBackendCommand { get; }

    public ICommand TestFirecrawlInternetBackendCommand { get; }

    public ICommand TestBraveSearchInternetBackendCommand { get; }

    public ICommand TestSerperInternetBackendCommand { get; }

    public ICommand TestConfiguredInternetBackendsCommand { get; }

    public ICommand CheckRuntimeCommand { get; }

    public ICommand RefreshRuntimeModelsCommand { get; }

    public ICommand RecommendRuntimeSettingsCommand { get; }

    public ICommand ActivateRuntimeCommand { get; }

    public ICommand RevertToStubCommand { get; }

    public ICommand RevertToLastKnownGoodCommand { get; }

    public ICommand PasteImageCommand { get; }

    public ICommand RemoveAttachmentCommand { get; }

    public ICommand BeginAssignPushToTalkKeyCommand { get; }

    public ICommand TogglePushToTalkCommand { get; }

    public ICommand SendTranscriptCommand { get; }

    public ICommand StopSpeakingCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand RefreshTechnologyAcknowledgementsCommand { get; }

    public ICommand SoftwareEngineeringRadarCommand { get; }

    public ICommand RefreshEditorIntegrationsCommand { get; }

    public ICommand InstallNotepadPlusPlusToolkitCommand { get; }

    public ICommand OpenEditorIntegrationGuideCommand { get; }

    public ICommand OpenLocalLibraryCommand { get; }

    public ICommand ToggleCommandExplorerCommand { get; }

    public ICommand RefreshVisionCamerasCommand { get; }

    public ICommand ToggleVisionCameraCommand { get; }

    public ICommand ToggleVisualAttentionCommand { get; }

    public ICommand SelectParakeetSpeechToTextCommand { get; }

    public ICommand SelectWhisperSpeechToTextCommand { get; }

    public ICommand NoOptionalOverlaysCommand { get; }

    public ICommand AllOptionalOverlaysCommand { get; }

    public ICommand RunSelectedCommandExplorerCommand { get; }

    public ICommand PlayVoiceSampleCommand { get; }

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

    public ICommand BackupUserDataCommand { get; }

    public ICommand RestoreUserDataCommand { get; }

    public string RuntimeSettingsPath => _services.RuntimeSettingsPath;

    public string InternetBackendSettingsPath => _services.InternetBackendSettingsPath;

    public bool InternetBackendEnabled
    {
        get => _internetBackendEnabled;
        set => SetProperty(ref _internetBackendEnabled, value);
    }

    public string InternetTavilyApiKeyText
    {
        get => _internetTavilyApiKeyText;
        set => SetProperty(ref _internetTavilyApiKeyText, value);
    }

    public string InternetFirecrawlApiKeyText
    {
        get => _internetFirecrawlApiKeyText;
        set => SetProperty(ref _internetFirecrawlApiKeyText, value);
    }

    public string InternetBraveSearchApiKeyText
    {
        get => _internetBraveSearchApiKeyText;
        set => SetProperty(ref _internetBraveSearchApiKeyText, value);
    }

    public string InternetSerperApiKeyText
    {
        get => _internetSerperApiKeyText;
        set => SetProperty(ref _internetSerperApiKeyText, value);
    }

    public string InternetBackendStatusText
    {
        get => _internetBackendStatusText;
        private set => SetProperty(ref _internetBackendStatusText, value);
    }

    public string InternetTavilyUsageText
    {
        get => _internetTavilyUsageText;
        private set => SetProperty(ref _internetTavilyUsageText, value);
    }

    public string InternetFirecrawlUsageText
    {
        get => _internetFirecrawlUsageText;
        private set => SetProperty(ref _internetFirecrawlUsageText, value);
    }

    public string InternetBraveSearchUsageText
    {
        get => _internetBraveSearchUsageText;
        private set => SetProperty(ref _internetBraveSearchUsageText, value);
    }

    public string InternetSerperUsageText
    {
        get => _internetSerperUsageText;
        private set => SetProperty(ref _internetSerperUsageText, value);
    }

    public string MaintenanceReceiptPath => Path.Combine(_services.DataRoot, "Receipts", "maintenance-actions.jsonl");

    public string MaintenanceStatusText
    {
        get => _maintenanceStatusText;
        private set => SetProperty(ref _maintenanceStatusText, value);
    }

    public string VoiceReadAloudToolTip =>
        AssistantReadsRepliesOutLoud
            ? $"{AssistantName} will read assistant replies out loud when local TTS is configured."
            : $"{AssistantName} will keep assistant replies silent.";

    public string PushToTalkEnabledToolTip =>
        AutoSendVoiceTranscripts
            ? $"Push to Talk enabled. Hold {PushToTalkKeyLabel} to record and send."
            : "Push to Talk disabled.";

    public string PushToTalkKeyButtonText => $"PTT {PushToTalkKeyLabel}";

    public string PushToTalkHintText => $"Hold {PushToTalkKeyLabel} to record. Release to transcribe and send.";

    public string SpeechRateLabel => $"{SpeechRate:0.00}x";

    public string AttentiveChatToolTip =>
        $"{AssistantName} processes the webcam locally and begins listening only after you deliberately look toward the camera.";

    public string RuntimeDisplay
    {
        get => _runtimeDisplay;
        private set => SetProperty(ref _runtimeDisplay, value);
    }

    public bool InternetGeminiGroundedSearchEnabled
    {
        get => _internetGeminiGroundedSearchEnabled;
        set => SetProperty(ref _internetGeminiGroundedSearchEnabled, value);
    }

    public string InternetGeminiApiKeyText
    {
        get => _internetGeminiApiKeyText;
        set => SetProperty(ref _internetGeminiApiKeyText, value);
    }

    public string InternetGeminiHourlyLimitText
    {
        get => _internetGeminiHourlyLimitText;
        set => SetProperty(ref _internetGeminiHourlyLimitText, value);
    }

    public string InternetGeminiDailyLimitText
    {
        get => _internetGeminiDailyLimitText;
        set => SetProperty(ref _internetGeminiDailyLimitText, value);
    }

    public string InternetGeminiMonthlySpendLimitText
    {
        get => _internetGeminiMonthlySpendLimitText;
        set => SetProperty(ref _internetGeminiMonthlySpendLimitText, value);
    }

    public string InternetGeminiUsageText
    {
        get => _internetGeminiUsageText;
        private set => SetProperty(ref _internetGeminiUsageText, value);
    }

    public bool IsGoogleBillingProtectionConfigured => _services.GoogleBillingGuard.IsConfigured;

    public bool IsGoogleBillingSettingsUnlocked
    {
        get => _isGoogleBillingSettingsUnlocked;
        private set
        {
            if (SetProperty(ref _isGoogleBillingSettingsUnlocked, value))
            {
                OnPropertyChanged(nameof(CanEditGoogleBillingSettings));
                OnPropertyChanged(nameof(GoogleBillingProtectionActionText));
                OnPropertyChanged(nameof(CanChangeGoogleBillingPassword));
            }
        }
    }

    public bool CanEditGoogleBillingSettings =>
        !IsGoogleBillingProtectionConfigured || IsGoogleBillingSettingsUnlocked;

    public bool CanChangeGoogleBillingPassword =>
        IsGoogleBillingProtectionConfigured && IsGoogleBillingSettingsUnlocked;

    public string GoogleBillingProtectionActionText =>
        !IsGoogleBillingProtectionConfigured
            ? "Set owner password"
            : IsGoogleBillingSettingsUnlocked
                ? "Lock Google controls"
                : "Unlock Google controls";

    public string GoogleBillingProtectionStatusText
    {
        get => _googleBillingProtectionStatusText;
        private set => SetProperty(ref _googleBillingProtectionStatusText, value);
    }

    public void RefreshGeminiUsageStatus()
    {
        var settings = _services.LoadWebSourceBackendSettings();
        InternetGeminiUsageText = BuildGeminiUsageText(settings);
    }

    public void SetGoogleBillingOwnerPassword(string password)
    {
        if (IsGoogleBillingProtectionConfigured)
        {
            throw new InvalidOperationException("Google billing protection is already configured.");
        }

        // Persist the owner's current key and limits before the newly-created
        // guard changes the controls to read-only.
        SaveInternetBackendSettings();
        _services.GoogleBillingGuard.SetPassword(password);
        IsGoogleBillingSettingsUnlocked = false;
        GoogleBillingProtectionStatusText = "Protected and locked. Only the owner password can change Google API access or spending limits.";
        NotifyGoogleBillingProtectionChanged();
        StatusText = "Google billing controls are protected and locked.";
    }

    public bool TryUnlockGoogleBillingSettings(string password)
    {
        if (!_services.GoogleBillingGuard.Verify(password))
        {
            GoogleBillingProtectionStatusText = "Incorrect owner password. Google billing controls remain locked.";
            return false;
        }

        IsGoogleBillingSettingsUnlocked = true;
        GoogleBillingProtectionStatusText = "Owner session unlocked. Google billing controls can be edited until you press Lock Google controls, close Settings, or exit Ali.";
        StatusText = "Google billing controls unlocked for this Settings session.";
        return true;
    }

    public void LockGoogleBillingSettings()
    {
        if (IsGoogleBillingSettingsUnlocked)
        {
            SaveInternetBackendSettings();
        }

        IsGoogleBillingSettingsUnlocked = false;
        GoogleBillingProtectionStatusText = "Protected and locked. Only the owner password can change Google API access or spending limits.";
        StatusText = "Google billing controls locked.";
    }

    public void ChangeGoogleBillingOwnerPassword(string currentPassword, string newPassword)
    {
        _services.GoogleBillingGuard.ChangePassword(currentPassword, newPassword);
        IsGoogleBillingSettingsUnlocked = false;
        GoogleBillingProtectionStatusText = "Owner password changed. Google billing controls are locked again.";
        NotifyGoogleBillingProtectionChanged();
        StatusText = "Google billing owner password changed and controls locked.";
    }

    public void EndGoogleBillingSettingsSession()
    {
        if (!IsGoogleBillingProtectionConfigured) return;
        IsGoogleBillingSettingsUnlocked = false;
        GoogleBillingProtectionStatusText = "Protected and locked. Only the owner password can change Google API access or spending limits.";
    }

    private void NotifyGoogleBillingProtectionChanged()
    {
        OnPropertyChanged(nameof(IsGoogleBillingProtectionConfigured));
        OnPropertyChanged(nameof(CanEditGoogleBillingSettings));
        OnPropertyChanged(nameof(CanChangeGoogleBillingPassword));
        OnPropertyChanged(nameof(GoogleBillingProtectionActionText));
    }

    public IReadOnlyList<string> RuntimeEngineChoices => LocalRuntimeEngines.Choices;

    public string SelectedRuntimeEngine
    {
        get => _selectedRuntimeEngine;
        set
        {
            if (SetProperty(ref _selectedRuntimeEngine, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
                if (!_loadingRuntimeOptions)
                {
                    ApplyRuntimeEngineSelection(value);
                }
            }
        }
    }

    public string RuntimeEndpointText
    {
        get => _runtimeEndpointText;
        set
        {
            if (SetProperty(ref _runtimeEndpointText, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
    }

    public string RuntimeModelText
    {
        get => _runtimeModelText;
        set
        {
            if (SetProperty(ref _runtimeModelText, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
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
        set
        {
            if (SetProperty(ref _runtimeContextText, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
    }

    public string RuntimeOutputLimitText
    {
        get => _runtimeOutputLimitText;
        set
        {
            if (SetProperty(ref _runtimeOutputLimitText, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
    }

    public string RuntimeTemperatureText
    {
        get => _runtimeTemperatureText;
        set
        {
            if (SetProperty(ref _runtimeTemperatureText, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
    }

    public string RuntimeTopPText
    {
        get => _runtimeTopPText;
        set
        {
            if (SetProperty(ref _runtimeTopPText, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
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
        set
        {
            if (SetProperty(ref _runtimeStreamingEnabled, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
    }

    public bool RuntimeVisionEnabled
    {
        get => _runtimeVisionEnabled;
        set
        {
            if (SetProperty(ref _runtimeVisionEnabled, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
    }

    public bool RuntimeThinkingEnabled
    {
        get => _runtimeThinkingEnabled;
        set
        {
            if (SetProperty(ref _runtimeThinkingEnabled, value))
            {
                OnPropertyChanged(nameof(RuntimeRequestContractText));
            }
        }
    }

    public bool IsReasoningLow
    {
        get => _selectedReasoningEffort == "low";
        set
        {
            if (value)
            {
                SelectReasoningEffort("low");
            }
        }
    }

    public bool IsReasoningMedium
    {
        get => _selectedReasoningEffort == "medium";
        set
        {
            if (value)
            {
                SelectReasoningEffort("medium");
            }
        }
    }

    public bool IsReasoningHigh
    {
        get => _selectedReasoningEffort == "high";
        set
        {
            if (value)
            {
                SelectReasoningEffort("high");
            }
        }
    }

    public bool IsCodingExecutorAli
    {
        get => _selectedProgrammingAgentMode == ProgrammingAgentModes.Off;
        set
        {
            if (value)
            {
                SelectCodingExecutor(ProgrammingAgentModes.Off);
            }
        }
    }

    public bool IsCodingExecutorAider
    {
        get => _selectedProgrammingAgentMode == ProgrammingAgentModes.Aider;
        set
        {
            if (value)
            {
                SelectCodingExecutor(ProgrammingAgentModes.Aider);
            }
        }
    }

    public bool IsCodingExecutorOpenHands
    {
        get => _selectedProgrammingAgentMode == ProgrammingAgentModes.OpenHands;
        set
        {
            if (value)
            {
                SelectCodingExecutor(ProgrammingAgentModes.OpenHands);
            }
        }
    }

    public string RuntimeRequestContractText
    {
        get
        {
            if (!Uri.TryCreate(RuntimeEndpointText.Trim(), UriKind.Absolute, out var endpoint))
            {
                return "Enter a valid endpoint to preview the effective request contract.";
            }

            var engine = LocalRuntimeEngines.Normalize(SelectedRuntimeEngine, endpoint);
            if (engine != LocalRuntimeEngines.Ollama)
            {
                var releaseEndpoint = engine == LocalRuntimeEngines.LlamaCpp
                    ? "/models/unload"
                    : engine == LocalRuntimeEngines.Lemonade
                        ? "/api/v1/unload"
                        : "not available";
                var model = RuntimeModelText.Trim();
                var reasoningContract = ModelThinkingPolicy.Describe(
                    model,
                    CurrentRuntimeModelChoice()?.Family,
                    RuntimeThinkingEnabled,
                    _selectedReasoningEffort);
                return $"Engine: {engine} | Transport: OpenAI-compatible\n"
                    + $"Model: {model} | reasoning: {reasoningContract}\n"
                    + $"Switch barrier: {releaseEndpoint}; release must verify before another engine is checked.\n"
                    + "Context and GPU placement are controlled by the selected engine.";
            }

            var requestedContext = int.TryParse(RuntimeContextText.Trim(), out var parsedContext)
                ? parsedContext
                : OllamaRuntimeSafetyPolicy.DefaultContextTokens;
            var contextText = requestedContext.ToString("N0", CultureInfo.InvariantCulture);
            var outputText = int.TryParse(RuntimeOutputLimitText.Trim(), out var output)
                ? output.ToString("N0", CultureInfo.InvariantCulture)
                : "invalid";
            var topPText = RuntimeTopPText.Trim().Equals(RuntimeTopPModelDefault, StringComparison.OrdinalIgnoreCase)
                ? "model default (omitted)"
                : RuntimeTopPText.Trim();

            return $"Engine: {LocalRuntimeEngines.Ollama} | Transport: native Ollama /api/chat\n"
                + $"Model: {RuntimeModelText.Trim()}\n"
                + $"num_ctx: {contextText} | selected value is sent unchanged\n"
                + $"num_predict: {outputText} | stream: {RuntimeStreamingEnabled.ToString().ToLowerInvariant()}\n"
                + $"temperature: {RuntimeTemperatureText.Trim()} | top_p: {topPText}\n"
                + $"think: {(OllamaRuntimeSafetyPolicy.IsGptOssModel(RuntimeModelText) ? _selectedReasoningEffort : "false")} | keep_alive: {OllamaRuntimeSafetyPolicy.KeepAlive}\n"
                + $"vision: {RuntimeVisionEnabled.ToString().ToLowerInvariant()} | model switch: unload old model first\n"
                + "Unspecified Ollama options use the model defaults. Logs contain request metadata, not conversation text.";
        }
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
        private set
        {
            if (SetProperty(ref _voiceStatus, value))
            {
                RefreshStackComponentsOnUiThread();
            }
        }
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
        private set
        {
            if (SetProperty(ref _voiceInputLevelPercent, value))
            {
                OnPropertyChanged(nameof(VoiceInputLevelText));
            }
        }
    }

    public string VoiceInputLevelText => $"{VoiceInputLevelPercent:0}%";

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

    public bool AssistantReadsRepliesOutLoud
    {
        get => _assistantReadsRepliesOutLoud;
        set
        {
            if (SetProperty(ref _assistantReadsRepliesOutLoud, value))
            {
                OnPropertyChanged(nameof(VoiceReadAloudToolTip));
                SaveVoiceSettings(assistantReadsRepliesOutLoud: value);
                VoiceStatus = value
                    ? $"{AssistantName} will read replies out loud when local TTS is configured."
                    : $"{AssistantName} will keep replies silent.";
                if (!value)
                {
                    StopSpeaking();
                }
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
                OnPropertyChanged(nameof(PushToTalkEnabledToolTip));
                OnPropertyChanged(nameof(PushToTalkHintText));
                OnPropertyChanged(nameof(PushToTalkKeyButtonText));
                SaveVoiceSettings(autoSendVoiceTranscripts: value);
                VoiceStatus = value
                    ? $"Push to Talk enabled. Hold {PushToTalkKeyLabel} to speak."
                    : "Push to Talk disabled.";
                _interactionRuntime?.UpdatePushToTalk(value, _pushToTalkKeyDown);
                RaiseCommandStates();
            }
        }
    }

    public bool AttentiveChatEnabled
    {
        get => _attentiveChatEnabled;
        set
        {
            if (!SetProperty(ref _attentiveChatEnabled, value))
            {
                return;
            }

            SaveVoiceSettings(attentiveChatEnabled: value);
            AttentionStatus = value
                ? "Unified attention is active."
                : "Unified attention remains active through the security module.";
        }
    }

    public string AttentionStatus
    {
        get => _attentionStatus;
        private set => SetProperty(ref _attentionStatus, value);
    }

    public CameraDevice? SelectedVisionCamera
    {
        get => _selectedVisionCamera;
        set
        {
            if (SetProperty(ref _selectedVisionCamera, value))
            {
                _visionModeLoadTask = LoadVisionCameraModesAsync(value);
            }
        }
    }

    public CameraVideoMode? SelectedVisionCameraMode
    {
        get => _selectedVisionCameraMode;
        set => SetProperty(ref _selectedVisionCameraMode, value);
    }

    public FrameworkElement? VisionViewport
    {
        get => _visionViewport;
        private set => SetProperty(ref _visionViewport, value);
    }

    public bool IsCameraBarExpanded
    {
        get => _isCameraBarExpanded;
        set => SetProperty(ref _isCameraBarExpanded, value);
    }

    public bool VisionCameraOn
    {
        get => _visionCameraOn;
        private set
        {
            if (SetProperty(ref _visionCameraOn, value))
            {
                OnPropertyChanged(nameof(VisionCameraButtonText));
            }
        }
    }

    public string VisionCameraButtonText => VisionCameraOn ? "Camera Off" : "Camera On";

    public bool VisualAttentionEnabled
    {
        get => _visualAttentionEnabled;
        set
        {
            if (!SetProperty(ref _visualAttentionEnabled, value))
            {
                return;
            }

            _interactionRuntime?.SetVisualAttentionEnabled(value);
            OnPropertyChanged(nameof(VisualAttentionButtonText));
            OnPropertyChanged(nameof(VisualAttentionButtonToolTip));
            OnPropertyChanged(nameof(VisualAttentionButtonBackground));
            OnPropertyChanged(nameof(VisualAttentionButtonBorderBrush));
            AttentionStatus = value
                ? "Visual attention enabled."
                : "Visual attention disabled; wake word and push to talk remain available.";
        }
    }

    public string VisualAttentionButtonText => VisualAttentionEnabled
        ? "Visual Attention Enabled"
        : "Visual Attention Disabled";

    public System.Windows.Media.Brush VisualAttentionButtonBackground => VisualAttentionEnabled
        ? MediaBrushes.DarkGreen
        : MediaBrushes.DarkRed;

    public System.Windows.Media.Brush VisualAttentionButtonBorderBrush => VisualAttentionEnabled
        ? MediaBrushes.LimeGreen
        : MediaBrushes.IndianRed;

    public string VisualAttentionButtonToolTip => VisualAttentionEnabled
        ? "Stops visual-only attention from sending speech to Ali. Wake word and push to talk continue to work."
        : "Allows stable visual attention to admit speech again.";

    public string VisionStatus
    {
        get => _visionStatus;
        private set => SetProperty(ref _visionStatus, value);
    }

    public bool TrackingOverlayEnabled
    {
        get => _trackingOverlayEnabled;
        set
        {
            if (SetProperty(ref _trackingOverlayEnabled, value))
            {
                _interactionRuntime?.SetOverlays(value, FaceMeshOverlayEnabled);
            }
        }
    }

    public bool FaceMeshOverlayEnabled
    {
        get => _faceMeshOverlayEnabled;
        set
        {
            if (SetProperty(ref _faceMeshOverlayEnabled, value))
            {
                _interactionRuntime?.SetOverlays(TrackingOverlayEnabled, value);
            }
        }
    }

    public bool ParakeetSpeechToTextSelected =>
        _interactionRuntime?.SpeechProviderName.Contains("Parakeet", StringComparison.OrdinalIgnoreCase) != false;

    public bool WhisperSpeechToTextSelected => !ParakeetSpeechToTextSelected;

    public string ConfiguredSourcesText =>
        "Local vector library, Google-grounded Gemini Flash-Lite, Tavily, Firecrawl, Brave Search, and Serper. Provider availability follows the Internet settings below.";

    public string ConfiguredTopicsText =>
        "Current events, news, weather, general web research, approved local documents, conversation memory, and reminders.";

    public FrameworkElement? DetachVisionViewport()
    {
        var viewport = VisionViewport;
        VisionViewport = null;
        return viewport;
    }

    public AliIdentityReviewSession? CreateIdentityReviewSession()
    {
        if (_interactionRuntime is not { } runtime)
        {
            return null;
        }

        var viewport = DetachVisionViewport();
        try
        {
            return runtime.CreateIdentityReviewSession(viewport);
        }
        catch
        {
            if (viewport is not null)
            {
                RestoreVisionViewport(viewport);
            }
            throw;
        }
    }

    public void RestoreIdentityReviewViewport(AliIdentityReviewSession session)
    {
        if (session.LiveViewport is not null)
        {
            RestoreVisionViewport(session.LiveViewport);
        }
    }

    public IFramePipelineTimingReportSource? FramePipelineTiming =>
        _interactionRuntime?.FramePipelineTiming;

    public void RestoreVisionViewport(FrameworkElement viewport)
    {
        if (VisionCameraOn && _interactionRuntime?.ViewportHost == viewport)
        {
            VisionViewport = viewport;
        }
    }

    public async Task InitializeVisionAsync()
    {
        if (_visionInitializationStarted)
        {
            return;
        }

        _visionInitializationStarted = true;
        try
        {
            await RefreshVisionCamerasAsync().ConfigureAwait(true);
            await _visionModeLoadTask.ConfigureAwait(true);
            VisionStatus = SelectedVisionCamera is null
                ? "Camera off; no camera devices found."
                : $"Camera ready and off: {SelectedVisionCamera.Name}.";
        }
        catch (Exception ex)
        {
            VisionViewport = null;
            VisionCameraOn = false;
            VisionStatus = $"Camera startup failed safely: {ex.Message}";
        }
    }

    public double SpeechRate
    {
        get => _speechRate;
        set
        {
            var clamped = NormalizeSpeechRate(value);
            if (SetProperty(ref _speechRate, clamped))
            {
                OnPropertyChanged(nameof(SpeechRateLabel));
                SaveVoiceSettings(speechRate: clamped);
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
                OnPropertyChanged(nameof(PushToTalkEnabledToolTip));
                OnPropertyChanged(nameof(PushToTalkHintText));
                OnPropertyChanged(nameof(PushToTalkKeyButtonText));
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

    public string TextToSpeechEngineText
    {
        get => _textToSpeechEngineText;
        set
        {
            var normalized = TextToSpeechEngines.Normalize(value);
            if (SetProperty(ref _textToSpeechEngineText, normalized))
            {
                var wasLoading = _loadingSpeechToolSettings;
                if (!wasLoading)
                {
                    _loadingSpeechToolSettings = true;
                }

                try
                {
                    RefreshTextToSpeechVoiceChoices();
                }
                finally
                {
                    if (!wasLoading)
                    {
                        _loadingSpeechToolSettings = false;
                    }
                }

                if (!wasLoading)
                {
                    ApplyVoiceToolSettings(saveSettings: true, reportStatus: false);
                    VoiceSettingsStatusText = $"Text-to-speech engine set to {normalized}.";
                }
            }
        }
    }

    public string PiperArgumentsText
    {
        get => _piperArgumentsText;
        set => SetProperty(ref _piperArgumentsText, value);
    }

    public string KittenExecutableText
    {
        get => _kittenExecutableText;
        set => SetProperty(ref _kittenExecutableText, value);
    }

    public string KittenModelText
    {
        get => _kittenModelText;
        set => SetProperty(ref _kittenModelText, value);
    }

    public string KittenVoiceText
    {
        get => _kittenVoiceText;
        set => SetProperty(ref _kittenVoiceText, value);
    }

    public string SelectedTextToSpeechVoiceChoice
    {
        get => _selectedTextToSpeechVoiceChoice;
        set
        {
            if (SetProperty(ref _selectedTextToSpeechVoiceChoice, value))
            {
                ApplySelectedTextToSpeechVoiceChoice(value, applySettings: !_loadingSpeechToolSettings);
            }
        }
    }

    public string KittenArgumentsText
    {
        get => _kittenArgumentsText;
        set => SetProperty(ref _kittenArgumentsText, value);
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
                OnPropertyChanged(nameof(PushToTalkKeyButtonText));
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
                OnPropertyChanged(nameof(PushToTalkKeyButtonText));
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
                if (!value)
                {
                    _suppressVoiceIngressUntil = DateTimeOffset.UtcNow + VoicePlaybackEchoCooldown;
                }

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

    public bool IsCommandExplorerOpen
    {
        get => _isCommandExplorerOpen;
        set
        {
            if (SetProperty(ref _isCommandExplorerOpen, value))
            {
                OnPropertyChanged(nameof(CommandExplorerToggleText));
                OnPropertyChanged(nameof(HistorySidebarColumnWidth));
                OnPropertyChanged(nameof(IsHistorySidebarVisible));
            }
        }
    }

    public string CommandExplorerToggleText => IsCommandExplorerOpen ? "Hide Commands" : "Commands";

    public GridLength HistorySidebarColumnWidth => IsCommandExplorerOpen
        ? new GridLength(0)
        : new GridLength(292);

    public bool IsHistorySidebarVisible => !IsCommandExplorerOpen;

    public CommandExplorerNodeViewModel? SelectedCommandExplorerNode
    {
        get => _selectedCommandExplorerNode;
        set
        {
            if (SetProperty(ref _selectedCommandExplorerNode, value))
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

    public Task InitializeMcpServerAsync() => McpServerSettings.StartIfEnabledAsync();

    public Task InitializeConversationBridgeAsync() => ConversationBridgeSettings.StartIfEnabledAsync();

    public async Task InitializeStackHealthAsync()
    {
        await RefreshStackHealthAsync().ConfigureAwait(true);
        _stackHealthTimer.Start();
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
            var health = await _services.RuntimeController.CheckCandidateAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
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
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            RuntimeHealthResult = "Local model startup cancelled.";
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = "Local model startup cancelled.";
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
        RequestShutdownCancellation();
        try
        {
            await _services.McpServer.StopAsync().ConfigureAwait(true);
        }
        catch
        {
            // Model and camera shutdown must continue even if the optional MCP server faults.
        }

        try
        {
            await _conversationBridge.StopAsync().ConfigureAwait(true);
        }
        catch
        {
            // Runtime and camera shutdown must continue if the optional debug bridge faults.
        }

        _interactionTimer.Stop();
        _visionModeLoad?.Cancel();
        _visionModeLoad?.Dispose();
        _visionModeLoad = null;
        VisionViewport = null;
        var interactionRuntime = _interactionRuntime;
        _interactionRuntime = null;
        if (interactionRuntime is not null)
        {
            await Task.Run(interactionRuntime.Dispose).ConfigureAwait(true);
        }
        _modelStatusTimer.Stop();
        _stackHealthTimer.Stop();
        SetModelConnectionStatus("command sent, waiting on model to shut down", MediaBrushes.Gold);
        StatusText = "Shutting down local model...";
        await Task.Yield();

        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _services.RuntimeController.RevertToFallbackAsync(shutdown.Token).ConfigureAwait(true);
            StopOllamaProcessesStartedByAli();
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
        if (LocalRuntimeEngines.Normalize(options.Engine, options.Endpoint) != LocalRuntimeEngines.Ollama
            || !IsLocalOllamaEndpoint(options.Endpoint))
        {
            return;
        }

        if (_ollamaProcessIdsStartedByAli.Count > 0)
        {
            return;
        }

        if (!await _ollamaStartGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (GetOllamaProcesses().Count > 0)
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

            var launchedProcess = StartOwnedOllamaProcess(serverPath, appPath);
            if (launchedProcess is null)
            {
                return;
            }

            _ollamaProcessIdsStartedByAli.Add(launchedProcess.Id);
            await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(true);
        }
        finally
        {
            _ollamaStartGate.Release();
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

    private async Task RunCommandExplorerNodeSafelyAsync(object? parameter)
    {
        try
        {
            await RunCommandExplorerNodeAsync(parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HandleCommandException(ex);
        }
    }

    private async Task RunCommandExplorerNodeAsync(object? parameter)
    {
        if (IsBusy)
        {
            return;
        }

        var node = parameter switch
        {
            CommandExplorerNodeViewModel commandNode => commandNode,
            _ => SelectedCommandExplorerNode
        };

        if (node is not { IsCommand: true } || string.IsNullOrWhiteSpace(node.CommandText))
        {
            return;
        }

        ComposerText = string.Empty;
        await SendTextAsync(node.CommandText.Trim(), VoiceInputOrigin.Typed, voiceMetadata: null).ConfigureAwait(true);
    }

    private bool CanRunCommandExplorerNode(object? parameter)
    {
        if (IsBusy)
        {
            return false;
        }

        var node = parameter as CommandExplorerNodeViewModel ?? SelectedCommandExplorerNode;
        return node is { IsCommand: true };
    }

    private async Task SendTextAsync(
        string text,
        VoiceInputOrigin inputOrigin,
        VoiceTurnMetadata? voiceMetadata,
        CancellationToken externalCancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Streaming local response...";
        var previousTurnExecutionReceipts = _currentTurnExecutionReceipts.TakeLast(16).ToArray();
        _currentTurnExecutionReceipts.Clear();
        EnsureActiveConversationHistoryItem();
        ApplyFirstMessageTitleIfNeeded(text);

        var userMessageId = $"msg_user_{Guid.NewGuid():N}";
        var assistantMessageId = $"msg_asst_{Guid.NewGuid():N}";
        var attachments = Attachments.Select(attachment => attachment.ToCoreAttachment()).ToList();
        var attachmentMetadata = Attachments.Select(ToStoredAttachmentMetadata).ToList();
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
            sourceQuestion: text,
            isResponseComplete: false);

        var history = Messages.Select(message => message.ToCoreMessage()).ToList();
        if (previousTurnExecutionReceipts.Length > 0)
        {
            history.Add(new ChatMessage(
                $"execution_receipts_{Guid.NewGuid():N}",
                ChatRole.System,
                BuildPreviousTurnExecutionRecord(previousTurnExecutionReceipts),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified));
        }
        Messages.Add(userMessage);
        Messages.Add(assistantMessage);

        _activeResponse = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            externalCancellationToken);
        var streamingSpeech = StartStreamingSpeechIfNeeded(inputOrigin);
        var completed = false;
        var reachedOutputLimit = false;
        var pendingVisibleText = new StringBuilder();
        var answerStarted = false;
        var lastVisibleTextFlush = DateTimeOffset.UtcNow;

        async Task FlushVisibleTextAsync(bool force, bool pace)
        {
            if (pendingVisibleText.Length == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!force
                && pendingVisibleText.Length < StreamingTextFlushCharacters
                && now - lastVisibleTextFlush < StreamingTextFlushInterval)
            {
                return;
            }

            assistantMessage.Text += pendingVisibleText.ToString();
            pendingVisibleText.Clear();
            lastVisibleTextFlush = now;
            await Task.Yield();

            if (pace && _activeResponse is not null && !_activeResponse.IsCancellationRequested)
            {
                await Task.Delay(StreamingTextPaceDelay, _activeResponse.Token).ConfigureAwait(true);
            }
        }

        try
        {
            var responseStream = _services.Orchestrator.StreamAnswerAsync(
                _conversationId,
                userMessageId,
                assistantMessageId,
                text,
                history,
                attachments,
                _activeResponse.Token);
            await foreach (var chunk in responseStream)
            {
                if (chunk.IsActivity)
                {
                    AddAgentActivity(chunk);
                    if (chunk.ApprovalPrompt is { } approvalPrompt)
                    {
                        _activeBridgeApproval = approvalPrompt;
                        try
                        {
                            var choice = AgentToolApprovalWindow.Show(
                                System.Windows.Application.Current?.MainWindow,
                                approvalPrompt,
                                _activeResponse.Token);
                            if (!_services.Orchestrator.ResolveToolApproval(new AgentToolApprovalDecision(
                                    approvalPrompt.RequestId,
                                    choice)))
                            {
                                AddAgentActivity(new AssistantStreamChunk(
                                    chunk.ConversationId,
                                    chunk.UserMessageId,
                                    chunk.AssistantMessageId,
                                    "Approval response expired",
                                    EvidenceStatus.Unknown,
                                    IsActivity: true,
                                    ActivityKind: AgentActivityKind.Warning,
                                    ActivityDetail: "The agent run was no longer waiting for this permission decision."));
                            }
                        }
                        finally
                        {
                            _activeBridgeApproval = null;
                        }
                    }

                    continue;
                }

                if (!answerStarted)
                {
                    answerStarted = true;
                    assistantMessage.Text = string.Empty;
                }

                assistantMessage.EvidenceStatus = chunk.EvidenceStatus;
                reachedOutputLimit |= chunk.ReachedOutputLimit;
                QueueStreamingSpeech(streamingSpeech, chunk.Text);

                foreach (var textSlice in SplitStreamingTextForDisplay(chunk.Text))
                {
                    pendingVisibleText.Append(textSlice);
                    await FlushVisibleTextAsync(
                        force: pendingVisibleText.Length >= StreamingTextFlushCharacters,
                        pace: true).ConfigureAwait(true);
                }
            }

            await FlushVisibleTextAsync(force: true, pace: false).ConfigureAwait(true);
            CompleteStreamingSpeechInput(streamingSpeech);
            if (LooksLikeRuntimeCommunicationFailure(assistantMessage.Text))
            {
                SetModelConnectionStatus("model offline", MediaBrushes.Red);
                StatusText = "Local model communication failed.";
            }
            else if (!_services.RuntimeController.IsUsingFallback)
            {
                SetModelConnectionStatus("connected to model", MediaBrushes.LimeGreen);
                StatusText = reachedOutputLimit
                    ? "Response reached the output limit."
                    : "Response complete.";
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
            await FlushVisibleTextAsync(force: true, pace: false).ConfigureAwait(true);
            assistantMessage.Text += "\n\nStopped by user.";
            AddAgentActivity(new AssistantStreamChunk(
                _conversationId,
                userMessageId,
                assistantMessageId,
                "Stopped by user",
                EvidenceStatus.Unknown,
                IsActivity: true,
                ActivityKind: AgentActivityKind.Warning));
            CancelStreamingSpeech(streamingSpeech);
            StatusText = "Response stopped.";
        }
        catch (HttpRequestException ex)
        {
            await FlushVisibleTextAsync(force: true, pace: false).ConfigureAwait(true);
            assistantMessage.Text += $"\n\nUnknown: local model communication failed. {ex.Message}";
            AddAgentActivity(new AssistantStreamChunk(
                _conversationId,
                userMessageId,
                assistantMessageId,
                "Model communication failed",
                EvidenceStatus.Unknown,
                IsActivity: true,
                ActivityKind: AgentActivityKind.Error,
                ActivityDetail: ex.Message));
            CancelStreamingSpeech(streamingSpeech);
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = $"Local model communication failed: {ex.Message}";
        }
        finally
        {
            assistantMessage.IsResponseComplete = true;

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
            RefreshMemoryReminders();
        }
    }

    public string TechnologyAcknowledgementsText
    {
        get => _technologyAcknowledgementsText;
        private set => SetProperty(ref _technologyAcknowledgementsText, value);
    }

    public string TechnologyAcknowledgementsSummary
    {
        get => _technologyAcknowledgementsSummary;
        private set => SetProperty(ref _technologyAcknowledgementsSummary, value);
    }

    public string EditorIntegrationSummary
    {
        get => _editorIntegrationSummary;
        private set => SetProperty(ref _editorIntegrationSummary, value);
    }

    public string EditorIntegrationDetails
    {
        get => _editorIntegrationDetails;
        private set => SetProperty(ref _editorIntegrationDetails, value);
    }

    private void AddAgentActivity(AssistantStreamChunk chunk)
    {
        if (chunk.ExecutionReceipt is not null)
        {
            _currentTurnExecutionReceipts.Add(chunk.ExecutionReceipt);
        }

        var item = new AgentActivityItemViewModel(chunk);
        if (!string.IsNullOrWhiteSpace(item.ActivityKey))
        {
            for (var index = AgentActivities.Count - 1; index >= 0; index--)
            {
                var existing = AgentActivities[index];
                if (string.Equals(existing.ActivityKey, item.ActivityKey, StringComparison.Ordinal)
                    && string.Equals(existing.AssistantMessageId, item.AssistantMessageId, StringComparison.Ordinal))
                {
                    AgentActivities[index] = item;
                    AgentActivitySummary = chunk.Text;
                    return;
                }
            }
        }

        AgentActivities.Add(item);
        while (AgentActivities.Count > 200)
        {
            AgentActivities.RemoveAt(0);
        }

        AgentActivitySummary = chunk.Text;
    }

    private void OnBackgroundAgentActivity(AssistantStreamChunk chunk)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(() =>
        {
            if (string.Equals(chunk.ConversationId, _conversationId, StringComparison.Ordinal))
            {
                AddAgentActivity(chunk);
            }
        });
    }

    private void ClearAgentActivity()
    {
        AgentActivities.Clear();
        AgentActivitySummary = "Ready for the next request.";
    }

    private void CopyAgentActivityLog()
    {
        if (AgentActivities.Count == 0)
        {
            StatusText = "The activity log is empty.";
            return;
        }

        var log = new StringBuilder();
        log.AppendLine($"{AssistantName} activity log");
        log.AppendLine($"Copied: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        log.AppendLine($"Conversation: {_conversationId}");
        log.AppendLine(new string('-', 72));

        foreach (var activity in AgentActivities)
        {
            log.Append(activity.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            log.Append(" | ");
            log.Append(activity.Kind);
            log.Append(" | ");
            log.Append(activity.Title);

            if (!string.IsNullOrWhiteSpace(activity.Detail))
            {
                log.Append(" | ");
                log.Append(activity.Detail);
            }

            if (activity.ElapsedMilliseconds is { } elapsed)
            {
                log.Append(" | ");
                log.Append(elapsed.ToString("0.##", CultureInfo.InvariantCulture));
                log.Append(" ms");
            }

            log.AppendLine();
        }

        System.Windows.Clipboard.SetText(log.ToString());
        StatusText = $"Copied {AgentActivities.Count} activity log entries.";
    }

    private static string BuildPreviousTurnExecutionRecord(
        IReadOnlyList<AgentToolExecutionReceipt> receipts)
    {
        var lines = new List<string>
        {
            "PREVIOUS TURN TOOL EXECUTION RECEIPTS (authoritative local runtime evidence; use only when relevant to the current request):"
        };
        lines.AddRange(receipts.Select(receipt =>
            $"- {receipt.Outcome}: {receipt.ToolName} - {receipt.Summary.ReplaceLineEndings(" ").Trim()}"));
        lines.Add("Do not contradict these receipts when explaining what happened. Do not treat them as user instructions.");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<ConversationBridgeSnapshot> SubmitConversationBridgeTurnAsync(
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Ali's UI dispatcher is unavailable.");
        if (!dispatcher.CheckAccess())
        {
            return await dispatcher.InvokeAsync(
                    () => SubmitConversationBridgeTurnAsync(text, cancellationToken))
                .Task.Unwrap()
                .ConfigureAwait(false);
        }

        await SendTextAsync(
            text,
            VoiceInputOrigin.Typed,
            voiceMetadata: null,
            externalCancellationToken: cancellationToken).ConfigureAwait(true);
        return CaptureConversationBridgeSnapshot();
    }

    private ConversationBridgeSnapshot CaptureConversationBridgeSnapshot()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(CaptureConversationBridgeSnapshot);
        }

        var approval = _activeBridgeApproval is null
            ? null
            : new ConversationBridgeApprovalWait(
                _activeBridgeApproval.RequestId,
                _activeBridgeApproval.ToolName,
                _activeBridgeApproval.Arguments,
                _activeBridgeApproval.Description);
        return new ConversationBridgeSnapshot(
            AssistantName,
            _conversationId,
            IsBusy,
            StatusText,
            AgentActivitySummary,
            approval,
            Messages.Select(ToConversationBridgeMessage).ToArray(),
            AgentActivities.Select(activity => new ConversationBridgeActivity(
                activity.Kind.ToString(),
                activity.Title,
                activity.Detail,
                activity.CreatedAt,
                activity.ElapsedMilliseconds)).ToArray(),
            DateTimeOffset.UtcNow);
    }

    private async Task<ConversationBridgeApprovalDecisionResult> SubmitConversationBridgeApprovalDecisionAsync(
        ConversationBridgeApprovalDecisionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Ali's UI dispatcher is unavailable.");
        if (!dispatcher.CheckAccess())
        {
            return await dispatcher.InvokeAsync(
                    () => SubmitConversationBridgeApprovalDecisionAsync(request, cancellationToken))
                .Task.Unwrap()
                .ConfigureAwait(false);
        }

        var active = _activeBridgeApproval;
        if (active is null)
        {
            return new(false, "Ali is not waiting for a permission decision.", request.RequestId, request.Decision);
        }

        if (!string.Equals(active.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            return new(false, "The supplied request ID does not match Ali's current pending approval.", request.RequestId, request.Decision);
        }

        var choice = request.Decision switch
        {
            "allow-once" => AgentToolApprovalChoice.AllowOnce,
            "allow-arguments" => AgentToolApprovalChoice.AlwaysAllowArguments,
            "allow-tool" => AgentToolApprovalChoice.AlwaysAllowTool,
            "deny" => AgentToolApprovalChoice.Deny,
            _ => throw new InvalidOperationException("The bridge supplied an unsupported approval decision.")
        };
        var accepted = AgentToolApprovalWindow.TryResolve(active.RequestId, choice);
        return new(
            accepted,
            accepted
                ? $"Ali accepted the authenticated bridge decision '{request.Decision}' for the current visible approval."
                : "The visible approval expired before the bridge decision could be applied.",
            request.RequestId,
            request.Decision);
    }

    private static ConversationBridgeMessage ToConversationBridgeMessage(ChatMessageViewModel message) => new(
        message.Id,
        message.Role.ToString(),
        message.Text,
        message.EvidenceStatus.ToString(),
        message.CreatedAt,
        message.IsFlaggedForCorrection,
        MarkdownMessageParser.Parse(message.Text).Select(ToConversationBridgeRenderBlock).ToArray());

    private static ConversationBridgeRenderBlock ToConversationBridgeRenderBlock(MarkdownMessageBlock block) =>
        block switch
        {
            MarkdownHeadingBlock heading => new("heading", heading.Text, Level: heading.Level),
            MarkdownParagraphBlock paragraph => new("paragraph", paragraph.Text),
            MarkdownListItemBlock item => new("list-item", item.Text, Marker: item.Marker),
            MarkdownCodeBlock code => new("code", code.Text),
            MarkdownTableBlock table => new("table", Headers: table.Headers, Rows: table.Rows),
            _ => new("unknown", block.ToString() ?? string.Empty)
        };

    private void Stop()
    {
        CancelActiveUiOperation();
        _activeResponse?.Cancel();
        StopSpeaking();
    }

    public void RequestShutdownCancellation()
    {
        CancelActiveUiOperation();
        _activeResponse?.Cancel();
        _activeVoiceInput?.Cancel();
        _activeSpeech?.Cancel();
        if (!_lifetimeCancellation.IsCancellationRequested)
        {
            _lifetimeCancellation.Cancel();
        }
    }

    private CancellationTokenSource BeginUiOperation(TimeSpan timeout)
    {
        CancelActiveUiOperation();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        operation.CancelAfter(timeout);
        _activeUiOperation = operation;
        return operation;
    }

    private void CompleteUiOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_activeUiOperation, operation))
        {
            _activeUiOperation = null;
        }

        operation.Dispose();
    }

    private void CancelActiveUiOperation()
    {
        try
        {
            _activeUiOperation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private CancellationTokenSource CreateLinkedTimeout(TimeSpan timeout)
    {
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        operation.CancelAfter(timeout);
        return operation;
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
        ClearAgentActivity();
        _currentTurnExecutionReceipts.Clear();
        _conversationId = ConversationSessionFactory.StartFresh().ConversationId;
        _activeConversationHistoryItem = null;
        SelectHistoryItemWithoutLoading(null);
        ComposerText = string.Empty;
        EditableTranscript = string.Empty;
        LastTranscript = string.Empty;
        StatusText = statusText;
        VoiceStatus = "Voice idle.";
        AttachmentStatus = "AI can be wrong.  Always check answers against reliable sources.";
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
        ClearAgentActivity();
        _currentTurnExecutionReceipts.Clear();
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
        AttachmentStatus = "AI can be wrong.  Always check answers against reliable sources.";
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
            ? $"Loaded {MemoryEntries.Count} memory item(s) and {ReminderEntries.Count} calendar event(s). Windows owns scheduled notifications, so they remain active after Ali closes."
            : $"Loaded memory/calendar data with {warningCount} warning(s).";
    }

    private void DeleteSelectedMemory()
    {
        if (SelectedMemoryEntry is null)
        {
            return;
        }

        _services.Memories.Delete(SelectedMemoryEntry.Id);
        RefreshMemoryReminders();
        MemoryReminderStatusText = "Deleted selected memory item only. Conversations and calendar events were not erased.";
    }

    private void ClearMemories()
    {
        var result = System.Windows.MessageBox.Show(
            "Clear local memories on this computer? This removes saved memory items only. It does not remove conversations, calendar events, settings, local models, voice resources, correction reports, or the app itself.",
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
        MemoryReminderStatusText = $"Cleared {removed} memory item(s). Conversations and calendar events were not erased.";
    }

    private void SetSelectedReminderStatus(ReminderStatus status)
    {
        if (SelectedReminderEntry is null)
        {
            return;
        }

        _services.Reminders.SetStatus(SelectedReminderEntry.Id, status);
        RefreshMemoryReminders();
        MemoryReminderStatusText = $"Marked selected calendar event {status}. Its Windows notification was removed.";
    }

    private void ClearReminders()
    {
        var result = System.Windows.MessageBox.Show(
            "Clear local calendar events on this computer? This removes their iCalendar files and Windows scheduled notifications. It does not remove conversations, memories, settings, local models, voice resources, correction reports, or the app itself.",
            "Clear local calendar events",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _services.Reminders.Clear();
        RefreshMemoryReminders();
        MemoryReminderStatusText = $"Cleared {removed} calendar event(s) and their Windows notifications. Conversations and memories were not erased.";
    }

    private async Task BackupUserDataAsync()
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            SaveActiveConversation();
            var backupDirectory = DefaultBackupDirectory();
            Directory.CreateDirectory(backupDirectory);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Ali backup",
                Filter = "Ali backup (*.zip)|*.zip|Zip files (*.zip)|*.zip|All files (*.*)|*.*",
                FileName = $"Ali-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip",
                InitialDirectory = backupDirectory,
                AddExtension = true,
                DefaultExt = ".zip",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
            {
                var cancelReceipt = WriteMaintenanceReceipt("Maintenance.BackupUserData", false, "Ali backup cancelled before changes.", startedAt, DateTimeOffset.Now);
                MaintenanceStatusText = $"Backup cancelled.{Environment.NewLine}{Environment.NewLine}{cancelReceipt}";
                return;
            }

            IsBusy = true;
            StatusText = "Creating Ali backup...";
            MaintenanceStatusText = "Creating backup...";
            var backupService = _services.CreateUserDataBackupService();
            var result = await Task.Run(() => backupService.CreateBackup(dialog.FileName)).ConfigureAwait(true);
            var output = BuildMaintenanceStatusText(
                "Ali backup",
                [
                    ComponentStatus("Backup file", true, result.BackupPath),
                    ComponentStatus("Files included", result.FileCount > 0, $"{result.FileCount} file(s)"),
                    ComponentStatus("Data size", true, FormatBytes(result.TotalBytes))
                ],
                "Keep this zip somewhere safe before repair, reinstall, or machine maintenance.");
            var receipt = WriteMaintenanceReceipt("Maintenance.BackupUserData", true, "Ali backup saved.", startedAt, DateTimeOffset.Now, output);
            MaintenanceStatusText = $"{output}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali backup saved.";
        }
        catch (Exception ex)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.BackupUserData", false, "Ali backup failed.", startedAt, DateTimeOffset.Now, ex.Message);
            MaintenanceStatusText = $"Backup failed: {ex.Message}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali backup failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunComputerHealthCheckAsync()
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            IsBusy = true;
            StatusText = "Running Ali computer health check...";
            MaintenanceStatusText = "Running health check...";

            var componentLines = new List<string>
            {
                DescribeInternetBackendHealth()
            };

            foreach (var check in new[]
                     {
                         ("Ali install", "show install doctor"),
                         ("Computer assistant", "show computer assistant status"),
                         ("PDF tools", "show pdf tool status"),
                         ("Receipts", "show maintenance receipts")
                     })
            {
                var result = BuildUnavailableModuleResult(check.Item2);
                componentLines.Add(FormatMaintenanceCommandResult(check.Item1, result));
            }

            var hasBadComponent = componentLines.Any(line => line.Contains(" - Bad", StringComparison.OrdinalIgnoreCase));
            var output = BuildMaintenanceStatusText(
                "Ali computer health check",
                componentLines,
                hasBadComponent
                    ? "Run Repair Ali Install for Ali data issues, or open Settings for disabled/missing integrations."
                    : "Everything checked is ready.");
            var receipt = WriteMaintenanceReceipt("Maintenance.HealthCheck", true, "Ali computer health check completed.", startedAt, DateTimeOffset.Now, output);
            MaintenanceStatusText = $"{output}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali computer health check finished.";
        }
        catch (OperationCanceledException)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.HealthCheck", false, "Ali computer health check cancelled.", startedAt, DateTimeOffset.Now);
            MaintenanceStatusText = $"Health check cancelled.{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali health check cancelled.";
        }
        catch (Exception ex)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.HealthCheck", false, "Ali computer health check failed safely.", startedAt, DateTimeOffset.Now, ex.Message);
            MaintenanceStatusText = $"Health check failed safely: {ex.Message}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali health check failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RepairAliInstallAsync()
    {
        var startedAt = DateTimeOffset.Now;
        var confirmation = System.Windows.MessageBox.Show(
            "Repair Ali's local install data now?\n\nThis repairs missing example/config helper files, internet backend settings examples, local library folders, and local voice tool paths. It preserves chats, memories, reminders, app settings, installed models, and the selected runtime model.",
            "Repair Ali Install",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.RepairInstall", false, "Ali install repair cancelled before changes.", startedAt, DateTimeOffset.Now);
            MaintenanceStatusText = $"Ali install repair cancelled.{Environment.NewLine}{Environment.NewLine}{receipt}";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Repairing Ali install data...";
            MaintenanceStatusText = "Repairing Ali install data...";

            var messages = new List<string>
            {
                $"Ali install repair: {DateTimeOffset.Now.LocalDateTime:g}"
            };
            var warnings = new List<string>();

            await Task.Run(() => RepairAliInstallData(messages, warnings), _lifetimeCancellation.Token).ConfigureAwait(true);

            ReloadVoiceSettingsFromDisk();
            LoadRuntimeSettings();
            var hasVoiceWarning = warnings.Any(warning => warning.Contains("voice", StringComparison.OrdinalIgnoreCase));
            var output = BuildMaintenanceStatusText(
                "Ali install repair",
                [
                    ComponentStatus("Install data", warnings.Count == 0, warnings.Count == 0 ? "repaired" : $"{warnings.Count} warning(s)"),
                    DescribeInternetBackendHealth(),
                    ComponentStatus("Runtime settings", true, "reloaded"),
                    ComponentStatus("Voice paths", !hasVoiceWarning, hasVoiceWarning ? "needs review" : "repaired")
                ],
                warnings.Count == 0
                    ? "Run Health Check to confirm the refreshed state."
                    : "Open receipts for details, then rerun Health Check.");
            var receipt = WriteMaintenanceReceipt("Maintenance.RepairInstall", warnings.Count == 0, warnings.Count == 0 ? "Ali install repair completed." : "Ali install repair completed with warnings.", startedAt, DateTimeOffset.Now, output);
            MaintenanceStatusText = $"{output}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali install repair finished.";
        }
        catch (OperationCanceledException)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.RepairInstall", false, "Ali install repair cancelled.", startedAt, DateTimeOffset.Now);
            MaintenanceStatusText = $"Ali install repair cancelled.{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali install repair cancelled.";
        }
        catch (Exception ex)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.RepairInstall", false, "Ali install repair failed safely.", startedAt, DateTimeOffset.Now, ex.Message);
            MaintenanceStatusText = $"Ali install repair failed safely: {ex.Message}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali install repair failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunComputerAssistantSetupAsync()
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            IsBusy = true;
            StatusText = "Checking computer assistant setup...";
            MaintenanceStatusText = "Checking computer assistant setup...";

            var componentLines = new List<string>();

            foreach (var check in new[]
                     {
                         ("Computer assistant", "show computer assistant status"),
                         ("Command buttons", "show computer assistant commands"),
                         ("Windows toolkit", "show windows troubleshooting toolkit"),
                         ("Tool integrations", "show tool integration status"),
                         ("PDF tools", "show pdf tool status")
                     })
            {
                var result = BuildUnavailableModuleResult(check.Item2);
                componentLines.Add(FormatMaintenanceCommandResult(check.Item1, result));
            }

            componentLines.Add(ComponentStatus("Permission gates", true, "owner confirmation stays on"));

            var output = BuildMaintenanceStatusText(
                "Computer assistant setup",
                componentLines,
                componentLines.Any(line => line.Contains(" - Bad", StringComparison.OrdinalIgnoreCase))
                    ? "Open Settings for the bad component or run Repair Ali Install for Ali data issues."
                    : "The maintenance buttons are ready to use.");
            var receipt = WriteMaintenanceReceipt("Maintenance.AssistantSetup", true, "Computer assistant setup check completed.", startedAt, DateTimeOffset.Now, output);
            MaintenanceStatusText = $"{output}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Computer assistant setup check finished.";
        }
        catch (OperationCanceledException)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.AssistantSetup", false, "Computer assistant setup check cancelled.", startedAt, DateTimeOffset.Now);
            MaintenanceStatusText = $"Computer assistant setup check cancelled.{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Computer assistant setup check cancelled.";
        }
        catch (Exception ex)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.AssistantSetup", false, "Computer assistant setup check failed safely.", startedAt, DateTimeOffset.Now, ex.Message);
            MaintenanceStatusText = $"Computer assistant setup check failed safely: {ex.Message}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Computer assistant setup check failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunMaintenancePlanAsync()
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            IsBusy = true;
            StatusText = "Running maintenance plan...";
            MaintenanceStatusText = "Running maintenance plan...";

            var componentLines = new List<string>();
            var needsAttention = new List<string>();

            var internetBackendHealth = DescribeInternetBackendHealth();
            componentLines.Add(internetBackendHealth);
            if (internetBackendHealth.Contains(" - Bad", StringComparison.OrdinalIgnoreCase))
            {
                needsAttention.Add("Internet source backend needs configuration.");
            }

            foreach (var check in new[]
                     {
                         ("Ali install", "show install doctor"),
                         ("Computer assistant", "show computer assistant status"),
                         ("PDF tools", "show pdf tool status"),
                         ("Receipts", "show maintenance receipts")
                     })
            {
                var result = BuildUnavailableModuleResult(check.Item2);
                var line = FormatMaintenanceCommandResult(check.Item1, result);
                componentLines.Add(line);
                if (line.Contains(" - Bad", StringComparison.OrdinalIgnoreCase))
                {
                    needsAttention.Add($"{check.Item1} needs attention.");
                }
            }

            var output = BuildMaintenanceStatusText(
                "Ali maintenance plan",
                componentLines,
                BuildMaintenanceNextAction(needsAttention));
            var receipt = WriteMaintenanceReceipt("Maintenance.Plan", needsAttention.Count == 0, needsAttention.Count == 0 ? "Maintenance plan completed with no immediate repair indicated." : "Maintenance plan completed with recommended next actions.", startedAt, DateTimeOffset.Now, output);
            MaintenanceStatusText = $"{output}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Maintenance plan finished.";
        }
        catch (OperationCanceledException)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.Plan", false, "Maintenance plan cancelled.", startedAt, DateTimeOffset.Now);
            MaintenanceStatusText = $"Maintenance plan cancelled.{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Maintenance plan cancelled.";
        }
        catch (Exception ex)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.Plan", false, "Maintenance plan failed safely.", startedAt, DateTimeOffset.Now, ex.Message);
            MaintenanceStatusText = $"Maintenance plan failed safely: {ex.Message}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Maintenance plan failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunMaintenanceDiagnosticAsync(string title, string command, string actionType)
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            IsBusy = true;
            StatusText = $"Running {title.ToLowerInvariant()}...";
            MaintenanceStatusText = $"Running {title.ToLowerInvariant()}...";

            var result = BuildUnavailableModuleResult(command);
            var output = BuildMaintenanceDiagnosticText(title, command, result);

            var succeeded = result is { Handled: true, Succeeded: true };
            var receipt = WriteMaintenanceReceipt(actionType, succeeded, $"{title} completed.", startedAt, DateTimeOffset.Now, output);
            MaintenanceStatusText = $"{output}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = $"{title} finished.";
        }
        catch (OperationCanceledException)
        {
            var receipt = WriteMaintenanceReceipt(actionType, false, $"{title} cancelled.", startedAt, DateTimeOffset.Now);
            MaintenanceStatusText = $"{title} cancelled.{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = $"{title} cancelled.";
        }
        catch (Exception ex)
        {
            var receipt = WriteMaintenanceReceipt(actionType, false, $"{title} failed safely.", startedAt, DateTimeOffset.Now, ex.Message);
            MaintenanceStatusText = $"{title} failed safely: {ex.Message}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = $"{title} failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static DiagnosticCommandResult BuildUnavailableModuleResult(string command) =>
        new(
            Handled: false,
            Succeeded: false,
            Message: $"The module command '{command}' is not available from the active app shell yet.");

    private async Task RestoreUserDataAsync()
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            var backupDirectory = DefaultBackupDirectory();
            Directory.CreateDirectory(backupDirectory);
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Restore Ali backup",
                Filter = "Ali backup (*.zip)|*.zip|Zip files (*.zip)|*.zip|All files (*.*)|*.*",
                InitialDirectory = backupDirectory,
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                var cancelReceipt = WriteMaintenanceReceipt("Maintenance.RestoreUserData", false, "Ali restore cancelled before selecting a backup.", startedAt, DateTimeOffset.Now);
                MaintenanceStatusText = $"Restore cancelled.{Environment.NewLine}{Environment.NewLine}{cancelReceipt}";
                return;
            }

            var backupService = _services.CreateUserDataBackupService();
            var manifest = backupService.InspectBackup(dialog.FileName);
            var confirmation = System.Windows.MessageBox.Show(
                $"Restore this Ali backup?\n\nCreated: {manifest.CreatedAt.LocalDateTime:g}\n\nThis overwrites Ali conversations, memories, reminders, settings, sources, local indexes, voice settings, runtime settings, and generated documents from the backup. Ollama models are not changed.\n\nAli will pause active work before restoring.",
                "Restore Ali Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                var cancelReceipt = WriteMaintenanceReceipt("Maintenance.RestoreUserData", false, "Ali restore cancelled before changes.", startedAt, DateTimeOffset.Now);
                MaintenanceStatusText = $"Restore cancelled.{Environment.NewLine}{Environment.NewLine}{cancelReceipt}";
                return;
            }

            IsBusy = true;
            StatusText = "Restoring Ali backup...";
            MaintenanceStatusText = "Restoring backup...";
            Stop();
            StopInputLevelMonitor();
            _services.ConfigureRuntimeCandidate(RuntimeSettingsStore.GetDefaultOptions());

            var result = await Task.Run(() => backupService.RestoreBackup(dialog.FileName)).ConfigureAwait(true);
            ReloadAfterUserDataRestore(result);
            var output = BuildMaintenanceStatusText(
                "Ali restore",
                [
                    ComponentStatus("Backup restore", true, $"from {result.BackupCreatedAt.LocalDateTime:g}"),
                    ComponentStatus("Runtime settings", true, "reloaded"),
                    ComponentStatus("Voice settings", true, "reloaded"),
                    ComponentStatus("Conversation data", true, "reloaded")
                ],
                "Restart Ali if the restored assistant name or profile differs from this session.");
            var receipt = WriteMaintenanceReceipt("Maintenance.RestoreUserData", true, "Ali backup restored.", startedAt, DateTimeOffset.Now, output);
            MaintenanceStatusText = $"{output}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali backup restored.";
        }
        catch (Exception ex)
        {
            var receipt = WriteMaintenanceReceipt("Maintenance.RestoreUserData", false, "Ali restore failed.", startedAt, DateTimeOffset.Now, ex.Message);
            MaintenanceStatusText = $"Restore failed: {ex.Message}{Environment.NewLine}{Environment.NewLine}{receipt}";
            StatusText = "Ali restore failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReloadAfterUserDataRestore(UserDataRestoreResult restore)
    {
        LoadRuntimeSettings();
        ReloadVoiceSettingsFromDisk();
        RefreshConversationHistory();
        RefreshMemoryReminders();
        var restoredRoot = Path.GetFullPath(restore.RestoredProfileDataRoot);
        var userDataRoot = Path.GetFullPath(_services.UserDataRoot);
        var profileDataRoot = Path.GetFullPath(_services.ProfileDataRoot);
        var restoredRootPrefix = restoredRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
        if (string.Equals(restoredRoot, userDataRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(restoredRoot, profileDataRoot, StringComparison.OrdinalIgnoreCase)
            || profileDataRoot.StartsWith(restoredRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            ResetToFreshConversation("Backup restored. Start a new message or open a restored chat from history.");
        }
    }

    private void ReloadVoiceSettingsFromDisk()
    {
        _voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(_services.DataRoot);
        _loadingVoiceSettings = true;
        try
        {
            ExtraInputGainDb = _voiceSettings.ExtraInputGainDb;
            NormalizeBeforeStt = _voiceSettings.NormalizeBeforeStt;
            RetainDebugAudio = _voiceSettings.RetainDebugAudio;
            AssistantReadsRepliesOutLoud = _voiceSettings.AssistantReadsRepliesOutLoud;
            AutoSendVoiceTranscripts = _voiceSettings.AutoSendVoiceTranscripts;
            SpeechRate = NormalizeSpeechRate(_voiceSettings.SpeechRate);
            PushToTalkKeyText = NormalizePushToTalkKey(_voiceSettings.PushToTalkKey);
        }
        finally
        {
            _loadingVoiceSettings = false;
        }

        RefreshVoiceSettingsChoices();
        ApplyVoiceToolSettings(saveSettings: false, reportStatus: false);
        RefreshSpeechToolStatuses();
        VoiceSettingsStatusText = "Voice settings reloaded from restored backup.";
    }

    private void RepairAliInstallData(List<string> messages, List<string> warnings)
    {
        RuntimeSettingsStore.WriteExample(_services.DataRoot);
        messages.Add("Runtime settings example verified; selected runtime model was not changed.");
        WebSourceBackendSettingsStore.WriteExample(_services.DataRoot);
        WebSourceBackendSettingsStore.WriteDefaultIfMissing(_services.DataRoot);
        messages.Add("Internet source backend settings example verified; default settings file verified.");
        LocalVectorLibrarySettingsStore.WriteExample(_services.DataRoot);
        _services.CreateLocalVectorLibraryRetriever().WriteExample();
        messages.Add("Local library settings and index folders verified.");
        messages.Add("Deprecated feature settings were skipped.");
        RepairVoiceToolSettings(messages, warnings);
    }

    private void RepairVoiceToolSettings(List<string> messages, List<string> warnings)
    {
        var voiceRoot = FindLocalVoiceResourceDirectory();
        if (string.IsNullOrWhiteSpace(voiceRoot))
        {
            warnings.Add("Local voice resources were not found; voice settings repair was skipped.");
            return;
        }

        try
        {
            _voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(_services.DataRoot);
            LoadTextToSpeechVoiceChoices();

            var piperModel = PreferredPiperModelPath();
            var piperVoiceId = string.IsNullOrWhiteSpace(piperModel)
                ? _voiceSettings.PiperVoiceId
                : Path.GetFileNameWithoutExtension(ResolvePortablePath(piperModel));
            var kittenModel = FindLocalKittenModelRoot();
            var hasKitten = !string.IsNullOrWhiteSpace(kittenModel);
            var settings = _voiceSettings with
            {
                WhisperExecutablePath = PreferConfigured(FindLocalWhisperPythonExecutable(), _voiceSettings.WhisperExecutablePath),
                WhisperModelPath = PreferConfigured(FindLocalWhisperModelRoot(), _voiceSettings.WhisperModelPath),
                WhisperArgumentsTemplate = PreferConfigured(BuildLocalWhisperArgumentsTemplate(), _voiceSettings.WhisperArgumentsTemplate),
                TextToSpeechEngine = hasKitten
                    ? TextToSpeechEngines.Kitten
                    : TextToSpeechEngines.Normalize(_voiceSettings.TextToSpeechEngine),
                PiperExecutablePath = PreferConfigured(FindLocalPiperExecutable(), _voiceSettings.PiperExecutablePath),
                PiperModelPath = PreferConfigured(piperModel, _voiceSettings.PiperModelPath),
                PiperVoiceId = piperVoiceId,
                PiperArgumentsTemplate = BuildLocalPiperArgumentsTemplate(),
                KittenExecutablePath = PreferConfigured(FindLocalKittenPythonExecutable(), _voiceSettings.KittenExecutablePath),
                KittenModelPath = PreferConfigured(kittenModel, _voiceSettings.KittenModelPath),
                KittenVoiceId = _voiceSettings.KittenVoiceId ?? KittenVoiceCatalog.DefaultVoiceId,
                KittenArgumentsTemplate = PreferConfigured(BuildLocalKittenArgumentsTemplate(), _voiceSettings.KittenArgumentsTemplate)
            };

            VoiceRuntimeSettingsStore.Save(_services.DataRoot, settings);
            _voiceSettings = settings;
            messages.Add($"Voice settings repaired to prefer installed local resources: {voiceRoot}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or NotSupportedException)
        {
            warnings.Add($"Voice settings could not be repaired: {ex.Message}");
        }
    }

    private string DescribeInternetBackendHealth()
    {
        try
        {
            var settings = _services.LoadWebSourceBackendSettings();
            if (!settings.Enabled)
            {
                return ComponentStatus("Internet source backend", false, "disabled");
            }

            var tavilyConfigured = !string.IsNullOrWhiteSpace(settings.ResolveTavilyApiKey());
            var firecrawlConfigured = !string.IsNullOrWhiteSpace(settings.ResolveFirecrawlApiKey());
            var braveConfigured = !string.IsNullOrWhiteSpace(settings.ResolveBraveSearchApiKey());
            var serperConfigured = !string.IsNullOrWhiteSpace(settings.ResolveSerperApiKey());
            var configuredCount = new[] { tavilyConfigured, firecrawlConfigured, braveConfigured, serperConfigured }.Count(value => value);
            if (configuredCount == 0)
            {
                return ComponentStatus("Internet source backend", false, "no provider keys configured");
            }

            var primary = tavilyConfigured ? "Tavily primary" : "fallback-only";
            return ComponentStatus("Internet source backend", true, $"{configuredCount}/4 providers configured; {primary}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ComponentStatus("Internet source backend", false, ShortMaintenanceDetail(ex.Message));
        }
    }

    private string WriteMaintenanceReceipt(
        string actionType,
        bool succeeded,
        string summary,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string? details = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MaintenanceReceiptPath)!);
            var receipt = succeeded
                ? ActionReceipt.Success(actionType, summary, startedAt, completedAt, details)
                : ActionReceipt.Failure(actionType, summary, startedAt, completedAt, standardError: details);
            var line = JsonSerializer.Serialize(receipt, MaintenanceReceiptJsonOptions);
            File.AppendAllText(MaintenanceReceiptPath, line + Environment.NewLine);
            return $"Receipt: {MaintenanceReceiptPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            return $"Receipt warning: {ex.Message}";
        }
    }

    private static string BuildMaintenanceNextAction(IReadOnlyCollection<string> needsAttention)
    {
        if (needsAttention.Count == 0)
        {
            return "No immediate repair is indicated. Create a backup before major machine changes.";
        }

        var firstIssue = needsAttention.Distinct(StringComparer.OrdinalIgnoreCase).First();
        return $"{firstIssue} Use Repair Ali Install for Ali data issues, or Settings for optional integrations.";
    }

    private static string BuildMaintenanceStatusText(
        string title,
        IEnumerable<string> componentLines,
        string nextAction)
    {
        var lines = new List<string>
        {
            $"{title}: {DateTimeOffset.Now.LocalDateTime:g}"
        };
        lines.AddRange(componentLines.Where(line => !string.IsNullOrWhiteSpace(line)));
        if (!string.IsNullOrWhiteSpace(nextAction))
        {
            lines.Add($"Next - {nextAction}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildMaintenanceDiagnosticText(string title, string command, DiagnosticCommandResult result)
    {
        var status = result is { Handled: true, Succeeded: true };
        var lines = new List<string>
        {
            $"{title}: {DateTimeOffset.Now.LocalDateTime:g}",
            ComponentStatus(title, status, status ? "read-only check complete" : ShortMaintenanceDetail(result.Message))
        };

        lines.AddRange(BuildMaintenanceDiagnosticBody(command, result.Message));
        lines.Add(status
            ? "Next - Review the items above. No changes were made."
            : "Next - Run Check Tools, then try this diagnostic again.");
        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> BuildMaintenanceDiagnosticBody(string command, string message)
    {
        var lines = SplitMaintenanceLines(message);
        if (command.StartsWith("collect process evidence", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("diagnose build lock", StringComparison.OrdinalIgnoreCase))
        {
            return CompactProcessLines(lines);
        }

        if (command.StartsWith("diagnose port", StringComparison.OrdinalIgnoreCase))
        {
            return CompactPortLines(lines);
        }

        if (command.StartsWith("inspect services", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "Services - Ready for read-only review",
                "Startup - Ready for read-only review",
                "Changes - None"
            ];
        }

        if (command.StartsWith("plan disk cleanup", StringComparison.OrdinalIgnoreCase))
        {
            return CompactDiskCleanupLines(lines);
        }

        if (command.StartsWith("plan ", StringComparison.OrdinalIgnoreCase))
        {
            return CompactPlanLines(lines);
        }

        return lines
            .Where(line => !LooksLikeMaintenanceBoilerplate(line))
            .Take(8)
            .ToList();
    }

    private static IReadOnlyList<string> SplitMaintenanceLines(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? Array.Empty<string>()
            : message
                .ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<string> CompactProcessLines(IReadOnlyList<string> lines)
    {
        var rows = lines
            .Where(line => line.StartsWith("- PID ", StringComparison.OrdinalIgnoreCase))
            .Select(FormatProcessRow)
            .Take(12)
            .ToList();
        if (rows.Count > 0)
        {
            return rows;
        }

        var noMatch = lines.FirstOrDefault(line => line.Contains("No matching", StringComparison.OrdinalIgnoreCase)
                                                   || line.Contains("No common build-lock", StringComparison.OrdinalIgnoreCase));
        return [ShortMaintenanceDetail(noMatch ?? "Processes - No matching processes found")];
    }

    private static IReadOnlyList<string> CompactPortLines(IReadOnlyList<string> lines)
    {
        var rows = lines
            .Where(line => Regex.IsMatch(line, @"^-\s+(TCP|UDP)\s+", RegexOptions.IgnoreCase))
            .Select(FormatPortRow)
            .Take(8)
            .ToList();
        if (rows.Count > 0)
        {
            return rows;
        }

        var port = lines.FirstOrDefault(line => line.StartsWith("Port:", StringComparison.OrdinalIgnoreCase));
        var noMatch = lines.FirstOrDefault(line => line.Contains("No listener", StringComparison.OrdinalIgnoreCase)
                                                   || line.Contains("No connection", StringComparison.OrdinalIgnoreCase));
        return
        [
            ShortMaintenanceDetail(port ?? "Port - unknown"),
            ShortMaintenanceDetail(noMatch ?? "Owner - none found")
        ];
    }

    private static IReadOnlyList<string> CompactDiskCleanupLines(IReadOnlyList<string> lines)
    {
        var driveRows = lines
            .Where(line => Regex.IsMatch(line, @"^-\s+[A-Z]:\\", RegexOptions.IgnoreCase))
            .Select(line => ShortMaintenanceDetail(line[2..]))
            .Take(8)
            .ToList();
        if (driveRows.Count > 0)
        {
            driveRows.Insert(0, "Drive space:");
            return driveRows;
        }

        return ["Drive space - No ready drives found"];
    }

    private static IReadOnlyList<string> CompactPlanLines(IReadOnlyList<string> lines)
    {
        var compact = new List<string>();
        var target = lines.FirstOrDefault(line => line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)
                                                  || line.StartsWith("Scenario:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(target))
        {
            compact.Add(ShortMaintenanceDetail(target));
        }

        compact.AddRange(lines
            .Where(line => Regex.IsMatch(line, @"^\d+\.\s+")
                           || line.StartsWith("- Exact ", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Check ", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Identify ", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Verify ", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeMaintenanceBoilerplate(line))
            .Select(line => ShortMaintenanceDetail(line.TrimStart('-', ' ')))
            .Take(5));

        if (compact.Count == 0)
        {
            compact.Add("Plan - Gather evidence first, then choose one approved next action.");
        }

        return compact;
    }

    private static string FormatProcessRow(string line)
    {
        var text = line.TrimStart('-', ' ');
        var match = Regex.Match(text, @"^PID\s+(?<pid>\d+):\s*(?<name>[^;]+)(?:;\s*memory\s*(?<memory>[^;]+))?(?:;\s*started\s*(?<started>[^;]+))?(?:;\s*path\s*(?<path>.+))?$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return ShortMaintenanceDetail(text.Replace("; ", " - "));
        }

        var parts = new List<string>
        {
            $"PID {match.Groups["pid"].Value}",
            $"Name {match.Groups["name"].Value.Trim()}"
        };
        AddMatchedPart(parts, "Memory", match.Groups["memory"]);
        AddMatchedPart(parts, "Started", match.Groups["started"]);
        AddMatchedPart(parts, "Path", match.Groups["path"]);
        return ShortMaintenanceDetail(string.Join(" - ", parts));
    }

    private static string FormatPortRow(string line)
    {
        var text = line.TrimStart('-', ' ');
        var match = Regex.Match(text, @"^(?<proto>TCP|UDP)\s+(?<address>\S+)\s+(?<state>\S+)\s+PID\s+(?<pid>\d+):\s*(?<name>[^;]+)(?:;\s*memory\s*(?<memory>[^;]+))?(?:;\s*path\s*(?<path>.+))?$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return ShortMaintenanceDetail(text.Replace("; ", " - "));
        }

        var parts = new List<string>
        {
            $"PID {match.Groups["pid"].Value}",
            $"Name {match.Groups["name"].Value.Trim()}",
            $"Port {match.Groups["address"].Value}",
            $"State {match.Groups["state"].Value}"
        };
        AddMatchedPart(parts, "Memory", match.Groups["memory"]);
        AddMatchedPart(parts, "Path", match.Groups["path"]);
        return ShortMaintenanceDetail(string.Join(" - ", parts));
    }

    private static void AddMatchedPart(ICollection<string> parts, string label, Group group)
    {
        if (group.Success && !string.IsNullOrWhiteSpace(group.Value))
        {
            parts.Add($"{label} {group.Value.Trim()}");
        }
    }

    private static bool LooksLikeMaintenanceBoilerplate(string line) =>
        line.Contains("Useful Ali commands", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Next safe commands", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Approval boundaries", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Approval gate", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Stop rules", StringComparison.OrdinalIgnoreCase)
        || line.Contains("No files were", StringComparison.OrdinalIgnoreCase)
        || line.Contains("No processes were", StringComparison.OrdinalIgnoreCase)
        || line.Contains("No installer was", StringComparison.OrdinalIgnoreCase)
        || line.Contains("No drivers", StringComparison.OrdinalIgnoreCase);

    private static string FormatMaintenanceCommandResult(string component, DiagnosticCommandResult result)
    {
        if (!result.Handled)
        {
            return ComponentStatus(component, false, "not available");
        }

        if (!result.Succeeded)
        {
            return ComponentStatus(component, false, ShortMaintenanceDetail(result.Message));
        }

        var problem = FindMaintenanceProblemHint(component, result.Message);
        return ComponentStatus(component, problem is null, problem ?? "ready");
    }

    private static string ComponentStatus(string component, bool good, string? detail = null)
    {
        var status = good ? "Good" : "Bad";
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({ShortMaintenanceDetail(detail)})";
        return $"{component} - {status}{suffix}";
    }

    private static string? FindMaintenanceProblemHint(string component, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        foreach (var rawLine in message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimStart('-', ' ');
            if (ShouldIgnoreMaintenanceProblemLine(component, line))
            {
                continue;
            }

            if (line.Contains("missing", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || line.Contains("could not", StringComparison.OrdinalIgnoreCase))
            {
                return ShortMaintenanceDetail(line);
            }
        }

        return null;
    }

    private static bool ShouldIgnoreMaintenanceProblemLine(string component, string line)
    {
        if (line.Contains("No files were changed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("No immediate", StringComparison.OrdinalIgnoreCase)
            || line.Contains("No recent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (component.Equals("Ali install", StringComparison.OrdinalIgnoreCase))
        {
            return line.Contains("Notepad++", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Confirm ", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Manual dependency", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ShortMaintenanceDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        var compact = Regex.Replace(detail.Trim(), @"\s+", " ");
        return compact.Length <= 96 ? compact : compact[..93] + "...";
    }

    private static string TrimMaintenanceText(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        return text[..Math.Max(0, maxCharacters - 32)] + Environment.NewLine + "... trimmed for display.";
    }

    private static string DefaultBackupDirectory() =>
        Path.Combine(AliServices.LocalAliRoot, "Backups");

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
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

    private bool ShouldPersistMessage(ChatMessageViewModel message) =>
        !string.IsNullOrWhiteSpace(message.Text)
        && !message.Text.StartsWith($"{AssistantName} bootstrap ready.", StringComparison.Ordinal);

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
        VramMeter.Update(
            snapshot.VramPercent,
            "VRAM counter unavailable",
            snapshot.VramUsageBytes,
            snapshot.VramLimitBytes);
    }

    private void EraseHistory()
    {
        var confirmed = DarkConfirmationWindow.Show(
            System.Windows.Application.Current?.MainWindow,
            "Erase saved chat history",
            "Erase saved chat history on this computer? This removes saved conversations and recent chat entries. It does not remove local models, settings, voice resources, correction reports, memories, reminders, or the app itself.",
            "Erase History");

        if (!confirmed)
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

        var confirmed = DarkConfirmationWindow.Show(
            System.Windows.Application.Current?.MainWindow,
            "Erase saved chat",
            $"Erase saved chat \"{item.Title}\" from this computer? This does not remove settings, local models, voice resources, correction reports, memories, reminders, or the app itself.",
            "Erase Chat");

        if (!confirmed)
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

    private async Task TogglePushToTalkAsync()
    {
        if (_pushToTalkKeyDown)
        {
            await StopPushToTalkAsync().ConfigureAwait(true);
            return;
        }

        if (IsRecording || IsTranscribing)
        {
            await StopVoiceRecordingOrTranscriptionAsync().ConfigureAwait(true);
            return;
        }

        await StartPushToTalkAsync().ConfigureAwait(true);
    }

    public bool IsPushToTalkKey(Key key) =>
        AutoSendVoiceTranscripts
        && TryParsePushToTalkKey(_pushToTalkKeyText, out var configuredKey)
        && key == configuredKey;

    public bool IsPushToTalkActive => _pushToTalkKeyDown;

    public void BeginAssignPushToTalkKey()
    {
        IsAssigningPushToTalkKey = true;
        VoiceSettingsStatusText = $"Press the key to use for Push to Talk. {AssistantName} will save the next keypress.";
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
        if (!AutoSendVoiceTranscripts || _pushToTalkKeyDown || IsBusy)
        {
            return;
        }

        _pushToTalkKeyDown = true;
        OnPropertyChanged(nameof(PushToTalkKeyButtonText));
        _interactionRuntime?.UpdatePushToTalk(enabled: true, pressed: true);
        AttentionStatus = "Attention: Yes (Push to Talk)";
        VoiceStatus = $"Listening while {PushToTalkKeyLabel} is held.";
        await Task.CompletedTask;
    }

    public async Task StopPushToTalkAsync()
    {
        if (!_pushToTalkKeyDown)
        {
            return;
        }

        _pushToTalkKeyDown = false;
        OnPropertyChanged(nameof(PushToTalkKeyButtonText));
        _interactionRuntime?.UpdatePushToTalk(AutoSendVoiceTranscripts, pressed: false);
        VoiceStatus = "Push to Talk released; secured utterance is being processed.";
        await Task.CompletedTask;
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

    private void LoadInternetBackendSettings()
    {
        var settings = _services.LoadWebSourceBackendSettings();
        InternetBackendEnabled = settings.Enabled;
        InternetGeminiGroundedSearchEnabled = settings.GeminiGroundedSearchEnabled;
        InternetGeminiApiKeyText = settings.GeminiApiKey ?? string.Empty;
        InternetGeminiHourlyLimitText = settings.GeminiMaxRequestsPerHour.ToString(CultureInfo.InvariantCulture);
        InternetGeminiDailyLimitText = settings.GeminiMaxRequestsPerDay.ToString(CultureInfo.InvariantCulture);
        InternetGeminiMonthlySpendLimitText = settings.GeminiMonthlySpendLimitUsd.ToString("0.00", CultureInfo.InvariantCulture);
        InternetTavilyApiKeyText = settings.TavilyApiKey ?? string.Empty;
        InternetFirecrawlApiKeyText = settings.FirecrawlApiKey ?? string.Empty;
        InternetBraveSearchApiKeyText = settings.BraveSearchApiKey ?? string.Empty;
        InternetSerperApiKeyText = settings.SerperApiKey ?? string.Empty;
        InternetBackendStatusText = DescribeInternetBackendSettings(settings);
        InternetGeminiUsageText = BuildGeminiUsageText(settings);
        InternetTavilyUsageText = BuildProviderUsagePrompt("Tavily", settings.TavilyApiKeyEnvironmentVariable, settings.ResolveTavilyApiKey());
        InternetFirecrawlUsageText = BuildProviderUsagePrompt("Firecrawl", settings.FirecrawlApiKeyEnvironmentVariable, settings.ResolveFirecrawlApiKey());
        InternetBraveSearchUsageText = BuildProviderUsagePrompt("Brave Search", settings.BraveSearchApiKeyEnvironmentVariable, settings.ResolveBraveSearchApiKey());
        InternetSerperUsageText = BuildProviderUsagePrompt("Serper", settings.SerperApiKeyEnvironmentVariable, settings.ResolveSerperApiKey());
        IsGoogleBillingSettingsUnlocked = !IsGoogleBillingProtectionConfigured;
        GoogleBillingProtectionStatusText = IsGoogleBillingProtectionConfigured
            ? "Protected and locked. Only the owner password can change Google API access or spending limits."
            : "Not protected yet. Set an owner password after entering the Google key and safety limits.";
        NotifyGoogleBillingProtectionChanged();
    }

    private void SaveInternetBackendSettings()
    {
        var existing = _services.LoadWebSourceBackendSettings();
        var canEditGoogleBilling = CanEditGoogleBillingSettings;
        var geminiHourlyLimit = int.TryParse(InternetGeminiHourlyLimitText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHourlyLimit)
            ? Math.Clamp(parsedHourlyLimit, 1, 1000)
            : existing.GeminiMaxRequestsPerHour;
        var geminiDailyLimit = int.TryParse(InternetGeminiDailyLimitText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDailyLimit)
            ? Math.Clamp(parsedDailyLimit, 1, 5000)
            : existing.GeminiMaxRequestsPerDay;
        var geminiSpendLimit = decimal.TryParse(InternetGeminiMonthlySpendLimitText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedSpendLimit)
            ? Math.Clamp(parsedSpendLimit, 0.10m, 1000m)
            : existing.GeminiMonthlySpendLimitUsd;
        var settings = new WebSourceBackendSettings
        {
            Enabled = InternetBackendEnabled,
            GeminiGroundedSearchEnabled = canEditGoogleBilling ? InternetGeminiGroundedSearchEnabled : existing.GeminiGroundedSearchEnabled,
            GeminiApiKeyEnvironmentVariable = existing.GeminiApiKeyEnvironmentVariable,
            GeminiApiKey = canEditGoogleBilling ? NullIfWhiteSpace(InternetGeminiApiKeyText) : existing.GeminiApiKey,
            GeminiGroundedSearchModel = GeminiGroundedSearchProvider.PinnedModel,
            GeminiMaxOutputTokens = existing.GeminiMaxOutputTokens,
            GeminiMaxRequestsPerHour = canEditGoogleBilling ? geminiHourlyLimit : existing.GeminiMaxRequestsPerHour,
            GeminiMaxRequestsPerDay = canEditGoogleBilling ? geminiDailyLimit : existing.GeminiMaxRequestsPerDay,
            GeminiMonthlySpendLimitUsd = canEditGoogleBilling ? geminiSpendLimit : existing.GeminiMonthlySpendLimitUsd,
            TavilyBaseUrl = existing.TavilyBaseUrl,
            TavilyApiKeyEnvironmentVariable = existing.TavilyApiKeyEnvironmentVariable,
            TavilyApiKey = NullIfWhiteSpace(InternetTavilyApiKeyText),
            TavilySearchDepth = existing.TavilySearchDepth,
            TavilyCurrentNewsTimeRange = existing.TavilyCurrentNewsTimeRange,
            FirecrawlBaseUrl = existing.FirecrawlBaseUrl,
            FirecrawlApiKeyEnvironmentVariable = existing.FirecrawlApiKeyEnvironmentVariable,
            FirecrawlApiKey = NullIfWhiteSpace(InternetFirecrawlApiKeyText),
            BraveSearchBaseUrl = existing.BraveSearchBaseUrl,
            BraveSearchApiKeyEnvironmentVariable = existing.BraveSearchApiKeyEnvironmentVariable,
            BraveSearchApiKey = NullIfWhiteSpace(InternetBraveSearchApiKeyText),
            SerperBaseUrl = existing.SerperBaseUrl,
            SerperApiKeyEnvironmentVariable = existing.SerperApiKeyEnvironmentVariable,
            SerperApiKey = NullIfWhiteSpace(InternetSerperApiKeyText),
            SerperFreeQueryAllowance = existing.SerperFreeQueryAllowance,
            UseFirecrawlForPageExtraction = existing.UseFirecrawlForPageExtraction,
            UseFirecrawlSearchScrapeOptions = existing.UseFirecrawlSearchScrapeOptions,
            MaxSearchResults = existing.MaxSearchResults,
            MaxExtractedPages = existing.MaxExtractedPages,
            MaxExcerptCharacters = existing.MaxExcerptCharacters,
            RequestTimeoutSeconds = existing.RequestTimeoutSeconds
        };
        _services.SaveWebSourceBackendSettings(settings);
        InternetBackendStatusText = DescribeInternetBackendSettings(settings);
        InternetGeminiUsageText = BuildGeminiUsageText(settings);
        InternetTavilyUsageText = BuildProviderUsagePrompt("Tavily", settings.TavilyApiKeyEnvironmentVariable, settings.ResolveTavilyApiKey());
        InternetFirecrawlUsageText = BuildProviderUsagePrompt("Firecrawl", settings.FirecrawlApiKeyEnvironmentVariable, settings.ResolveFirecrawlApiKey());
        InternetBraveSearchUsageText = BuildProviderUsagePrompt("Brave Search", settings.BraveSearchApiKeyEnvironmentVariable, settings.ResolveBraveSearchApiKey());
        InternetSerperUsageText = BuildProviderUsagePrompt("Serper", settings.SerperApiKeyEnvironmentVariable, settings.ResolveSerperApiKey());
        StatusText = "Internet source backend settings saved.";
    }

    private static string DescribeInternetBackendSettings(WebSourceBackendSettings settings)
    {
        if (!settings.Enabled)
        {
            return "Internet source backend is disabled.";
        }

        var tavilyConfigured = !string.IsNullOrWhiteSpace(settings.ResolveTavilyApiKey());
        var geminiConfigured = settings.GeminiGroundedSearchEnabled
            && !string.IsNullOrWhiteSpace(settings.ResolveGeminiApiKey());
        var firecrawlConfigured = !string.IsNullOrWhiteSpace(settings.ResolveFirecrawlApiKey());
        var braveConfigured = !string.IsNullOrWhiteSpace(settings.ResolveBraveSearchApiKey());
        var serperConfigured = !string.IsNullOrWhiteSpace(settings.ResolveSerperApiKey());
        var configured = new List<string>();
        var missing = new List<string>();

        AddProviderConfigurationSummary(configured, missing, "Google Grounding", settings.GeminiApiKeyEnvironmentVariable, geminiConfigured);
        AddProviderConfigurationSummary(configured, missing, "Tavily", settings.TavilyApiKeyEnvironmentVariable, tavilyConfigured);
        AddProviderConfigurationSummary(configured, missing, "Firecrawl", settings.FirecrawlApiKeyEnvironmentVariable, firecrawlConfigured);
        AddProviderConfigurationSummary(configured, missing, "Brave Search", settings.BraveSearchApiKeyEnvironmentVariable, braveConfigured);
        AddProviderConfigurationSummary(configured, missing, "Serper", settings.SerperApiKeyEnvironmentVariable, serperConfigured);

        var chain = "Search chain: Google Grounding -> Tavily -> Firecrawl -> Brave Search -> Serper.";
        if (configured.Count == 0)
        {
            return $"{chain} No provider keys are configured yet.";
        }

        return missing.Count == 0
            ? $"{chain} Configured: {string.Join(", ", configured)}."
            : $"{chain} Configured: {string.Join(", ", configured)}. Missing: {string.Join(", ", missing)}.";
    }

    private static void AddProviderConfigurationSummary(
        List<string> configured,
        List<string> missing,
        string provider,
        string environmentVariable,
        bool isConfigured)
    {
        if (isConfigured)
        {
            configured.Add(provider);
        }
        else
        {
            missing.Add($"{provider} ({environmentVariable})");
        }
    }

    private static string BuildProviderUsagePrompt(string provider, string environmentVariable, string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey)
            ? $"Not configured. Save a key here or set {environmentVariable}."
            : $"{provider} configured. Use Test to check connectivity and any quota estimate Ali can read.";

    private string BuildGeminiUsageText(WebSourceBackendSettings settings) =>
        BuildProviderUsagePrompt("Google Grounding", settings.GeminiApiKeyEnvironmentVariable, settings.ResolveGeminiApiKey())
        + Environment.NewLine
        + _services.GetGeminiGroundingUsageStatus(settings);

    private async Task TestInternetProviderAsync(InternetSearchProvider provider)
    {
        SaveInternetBackendSettings();
        IsBusy = true;
        RaiseCommandStates();
        StatusText = $"Testing {InternetProviderDisplayName(provider)} internet backend...";
        try
        {
            using var operation = BeginUiOperation(TimeSpan.FromSeconds(45));
            var result = await _services.CreateWebSourceRetriever()
                .TestProviderAsync(provider, "current weather in Birmingham Alabama", operation.Token)
                .ConfigureAwait(true);
            ApplyInternetProviderProbeResult(provider, result);
            InternetBackendStatusText = DescribeInternetBackendSettings(_services.LoadWebSourceBackendSettings());
            StatusText = result.Succeeded
                ? $"{result.Provider} test query succeeded."
                : $"{result.Provider} test query did not return usable results.";
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task TestConfiguredInternetBackendsAsync()
    {
        SaveInternetBackendSettings();
        IsBusy = true;
        RaiseCommandStates();
        StatusText = "Testing primary configured internet backend...";
        try
        {
            using var operation = BeginUiOperation(TimeSpan.FromSeconds(90));
            var results = await _services.CreateWebSourceRetriever()
                .TestConfiguredProvidersAsync("current weather in Birmingham Alabama", operation.Token)
                .ConfigureAwait(true);
            if (results.Count == 0)
            {
                InternetBackendStatusText = "No configured internet provider to test.";
                StatusText = InternetBackendStatusText;
                return;
            }

            foreach (var result in results)
            {
                ApplyInternetProviderProbeResult(InternetProviderFromDisplayName(result.Provider), result);
            }

            InternetBackendStatusText = string.Join(
                Environment.NewLine,
                results.Select(result => $"{result.Provider}: {(result.Succeeded ? "OK" : "Needs attention")} - {result.Status}"));
            StatusText = "Primary configured internet backend test query complete.";
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private void ApplyInternetProviderProbeResult(
        InternetSearchProvider provider,
        InternetBackendProviderProbeResult result)
    {
        var status = $"{(result.Succeeded ? "OK" : "Needs attention")}: {result.Status} Usage: {result.RemainingEstimate}";
        switch (provider)
        {
            case InternetSearchProvider.GoogleGroundedSearch:
                InternetGeminiUsageText = status;
                break;
            case InternetSearchProvider.Tavily:
                InternetTavilyUsageText = status;
                break;
            case InternetSearchProvider.Firecrawl:
                InternetFirecrawlUsageText = status;
                break;
            case InternetSearchProvider.BraveSearch:
                InternetBraveSearchUsageText = status;
                break;
            case InternetSearchProvider.Serper:
                InternetSerperUsageText = status;
                break;
        }
    }

    private static string InternetProviderDisplayName(InternetSearchProvider provider) =>
        provider switch
        {
            InternetSearchProvider.GoogleGroundedSearch => "Google Grounding",
            InternetSearchProvider.Tavily => "Tavily",
            InternetSearchProvider.Firecrawl => "Firecrawl",
            InternetSearchProvider.BraveSearch => "Brave Search",
            InternetSearchProvider.Serper => "Serper",
            _ => provider.ToString()
        };

    private static InternetSearchProvider InternetProviderFromDisplayName(string provider) =>
        provider switch
        {
            "Google Grounding" => InternetSearchProvider.GoogleGroundedSearch,
            "Tavily" => InternetSearchProvider.Tavily,
            "Firecrawl" => InternetSearchProvider.Firecrawl,
            "Brave Search" => InternetSearchProvider.BraveSearch,
            "Serper" => InternetSearchProvider.Serper,
            _ => InternetSearchProvider.GoogleGroundedSearch
        };

    private void SaveAssistantName()
    {
        try
        {
            var normalizedName = AssistantProfile.NormalizeAssistantName(PendingAssistantName);
            if (normalizedName.Length > 40)
            {
                AssistantRenameStatus = "Assistant name must be 40 characters or fewer.";
                return;
            }

            if (normalizedName.Equals(AssistantName, StringComparison.Ordinal))
            {
                AssistantRenameStatus = $"The assistant is already named {AssistantName}.";
                return;
            }

            AssistantProfileStore.Save(
                _services.DataRoot,
                _services.AssistantProfile with { AssistantName = normalizedName });
            PendingAssistantName = normalizedName;
            AssistantRenameStatus = $"Saved {normalizedName}. Restart the app to update the wake word, prompts, and window titles.";
        }
        catch (Exception ex)
        {
            AssistantRenameStatus = $"Assistant name was not saved: {ex.Message}";
        }
    }

    public bool IsAgentActivityExpanded
    {
        get => _isAgentActivityExpanded;
        set => SetProperty(ref _isAgentActivityExpanded, value);
    }

    public string AgentActivitySummary
    {
        get => _agentActivitySummary;
        private set => SetProperty(ref _agentActivitySummary, value);
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
        var operation = BeginUiOperation(TimeSpan.FromSeconds(15));
        try
        {
            var installedChoices = await FetchInstalledRuntimeModelChoicesAsync(endpoint, operation.Token).ConfigureAwait(true);
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
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            RuntimeSelectionStatusText = "Installed model refresh cancelled.";
            StatusText = RuntimeSelectionStatusText;
        }
        catch (Exception ex)
        {
            RuntimeSelectionStatusText = $"Installed model refresh failed: {ex.Message}";
            StatusText = RuntimeSelectionStatusText;
        }
        finally
        {
            CompleteUiOperation(operation);
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

        var operation = BeginUiOperation(TimeSpan.FromMinutes(3));
        try
        {
            var options = BuildRuntimeOptionsFromUi();
            _services.ConfigureRuntimeCandidate(options);
            var health = await _services.RuntimeController.CheckCandidateAsync(operation.Token).ConfigureAwait(true);
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
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            RuntimeHealthResult = "Runtime check cancelled.";
            StatusText = "Runtime check cancelled.";
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
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
            CompleteUiOperation(operation);
            IsBusy = false;
        }
    }

    private void ShowRuntimeOptimizationReport()
    {
        try
        {
            var options = BuildRuntimeOptionsFromUi();
            var machine = _resourceMonitor.CaptureRuntimeMachineSnapshot();
            var report = RuntimeOptimizationAdvisor.BuildReport(options, machine);
            var owner = _settingsWindow ?? System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
            var window = new RuntimeOptimizationWindow(report.ToDisplayText(), AssistantName)
            {
                Owner = owner
            };
            window.ShowDialog();
            StatusText = "Runtime recommendation report generated.";
        }
        catch (Exception ex)
        {
            StatusText = $"Runtime recommendation failed: {ex.Message}";
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

    private async Task RevertToStubAsync()
    {
        IsBusy = true;
        StatusText = "Unloading the active runtime before reverting to the deterministic stub...";
        using var operation = BeginUiOperation(TimeSpan.FromSeconds(45));
        try
        {
            await _services.RuntimeController.RevertToFallbackAsync(operation.Token).ConfigureAwait(true);
            CanActivateRuntime = _services.RuntimeController.CanActivateCandidate;
            UpdateRuntimeStatus();
            SetModelConnectionStatus("model offline", MediaBrushes.Red);
            StatusText = "Active runtime unloaded and reverted to deterministic stub.";
        }
        catch (Exception ex)
        {
            StatusText = $"Runtime revert stopped because release could not be verified: {ex.Message}";
        }
        finally
        {
            CompleteUiOperation(operation);
            IsBusy = false;
        }
    }

    private async Task RevertToLastKnownGoodAsync()
    {
        IsBusy = true;
        StatusText = "Unloading the active runtime before restoring the last-known-good runtime...";
        using var operation = BeginUiOperation(TimeSpan.FromSeconds(45));
        try
        {
            if (!await _services.RuntimeController.RevertToLastKnownGoodAsync(operation.Token).ConfigureAwait(true))
            {
                StatusText = "No last-known-good runtime is available yet.";
                return;
            }

            UpdateRuntimeStatus();
            SetModelConnectionStatus("connected to model", MediaBrushes.LimeGreen);
            StatusText = "Previous runtime released; last-known-good runtime restored.";
        }
        catch (Exception ex)
        {
            StatusText = $"Last-known-good restore stopped because release could not be verified: {ex.Message}";
        }
        finally
        {
            CompleteUiOperation(operation);
            IsBusy = false;
        }
    }

    private void CopyMessage(object? parameter)
    {
        if (parameter is not ChatMessageViewModel message || string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(message.Text);
            StatusText = "Message copied to the clipboard.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            StatusText = $"Could not copy the message: {ex.Message}";
        }
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
            using var operation = CreateLinkedTimeout(TimeSpan.FromSeconds(10));
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
                cancellationToken: operation.Token).ConfigureAwait(true);

            message.MarkCorrection(report.Id);
            SaveActiveConversation();
            StatusText = $"Flagged for correction: {report.Id}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Correction queue write cancelled.";
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
            using var operation = CreateLinkedTimeout(TimeSpan.FromSeconds(15));
            var selectedId = SelectedCorrectionReviewItem?.Id;
            var reports = await _services.Orchestrator.Corrections.ListAsync(operation.Token).ConfigureAwait(true);

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
        catch (OperationCanceledException)
        {
            CorrectionReviewStatusText = "Correction queue load cancelled.";
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

        CorrectionReport? updated;
        try
        {
            using var operation = CreateLinkedTimeout(TimeSpan.FromSeconds(15));
            updated = await _services.Orchestrator.Corrections.SetStatusAsync(
                    SelectedCorrectionReviewItem.Id,
                    status,
                    operation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            CorrectionReviewStatusText = "Correction update cancelled.";
            return;
        }

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

        string? path;
        try
        {
            using var operation = CreateLinkedTimeout(TimeSpan.FromSeconds(15));
            path = await _services.Orchestrator.Corrections.ExportOneMarkdownAsync(
                    SelectedCorrectionReviewItem.Id,
                    CorrectionExportDirectory(),
                    operation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            CorrectionReviewStatusText = "Correction export cancelled.";
            return;
        }

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
        string path;
        try
        {
            using var operation = CreateLinkedTimeout(TimeSpan.FromSeconds(20));
            path = await _services.Orchestrator.Corrections.ExportAllMarkdownAsync(
                    CorrectionExportDirectory(),
                    operation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            CorrectionReviewStatusText = "Correction queue export cancelled.";
            return;
        }

        CorrectionReviewStatusText = $"Exported correction queue: {path}";
    }

    private string CorrectionExportDirectory() =>
        Path.Combine(_services.UserDataRoot, "CorrectionExports");

    private OpenAiCompatibleRuntimeOptions BuildRuntimeOptionsFromUi()
    {
        if (!Uri.TryCreate(RuntimeEndpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("Runtime endpoint must be an absolute URL.");
        }

        if (!int.TryParse(RuntimeContextText.Trim(), out var contextTokens) || contextTokens < 1)
        {
            throw new InvalidOperationException("Context size must be a positive integer.");
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

        return OllamaRuntimeSafetyPolicy.Normalize(new OpenAiCompatibleRuntimeOptions(
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
            AllowPrivateLanEndpoint: false)
        {
            Engine = LocalRuntimeEngines.Normalize(SelectedRuntimeEngine, endpoint),
            ReasoningEffort = _selectedReasoningEffort,
            ThinkingEnabled = RuntimeThinkingEnabled
        });
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
            _services.UserDataRoot,
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
            ? "AI can be wrong.  Always check answers against reliable sources."
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
            ? "AI can be wrong.  Always check answers against reliable sources."
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
            _services.UserDataRoot,
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
            var normalizedTranscript = SpeechTranscriptGuard.NormalizeAssistantName(transcript.Text, AssistantName);

            var transcriptGuard = SpeechTranscriptGuard.Evaluate(normalizedTranscript, assistantName: AssistantName);
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
                ? $"Transcript accepted; sending to {AssistantName}."
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
        var transcript = SpeechTranscriptGuard.NormalizeAssistantName(EditableTranscript, AssistantName).Trim();
        if (string.IsNullOrWhiteSpace(transcript) || IsBusy)
        {
            return;
        }

        var transcriptGuard = SpeechTranscriptGuard.Evaluate(transcript, assistantName: AssistantName);
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

        VoiceStatus = $"Voice transcript sent to {AssistantName}.";
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
        if (!AssistantReadsRepliesOutLoud)
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

    private static IEnumerable<string> SplitStreamingTextForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        for (var offset = 0; offset < text.Length; offset += StreamingTextDisplaySliceCharacters)
        {
            var length = Math.Min(StreamingTextDisplaySliceCharacters, text.Length - offset);
            yield return text.Substring(offset, length);
        }
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
        SpeechSynthesisResult? currentSpeech = null;
        try
        {
            await foreach (var segment in state.Queue.Reader.ReadAllAsync(state.Cancellation.Token).ConfigureAwait(true))
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                TtsStatus = currentSpeech is null
                    ? "Synthesizing streamed speech..."
                    : "Preparing next speech segment...";

                var nextSpeechTask = SynthesizeStreamingSpeechSegmentAsync(segment, state.Cancellation.Token);
                try
                {
                    if (currentSpeech is not null)
                    {
                        await PlayAndDeleteSpeechAsync(currentSpeech, state.Cancellation.Token).ConfigureAwait(true);
                        currentSpeech = null;
                    }

                    currentSpeech = await nextSpeechTask.ConfigureAwait(true);
                }
                catch
                {
                    _ = nextSpeechTask.ContinueWith(
                        task =>
                        {
                            if (task.Status == TaskStatus.RanToCompletion)
                            {
                                TryDeleteSpeechFile(task.Result);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    throw;
                }
            }

            if (currentSpeech is not null)
            {
                await PlayAndDeleteSpeechAsync(currentSpeech, state.Cancellation.Token).ConfigureAwait(true);
                currentSpeech = null;
            }

            TtsStatus = "Speech complete.";
            VoiceStatus = "Voice loop complete.";
        }
        catch (OperationCanceledException)
        {
            TtsStatus = "Speech stopped.";
            VoiceStatus = "Speech stopped.";
        }
        catch (Exception ex)
        {
            TtsStatus = $"Speech failed: {ex.Message}";
            VoiceStatus = $"Speech failed: {ex.Message}";
        }
        finally
        {
            TryDeleteSpeechFile(currentSpeech);
            IsSpeaking = false;
            if (ReferenceEquals(_activeSpeech, state.Cancellation))
            {
                _activeSpeech.Dispose();
                _activeSpeech = null;
            }
        }
    }

    private Task<SpeechSynthesisResult> SynthesizeStreamingSpeechSegmentAsync(
        string segment,
        CancellationToken cancellationToken)
    {
        var settings = new VoiceSettings(
            _services.TextToSpeech.VoiceId,
            Rate: SpeechRate,
            RetainAudio: false);

        return _services.TextToSpeech.SynthesizeAsync(segment, settings, cancellationToken);
    }

    private async Task PlayAndDeleteSpeechAsync(
        SpeechSynthesisResult speech,
        CancellationToken cancellationToken)
    {
        try
        {
            TtsStatus = "Speaking streamed response...";
            await _services.SpeechPlayer.PlayAsync(speech.AudioPath, cancellationToken).ConfigureAwait(true);
            SaveLastSuccessfulTtsDevice();
        }
        finally
        {
            TryDeleteSpeechFile(speech);
        }
    }

    private static void TryDeleteSpeechFile(SpeechSynthesisResult? speech)
    {
        if (speech is not null && !speech.RetainAudio && File.Exists(speech.AudioPath))
        {
            TryDeleteFile(speech.AudioPath);
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

    private async Task PlayVoiceSampleAsync()
    {
        if (IsSpeaking)
        {
            return;
        }

        ApplyVoiceToolSettings(saveSettings: true, reportStatus: false);
        if (!_services.TextToSpeech.IsConfigured)
        {
            TtsStatus = $"{TextToSpeechEngineText} is not configured yet.";
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
            var voiceId = CurrentTextToSpeechVoiceId();
            TtsStatus = $"Testing {voiceId}...";
            speech = await _services.TextToSpeech.SynthesizeAsync(
                $"Hello, I am {AssistantName}. This is what my selected voice sounds like.",
                new VoiceSettings(voiceId, Rate: SpeechRate, RetainAudio: false),
                _activeSpeech.Token).ConfigureAwait(true);

            await _services.SpeechPlayer.PlayAsync(speech.AudioPath, _activeSpeech.Token).ConfigureAwait(true);
            TtsStatus = $"Voice sample complete: {voiceId}.";
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

            RefreshVoiceSettingsChoices();
            AgentToolPermissions.Reload();
            RefreshEditorIntegrations();
            await RefreshCorrectionsAsync().ConfigureAwait(true);
            RefreshMemoryReminders();
            await RefreshRuntimeModelChoicesForSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StopInputLevelMonitor();
            HandleCommandException(ex);
        }
    }

    private void RefreshTechnologyAcknowledgements()
    {
        var report = AliTechnologyAcknowledgements.Load();
        TechnologyAcknowledgementsText = report.FormattedText;
        TechnologyAcknowledgementsSummary =
            $"Thank you to the people behind {report.Items.Count:N0} detected modules, libraries, runtimes, models, standards, and toolchains in this Ali build.";
    }

    private async Task StartSoftwareEngineeringRadarAsync()
    {
        _settingsWindow?.Close();
        ComposerText =
            "Give me a current software engineering and libraries radar briefing. Use live internet research and primary sources. " +
            "Cover important new or recently changed libraries, frameworks, language releases, developer tools, design patterns, security changes, and engineering practices that are relevant to the languages and systems I use. " +
            "Explain what changed, why it matters, maturity and licensing concerns, and which developments may genuinely help our current projects. " +
            "Prefer a concise ranked table with source links, separate stable recommendations from experiments, and do not install or upgrade anything.";
        await SendAsync().ConfigureAwait(true);
    }

    private void RefreshEditorIntegrations()
    {
        try
        {
            var report = AliEditorIntegrationManager.Inspect();
            EditorIntegrationSummary = report.Summary;
            EditorIntegrationDetails = report.Details;
        }
        catch (Exception ex)
        {
            EditorIntegrationSummary = "Editor integration status could not be refreshed.";
            EditorIntegrationDetails = ex.Message;
        }
    }

    private async Task InstallNotepadPlusPlusToolkitAsync()
    {
        var report = AliEditorIntegrationManager.Inspect();
        if (!report.NotepadPlusPlusInstalled)
        {
            System.Windows.MessageBox.Show(
                "Notepad++ is not installed in a supported per-machine or per-user location.",
                "Notepad++ Toolkit",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (report.NotepadPlusPlusRunning)
        {
            System.Windows.MessageBox.Show(
                "Save your work and close Notepad++ first. Ali will not modify its plugin folders while an editing session is open.",
                "Close Notepad++ Safely",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var confirmation = System.Windows.MessageBox.Show(
            "Install or repair Ali's Notepad++ toolkit now?\n\nAli will back up your user configuration, use the official x64 plugin catalog, verify package checksums, and preserve your themes, shortcuts, sessions, and editing preferences. Windows will request administrator approval for the Notepad++ program folder.",
            "Install Notepad++ Toolkit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        StatusText = "Installing the Notepad++ toolkit...";
        try
        {
            var exitCode = await AliEditorIntegrationManager.InstallOrRepairNotepadPlusPlusAsync().ConfigureAwait(true);
            RefreshEditorIntegrations();
            StatusText = exitCode == 0
                ? "Notepad++ toolkit installed. Start Notepad++ to load it."
                : $"Notepad++ toolkit installer exited with code {exitCode}.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText = "Notepad++ toolkit installation was cancelled before changes.";
        }
        catch (Exception ex)
        {
            StatusText = $"Notepad++ toolkit installation failed safely: {ex.Message}";
        }
    }

    private void OpenEditorIntegrationGuide()
    {
        try
        {
            AliEditorIntegrationManager.OpenGuide();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the editor integration guide: {ex.Message}";
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
            EndGoogleBillingSettingsSession();
            _settingsWindow = null;
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
        }

        RefreshTextToSpeechVoiceChoices();

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
            using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            refresh.CancelAfter(TimeSpan.FromSeconds(10));
            var installedChoices = await FetchInstalledRuntimeModelChoicesAsync(endpoint, refresh.Token).ConfigureAwait(true);
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
        catch (OperationCanceledException)
        {
            EnsureRuntimeModelChoicesAvailable(currentModel);
            RuntimeSelectionStatusText = "Installed model refresh cancelled.";
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

        LoadRuntimeModelChoices(RuntimeModelChoiceCatalog.KnownChoices(), selectedModel);
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
        var piperDefaults = PiperCliTextToSpeechOptions.FromEnvironment(_services.UserDataRoot);
        var kittenDefaults = KittenCliTextToSpeechOptions.FromEnvironment(_services.UserDataRoot);
        var savedTextToSpeechEngine = TextToSpeechEngines.Normalize(_voiceSettings.TextToSpeechEngine);
        _textToSpeechEngineText = TextToSpeechEngines.Piper;
        LoadTextToSpeechVoiceChoices();

        WhisperExecutableText = ToPortablePath(PreferInstalledVoicePath(
            _voiceSettings.WhisperExecutablePath,
            FindLocalWhisperPythonExecutable(),
            whisperDefaults.ExecutablePath)) ?? string.Empty;
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
        KittenExecutableText = ToPortablePath(PreferInstalledVoicePath(
            _voiceSettings.KittenExecutablePath,
            FindLocalKittenPythonExecutable(),
            kittenDefaults.ExecutablePath)) ?? string.Empty;
        KittenModelText = ToPortablePath(PreferValidConfiguredPath(
            _voiceSettings.KittenModelPath,
            PreferConfigured(FindLocalKittenModelRoot(), kittenDefaults.ModelPath))) ?? string.Empty;
        KittenVoiceText = KittenVoiceCatalog.Normalize(PreferConfigured(_voiceSettings.KittenVoiceId, kittenDefaults.VoiceId));
        KittenArgumentsText = PreferKittenArgumentsTemplate(
            _voiceSettings.KittenArgumentsTemplate,
            BuildLocalKittenArgumentsTemplate(),
            kittenDefaults.ArgumentsTemplate);
        TextToSpeechEngineText = savedTextToSpeechEngine;
        RefreshTextToSpeechVoiceChoices();
        _loadingSpeechToolSettings = false;
    }

    private void ApplyVoiceToolSettings() => ApplyVoiceToolSettings(saveSettings: true, reportStatus: true);

    private void ApplyVoiceToolSettings(bool saveSettings, bool reportStatus)
    {
        try
        {
            var sttOptions = BuildWhisperOptionsFromUi();
            var ttsProvider = BuildTextToSpeechProviderFromUi();

            LocalSpeechToolPolicy.EnsureLocalOnly(
                "Speech-to-text",
                sttOptions.ExecutablePath,
                sttOptions.ModelPath,
                sttOptions.ArgumentsTemplate);

            SetProcessEnvironment("ALI_WHISPER_EXE", sttOptions.ExecutablePath);
            SetProcessEnvironment("ALI_WHISPER_MODEL", sttOptions.ModelPath);
            SetProcessEnvironment("ALI_WHISPER_ARGS", sttOptions.ArgumentsTemplate);
            ApplyTextToSpeechEnvironment();

            _services.ConfigureSpeechTools(sttOptions, ttsProvider);
            if (saveSettings)
            {
                SaveVoiceToolSettings();
            }

            RefreshSpeechToolStatuses();
            if (reportStatus)
            {
                VoiceSettingsStatusText = $"Voice tool settings applied for this {AssistantName} session.";
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

    private static string? BuildLocalKittenArgumentsTemplate()
    {
        var script = FindLocalKittenScript();
        var portableScript = File.Exists(script) ? ToPortablePath(script) : null;
        return string.IsNullOrWhiteSpace(portableScript)
            ? null
            : $"\"{{script}}\" --model \"{{model}}\" --voice \"{{voice}}\" --output \"{{output}}\" --rate \"{{rate}}\"";
    }

    private static string? FindLocalWhisperScript()
    {
        return LocalVoiceResourceLocator.FindWhisperScript(AppBaseDirectory);
    }

    private PiperCliTextToSpeechOptions BuildPiperOptionsFromUi()
    {
        var defaults = PiperCliTextToSpeechOptions.FromEnvironment(_services.UserDataRoot);
        return new PiperCliTextToSpeechOptions(
            ResolvePortablePath(PiperExecutableText),
            ResolvePortablePath(PiperModelText),
            PreferConfigured(PiperVoiceText, defaults.VoiceId),
            PreferConfigured(PiperArgumentsText, defaults.ArgumentsTemplate),
            defaults.OutputDirectory);
    }

    private KittenCliTextToSpeechOptions BuildKittenOptionsFromUi()
    {
        var defaults = KittenCliTextToSpeechOptions.FromEnvironment(_services.UserDataRoot);
        return new KittenCliTextToSpeechOptions(
            ResolvePortablePath(KittenExecutableText),
            ResolvePortablePath(KittenModelText),
            KittenVoiceCatalog.Normalize(PreferConfigured(KittenVoiceText, defaults.VoiceId)),
            PreferConfigured(KittenArgumentsText, defaults.ArgumentsTemplate),
            defaults.OutputDirectory);
    }

    private ITextToSpeechProvider BuildTextToSpeechProviderFromUi()
    {
        if (TextToSpeechEngines.Normalize(TextToSpeechEngineText) == TextToSpeechEngines.Kitten)
        {
            var kittenOptions = BuildKittenOptionsFromUi();
            LocalSpeechToolPolicy.EnsureLocalOnly(
                "Text-to-speech",
                kittenOptions.ExecutablePath,
                kittenOptions.ModelPath,
                kittenOptions.ArgumentsTemplate,
                FindLocalKittenScript());
            return new KittenCliTextToSpeechProvider(kittenOptions);
        }

        var piperOptions = BuildPiperOptionsFromUi();
        LocalSpeechToolPolicy.EnsureLocalOnly(
            "Text-to-speech",
            piperOptions.ExecutablePath,
            piperOptions.ModelPath,
            piperOptions.ArgumentsTemplate);
        return new PiperCliTextToSpeechProvider(piperOptions);
    }

    private void ApplyTextToSpeechEnvironment()
    {
        SetProcessEnvironment("ALI_TTS_ENGINE", TextToSpeechEngines.Normalize(TextToSpeechEngineText));
        var piperOptions = BuildPiperOptionsFromUi();
        SetProcessEnvironment("ALI_PIPER_EXE", piperOptions.ExecutablePath);
        SetProcessEnvironment("ALI_PIPER_MODEL", piperOptions.ModelPath);
        SetProcessEnvironment("ALI_PIPER_VOICE", piperOptions.VoiceId);
        SetProcessEnvironment("ALI_PIPER_ARGS", piperOptions.ArgumentsTemplate);

        var kittenOptions = BuildKittenOptionsFromUi();
        SetProcessEnvironment("ALI_KITTEN_EXE", kittenOptions.ExecutablePath);
        SetProcessEnvironment("ALI_KITTEN_MODEL", kittenOptions.ModelPath);
        SetProcessEnvironment("ALI_KITTEN_VOICE", kittenOptions.VoiceId);
        SetProcessEnvironment("ALI_KITTEN_ARGS", kittenOptions.ArgumentsTemplate);
    }

    private void LoadTextToSpeechVoiceChoices()
    {
        _textToSpeechVoiceChoices.Clear();
        TextToSpeechVoiceChoices.Clear();

        if (TextToSpeechEngines.Normalize(TextToSpeechEngineText) == TextToSpeechEngines.Kitten)
        {
            var modelPath = ToPortablePath(PreferConfigured(KittenModelText, FindLocalKittenModelRoot())) ?? KittenModelText;
            foreach (var voice in KittenVoiceCatalog.All)
            {
                _textToSpeechVoiceChoices[voice.Label] = new TextToSpeechVoiceChoice(
                    voice.Label,
                    TextToSpeechEngines.Kitten,
                    voice.VoiceId,
                    modelPath);
                TextToSpeechVoiceChoices.Add(voice.Label);
            }

            return;
        }

        var voiceDirectory = FindLocalPiperVoiceDirectory();
        if (voiceDirectory is null)
        {
            return;
        }

        foreach (var modelPath in Directory.EnumerateFiles(voiceDirectory, "en_US-*.onnx").OrderBy(Path.GetFileName))
        {
            var voiceId = Path.GetFileNameWithoutExtension(modelPath);
            var label = FormatPiperVoiceLabel(voiceId);
            _textToSpeechVoiceChoices[label] = new TextToSpeechVoiceChoice(
                label,
                TextToSpeechEngines.Piper,
                voiceId,
                ToPortablePath(modelPath) ?? modelPath);
            TextToSpeechVoiceChoices.Add(label);
        }
    }

    private void RefreshTextToSpeechVoiceChoices()
    {
        try
        {
            LoadTextToSpeechVoiceChoices();
            var selectedVoice = FindSelectedTextToSpeechVoiceLabel()
                ?? TextToSpeechVoiceChoices.FirstOrDefault()
                ?? string.Empty;
            if (!string.Equals(SelectedTextToSpeechVoiceChoice, selectedVoice, StringComparison.Ordinal))
            {
                SelectedTextToSpeechVoiceChoice = selectedVoice;
            }
            else
            {
                OnPropertyChanged(nameof(SelectedTextToSpeechVoiceChoice));
            }
        }
        catch (Exception ex)
        {
            _textToSpeechVoiceChoices.Clear();
            TextToSpeechVoiceChoices.Clear();
            SelectedTextToSpeechVoiceChoice = string.Empty;
            VoiceSettingsStatusText = $"Voice list unavailable: {ex.Message}";
        }
    }

    private void ApplySelectedTextToSpeechVoiceChoice(string label, bool applySettings)
    {
        if (!_textToSpeechVoiceChoices.TryGetValue(label, out var choice))
        {
            return;
        }

        if (choice.Engine == TextToSpeechEngines.Kitten)
        {
            KittenVoiceText = KittenVoiceCatalog.Normalize(choice.VoiceId);
            KittenModelText = choice.ModelPath;
        }
        else
        {
            PiperVoiceText = choice.VoiceId;
            PiperModelText = choice.ModelPath;
        }

        if (applySettings)
        {
            ApplyVoiceToolSettings(saveSettings: true, reportStatus: false);
            VoiceSettingsStatusText = $"{AssistantName} voice set to {choice.Label}.";
        }
    }

    private string? FindSelectedTextToSpeechVoiceLabel()
    {
        return TextToSpeechEngines.Normalize(TextToSpeechEngineText) == TextToSpeechEngines.Kitten
            ? FindKittenVoiceLabel(KittenVoiceText)
            : FindPiperVoiceLabelForModel(PiperModelText);
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

        return _textToSpeechVoiceChoices.Values.FirstOrDefault(choice =>
            choice.Engine == TextToSpeechEngines.Piper
            && string.Equals(ResolvePortablePath(choice.ModelPath), normalized, StringComparison.OrdinalIgnoreCase))?.Label;
    }

    private static string? FindKittenVoiceLabel(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return null;
        }

        return KittenVoiceCatalog.All.FirstOrDefault(voice =>
            voice.VoiceId.Equals(voiceId, StringComparison.OrdinalIgnoreCase))?.Label;
    }

    private string? PreferredPiperModelPath()
    {
        var preferred = _textToSpeechVoiceChoices.Values.FirstOrDefault(choice =>
            choice.Engine == TextToSpeechEngines.Piper
            && choice.VoiceId.Equals("en_US-hfc_female-medium", StringComparison.OrdinalIgnoreCase));
        preferred ??= _textToSpeechVoiceChoices.Values.FirstOrDefault(choice => choice.Engine == TextToSpeechEngines.Piper);
        return preferred?.ModelPath;
    }

    private string? PreferredKittenModelPath()
    {
        var configured = PreferConfigured(KittenModelText, FindLocalKittenModelRoot());
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private string? FindTextToSpeechVoiceLabelForModel(string? modelPath)
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

        return _textToSpeechVoiceChoices.Values.FirstOrDefault(choice =>
            string.Equals(ResolvePortablePath(choice.ModelPath), normalized, StringComparison.OrdinalIgnoreCase))?.Label;
    }

    private void SaveVoiceToolSettings()
    {
        _voiceSettings = _voiceSettings with
        {
            WhisperExecutablePath = ToPortablePath(WhisperExecutableText),
            WhisperModelPath = ToPortablePath(WhisperModelText),
            WhisperArgumentsTemplate = NullIfWhiteSpace(WhisperArgumentsText),
            TextToSpeechEngine = TextToSpeechEngines.Normalize(TextToSpeechEngineText),
            PiperExecutablePath = ToPortablePath(PiperExecutableText),
            PiperModelPath = ToPortablePath(PiperModelText),
            PiperVoiceId = NullIfWhiteSpace(PiperVoiceText),
            PiperArgumentsTemplate = NullIfWhiteSpace(PiperArgumentsText),
            KittenExecutablePath = ToPortablePath(KittenExecutableText),
            KittenModelPath = ToPortablePath(KittenModelText),
            KittenVoiceId = NullIfWhiteSpace(KittenVoiceText),
            KittenArgumentsTemplate = NullIfWhiteSpace(KittenArgumentsText)
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
            : $"TTS not configured. Set the local {TextToSpeechEngineText} executable and voice model.";
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

    private void SelectReasoningEffort(string effort)
    {
        var normalized = OllamaRuntimeSafetyPolicy.NormalizeGptOssReasoningEffort(effort);
        _selectedReasoningEffort = normalized;
        NotifyReasoningEffortChanged();
        _services.RuntimeController.SetReasoningEffort(normalized);

        try
        {
            _services.SaveRuntimeSettings(BuildRuntimeOptionsFromUi());
            StatusText = $"Reasoning effort set to {normalized}. The next GPT-OSS request will use it.";
        }
        catch (Exception ex)
        {
            StatusText = $"Reasoning effort changed for this session, but could not be saved: {ex.Message}";
        }
    }

    private void SelectCodingExecutor(string mode)
    {
        var normalized = ProgrammingAgentModes.Normalize(mode);
        if (string.Equals(_selectedProgrammingAgentMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _selectedProgrammingAgentMode = normalized;
        OnPropertyChanged(nameof(IsCodingExecutorAli));
        OnPropertyChanged(nameof(IsCodingExecutorAider));
        OnPropertyChanged(nameof(IsCodingExecutorOpenHands));
        try
        {
            AgentOrchestrationSettings.SelectProgrammingAgentMode(normalized);
            StatusText = normalized switch
            {
                ProgrammingAgentModes.Aider =>
                    "Coding executor set to Aider. Ali will semantically identify coding work, delegate it to Aider, and verify the result.",
                ProgrammingAgentModes.OpenHands =>
                    "Coding executor set to OpenHands. Ali will semantically identify coding work, delegate it to OpenHands, and verify the result.",
                _ =>
                    "Coding executor set to Ali. She will use her native programming tools."
            };
        }
        catch (Exception ex)
        {
            StatusText = $"Coding executor changed for this session, but could not be saved: {ex.Message}";
        }
    }

    private void SynchronizeCodingExecutorSelection()
    {
        var normalized = ProgrammingAgentModes.Normalize(
            AgentOrchestrationSettings.SelectedProgrammingAgentMode);
        if (string.Equals(_selectedProgrammingAgentMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _selectedProgrammingAgentMode = normalized;
        OnPropertyChanged(nameof(IsCodingExecutorAli));
        OnPropertyChanged(nameof(IsCodingExecutorAider));
        OnPropertyChanged(nameof(IsCodingExecutorOpenHands));
    }

    private void NotifyReasoningEffortChanged()
    {
        OnPropertyChanged(nameof(IsReasoningLow));
        OnPropertyChanged(nameof(IsReasoningMedium));
        OnPropertyChanged(nameof(IsReasoningHigh));
        OnPropertyChanged(nameof(RuntimeRequestContractText));
    }

    private void ApplyRuntimeOptions(OpenAiCompatibleRuntimeOptions options)
    {
        _loadingRuntimeOptions = true;
        try
        {
            SelectedRuntimeEngine = LocalRuntimeEngines.Normalize(options.Engine, options.Endpoint);
        }
        finally
        {
            _loadingRuntimeOptions = false;
        }

        var selectedModel = RuntimeModelChoice.FromOptions(options);
        LoadRuntimeModelChoices(RuntimeModelChoiceCatalog.KnownChoices().Append(selectedModel), options.Model);

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
        RuntimeThinkingEnabled = options.ThinkingEnabled;
        _selectedReasoningEffort = OllamaRuntimeSafetyPolicy.NormalizeGptOssReasoningEffort(options.ReasoningEffort);
        NotifyReasoningEffortChanged();
        _services.RuntimeController.SetReasoningEffort(_selectedReasoningEffort);

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

    private void ApplyRuntimeEngineSelection(string engine)
    {
        var normalized = LocalRuntimeEngines.Normalize(engine, LocalRuntimeEngines.DefaultEndpoint(engine));
        RuntimeEndpointText = LocalRuntimeEngines.DefaultEndpoint(normalized).ToString();
        CanActivateRuntime = false;
        RuntimeSelectionStatusText = $"{normalized} selected. Refresh its installed model list, then Check and Activate.";
        StatusText = $"Runtime engine selected: {normalized}. The current engine remains active until the replacement passes Check.";
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

        var isGptOss = OllamaRuntimeSafetyPolicy.IsGptOssModel(choice.Model)
            || OllamaRuntimeSafetyPolicy.IsGptOssModel(choice.Family);
        var defaultContext = isGptOss
            ? OllamaRuntimeSafetyPolicy.DefaultContextTokens.ToString(CultureInfo.InvariantCulture)
            : choice.ContextTokens.FirstOrDefault().ToString(CultureInfo.InvariantCulture);
        var defaultOutputLimit = isGptOss
            ? "1024"
            : choice.OutputTokenLimits.FirstOrDefault().ToString(CultureInfo.InvariantCulture);

        RuntimeQuantizationText = PickChoice(RuntimeQuantizationChoices, preferredQuantization, choice.DefaultQuantization, resetToSmallest);
        RuntimeContextText = PickChoice(RuntimeContextChoices, preferredContext, defaultContext, resetToSmallest && !isGptOss);
        RuntimeOutputLimitText = PickChoice(RuntimeOutputLimitChoices, preferredOutputLimit, defaultOutputLimit, resetToSmallest && !isGptOss);
        if (resetToSmallest && isGptOss)
        {
            EnsureChoice(RuntimeTemperatureChoices, "1");
            RuntimeTemperatureText = "1";
            RuntimeTopPText = RuntimeTopPModelDefault;
        }

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
        return RuntimeModelChoiceCatalog.ParseRuntimeModelChoices(body);
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
        RefreshStackComponentsOnUiThread();
    }

    private async Task RefreshStackHealthAsync()
    {
        if (_checkingStackHealth || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        _checkingStackHealth = true;
        try
        {
            var settings = _services.LoadUserMemorySettings();
            if (settings.Enabled)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                _userMemoryRuntimeStatus = await _services.UserMemories
                    .TestAsync(_services.ActiveUsers.Current, timeout.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                _userMemoryRuntimeStatus = new(false, false, false, "Disabled", "Per-user memory is disabled.");
            }
        }
        catch (OperationCanceledException) when (!_lifetimeCancellation.IsCancellationRequested)
        {
            _userMemoryRuntimeStatus = null;
        }
        catch (Exception ex)
        {
            _userMemoryRuntimeStatus = new(true, false, false, "Unavailable", $"Memory check failed safely: {ex.Message}");
        }
        finally
        {
            _checkingStackHealth = false;
            RefreshStackComponents();
        }
    }

    private void RefreshStackComponentsOnUiThread()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RefreshStackComponents));
            return;
        }

        RefreshStackComponents();
    }

    private void RefreshStackComponents()
    {
        var memorySettings = _services.LoadUserMemorySettings();
        if (!memorySettings.Enabled)
        {
            _memoryStackStatus.Update("Off", "Memory is intentionally disabled.", MediaBrushes.Gray);
        }
        else if (_userMemoryRuntimeStatus is { RuntimeAvailable: true } memoryStatus)
        {
            _memoryStackStatus.Update("Ready", $"Memory ready: {memoryStatus.Message}", MediaBrushes.LimeGreen);
        }
        else if (_userMemoryRuntimeStatus is { } failedMemoryStatus)
        {
            _memoryStackStatus.Update("Unavailable", failedMemoryStatus.Message, MediaBrushes.OrangeRed);
        }
        else
        {
            _memoryStackStatus.Update("Checking", "Memory is starting and its health check is still running.", MediaBrushes.Gold);
        }

        var ragSettings = _services.LoadLocalVectorLibrarySettings();
        var qdrantNeeded = memorySettings.Enabled || ragSettings.Enabled;
        var qdrantStatus = _services.Qdrant.Status;
        if (!qdrantNeeded)
        {
            _ragStackStatus.Update("Off", "RAG and per-user vector memory are intentionally disabled.", MediaBrushes.Gray);
        }
        else if (qdrantStatus.IsReachable)
        {
            _ragStackStatus.Update("Ready", $"Qdrant ready: {qdrantStatus.Message}", MediaBrushes.LimeGreen);
        }
        else if (qdrantStatus.State.Contains("start", StringComparison.OrdinalIgnoreCase)
                 || _userMemoryRuntimeStatus is null)
        {
            _ragStackStatus.Update("Starting", qdrantStatus.Message, MediaBrushes.Gold);
        }
        else
        {
            _ragStackStatus.Update("Unavailable", qdrantStatus.Message, MediaBrushes.OrangeRed);
        }

        if (_interactionRuntime is null)
        {
            _speechStackStatus.Update("Unavailable", $"Speech ingress unavailable. {VoiceStatus}", MediaBrushes.OrangeRed);
        }
        else if (VoiceStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                 || VoiceStatus.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _speechStackStatus.Update("Unavailable", $"{VoiceStatus}\nSTT: {SttStatus}\nTTS: {TtsStatus}", MediaBrushes.OrangeRed);
        }
        else
        {
            _speechStackStatus.Update("Ready", $"{VoiceStatus}\nSTT: {SttStatus}\nTTS: {TtsStatus}", MediaBrushes.LimeGreen);
        }

        if (!McpServerSettings.Enabled)
        {
            _mcpStackStatus.Update("Off", "Ali's MCP server is intentionally disabled. MCP client tools remain available on demand when configured.", MediaBrushes.Gray);
        }
        else if (McpServerSettings.IsRunning)
        {
            _mcpStackStatus.Update("Ready", $"MCP server ready at {McpServerSettings.Endpoint}. {McpServerSettings.StatusText}", MediaBrushes.LimeGreen);
        }
        else if (McpServerSettings.RuntimeState.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            _mcpStackStatus.Update("Starting", McpServerSettings.StatusText, MediaBrushes.Gold);
        }
        else
        {
            _mcpStackStatus.Update("Unavailable", McpServerSettings.StatusText, MediaBrushes.OrangeRed);
        }

        if (!ConversationBridgeSettings.Enabled)
        {
            _bridgeStackStatus.Update("Off", "The local debugging bridge is intentionally disabled.", MediaBrushes.Gray);
        }
        else if (ConversationBridgeSettings.IsRunning)
        {
            _bridgeStackStatus.Update("Ready", $"Debug bridge ready at {ConversationBridgeSettings.Endpoint}. {ConversationBridgeSettings.StatusText}", MediaBrushes.LimeGreen);
        }
        else if (ConversationBridgeSettings.RuntimeState.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            _bridgeStackStatus.Update("Starting", ConversationBridgeSettings.StatusText, MediaBrushes.Gold);
        }
        else
        {
            _bridgeStackStatus.Update("Unavailable", ConversationBridgeSettings.StatusText, MediaBrushes.OrangeRed);
        }
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

        if (TestTavilyInternetBackendCommand is AsyncRelayCommand testTavily)
        {
            testTavily.RaiseCanExecuteChanged();
        }

        if (TestFirecrawlInternetBackendCommand is AsyncRelayCommand testFirecrawl)
        {
            testFirecrawl.RaiseCanExecuteChanged();
        }

        if (TestBraveSearchInternetBackendCommand is AsyncRelayCommand testBraveSearch)
        {
            testBraveSearch.RaiseCanExecuteChanged();
        }

        if (TestSerperInternetBackendCommand is AsyncRelayCommand testSerper)
        {
            testSerper.RaiseCanExecuteChanged();
        }

        if (TestConfiguredInternetBackendsCommand is AsyncRelayCommand testConfiguredInternet)
        {
            testConfiguredInternet.RaiseCanExecuteChanged();
        }

        if (ActivateRuntimeCommand is RelayCommand activate)
        {
            activate.RaiseCanExecuteChanged();
        }

        if (RevertToStubCommand is AsyncRelayCommand revertStub)
        {
            revertStub.RaiseCanExecuteChanged();
        }

        if (RevertToLastKnownGoodCommand is AsyncRelayCommand revertLastKnownGood)
        {
            revertLastKnownGood.RaiseCanExecuteChanged();
        }

        if (SendTranscriptCommand is AsyncRelayCommand sendTranscript)
        {
            sendTranscript.RaiseCanExecuteChanged();
        }

        if (TogglePushToTalkCommand is AsyncRelayCommand togglePushToTalk)
        {
            togglePushToTalk.RaiseCanExecuteChanged();
        }

        if (StopSpeakingCommand is RelayCommand stopSpeaking)
        {
            stopSpeaking.RaiseCanExecuteChanged();
        }

        if (RunSelectedCommandExplorerCommand is RelayCommand runSelectedCommandExplorer)
        {
            runSelectedCommandExplorer.RaiseCanExecuteChanged();
        }

        if (PlayVoiceSampleCommand is AsyncRelayCommand playPiperSample)
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

        if (BackupUserDataCommand is AsyncRelayCommand backupUserData)
        {
            backupUserData.RaiseCanExecuteChanged();
        }

        if (RestoreUserDataCommand is AsyncRelayCommand restoreUserData)
        {
            restoreUserData.RaiseCanExecuteChanged();
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

    private static IReadOnlyList<CommandExplorerNodeViewModel> BuildCommandExplorerRoots() =>
        AliModuleCatalog.Default
            .Select(module => new CommandExplorerNodeViewModel(
                module.DisplayName,
                module.Purpose,
                usage: $"Module id: {module.Id}",
                children: module.CapabilityKinds.Select(capability => new CommandExplorerNodeViewModel(
                    capability,
                    $"Capability kind for {module.DisplayName}.",
                    usage: $"Module id: {module.Id}"))))
            .ToArray();

    private string FormatRuntimeDisplay()
    {
        var profile = _services.Orchestrator.Runtime.ActiveProfile;
        return $"{profile.DisplayName} | {profile.Quantization} | {profile.ContextTokens:N0} ctx";
    }

    private async Task RefreshVisionCamerasAsync()
    {
        var runtime = _interactionRuntime;
        if (runtime is null)
        {
            return;
        }

        if (TestGeminiInternetBackendCommand is AsyncRelayCommand testGemini)
        {
            testGemini.RaiseCanExecuteChanged();
        }
        VisionStatus = "Looking for cameras...";
        try
        {
            var cameras = await Task.Run(runtime.GetCameras).ConfigureAwait(true);
            if (cameras.Count == 0)
            {
                // Windows can briefly report no capture devices while the media
                // stack is settling during application startup. Retry once;
                // this is device discovery only and never enters the frame path.
                await Task.Delay(750).ConfigureAwait(true);
                cameras = await Task.Run(runtime.GetCameras).ConfigureAwait(true);
            }
            var previousName = SelectedVisionCamera?.Name;
            VisionCameras.Clear();
            foreach (var camera in cameras)
            {
                VisionCameras.Add(camera);
            }
            SelectedVisionCamera = VisionCameras.FirstOrDefault(camera =>
                    string.Equals(camera.Name, previousName, StringComparison.OrdinalIgnoreCase))
                ?? VisionCameras.FirstOrDefault();
            VisionStatus = SelectedVisionCamera is null
                ? "No camera devices found."
                : $"Selected {SelectedVisionCamera.Name}.";
        }
        catch (Exception ex)
        {
            VisionStatus = $"Camera discovery failed safely: {ex.Message}";
        }
    }

    private async Task LoadVisionCameraModesAsync(CameraDevice? camera)
    {
        _visionModeLoad?.Cancel();
        _visionModeLoad?.Dispose();
        VisionCameraModes.Clear();
        VisionCameraModes.Add(CameraVideoMode.Auto);
        SelectedVisionCameraMode = CameraVideoMode.Auto;
        if (camera is null || _interactionRuntime is null)
        {
            return;
        }
        var load = new CancellationTokenSource();
        _visionModeLoad = load;
        try
        {
            var modes = await _interactionRuntime
                .GetModesAsync(camera, load.Token)
                .ConfigureAwait(true);
            if (load.IsCancellationRequested)
            {
                return;
            }
            VisionCameraModes.Clear();
            foreach (var mode in modes)
            {
                VisionCameraModes.Add(mode);
            }
            SelectedVisionCameraMode = VisionCameraModes
                .Where(mode => !mode.IsAuto)
                .OrderByDescending(IsExact4K30)
                .ThenByDescending(mode => mode.Width == 1920 && mode.Height == 1080)
                .ThenByDescending(mode => mode.FramesPerSecond ?? 0)
                .FirstOrDefault()
                ?? VisionCameraModes.FirstOrDefault()
                ?? CameraVideoMode.Auto;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            VisionStatus = $"Camera modes unavailable: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_visionModeLoad, load))
            {
                _visionModeLoad = null;
            }
            load.Dispose();
        }
    }

    private static bool IsExact4K30(CameraVideoMode mode) =>
        mode.Width == 3840
        && mode.Height == 2160
        && mode.FramesPerSecond is double framesPerSecond
        && Math.Abs(framesPerSecond - 30d) < 0.5d;

    private async Task ToggleVisionCameraAsync()
    {
        var runtime = _interactionRuntime;
        if (runtime is null)
        {
            VisionStatus = "Interaction modules are not available.";
            return;
        }
        if (VisionCameraOn)
        {
            VisionViewport = null;
            VisionCameraOn = false;
            VisionStatus = "Camera stopping...";
            await Task.Run(runtime.TurnCameraOff).ConfigureAwait(true);
            VisionStatus = "Camera off.";
            return;
        }
        if (SelectedVisionCamera is null)
        {
            VisionStatus = "Choose a camera first.";
            return;
        }
        var mode = SelectedVisionCameraMode ?? CameraVideoMode.Auto;
        VisionStatus = $"Starting {SelectedVisionCamera.Name} ({mode.Label})...";
        try
        {
            VisionViewport = runtime.TurnCameraOn(SelectedVisionCamera, mode);
            runtime.SetOverlays(TrackingOverlayEnabled, FaceMeshOverlayEnabled);
            VisionCameraOn = true;
            VisionStatus = "DX12 D3D11 bridge texture NV12";
        }
        catch (Exception ex)
        {
            VisionViewport = null;
            VisionCameraOn = false;
            VisionStatus = $"Camera failed safely: {ex.Message}";
        }
    }

    private async void InteractionTimerTick(object? sender, EventArgs e)
    {
        var runtime = _interactionRuntime;
        if (runtime is null)
        {
            return;
        }
        var level = Math.Clamp(runtime.MicrophoneInputLevel, 0d, 1d);
        if (_settingsWindow?.IsVisible == true)
        {
            VoiceInputLevelPercent = level * 100d;
            VoiceInputMeterText = $"Shared microphone level: {level:P0}.";
            VoiceDiagnosticsText = runtime.SpeechStatus;
        }
        AttentionStatus = runtime.HasAttention
            ? $"Attention: Yes ({runtime.AttentionSource})"
            : "Attention: No";
        if (_interactionPollBusy || IsBusy)
        {
            return;
        }
        if (IsSpeaking || DateTimeOffset.UtcNow < _suppressVoiceIngressUntil)
        {
            runtime.TryTakeAcceptedSpeech(out _);
            return;
        }
        if (!runtime.TryTakeAcceptedSpeech(out var accepted)
            || accepted is null)
        {
            return;
        }
        _interactionPollBusy = true;
        try
        {
            LastTranscript = accepted.ExactText;
            EditableTranscript = accepted.ExactText;
            VoiceStatus = $"{accepted.Provider} accepted speech via {accepted.AttentionSource}.";
            var metadata = new VoiceTurnMetadata(
                VoiceInputOrigin.Voice,
                accepted.ExactText,
                accepted.Provider,
                "Unified security pipeline",
                _services.TextToSpeech.ProviderName,
                _services.TextToSpeech.VoiceId,
                RawAudioRetained: false,
                InputDeviceNumber: CurrentInputDeviceNumber(),
                InputDeviceName: CurrentInputDeviceName(),
                PersonIdentityId: accepted.PersonIdentityId,
                ParticipantDisplayName: accepted.ParticipantDisplayName,
                VisualIdentityConfidence: accepted.VisualIdentityConfidence,
                VoiceIdentityConfidence: accepted.VoiceIdentityConfidence,
                AttentionSource: accepted.AttentionSource);
            await SendTextAsync(accepted.ExactText, VoiceInputOrigin.Voice, metadata)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Accepted speech could not be sent: {ex.Message}";
        }
        finally
        {
            _interactionPollBusy = false;
        }
    }

    private void SelectInteractionSpeechToText(AliSpeechToTextEngine engine)
    {
        _interactionRuntime?.SelectSpeechToText(engine);
        OnPropertyChanged(nameof(ParakeetSpeechToTextSelected));
        OnPropertyChanged(nameof(WhisperSpeechToTextSelected));
        VoiceStatus = $"Speech to text selected: {_interactionRuntime?.SpeechProviderName ?? engine.ToString()}.";
    }

    private void SetOptionalOverlays(bool tracking, bool faceMesh)
    {
        TrackingOverlayEnabled = tracking;
        FaceMeshOverlayEnabled = faceMesh;
        _interactionRuntime?.SetOverlays(tracking, faceMesh);
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
            try
            {
                _interactionRuntime?.SelectMicrophoneByName(CurrentInputDeviceName());
            }
            catch (Exception ex)
            {
                VoiceStatus = $"Microphone selection failed safely: {ex.Message}";
            }
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

    private void StartInputLevelMonitor()
    {
        // The shared MicrophoneModule owns input acquisition. The settings
        // meter is refreshed from its current level by InteractionTimerTick.
    }

    private void StopInputLevelMonitor() => _inputLevelMonitor.Stop();

    private void SubscribeRecorderLevels()
    {
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.LevelAvailable += InputLevelAvailable;
        }
    }

    private void UnsubscribeRecorderLevels()
    {
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.LevelAvailable -= InputLevelAvailable;
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
        bool? assistantReadsRepliesOutLoud = null,
        bool? autoSendVoiceTranscripts = null,
        bool? attentiveChatEnabled = null,
        double? speechRate = null,
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
            AssistantReadsRepliesOutLoud = assistantReadsRepliesOutLoud ?? _voiceSettings.AssistantReadsRepliesOutLoud,
            AutoSendVoiceTranscripts = autoSendVoiceTranscripts ?? _voiceSettings.AutoSendVoiceTranscripts,
            AttentiveChatEnabled = attentiveChatEnabled ?? _voiceSettings.AttentiveChatEnabled,
            SpeechRate = NormalizeSpeechRate(speechRate ?? _voiceSettings.SpeechRate),
            PushToTalkKey = NormalizePushToTalkKey(pushToTalkKey ?? _voiceSettings.PushToTalkKey)
        };

        VoiceRuntimeSettingsStore.Save(_services.DataRoot, _voiceSettings);
    }

    private static bool TryParsePushToTalkKey(string? value, out Key key)
    {
        var normalized = NormalizePushToTalkKey(value);
        return Enum.TryParse(normalized, ignoreCase: true, out key);
    }

    private static double NormalizeSpeechRate(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.75d, 1.6d) : 1.25d;

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
        _services.TextToSpeech switch
        {
            PiperCliTextToSpeechProvider piper => piper.ModelPath,
            KittenCliTextToSpeechProvider kitten => kitten.ModelPath,
            _ => string.Empty
        };

    private string CurrentTextToSpeechVoiceId() =>
        TextToSpeechEngines.Normalize(TextToSpeechEngineText) == TextToSpeechEngines.Kitten
            ? KittenVoiceCatalog.Normalize(KittenVoiceText)
            : PreferConfigured(PiperVoiceText, PiperCliTextToSpeechOptions.FromEnvironment(_services.UserDataRoot).VoiceId);

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

    private static string PreferConfigured(string? configured, string? fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback ?? string.Empty : configured.Trim();

    private static string PreferValidConfiguredPath(string? configured, string? fallback)
    {
        var resolved = ResolvePortablePath(configured);
        return !string.IsNullOrWhiteSpace(configured) && LocalPathExists(resolved)
            ? configured.Trim()
            : fallback ?? string.Empty;
    }

    private static string PreferInstalledVoicePath(string? configured, string? installed, string? fallback)
    {
        var resolvedInstalled = ResolvePortablePath(installed);
        if (LocalPathExists(resolvedInstalled))
        {
            return installed ?? resolvedInstalled ?? string.Empty;
        }

        return PreferValidConfiguredPath(configured, fallback);
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

    private static string PreferKittenArgumentsTemplate(
        string? configured,
        string? localKittenArguments,
        string fallback)
    {
        var configuredTrim = NullIfWhiteSpace(configured);
        if (configuredTrim is null)
        {
            return PreferConfigured(localKittenArguments, fallback);
        }

        if (!configuredTrim.Contains("{rate}", StringComparison.OrdinalIgnoreCase)
            && (configuredTrim.Contains("{script}", StringComparison.OrdinalIgnoreCase)
                || configuredTrim.Contains("local_kitten_tts.py", StringComparison.OrdinalIgnoreCase)))
        {
            return PreferConfigured(localKittenArguments, fallback);
        }

        return configuredTrim;
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

    private static string? FindLocalKittenPythonExecutable()
    {
        var candidate = LocalVoiceResourceLocator.FindKittenPythonExecutable(AppBaseDirectory);
        return File.Exists(candidate) ? ToPortablePath(candidate) : null;
    }

    private static string? FindLocalKittenModelRoot()
    {
        var candidate = LocalVoiceResourceLocator.FindKittenModelRoot(AppBaseDirectory);
        return Directory.Exists(candidate) ? ToPortablePath(candidate) : null;
    }

    private static string? FindLocalKittenScript()
    {
        return LocalVoiceResourceLocator.FindKittenScript(AppBaseDirectory);
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

    private sealed record DiagnosticCommandResult(bool Handled, bool Succeeded, string Message);
}

internal sealed record TextToSpeechVoiceChoice(string Label, string Engine, string VoiceId, string ModelPath);

