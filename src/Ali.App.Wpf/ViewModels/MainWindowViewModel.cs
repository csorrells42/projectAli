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
using Ali.Infrastructure.Sources;
using Ali.Infrastructure.Storage;
using Ali.Infrastructure.Voice;
using MediaBrushes = System.Windows.Media.Brushes;

namespace Ali.App.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const double SpectrumRenderWidth = 720d;
    private const double SpectrumRenderHeight = 130d;
    private const double SpectrumRenderInset = 12d;
    private const string RuntimeTopPModelDefault = "Model default";
    private const int StreamingTextFlushCharacters = 32;
    private const int StreamingTextDisplaySliceCharacters = 72;
    private static readonly TimeSpan ModelStatusPingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OllamaStartRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StreamingTextFlushInterval = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan StreamingTextPaceDelay = TimeSpan.FromMilliseconds(12);
    private static readonly JsonSerializerOptions MaintenanceReceiptJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RuntimeTemperatureChoiceValues = ["0", "0.1", "0.2", "0.3", "0.5", "0.7", "1", "1.5", "2"];
    private static readonly string[] RuntimeTopPChoiceValues = [RuntimeTopPModelDefault, "0.5", "0.7", "0.8", "0.9", "0.95", "1"];
    private static readonly string[] CodingWorkspaceAccessModeChoiceValues = [CodingPermissionModes.Allowed];
    private static readonly string[] CodingExplicitOutsideFileOpenModeChoiceValues = [CodingPermissionModes.Allowed, CodingPermissionModes.Disabled];
    private static readonly string[] CodingSearchOutsideWorkspaceModeChoiceValues = [CodingPermissionModes.AskFirst, CodingPermissionModes.Disabled];
    private static readonly string[] CodingConfirmOrDisabledModeChoiceValues = [CodingPermissionModes.ConfirmEachTime, CodingPermissionModes.Disabled];
    private static readonly string[] CodingDestructiveActionModeChoiceValues = [CodingPermissionModes.ExtraConfirmation, CodingPermissionModes.Disabled];
    private static readonly string[] CodingHighRiskModeChoiceValues =
    [
        CodingPermissionModes.Blocked,
        CodingPermissionModes.ExtraConfirmation,
        CodingPermissionModes.ConfirmEachTime,
        CodingPermissionModes.Disabled
    ];
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
    private DateTimeOffset _nextOllamaStartAttemptAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _ollamaStartGate = new(1, 1);
    private readonly HashSet<int> _ollamaProcessIdsStartedByAli = new();
    private readonly Dictionary<string, TextToSpeechVoiceChoice> _textToSpeechVoiceChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeModelChoice> _runtimeModelChoices = new(StringComparer.OrdinalIgnoreCase);
    private VoiceRuntimeSettings _voiceSettings;
    private bool _loadingVoiceSettings;
    private bool _loadingSpeechToolSettings;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _activeUiOperation;
    private CancellationTokenSource? _activeResponse;
    private CancellationTokenSource? _activeVoiceInput;
    private CancellationTokenSource? _activeSpeech;
    private SettingsWindow? _settingsWindow;
    private MaintenanceDashboardWindow? _maintenanceDashboardWindow;
    private ProgrammingDashboardWindow? _programmingDashboardWindow;
    private LocalLibraryWindow? _localLibraryWindow;
    private SourcesTopicsWindow? _sourcesTopicsWindow;
    private bool _voiceMonitorRequested;
    private bool _suppressInputMonitorRestart;
    private VoiceCaptureDiagnostics? _lastCaptureDiagnostics;
    private double[] _lastSpectrumMagnitudes = new double[SpectrumAnalyzer.BarCount];
    private double[] _renderedSpectrumMagnitudes = new double[SpectrumAnalyzer.BarCount];
    private double _spectrumVisualCeiling = 0.25d;
    private double _lastSpectrumPeakLevel;
    private string _composerText = string.Empty;
    private bool _isCommandExplorerOpen;
    private CommandExplorerNodeViewModel? _selectedCommandExplorerNode;
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
    private string _maintenanceStatusText = "Backups include conversations, memories, reminders, settings, sources, local indexes, voice settings, runtime settings, and generated documents. Temporary session audio/images are skipped.";

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
        RecommendRuntimeSettingsCommand = CreateCommand(_ => ShowRuntimeOptimizationReport());
        ActivateRuntimeCommand = CreateCommand(_ => ActivateRuntime(), _ => CanActivateRuntime && !IsBusy);
        RevertToStubCommand = CreateCommand(_ => RevertToStub(), _ => !IsBusy);
        RevertToLastKnownGoodCommand = CreateCommand(_ => RevertToLastKnownGood(), _ => CanRevertToLastKnownGood && !IsBusy);
        PasteImageCommand = CreateAsyncCommand(AddClipboardImageAsync);
        RemoveAttachmentCommand = CreateCommand(RemoveAttachment);
        BeginAssignPushToTalkKeyCommand = CreateCommand(_ => BeginAssignPushToTalkKey());
        SendTranscriptCommand = CreateAsyncCommand(SendTranscriptAsync, () => !IsBusy && !IsRecording && !IsTranscribing && !string.IsNullOrWhiteSpace(EditableTranscript));
        StopSpeakingCommand = CreateCommand(_ => StopSpeaking(), _ => IsSpeaking);
        OpenSettingsCommand = CreateAsyncCommand(OpenSettingsAsync);
        OpenMaintenanceDashboardCommand = CreateCommand(_ => OpenMaintenanceDashboard());
        OpenProgrammingDashboardCommand = CreateCommand(_ => OpenProgrammingDashboard());
        OpenLocalLibraryCommand = CreateCommand(_ => OpenLocalLibrary());
        OpenSourcesTopicsCommand = CreateCommand(_ => OpenSourcesTopics());
        ToggleCommandExplorerCommand = CreateCommand(_ => IsCommandExplorerOpen = !IsCommandExplorerOpen);
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
        SaveCodingPermissionsCommand = CreateCommand(_ => SaveCodingPermissions());
        ResetCodingPermissionsCommand = CreateCommand(_ => ResetCodingPermissionsToDefault());
        BrowseCodingWorkspaceRootCommand = CreateCommand(_ => BrowseCodingWorkspaceRoot());
        BrowseCodingPdfWorkspaceRootCommand = CreateCommand(_ => BrowseCodingPdfWorkspaceRoot());
        BrowseNotepadPlusPlusPathCommand = CreateCommand(_ => BrowseCodingToolPath("Choose notepad++.exe", "Notepad++ (notepad++.exe)|notepad++.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*", path => CodingNotepadPlusPlusPathText = path));
        BrowseVisualStudioPathCommand = CreateCommand(_ => BrowseCodingToolPath("Choose Visual Studio devenv.exe", "Visual Studio (devenv.exe)|devenv.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*", path => CodingVisualStudioPathText = path));
        RunComputerHealthCheckCommand = CreateAsyncCommand(RunComputerHealthCheckAsync, () => !IsBusy && !IsRecording && !IsTranscribing);
        RepairAliInstallCommand = CreateAsyncCommand(RepairAliInstallAsync, () => !IsBusy && !IsRecording && !IsTranscribing);
        RunComputerAssistantSetupCommand = CreateAsyncCommand(RunComputerAssistantSetupAsync, () => !IsBusy && !IsRecording && !IsTranscribing);
        RunMaintenancePlanCommand = CreateAsyncCommand(RunMaintenancePlanAsync, () => !IsBusy && !IsRecording && !IsTranscribing);
        RunProcessEvidenceCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("Running processes", "collect process evidence", "Maintenance.ProcessEvidence"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunBuildLockDiagnosticCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("Build lock check", "diagnose build lock", "Maintenance.BuildLock"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunPortDiagnosticCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("Port owner check", "diagnose port 8765", "Maintenance.PortDiagnostic"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunServicesStartupInspectionCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("Services and startup", "inspect services and startup", "Maintenance.ServicesStartup"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunDiskCleanupPlanCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("Disk cleanup plan", "plan disk cleanup", "Maintenance.DiskCleanupPlan"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunSuspiciousActivityPlanCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("Suspicious activity plan", "plan suspicious activity check unknown startup item", "Maintenance.SuspiciousActivityPlan"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunAppInstallTroubleshootingCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("App install troubleshooting", "plan app install troubleshooting recent installer issue", "Maintenance.AppInstallTroubleshooting"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunPeripheralSetupPlanCommand = CreateAsyncCommand(() => RunMaintenanceDiagnosticAsync("Peripheral setup plan", "plan peripheral setup audio microphone or USB device", "Maintenance.PeripheralSetupPlan"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingWorkspaceDiagnosticCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Coding workspace", "inspect coding workspace", "Coding.WorkspaceDiagnostic"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingProjectIntelligenceCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Project intelligence", "show project intelligence", "Coding.ProjectIntelligence"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingProjectIndexCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Project index", "project index", "Coding.ProjectIndex"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingRepoUnderstandingCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Understand repo", "understand repo", "Coding.RepoUnderstanding"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingContextPacketCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Context packet", "coding context packet current coding work", "Coding.ContextPacket"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFullReadinessCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Full readiness", "full coding readiness", "Coding.FullReadiness"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingMiniCodexStatusCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Mini-Codex status", "mini codex status", "Coding.MiniCodexStatus"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingReadinessReportCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Mini-Codex readiness", "mini codex readiness report", "Coding.ReadinessReport"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingNextBestActionCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Next best coding action", "show coding next best action", "Coding.NextBestAction"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingValidationQueueCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Validation queue", "show validation queue runner", "Coding.ValidationQueue"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPatchBatchCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Patch batch", "show owner safe patch batch", "Coding.PatchBatch"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingSymbolDiffAuditCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Symbol diff audit", "show mandatory symbol diff audit", "Coding.SymbolDiffAudit"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingGeneratedFileGuardCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Generated file guard", "show generated file guard", "Coding.GeneratedFileGuard"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingBuildThisCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Build this feature", "build this for me current feature", "Coding.BuildThis"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureBuilderCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Guided feature workflow", "guided feature workflow current feature", "Coding.FeatureBuilder"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingGuidedBundlePreviewCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Paired feature preview", "preview guided feature bundle current feature", "Coding.GuidedBundlePreview"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingImplementationPlannerCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Implementation planner", "feature implementation planner current feature", "Coding.ImplementationPlanner"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureIntakeCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Feature intake", "feature intake current feature", "Coding.FeatureIntake"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureOrchestratorCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Feature orchestrator", "autonomous feature orchestrator current feature", "Coding.FeatureOrchestrator"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingEvidencePackCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Evidence pack", "implementation evidence pack current feature", "Coding.EvidencePack"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingRoslynPlannerCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Roslyn edit planner", "roslyn edit planner current feature", "Coding.RoslynPlanner"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPatchSynthesisV2Command = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Patch synthesis v2", "multi-file patch synthesis current feature", "Coding.PatchSynthesisV2"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPatternCopyCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Pattern copy", "pattern copy current feature", "Coding.PatternCopy"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingTestGeneratorV2Command = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Behavior test generator", "behavior test generator current feature", "Coding.TestGeneratorV2"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingSliceStateCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Slice state", "implementation slice state current feature", "Coding.SliceState"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingSemanticDiffCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Semantic diff", "semantic diff summary current feature", "Coding.SemanticDiff"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingScoreV3Command = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Score v3", "mini codex score v3 current feature", "Coding.ScoreV3"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingBuildFeatureLaneCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Build feature lane", "show build feature lane", "Coding.BuildFeatureLane"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureWorkContextCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Feature work context", "feature work context current feature", "Coding.FeatureWorkContext"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureIntentCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Feature intent", "feature intent packet current feature", "Coding.FeatureIntent"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingBehaviorContractCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Behavior contract", "behavior contract current feature", "Coding.BehaviorContract"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingBehaviorTestsCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Behavior tests", "behavior test plan current feature", "Coding.BehaviorTests"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingBehaviorTestPreviewCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Behavior test preview", "preview behavior test patch current feature", "Coding.BehaviorTestPreview"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingImplementationSlicesCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Implementation slices", "implementation slice plan current feature", "Coding.ImplementationSlices"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPatchSlicesCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Patch slices", "patch slice plan current feature", "Coding.PatchSlices"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingExactPatchCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Exact patch", "exact patch synthesis current feature", "Coding.ExactPatch"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPatchIntelligenceCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Patch intelligence", "patch intelligence current feature", "Coding.PatchIntelligence"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPatchLoopCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Patch loop", "autonomous patch loop current feature", "Coding.PatchLoop"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureSessionLedgerCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Session ledger", "feature session ledger current feature", "Coding.FeatureSessionLedger"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureRunControllerCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Run controller", "feature run controller current feature", "Coding.FeatureRunController"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureExecutionPacketCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Feature execution packet", "feature execution packet current feature", "Coding.FeatureExecutionPacket"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingApplyGateCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Apply gate", "apply gate current feature", "Coding.ApplyGate"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPostPatchValidationCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Validation router", "post patch validation current feature", "Coding.PostPatchValidation"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingFeatureCompletionReceiptCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Completion receipt", "feature completion receipt", "Coding.FeatureCompletionReceipt"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingSymbolIndexCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Symbol index", "show csharp symbol index", "Coding.SymbolIndex"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingCallGraphCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Call graph", "show call graph", "Coding.CallGraph"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingOwnershipMapCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Ownership map", "ownership map current coding work", "Coding.OwnershipMap"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingBindingCheckCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Binding check", "xaml binding check", "Coding.BindingCheck"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingImpactedTestsCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Impacted tests", "show impacted tests", "Coding.ImpactedTests"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingTestTargetCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Test target", "resolve test target current coding work", "Coding.TestTarget"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingSafeEditWorkflowCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Safe edit", "safe edit workflow current change", "Coding.SafeEditWorkflow"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingExecutionPacketCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Execution packet", "show execution packet", "Coding.ExecutionPacket"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingApprovePacketCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Approve packet", "approve execution packet", "Coding.ApprovePacket"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingShowApprovedPacketCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Approved packet", "show approved packet", "Coding.ApprovedPacket"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPacketProgressCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Packet progress", "show packet progress", "Coding.PacketProgress"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingPacketCommandsCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Packet commands", "show packet commands", "Coding.PacketCommands"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingHealthScoreCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Health score", "workspace health score", "Coding.HealthScore"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingGitStatusCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Git status", "git status", "Coding.GitStatus"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingReviewChangesCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Review changes", "review current changes", "Coding.ReviewChanges"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingValidationPlanCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Validation plan", "validation plan", "Coding.ValidationPlan"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingSafeCommitCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Safe commit", "can i safely commit", "Coding.SafeCommit"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingCommitMessageCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Commit message", "draft commit message", "Coding.CommitMessage"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingReleaseNotesCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Release notes", "draft release notes", "Coding.ReleaseNotes"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingTimelineCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Session timeline", "show coding session timeline", "Coding.Timeline"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingRollbackPlanCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Rollback plan", "show rollback plan", "Coding.RollbackPlan"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingBuildCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Build", BuildConfirmedDotNetCommand("build"), "Coding.Build"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingTestCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Tests", BuildConfirmedDotNetCommand("test"), "Coding.Tests"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingLastFailureCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Last failure", "diagnose last build failure", "Coding.LastFailure"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingRepairRunnerCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Repair runner", "validation repair runner current feature", "Coding.RepairRunner"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingSuggestFixCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Suggest fix", "suggest patch from last failure", "Coding.SuggestFix"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingShowPatchPreviewCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Patch preview", "show pending patch preview", "Coding.PatchPreview"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingApplyPreviewCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Apply preview", "confirm apply last patch preview", "Coding.ApplyPreview"), () => !IsBusy && !IsRecording && !IsTranscribing);
        RunCodingReceiptsCommand = CreateAsyncCommand(() => RunCodingDiagnosticAsync("Coding receipts", "show coding receipts", "Coding.Receipts"), () => !IsBusy && !IsRecording && !IsTranscribing);
        OpenMaintenanceReceiptFolderCommand = CreateCommand(_ => OpenMaintenanceReceiptFolder());
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
        ReplaceChoices(CodingOutsideEditRunModeChoices, CodingHighRiskModeChoiceValues);
        ReplaceChoices(CodingSystemAdminActionModeChoices, CodingHighRiskModeChoiceValues);
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

    public ObservableCollection<CommandExplorerNodeViewModel> CommandExplorerRoots { get; } = new();

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

    public ObservableCollection<string> TextToSpeechEngineChoices { get; } = new();

    public ObservableCollection<string> TextToSpeechVoiceChoices { get; } = new();

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

    public string AssistantName => _services.AssistantProfile.AssistantName;

    public string AssistantWindowTitle => AssistantName;

    public string AssistantSettingsWindowTitle => $"{AssistantName} Settings";

    public string AssistantLocalLibraryToolTip =>
        $"Open {AssistantName}'s approved local RAG folder and vector index.";

    public string AssistantSourcesTopicsToolTip =>
        $"Manage approved sources and topics {AssistantName} can use for source-backed answers.";

    public string AssistantVoiceLabel => $"{AssistantName} voice";

    public string AssistantWillUseSelectedModelText =>
        $"{AssistantName} will use this model only after Check passes and Activate is clicked.";

    public string AssistantCodingWorkspaceDescription =>
        $"{AssistantName} can open, inspect, and later assist with projects in this folder. Keep this as the main coding domain.";

    public string AssistantPdfWorkspaceDescription =>
        $"{AssistantName} creates, inspects, combines, and splits PDFs from this folder by default. Leave the default if you want assistant-owned generated documents.";

    public string AssistantCodingGuardrailsDescription =>
        $"These are {AssistantName}'s coding guardrails. Locked rows are shown for clarity and require a separate high-trust workflow before they can change.";

    public string AssistantExtraConfirmationDescription =>
        $"Future destructive file behavior. Extra confirmation means {AssistantName} must ask before proceeding.";

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

    public ICommand RecommendRuntimeSettingsCommand { get; }

    public ICommand ActivateRuntimeCommand { get; }

    public ICommand RevertToStubCommand { get; }

    public ICommand RevertToLastKnownGoodCommand { get; }

    public ICommand PasteImageCommand { get; }

    public ICommand RemoveAttachmentCommand { get; }

    public ICommand BeginAssignPushToTalkKeyCommand { get; }

    public ICommand SendTranscriptCommand { get; }

    public ICommand StopSpeakingCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenMaintenanceDashboardCommand { get; }

    public ICommand OpenProgrammingDashboardCommand { get; }

    public ICommand OpenLocalLibraryCommand { get; }

    public ICommand OpenSourcesTopicsCommand { get; }

    public ICommand ToggleCommandExplorerCommand { get; }

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

    public ICommand SaveCodingPermissionsCommand { get; }

    public ICommand ResetCodingPermissionsCommand { get; }

    public ICommand BrowseCodingWorkspaceRootCommand { get; }

    public ICommand BrowseCodingPdfWorkspaceRootCommand { get; }

    public ICommand BrowseNotepadPlusPlusPathCommand { get; }

    public ICommand BrowseVisualStudioPathCommand { get; }

    public ICommand RunComputerHealthCheckCommand { get; }

    public ICommand RepairAliInstallCommand { get; }

    public ICommand RunComputerAssistantSetupCommand { get; }

    public ICommand RunMaintenancePlanCommand { get; }

    public ICommand RunProcessEvidenceCommand { get; }

    public ICommand RunBuildLockDiagnosticCommand { get; }

    public ICommand RunPortDiagnosticCommand { get; }

    public ICommand RunServicesStartupInspectionCommand { get; }

    public ICommand RunDiskCleanupPlanCommand { get; }

    public ICommand RunSuspiciousActivityPlanCommand { get; }

    public ICommand RunAppInstallTroubleshootingCommand { get; }

    public ICommand RunPeripheralSetupPlanCommand { get; }

    public ICommand RunCodingWorkspaceDiagnosticCommand { get; }

    public ICommand RunCodingProjectIntelligenceCommand { get; }

    public ICommand RunCodingProjectIndexCommand { get; }

    public ICommand RunCodingRepoUnderstandingCommand { get; }

    public ICommand RunCodingContextPacketCommand { get; }

    public ICommand RunCodingFullReadinessCommand { get; }

    public ICommand RunCodingMiniCodexStatusCommand { get; }

    public ICommand RunCodingReadinessReportCommand { get; }

    public ICommand RunCodingNextBestActionCommand { get; }

    public ICommand RunCodingValidationQueueCommand { get; }

    public ICommand RunCodingPatchBatchCommand { get; }

    public ICommand RunCodingSymbolDiffAuditCommand { get; }

    public ICommand RunCodingGeneratedFileGuardCommand { get; }

    public ICommand RunCodingBuildThisCommand { get; }

    public ICommand RunCodingFeatureBuilderCommand { get; }

    public ICommand RunCodingGuidedBundlePreviewCommand { get; }

    public ICommand RunCodingImplementationPlannerCommand { get; }

    public ICommand RunCodingFeatureIntakeCommand { get; }

    public ICommand RunCodingFeatureOrchestratorCommand { get; }

    public ICommand RunCodingEvidencePackCommand { get; }

    public ICommand RunCodingRoslynPlannerCommand { get; }

    public ICommand RunCodingPatchSynthesisV2Command { get; }

    public ICommand RunCodingPatternCopyCommand { get; }

    public ICommand RunCodingTestGeneratorV2Command { get; }

    public ICommand RunCodingSliceStateCommand { get; }

    public ICommand RunCodingSemanticDiffCommand { get; }

    public ICommand RunCodingScoreV3Command { get; }

    public ICommand RunCodingBuildFeatureLaneCommand { get; }

    public ICommand RunCodingFeatureWorkContextCommand { get; }

    public ICommand RunCodingFeatureIntentCommand { get; }

    public ICommand RunCodingBehaviorContractCommand { get; }

    public ICommand RunCodingBehaviorTestsCommand { get; }

    public ICommand RunCodingBehaviorTestPreviewCommand { get; }

    public ICommand RunCodingImplementationSlicesCommand { get; }

    public ICommand RunCodingPatchSlicesCommand { get; }

    public ICommand RunCodingExactPatchCommand { get; }

    public ICommand RunCodingPatchIntelligenceCommand { get; }

    public ICommand RunCodingPatchLoopCommand { get; }

    public ICommand RunCodingFeatureSessionLedgerCommand { get; }

    public ICommand RunCodingFeatureRunControllerCommand { get; }

    public ICommand RunCodingFeatureExecutionPacketCommand { get; }

    public ICommand RunCodingApplyGateCommand { get; }

    public ICommand RunCodingPostPatchValidationCommand { get; }

    public ICommand RunCodingFeatureCompletionReceiptCommand { get; }

    public ICommand RunCodingSymbolIndexCommand { get; }

    public ICommand RunCodingCallGraphCommand { get; }

    public ICommand RunCodingOwnershipMapCommand { get; }

    public ICommand RunCodingBindingCheckCommand { get; }

    public ICommand RunCodingImpactedTestsCommand { get; }

    public ICommand RunCodingTestTargetCommand { get; }

    public ICommand RunCodingSafeEditWorkflowCommand { get; }

    public ICommand RunCodingExecutionPacketCommand { get; }

    public ICommand RunCodingApprovePacketCommand { get; }

    public ICommand RunCodingShowApprovedPacketCommand { get; }

    public ICommand RunCodingPacketProgressCommand { get; }

    public ICommand RunCodingPacketCommandsCommand { get; }

    public ICommand RunCodingHealthScoreCommand { get; }

    public ICommand RunCodingGitStatusCommand { get; }

    public ICommand RunCodingReviewChangesCommand { get; }

    public ICommand RunCodingValidationPlanCommand { get; }

    public ICommand RunCodingSafeCommitCommand { get; }

    public ICommand RunCodingCommitMessageCommand { get; }

    public ICommand RunCodingReleaseNotesCommand { get; }

    public ICommand RunCodingTimelineCommand { get; }

    public ICommand RunCodingRollbackPlanCommand { get; }

    public ICommand RunCodingBuildCommand { get; }

    public ICommand RunCodingTestCommand { get; }

    public ICommand RunCodingLastFailureCommand { get; }

    public ICommand RunCodingRepairRunnerCommand { get; }

    public ICommand RunCodingSuggestFixCommand { get; }

    public ICommand RunCodingShowPatchPreviewCommand { get; }

    public ICommand RunCodingApplyPreviewCommand { get; }

    public ICommand RunCodingReceiptsCommand { get; }

    public ICommand OpenMaintenanceReceiptFolderCommand { get; }

    public ICommand BackupUserDataCommand { get; }

    public ICommand RestoreUserDataCommand { get; }

    public string RuntimeSettingsPath => _services.RuntimeSettingsPath;

    public string CodingToolSettingsPath => _services.CodingToolSettingsPath;

    public string MaintenanceReceiptPath => Path.Combine(_services.DataRoot, "Receipts", "maintenance-actions.jsonl");

    public string MaintenanceReceiptFolder => Path.GetDirectoryName(MaintenanceReceiptPath) ?? _services.DataRoot;

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

    public string MaintenanceStatusText
    {
        get => _maintenanceStatusText;
        private set => SetProperty(ref _maintenanceStatusText, value);
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
                RaiseCommandStates();
            }
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
        var reachedOutputLimit = false;
        var pendingVisibleText = new StringBuilder();
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
            await foreach (var chunk in _services.Orchestrator.StreamAnswerAsync(
                               _conversationId,
                               userMessageId,
                               assistantMessageId,
                               text,
                               history,
                               attachments,
                               _activeResponse.Token))
            {
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
            CancelStreamingSpeech(streamingSpeech);
            StatusText = "Response stopped.";
        }
        catch (HttpRequestException ex)
        {
            await FlushVisibleTextAsync(force: true, pace: false).ConfigureAwait(true);
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
                DescribeSourceCatalogHealth()
            };

            foreach (var check in new[]
                     {
                         ("Ali install", "show install doctor"),
                         ("Computer assistant", "show computer assistant status"),
                         ("PDF tools", "show pdf tool status"),
                         ("Visual Studio", "show visual studio integration"),
                         ("Receipts", "show coding receipts")
                     })
            {
                var result = await _services.LocalCodingTool.TryHandleAsync(check.Item2, _lifetimeCancellation.Token).ConfigureAwait(true);
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
            "Repair Ali's local install data now?\n\nThis repairs the bundled Sources & Topics catalog, missing example/config helper files, and local voice tool paths. It preserves chats, memories, reminders, app settings, installed models, and the selected runtime model.",
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
            LoadCodingPermissions();

            var hasVoiceWarning = warnings.Any(warning => warning.Contains("voice", StringComparison.OrdinalIgnoreCase));
            var output = BuildMaintenanceStatusText(
                "Ali install repair",
                [
                    ComponentStatus("Install data", warnings.Count == 0, warnings.Count == 0 ? "repaired" : $"{warnings.Count} warning(s)"),
                    DescribeSourceCatalogHealth(),
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
                         ("Visual Studio", "show visual studio integration"),
                         ("PDF tools", "show pdf tool status")
                     })
            {
                var result = await _services.LocalCodingTool.TryHandleAsync(check.Item2, _lifetimeCancellation.Token).ConfigureAwait(true);
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

            var sourceHealth = DescribeSourceCatalogHealth();
            componentLines.Add(sourceHealth);
            if (sourceHealth.Contains(" - Bad", StringComparison.OrdinalIgnoreCase))
            {
                needsAttention.Add("Sources & Topics need repair.");
            }

            foreach (var check in new[]
                     {
                         ("Ali install", "show install doctor"),
                         ("Computer assistant", "show computer assistant status"),
                         ("PDF tools", "show pdf tool status"),
                         ("Visual Studio", "show visual studio integration"),
                         ("Receipts", "show coding receipts")
                     })
            {
                var result = await _services.LocalCodingTool.TryHandleAsync(check.Item2, _lifetimeCancellation.Token).ConfigureAwait(true);
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

            var result = await _services.LocalCodingTool.TryHandleAsync(command, _lifetimeCancellation.Token).ConfigureAwait(true);
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

    private async Task RunCodingDiagnosticAsync(string title, string command, string actionType)
    {
        var startedAt = DateTimeOffset.Now;
        try
        {
            IsBusy = true;
            StatusText = $"Running {title.ToLowerInvariant()}...";
            MaintenanceStatusText = $"Running {title.ToLowerInvariant()}...";

            var result = await _services.LocalCodingTool.TryHandleAsync(command, _lifetimeCancellation.Token).ConfigureAwait(true);
            var output = BuildCodingDiagnosticText(title, command, result);

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

    private string BuildConfirmedDotNetCommand(string verb)
        => $"confirm dotnet {verb} \"{EscapeCodingCommandPath(FindCodingDiagnosticTarget())}\"";

    private string FindCodingDiagnosticTarget()
    {
        var target = CodingWorkspaceRootText;
        if (!CodingWorkspacePolicy.TryNormalizePath(target, out var normalizedTarget))
        {
            normalizedTarget = _services.LocalCodingTool.Policy.WorkspaceRoot;
        }

        if (File.Exists(normalizedTarget))
        {
            return normalizedTarget;
        }

        if (!Directory.Exists(normalizedTarget))
        {
            return _services.LocalCodingTool.Policy.WorkspaceRoot;
        }

        try
        {
            var topLevelTarget = Directory.EnumerateFiles(normalizedTarget, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsDotNetTargetFile)
                .OrderBy(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(topLevelTarget))
            {
                return topLevelTarget;
            }

            return Directory.EnumerateFiles(normalizedTarget, "*.*", SearchOption.AllDirectories)
                .Where(IsDotNetTargetFile)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                               && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? normalizedTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return normalizedTarget;
        }
    }

    private static bool IsDotNetTargetFile(string path)
        => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static string EscapeCodingCommandPath(string path)
        => path.Replace("\"", string.Empty, StringComparison.Ordinal);

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
        LoadCodingPermissions();
        ReloadVoiceSettingsFromDisk();
        RefreshConversationHistory();
        RefreshMemoryReminders();
        if (string.Equals(Path.GetFullPath(restore.RestoredProfileDataRoot), Path.GetFullPath(_services.ProfileDataRoot), StringComparison.OrdinalIgnoreCase))
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
        RepairStarterSources(messages, warnings);
        RuntimeSettingsStore.WriteExample(_services.DataRoot);
        messages.Add("Runtime settings example verified; selected runtime model was not changed.");
        LocalVectorLibrarySettingsStore.WriteExample(_services.DataRoot);
        _services.CreateLocalVectorLibraryRetriever().WriteExample();
        messages.Add("Local library settings and index folders verified.");
        CodingToolSettingsStore.WriteExample(_services.DataRoot);
        messages.Add("Coding permission settings example verified.");
        RepairVoiceToolSettings(messages, warnings);
    }

    private void RepairStarterSources(List<string> messages, List<string> warnings)
    {
        try
        {
            var sourceStore = _services.CreateFileSourceRetriever();
            var result = sourceStore.RepairStarterCatalog();
            sourceStore.WriteExample();

            if (result.CatalogCreated)
            {
                messages.Add($"Sources & Topics catalog created with {result.AddedStarterSourceCount} approved source(s).");
            }
            else if (result.AddedStarterSourceCount > 0)
            {
                messages.Add($"Sources & Topics repaired: added {result.AddedStarterSourceCount} missing approved source(s), preserved {result.ExistingSourceCount} existing source(s).");
            }
            else
            {
                messages.Add($"Sources & Topics verified: {result.ExistingSourceCount} approved source(s) already present.");
            }

            if (!string.IsNullOrWhiteSpace(result.BackupPath))
            {
                warnings.Add($"Invalid Sources & Topics catalog was backed up before repair: {result.BackupPath}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            warnings.Add($"Sources & Topics could not be repaired: {ex.Message}");
        }
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

    private string DescribeSourceCatalogHealth()
    {
        try
        {
            var sources = _services.LoadCuratedSources();
            return ComponentStatus("Sources & Topics", sources.Count > 0, $"{sources.Count} sources");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ComponentStatus("Sources & Topics", false, ShortMaintenanceDetail(ex.Message));
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

    private static string BuildMaintenanceDiagnosticText(string title, string command, CodingToolResult result)
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

    private static string BuildCodingDiagnosticText(string title, string command, CodingToolResult result)
    {
        var status = result is { Handled: true, Succeeded: true };
        var lines = new List<string>
        {
            $"{title}: {DateTimeOffset.Now.LocalDateTime:g}",
            ComponentStatus(title, status, status ? "complete" : ShortMaintenanceDetail(result.Message))
        };

        lines.AddRange(BuildCodingDiagnosticBody(command, result.Message));
        lines.Add(status
            ? BuildCodingDiagnosticNextAction(command)
            : "Next - Review the bad row above, then run Last Failure or Check Tools.");
        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> BuildCodingDiagnosticBody(string command, string message)
    {
        var lines = SplitMaintenanceLines(message);
        if (command.StartsWith("inspect coding workspace", StringComparison.OrdinalIgnoreCase))
        {
            return CompactCodingWorkspaceLines(lines);
        }

        if (command.StartsWith("show project intelligence", StringComparison.OrdinalIgnoreCase))
        {
            return CompactProjectIntelligenceLines(lines);
        }

        if (command.StartsWith("understand repo", StringComparison.OrdinalIgnoreCase))
        {
            return CompactRepoUnderstandingLines(lines);
        }

        if (command.StartsWith("git status", StringComparison.OrdinalIgnoreCase))
        {
            return CompactGitStatusLines(lines);
        }

        if (command.StartsWith("review current changes", StringComparison.OrdinalIgnoreCase))
        {
            return CompactReviewChangesLines(lines);
        }

        if (command.StartsWith("validation plan", StringComparison.OrdinalIgnoreCase))
        {
            return CompactValidationPlanLines(lines);
        }

        if (command.StartsWith("can i safely commit", StringComparison.OrdinalIgnoreCase))
        {
            return CompactSafeCommitLines(lines);
        }

        if (command.StartsWith("confirm dotnet build", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("confirm dotnet test", StringComparison.OrdinalIgnoreCase))
        {
            return CompactDotNetLines(lines);
        }

        if (command.StartsWith("diagnose last", StringComparison.OrdinalIgnoreCase))
        {
            return CompactLastFailureLines(lines);
        }

        if (command.StartsWith("suggest patch from last failure", StringComparison.OrdinalIgnoreCase))
        {
            return CompactSuggestFixLines(lines);
        }

        if (command.StartsWith("show pending patch preview", StringComparison.OrdinalIgnoreCase))
        {
            return CompactPatchPreviewLines(lines);
        }

        if (command.StartsWith("confirm apply last patch preview", StringComparison.OrdinalIgnoreCase))
        {
            return CompactApplyPreviewLines(lines);
        }

        if (command.StartsWith("show coding receipts", StringComparison.OrdinalIgnoreCase))
        {
            return CompactReceiptLines(lines);
        }

        if (command.StartsWith("mini codex readiness report", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show coding next best action", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show validation queue runner", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show owner safe patch batch", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show mandatory symbol diff audit", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show generated file guard", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("guided feature workflow", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("preview guided feature bundle", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature implementation planner", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature intake", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("autonomous feature orchestrator", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("implementation evidence pack", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("build this for me", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("roslyn edit planner", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("multi-file patch synthesis", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("multi file patch synthesis", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("pattern copy", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("behavior test generator", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("implementation slice state", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("post apply repair loop", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("post-apply repair loop", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("semantic diff summary", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("mini codex score v3", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show build feature lane", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature work context", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature intent packet", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("behavior contract", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("behavior test plan", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("preview behavior test patch", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("implementation slice plan", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("patch slice plan", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature execution packet", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("apply gate", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("post patch validation", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature completion receipt", StringComparison.OrdinalIgnoreCase))
        {
            return CompactCodingCockpitLines(lines);
        }

        return lines
            .Where(line => !LooksLikeMaintenanceBoilerplate(line))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(ShortMaintenanceDetail)
            .Take(8)
            .ToList();
    }

    private static string BuildCodingDiagnosticNextAction(string command)
    {
        if (command.StartsWith("git status", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Review changed files before build, test, or commit.";
        }

        if (command.StartsWith("show project intelligence", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Use Plan, Build, Tests, or Review from the Programming dashboard.";
        }

        if (command.StartsWith("understand repo", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Pick the action that matches the weakest row above.";
        }

        if (command.StartsWith("review current changes", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Run build/tests before committing reviewed changes.";
        }

        if (command.StartsWith("validation plan", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Run Build, then Tests, then Review.";
        }

        if (command.StartsWith("can i safely commit", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Commit only if Safe to commit says Yes.";
        }

        if (command.StartsWith("confirm dotnet build", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Run Tests if build is good.";
        }

        if (command.StartsWith("confirm dotnet test", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Review receipts and commit only after expected changes are verified.";
        }

        if (command.StartsWith("diagnose last", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Run Suggest Fix if the failure points to a simple code patch.";
        }

        if (command.StartsWith("suggest patch from last failure", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Run Patch Preview, then Apply Preview only if it is clearly right.";
        }

        if (command.StartsWith("show pending patch preview", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Apply Preview only if the shown change is clearly right.";
        }

        if (command.StartsWith("mini codex readiness report", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show coding next best action", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Use the cockpit row to run the next safe diagnostic.";
        }

        if (command.StartsWith("show validation queue runner", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Approve only the validation command you intend to run.";
        }

        if (command.StartsWith("show owner safe patch batch", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("show mandatory symbol diff audit", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Review the patch and symbol rows before applying anything.";
        }

        if (command.StartsWith("show generated file guard", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Avoid generated files unless the owner explicitly asked for them.";
        }

        if (command.StartsWith("show build feature lane", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("guided feature workflow", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("preview guided feature bundle", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature implementation planner", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature intake", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("autonomous feature orchestrator", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("implementation evidence pack", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("build this for me", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("roslyn edit planner", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("multi-file patch synthesis", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("multi file patch synthesis", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("pattern copy", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("behavior test generator", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("implementation slice state", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("post apply repair loop", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("post-apply repair loop", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("semantic diff summary", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("mini codex score v3", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature work context", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature intent packet", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("behavior contract", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("behavior test plan", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("preview behavior test patch", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("implementation slice plan", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("patch slice plan", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature execution packet", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("apply gate", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("post patch validation", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("feature completion receipt", StringComparison.OrdinalIgnoreCase))
        {
            return command.StartsWith("guided feature workflow", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("preview guided feature bundle", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("feature implementation planner", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("feature intake", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("autonomous feature orchestrator", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("implementation evidence pack", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("build this for me", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("roslyn edit planner", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("multi-file patch synthesis", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("multi file patch synthesis", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("pattern copy", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("behavior test generator", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("implementation slice state", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("post apply repair loop", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("post-apply repair loop", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("semantic diff summary", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("mini codex score v3", StringComparison.OrdinalIgnoreCase)
                ? "Next - Run the single next command shown by the workflow."
                : "Next - Work the feature lane top to bottom before previewing a patch.";
        }

        if (command.StartsWith("confirm apply last patch preview", StringComparison.OrdinalIgnoreCase))
        {
            return "Next - Run Validate, then Review changes.";
        }

        return "Next - Pick the next coding diagnostic button as needed.";
    }


    private static IReadOnlyList<string> CompactCodingCockpitLines(IReadOnlyList<string> lines)
    {
        var prefixes = new[]
        {
            "Overall score:",
            "Status:",
            "Next:",
            "Mode:",
            "Changed files:",
            "Coverage:",
            "Receipt enforcement:",
            "Pending batch:",
            "Report card:",
            "Endzone estimate:",
            "Validation:",
            "Patch:",
            "Git:",
            "Latest validation:",
            "Plan confidence:",
            "Plan reasons:",
            "Request:",
            "Guided feature workflow",
            "Workflow stage:",
            "Build readiness:",
            "Code preview:",
            "Test preview:",
            "Patch/test pairing:",
            "Guided feature bundle preview:",
            "Behavior test edit:",
            "Code edits:",
            "Bundle edits:",
            "Pairing rule:",
            "Feature implementation planner",
            "Implementation order:",
            "Files by role:",
            "Roslyn targeting:",
            "Validation matrix:",
            "Rollback and stop plan:",
            "Feature intake normalizer",
            "Normalized brief:",
            "Ambiguity check:",
            "Acceptance checks:",
            "Starting route:",
            "Autonomous feature orchestrator",
            "Readiness score:",
            "12-cycle board:",
            "Stop rules:",
            "Implementation evidence pack",
            "Evidence status:",
            "Proof checklist:",
            "Recent receipts:",
            "Risk and rollback:",
            "Build this feature",
            "Front-door plan:",
            "Execution route:",
            "Repair route:",
            "Roslyn edit planner",
            "Symbol targets:",
            "Edit order:",
            "Call and reference guard:",
            "Multi-file patch synthesis",
            "Bundle candidates:",
            "Cross-file roles:",
            "Pattern copy plan",
            "Nearby patterns:",
            "Copy rules:",
            "Behavior test generator",
            "Generated shape:",
            "Assertion guidance:",
            "Implementation slice state",
            "Slices:",
            "State memory:",
            "Post-apply repair loop",
            "Likely scope:",
            "Semantic diff summary",
            "Semantic changes:",
            "Patch checks:",
            "Mini-Codex score v3:",
            "Category scores:",
            "Next score move:",
            "Goal:",
            "Behavior test patch preview:",
            "Test file:",
            "Framework:",
            "Generated test:",
            "Top target:",
            "Risk labels:",
            "Test target:",
            "Gate result:",
            "Validation router:",
            "User-facing behavior:",
            "Affected area:",
            "Validation depth:",
            "Risk-aware test depth:",
            "Scores:",
            "Closeout checklist:",
            "Feature brief:",
            "Target map:",
            "Patch draft path:",
            "Preview/apply readiness:",
            "Validation and repair:",
            "Builder status:",
            "Builder runbook:",
            "Failure repair packet:",
            "One-command path:",
            "Ask/stop rules:",
            "Owner boundary:",
            "Next command:",
            "Preview-ready edits:",
            "Patch blocks:",
            "Preview route:",
            "Loop steps:",
            "Failure classifier:",
            "Stage:",
            "Current stage:",
            "Session score:",
            "Phase:",
            "Go/no-go:",
            "Controller score:",
            "Failure type:",
            "Failed action:",
            "Failed target:",
            "Retry budget:",
            "Checklist:",
            "Evidence:",
            "Owner-safe rule:",
            "Repair focus:",
            "Likely fix candidates:",
            "Repair steps:",
            "Preview attempt:",
            "Run state:",
            "Stop gates:",
            "Owner controls:",
            "Patch intelligence:",
            "Slice approval packet:",
            "Patch preview gate v3:",
            "Mini-Codex score audit:",
            "- Feature type:",
            "- User result:",
            "- Acceptance check:",
            "- Primary file:",
            "- Candidate files:",
            "- Test path:",
            "- Latest validation:",
            "- State:",
            "- Active slice:",
            "- Repair route:",
            "- Preview guard:",
            "- Mechanical transform:",
            "- File:",
            "- Ready:",
            "- Category:",
            "- Intent:",
            "- Target:",
            "- Synthesis:",
            "- Preview:",
            "- Apply:",
            "- Closeout:",
            "- Validation route:",
            "- Active slice:",
            "- First diagnostic:",
            "- Code:",
            "- Line:",
            "- Nearest symbol:",
            "- Targeted validation:",
            "- Input:",
            "- Expected result:",
            "- Failure behavior:",
            "- Permission boundary:",
            "- Target confidence:",
            "- Test readiness:",
            "- Validation receipt:",
            "- Route template:",
            "- Score estimate:",
            "- Protected files:",
            "- Gate result:",
            "- Pending preview:",
            "- Final:",
            "- Codebase awareness:",
            "- Edit planning:",
            "- Patch safety:",
            "- Validation/release:",
            "- Autonomous workflow:",
            "- Dashboard usability:",
            "- 1.",
            "- 2.",
            "- 3.",
            "- 4.",
            "- 5.",
            "- 6.",
            "- 7.",
            "- 8.",
            "- 9.",
            "- 10.",
            "- 11.",
            "- 12.",
            "- High",
            "- Medium",
            "- Low",
            "- Intake:",
            "- Target:",
            "- Primary target:",
            "- Risk level:",
            "- Acceptance:",
            "- Source edits:",
            "- Exact preview edits:",
            "- UI/ViewModel candidates:",
            "- Bundle size guard:",
            "- Pattern source:",
            "- Copy naming",
            "- Arrange",
            "- Act",
            "- Assert",
            "- Goal memory:",
            "- Recent receipts:",
            "- Test intent:",
            "- Code diff:",
            "- Review:",
            "- Edit proof:",
            "- Validation proof:",
            "- Git proof:",
            "- Behavior test:",
            "- Code patch:",
            "- Paired preview:",
            "- Apply state:",
            "- Release state:",
            "- Git state:"
        };
        var compact = lines
            .Where(line => prefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(line => ShortMaintenanceDetail(line.TrimStart('-', ' ')))
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["Cockpit - No summary rows returned"];
    }
    private static IReadOnlyList<string> CompactReviewChangesLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Changed files:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Staged:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Unstaged:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Untracked:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Diff check:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Project/dependency", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Source files", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Deleted files", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Renamed files", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Large change", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
            .Select(ShortMaintenanceDetail)
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["No review details found."];
    }

    private static IReadOnlyList<string> CompactValidationPlanLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Git:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Latest validation:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Patch preview:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Build:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Tests:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Review:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- Commit:", StringComparison.OrdinalIgnoreCase))
            .Select(ShortMaintenanceDetail)
            .Take(8)
            .ToList();
        return compact.Count > 0 ? compact : ["No validation plan found."];
    }

    private static IReadOnlyList<string> CompactCodingWorkspaceLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Workspace", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Primary", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Projects", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Solutions", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Test projects", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Existing", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(line => ShortMaintenanceDetail(line.TrimStart('-', ' ')))
            .Take(8)
            .ToList();
        return compact.Count > 0 ? compact : ["Workspace - No workspace summary returned"];
    }

    private static IReadOnlyList<string> CompactProjectIntelligenceLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Shape:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Project roles:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Primary target:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Likely app", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Likely test", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Important entry", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Other project", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Build:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Tests:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- No obvious", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Multiple", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(line => ShortMaintenanceDetail(line.TrimStart('-', ' ')))
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["Project intelligence - No project summary returned"];
    }

    private static IReadOnlyList<string> CompactRepoUnderstandingLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Shape:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Detected stacks:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Style signals:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Project roles:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Primary target:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Build commands:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Test commands:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Safe to commit:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Git:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Validation:", StringComparison.OrdinalIgnoreCase))
            .Select(line => ShortMaintenanceDetail(line.TrimStart('-', ' ')))
            .Take(12)
            .ToList();
        return compact.Count > 0 ? compact : ["Repo understanding - No summary returned"];
    }

    private static IReadOnlyList<string> CompactSafeCommitLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Safe to commit:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Git:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Validation:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Pending patch preview:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Git", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- No successful", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- A pending", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Run ", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- Review ", StringComparison.OrdinalIgnoreCase))
            .Select(line => ShortMaintenanceDetail(line.TrimStart('-', ' ')))
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["Safe commit - No readiness summary returned"];
    }

    private static IReadOnlyList<string> CompactGitStatusLines(IReadOnlyList<string> lines)
    {
        var entries = lines
            .Where(line => !line.StartsWith("Git", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Where(line => Regex.IsMatch(line, @"^(##|[ MADRCU?!]{1,2}\s+)"))
            .Select(ShortMaintenanceDetail)
            .Take(12)
            .ToList();
        if (entries.Count > 0)
        {
            entries.Insert(0, $"Changes - {entries.Count} shown");
            return entries;
        }

        var clean = lines.FirstOrDefault(line => line.Contains("clean", StringComparison.OrdinalIgnoreCase));
        return [ShortMaintenanceDetail(clean ?? "Git - Clean or no changes returned")];
    }

    private static IReadOnlyList<string> CompactDotNetLines(IReadOnlyList<string> lines)
    {
        var compact = new List<string>();
        AddFirstMatching(compact, lines, "Action", line => line.StartsWith("Action:", StringComparison.OrdinalIgnoreCase));
        AddFirstMatching(compact, lines, "Target", line => line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase));
        AddFirstMatching(compact, lines, "Exit code", line => line.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase));
        AddFirstMatching(compact, lines, "Summary", line => line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                                                           || line.Contains("Build failed", StringComparison.OrdinalIgnoreCase)
                                                           || line.Contains("Test Run Successful", StringComparison.OrdinalIgnoreCase)
                                                           || line.Contains("Failed!", StringComparison.OrdinalIgnoreCase)
                                                           || line.Contains("Passed!", StringComparison.OrdinalIgnoreCase));
        compact.AddRange(lines
            .Where(IsLikelyCompilerOrTestError)
            .Select(ShortMaintenanceDetail)
            .Take(6));

        if (compact.Count == 0)
        {
            compact.Add("Output - No concise build/test summary returned");
        }

        return compact;
    }

    private static IReadOnlyList<string> CompactLastFailureLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Category:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Action:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase)
                           || IsLikelyCompilerOrTestError(line)
                           || line.Contains("No failed dotnet command", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(ShortMaintenanceDetail)
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["Failure - No stored failure found"];
    }

    private static IReadOnlyList<string> CompactSuggestFixLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Suggested patch", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("No deterministic patch", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("No failed dotnet command", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Diagnostic:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Line:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- File:", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("No files were changed", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(ShortMaintenanceDetail)
            .Take(8)
            .ToList();
        return compact.Count > 0 ? compact : ["Fix preview - No deterministic patch suggestion found"];
    }

    private static IReadOnlyList<string> CompactPatchPreviewLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Patch preview", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Patch bundle preview", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Pending patch", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("No patch preview", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Line:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Edit ", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- File:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Before:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("After:", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(ShortMaintenanceDetail)
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["Patch preview - Nothing pending"];
    }

    private static IReadOnlyList<string> CompactApplyPreviewLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.StartsWith("Applied last patch preview", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Last patch preview was not applied", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("No patch preview", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Applied ", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Changed file:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("- ", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(ShortMaintenanceDetail)
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["Apply preview - No patch was applied"];
    }

    private static IReadOnlyList<string> CompactReceiptLines(IReadOnlyList<string> lines)
    {
        var compact = lines
            .Where(line => line.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("receipt", StringComparison.OrdinalIgnoreCase))
            .Where(line => !LooksLikeCommandSuggestion(line))
            .Select(ShortMaintenanceDetail)
            .Take(10)
            .ToList();
        return compact.Count > 0 ? compact : ["Receipts - None found"];
    }

    private static void AddFirstMatching(ICollection<string> target, IReadOnlyList<string> lines, string label, Func<string, bool> predicate)
    {
        var match = lines.FirstOrDefault(predicate);
        if (!string.IsNullOrWhiteSpace(match))
        {
            target.Add($"{label} - {ShortMaintenanceDetail(match)}");
        }
    }

    private static bool IsLikelyCompilerOrTestError(string line)
        => Regex.IsMatch(line, @"\b(error|failed|failure|exception|CS\d{4}|CA\d{4}|NETSDK\d{4}|NU\d{4})\b", RegexOptions.IgnoreCase);

    private static bool LooksLikeCommandSuggestion(string line)
    {
        var trimmed = line.TrimStart('-', ' ');
        return trimmed.StartsWith("run:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("confirm ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("git ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("show ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("diagnose ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("preview ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("apply ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("mark ", StringComparison.OrdinalIgnoreCase);
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

    private static string FormatMaintenanceCommandResult(string component, CodingToolResult result)
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
                   || line.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("VSIX", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Confirm ", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Manual dependency", StringComparison.OrdinalIgnoreCase);
        }

        if (component.Equals("Visual Studio", StringComparison.OrdinalIgnoreCase))
        {
            return line.Contains("Primary solution/project", StringComparison.OrdinalIgnoreCase);
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

    private static string DefaultBackupDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "Ali Backups");
    }

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
            Description = $"Choose {AssistantName}'s coding workspace folder",
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
            Description = $"Choose {AssistantName}'s PDF workspace folder",
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
        if (!AutoSendVoiceTranscripts || _pushToTalkKeyDown || IsRecording || IsTranscribing || IsBusy)
        {
            return;
        }

        _pushToTalkKeyDown = true;
        OnPropertyChanged(nameof(PushToTalkKeyButtonText));
        _currentVoiceInputShouldAutoSend = true;
        try
        {
            await StartVoiceRecordingAsync().ConfigureAwait(true);
        }
        catch
        {
            _pushToTalkKeyDown = false;
            OnPropertyChanged(nameof(PushToTalkKeyButtonText));
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
        OnPropertyChanged(nameof(PushToTalkKeyButtonText));
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
            var selectedId = SelectedCorrectionReviewItem?.Id;
            using var operation = CreateLinkedTimeout(TimeSpan.FromSeconds(10));
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
            using var operation = CreateLinkedTimeout(TimeSpan.FromSeconds(10));
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
        }
        catch (Exception ex)
        {
            TtsStatus = $"Speech failed: {ex.Message}";
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

    private void OpenMaintenanceDashboard()
    {
        if (_maintenanceDashboardWindow is not null)
        {
            if (!_maintenanceDashboardWindow.IsVisible)
            {
                _maintenanceDashboardWindow.Show();
            }

            _maintenanceDashboardWindow.Activate();
            return;
        }

        var owner = System.Windows.Application.Current?.MainWindow;
        _maintenanceDashboardWindow = new MaintenanceDashboardWindow
        {
            DataContext = this,
            Owner = owner
        };
        _maintenanceDashboardWindow.Closed += (_, _) => _maintenanceDashboardWindow = null;
        _maintenanceDashboardWindow.Show();
        _maintenanceDashboardWindow.Activate();
    }

    private void OpenProgrammingDashboard()
    {
        if (_programmingDashboardWindow is not null)
        {
            if (!_programmingDashboardWindow.IsVisible)
            {
                _programmingDashboardWindow.Show();
            }

            _programmingDashboardWindow.Activate();
            return;
        }

        var owner = System.Windows.Application.Current?.MainWindow;
        _programmingDashboardWindow = new ProgrammingDashboardWindow
        {
            DataContext = this,
            Owner = owner
        };
        _programmingDashboardWindow.Closed += (_, _) => _programmingDashboardWindow = null;
        _programmingDashboardWindow.Show();
        _programmingDashboardWindow.Activate();
    }

    private void OpenMaintenanceReceiptFolder()
    {
        try
        {
            Directory.CreateDirectory(MaintenanceReceiptFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = MaintenanceReceiptFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MaintenanceStatusText = $"Could not open receipt folder: {ex.Message}";
        }
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

    private void OpenSourcesTopics()
    {
        if (_sourcesTopicsWindow is not null)
        {
            if (!_sourcesTopicsWindow.IsVisible)
            {
                _sourcesTopicsWindow.Show();
            }

            _sourcesTopicsWindow.Activate();
            return;
        }

        var owner = System.Windows.Application.Current?.MainWindow;
        _sourcesTopicsWindow = new SourcesTopicsWindow(_services)
        {
            Owner = owner
        };
        _sourcesTopicsWindow.Closed += (_, _) => _sourcesTopicsWindow = null;
        _sourcesTopicsWindow.Show();
        _sourcesTopicsWindow.Activate();
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
        var piperDefaults = PiperCliTextToSpeechOptions.FromEnvironment(_services.DataRoot);
        var kittenDefaults = KittenCliTextToSpeechOptions.FromEnvironment(_services.DataRoot);
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
        var defaults = PiperCliTextToSpeechOptions.FromEnvironment(_services.DataRoot);
        return new PiperCliTextToSpeechOptions(
            ResolvePortablePath(PiperExecutableText),
            ResolvePortablePath(PiperModelText),
            PreferConfigured(PiperVoiceText, defaults.VoiceId),
            PreferConfigured(PiperArgumentsText, defaults.ArgumentsTemplate),
            defaults.OutputDirectory);
    }

    private KittenCliTextToSpeechOptions BuildKittenOptionsFromUi()
    {
        var defaults = KittenCliTextToSpeechOptions.FromEnvironment(_services.DataRoot);
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

    private void ApplyRuntimeOptions(OpenAiCompatibleRuntimeOptions options)
    {
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

        if (RunComputerHealthCheckCommand is AsyncRelayCommand runComputerHealthCheck)
        {
            runComputerHealthCheck.RaiseCanExecuteChanged();
        }

        if (RepairAliInstallCommand is AsyncRelayCommand repairAliInstall)
        {
            repairAliInstall.RaiseCanExecuteChanged();
        }

        if (RunComputerAssistantSetupCommand is AsyncRelayCommand runComputerAssistantSetup)
        {
            runComputerAssistantSetup.RaiseCanExecuteChanged();
        }

        if (RunMaintenancePlanCommand is AsyncRelayCommand runMaintenancePlan)
        {
            runMaintenancePlan.RaiseCanExecuteChanged();
        }

        if (RunProcessEvidenceCommand is AsyncRelayCommand runProcessEvidence)
        {
            runProcessEvidence.RaiseCanExecuteChanged();
        }

        if (RunBuildLockDiagnosticCommand is AsyncRelayCommand runBuildLockDiagnostic)
        {
            runBuildLockDiagnostic.RaiseCanExecuteChanged();
        }

        if (RunPortDiagnosticCommand is AsyncRelayCommand runPortDiagnostic)
        {
            runPortDiagnostic.RaiseCanExecuteChanged();
        }

        if (RunServicesStartupInspectionCommand is AsyncRelayCommand runServicesStartupInspection)
        {
            runServicesStartupInspection.RaiseCanExecuteChanged();
        }

        if (RunDiskCleanupPlanCommand is AsyncRelayCommand runDiskCleanupPlan)
        {
            runDiskCleanupPlan.RaiseCanExecuteChanged();
        }

        if (RunSuspiciousActivityPlanCommand is AsyncRelayCommand runSuspiciousActivityPlan)
        {
            runSuspiciousActivityPlan.RaiseCanExecuteChanged();
        }

        if (RunAppInstallTroubleshootingCommand is AsyncRelayCommand runAppInstallTroubleshooting)
        {
            runAppInstallTroubleshooting.RaiseCanExecuteChanged();
        }

        if (RunPeripheralSetupPlanCommand is AsyncRelayCommand runPeripheralSetupPlan)
        {
            runPeripheralSetupPlan.RaiseCanExecuteChanged();
        }

        if (RunCodingWorkspaceDiagnosticCommand is AsyncRelayCommand runCodingWorkspaceDiagnostic)
        {
            runCodingWorkspaceDiagnostic.RaiseCanExecuteChanged();
        }

        if (RunCodingProjectIntelligenceCommand is AsyncRelayCommand runCodingProjectIntelligence)
        {
            runCodingProjectIntelligence.RaiseCanExecuteChanged();
        }

        if (RunCodingProjectIndexCommand is AsyncRelayCommand runCodingProjectIndex)
        {
            runCodingProjectIndex.RaiseCanExecuteChanged();
        }

        if (RunCodingRepoUnderstandingCommand is AsyncRelayCommand runCodingRepoUnderstanding)
        {
            runCodingRepoUnderstanding.RaiseCanExecuteChanged();
        }

        if (RunCodingContextPacketCommand is AsyncRelayCommand runCodingContextPacket)
        {
            runCodingContextPacket.RaiseCanExecuteChanged();
        }

        if (RunCodingFullReadinessCommand is AsyncRelayCommand runCodingFullReadiness)
        {
            runCodingFullReadiness.RaiseCanExecuteChanged();
        }

        if (RunCodingMiniCodexStatusCommand is AsyncRelayCommand runCodingMiniCodexStatus)
        {
            runCodingMiniCodexStatus.RaiseCanExecuteChanged();
        }

        if (RunCodingReadinessReportCommand is AsyncRelayCommand runCodingReadinessReport)
        {
            runCodingReadinessReport.RaiseCanExecuteChanged();
        }

        if (RunCodingNextBestActionCommand is AsyncRelayCommand runCodingNextBestAction)
        {
            runCodingNextBestAction.RaiseCanExecuteChanged();
        }

        if (RunCodingValidationQueueCommand is AsyncRelayCommand runCodingValidationQueue)
        {
            runCodingValidationQueue.RaiseCanExecuteChanged();
        }

        if (RunCodingPatchBatchCommand is AsyncRelayCommand runCodingPatchBatch)
        {
            runCodingPatchBatch.RaiseCanExecuteChanged();
        }

        if (RunCodingSymbolDiffAuditCommand is AsyncRelayCommand runCodingSymbolDiffAudit)
        {
            runCodingSymbolDiffAudit.RaiseCanExecuteChanged();
        }

        if (RunCodingGeneratedFileGuardCommand is AsyncRelayCommand runCodingGeneratedFileGuard)
        {
            runCodingGeneratedFileGuard.RaiseCanExecuteChanged();
        }

        if (RunCodingBuildThisCommand is AsyncRelayCommand runCodingBuildThis)
        {
            runCodingBuildThis.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureBuilderCommand is AsyncRelayCommand runCodingFeatureBuilder)
        {
            runCodingFeatureBuilder.RaiseCanExecuteChanged();
        }

        if (RunCodingGuidedBundlePreviewCommand is AsyncRelayCommand runCodingGuidedBundlePreview)
        {
            runCodingGuidedBundlePreview.RaiseCanExecuteChanged();
        }

        if (RunCodingImplementationPlannerCommand is AsyncRelayCommand runCodingImplementationPlanner)
        {
            runCodingImplementationPlanner.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureIntakeCommand is AsyncRelayCommand runCodingFeatureIntake)
        {
            runCodingFeatureIntake.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureOrchestratorCommand is AsyncRelayCommand runCodingFeatureOrchestrator)
        {
            runCodingFeatureOrchestrator.RaiseCanExecuteChanged();
        }

        if (RunCodingEvidencePackCommand is AsyncRelayCommand runCodingEvidencePack)
        {
            runCodingEvidencePack.RaiseCanExecuteChanged();
        }

        if (RunCodingRoslynPlannerCommand is AsyncRelayCommand runCodingRoslynPlanner)
        {
            runCodingRoslynPlanner.RaiseCanExecuteChanged();
        }

        if (RunCodingPatchSynthesisV2Command is AsyncRelayCommand runCodingPatchSynthesisV2)
        {
            runCodingPatchSynthesisV2.RaiseCanExecuteChanged();
        }

        if (RunCodingPatternCopyCommand is AsyncRelayCommand runCodingPatternCopy)
        {
            runCodingPatternCopy.RaiseCanExecuteChanged();
        }

        if (RunCodingTestGeneratorV2Command is AsyncRelayCommand runCodingTestGeneratorV2)
        {
            runCodingTestGeneratorV2.RaiseCanExecuteChanged();
        }

        if (RunCodingSliceStateCommand is AsyncRelayCommand runCodingSliceState)
        {
            runCodingSliceState.RaiseCanExecuteChanged();
        }

        if (RunCodingSemanticDiffCommand is AsyncRelayCommand runCodingSemanticDiff)
        {
            runCodingSemanticDiff.RaiseCanExecuteChanged();
        }

        if (RunCodingScoreV3Command is AsyncRelayCommand runCodingScoreV3)
        {
            runCodingScoreV3.RaiseCanExecuteChanged();
        }

        if (RunCodingBuildFeatureLaneCommand is AsyncRelayCommand runCodingBuildFeatureLane)
        {
            runCodingBuildFeatureLane.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureWorkContextCommand is AsyncRelayCommand runCodingFeatureWorkContext)
        {
            runCodingFeatureWorkContext.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureIntentCommand is AsyncRelayCommand runCodingFeatureIntent)
        {
            runCodingFeatureIntent.RaiseCanExecuteChanged();
        }

        if (RunCodingBehaviorContractCommand is AsyncRelayCommand runCodingBehaviorContract)
        {
            runCodingBehaviorContract.RaiseCanExecuteChanged();
        }

        if (RunCodingBehaviorTestsCommand is AsyncRelayCommand runCodingBehaviorTests)
        {
            runCodingBehaviorTests.RaiseCanExecuteChanged();
        }

        if (RunCodingBehaviorTestPreviewCommand is AsyncRelayCommand runCodingBehaviorTestPreview)
        {
            runCodingBehaviorTestPreview.RaiseCanExecuteChanged();
        }

        if (RunCodingImplementationSlicesCommand is AsyncRelayCommand runCodingImplementationSlices)
        {
            runCodingImplementationSlices.RaiseCanExecuteChanged();
        }

        if (RunCodingPatchSlicesCommand is AsyncRelayCommand runCodingPatchSlices)
        {
            runCodingPatchSlices.RaiseCanExecuteChanged();
        }

        if (RunCodingExactPatchCommand is AsyncRelayCommand runCodingExactPatch)
        {
            runCodingExactPatch.RaiseCanExecuteChanged();
        }

        if (RunCodingPatchIntelligenceCommand is AsyncRelayCommand runCodingPatchIntelligence)
        {
            runCodingPatchIntelligence.RaiseCanExecuteChanged();
        }

        if (RunCodingPatchLoopCommand is AsyncRelayCommand runCodingPatchLoop)
        {
            runCodingPatchLoop.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureSessionLedgerCommand is AsyncRelayCommand runCodingFeatureSessionLedger)
        {
            runCodingFeatureSessionLedger.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureRunControllerCommand is AsyncRelayCommand runCodingFeatureRunController)
        {
            runCodingFeatureRunController.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureExecutionPacketCommand is AsyncRelayCommand runCodingFeatureExecutionPacket)
        {
            runCodingFeatureExecutionPacket.RaiseCanExecuteChanged();
        }

        if (RunCodingApplyGateCommand is AsyncRelayCommand runCodingApplyGate)
        {
            runCodingApplyGate.RaiseCanExecuteChanged();
        }

        if (RunCodingPostPatchValidationCommand is AsyncRelayCommand runCodingPostPatchValidation)
        {
            runCodingPostPatchValidation.RaiseCanExecuteChanged();
        }

        if (RunCodingFeatureCompletionReceiptCommand is AsyncRelayCommand runCodingFeatureCompletionReceipt)
        {
            runCodingFeatureCompletionReceipt.RaiseCanExecuteChanged();
        }

        if (RunCodingSymbolIndexCommand is AsyncRelayCommand runCodingSymbolIndex)
        {
            runCodingSymbolIndex.RaiseCanExecuteChanged();
        }

        if (RunCodingCallGraphCommand is AsyncRelayCommand runCodingCallGraph)
        {
            runCodingCallGraph.RaiseCanExecuteChanged();
        }

        if (RunCodingOwnershipMapCommand is AsyncRelayCommand runCodingOwnershipMap)
        {
            runCodingOwnershipMap.RaiseCanExecuteChanged();
        }

        if (RunCodingBindingCheckCommand is AsyncRelayCommand runCodingBindingCheck)
        {
            runCodingBindingCheck.RaiseCanExecuteChanged();
        }

        if (RunCodingImpactedTestsCommand is AsyncRelayCommand runCodingImpactedTests)
        {
            runCodingImpactedTests.RaiseCanExecuteChanged();
        }

        if (RunCodingTestTargetCommand is AsyncRelayCommand runCodingTestTarget)
        {
            runCodingTestTarget.RaiseCanExecuteChanged();
        }

        if (RunCodingSafeEditWorkflowCommand is AsyncRelayCommand runCodingSafeEditWorkflow)
        {
            runCodingSafeEditWorkflow.RaiseCanExecuteChanged();
        }

        if (RunCodingExecutionPacketCommand is AsyncRelayCommand runCodingExecutionPacket)
        {
            runCodingExecutionPacket.RaiseCanExecuteChanged();
        }

        if (RunCodingApprovePacketCommand is AsyncRelayCommand runCodingApprovePacket)
        {
            runCodingApprovePacket.RaiseCanExecuteChanged();
        }

        if (RunCodingShowApprovedPacketCommand is AsyncRelayCommand runCodingShowApprovedPacket)
        {
            runCodingShowApprovedPacket.RaiseCanExecuteChanged();
        }

        if (RunCodingPacketProgressCommand is AsyncRelayCommand runCodingPacketProgress)
        {
            runCodingPacketProgress.RaiseCanExecuteChanged();
        }

        if (RunCodingPacketCommandsCommand is AsyncRelayCommand runCodingPacketCommands)
        {
            runCodingPacketCommands.RaiseCanExecuteChanged();
        }

        if (RunCodingHealthScoreCommand is AsyncRelayCommand runCodingHealthScore)
        {
            runCodingHealthScore.RaiseCanExecuteChanged();
        }

        if (RunCodingGitStatusCommand is AsyncRelayCommand runCodingGitStatus)
        {
            runCodingGitStatus.RaiseCanExecuteChanged();
        }

        if (RunCodingReviewChangesCommand is AsyncRelayCommand runCodingReviewChanges)
        {
            runCodingReviewChanges.RaiseCanExecuteChanged();
        }

        if (RunCodingValidationPlanCommand is AsyncRelayCommand runCodingValidationPlan)
        {
            runCodingValidationPlan.RaiseCanExecuteChanged();
        }

        if (RunCodingSafeCommitCommand is AsyncRelayCommand runCodingSafeCommit)
        {
            runCodingSafeCommit.RaiseCanExecuteChanged();
        }

        if (RunCodingCommitMessageCommand is AsyncRelayCommand runCodingCommitMessage)
        {
            runCodingCommitMessage.RaiseCanExecuteChanged();
        }

        if (RunCodingReleaseNotesCommand is AsyncRelayCommand runCodingReleaseNotes)
        {
            runCodingReleaseNotes.RaiseCanExecuteChanged();
        }

        if (RunCodingTimelineCommand is AsyncRelayCommand runCodingTimeline)
        {
            runCodingTimeline.RaiseCanExecuteChanged();
        }

        if (RunCodingRollbackPlanCommand is AsyncRelayCommand runCodingRollbackPlan)
        {
            runCodingRollbackPlan.RaiseCanExecuteChanged();
        }

        if (RunCodingBuildCommand is AsyncRelayCommand runCodingBuild)
        {
            runCodingBuild.RaiseCanExecuteChanged();
        }

        if (RunCodingTestCommand is AsyncRelayCommand runCodingTest)
        {
            runCodingTest.RaiseCanExecuteChanged();
        }

        if (RunCodingLastFailureCommand is AsyncRelayCommand runCodingLastFailure)
        {
            runCodingLastFailure.RaiseCanExecuteChanged();
        }

        if (RunCodingRepairRunnerCommand is AsyncRelayCommand runCodingRepairRunner)
        {
            runCodingRepairRunner.RaiseCanExecuteChanged();
        }

        if (RunCodingSuggestFixCommand is AsyncRelayCommand runCodingSuggestFix)
        {
            runCodingSuggestFix.RaiseCanExecuteChanged();
        }

        if (RunCodingShowPatchPreviewCommand is AsyncRelayCommand runCodingShowPatchPreview)
        {
            runCodingShowPatchPreview.RaiseCanExecuteChanged();
        }

        if (RunCodingApplyPreviewCommand is AsyncRelayCommand runCodingApplyPreview)
        {
            runCodingApplyPreview.RaiseCanExecuteChanged();
        }

        if (RunCodingReceiptsCommand is AsyncRelayCommand runCodingReceipts)
        {
            runCodingReceipts.RaiseCanExecuteChanged();
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
        CodingAbilityCatalog.UserCommandHelpTopics
            .Select(topic => new CommandExplorerNodeViewModel(
                topic.Name,
                topic.Summary,
                children: topic.Entries.Select(entry => new CommandExplorerNodeViewModel(
                    entry.Title,
                    entry.Summary,
                    entry.Command,
                    entry.Usage))))
            .ToArray();

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
        bool? assistantReadsRepliesOutLoud = null,
        bool? autoSendVoiceTranscripts = null,
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
            : PreferConfigured(PiperVoiceText, PiperCliTextToSpeechOptions.FromEnvironment(_services.DataRoot).VoiceId);

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
}

internal sealed record TextToSpeechVoiceChoice(string Label, string Engine, string VoiceId, string ModelPath);
