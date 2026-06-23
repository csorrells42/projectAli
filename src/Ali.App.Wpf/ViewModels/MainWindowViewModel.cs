using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ali.App.Wpf;
using Ali.Core.Evidence;
using Ali.Core.Feedback;
using Ali.Core.Runtime;
using Ali.Core.Voice;
using Ali.Infrastructure.Bootstrap;
using Ali.Infrastructure.Runtime;
using Ali.Infrastructure.Voice;

namespace Ali.App.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const double SpectrumRenderWidth = 720d;
    private const double SpectrumRenderHeight = 130d;
    private const double SpectrumRenderInset = 12d;
    private readonly AliServices _services;
    private readonly NAudioInputLevelMonitor _inputLevelMonitor = new();
    private readonly VoiceDiagnosticSampleService _sampleService;
    private string _conversationId = $"conv_{Guid.NewGuid():N}";
    private ConversationHistoryItemViewModel? _activeConversationHistoryItem;
    private readonly Dictionary<string, PiperVoiceChoice> _piperVoiceChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeModelChoice> _runtimeModelChoices = new(StringComparer.OrdinalIgnoreCase);
    private VoiceRuntimeSettings _voiceSettings;
    private bool _loadingVoiceSettings;
    private bool _loadingSpeechToolSettings;
    private CancellationTokenSource? _activeResponse;
    private CancellationTokenSource? _activeVoiceInput;
    private CancellationTokenSource? _activeSpeech;
    private CancellationTokenSource? _activeSample;
    private VoiceSettingsWindow? _voiceSettingsWindow;
    private bool _voiceMonitorRequested;
    private VoiceDiagnosticSample? _lastDiagnosticSample;
    private VoiceCaptureDiagnostics? _lastCaptureDiagnostics;
    private VoiceCalibrationResult? _lastCalibrationResult;
    private double[] _lastSpectrumMagnitudes = new double[SpectrumAnalyzer.BarCount];
    private double[] _renderedSpectrumMagnitudes = new double[SpectrumAnalyzer.BarCount];
    private double _spectrumVisualCeiling = 0.25d;
    private double _lastSpectrumPeakLevel;
    private string _composerText = string.Empty;
    private bool _isBusy;
    private bool _isRecording;
    private bool _isTranscribing;
    private bool _isSpeaking;
    private bool _isRecordingSample;
    private bool _isCalibrating;
    private string _statusText = "Ready. Local runtime is not configured yet.";
    private string _runtimeDisplay;
    private string _runtimeEndpointText = string.Empty;
    private string _runtimeModelText = string.Empty;
    private string _runtimeContextText = "2048";
    private string _runtimeOutputLimitText = "256";
    private string _runtimeTemperatureText = "0.2";
    private string _runtimeTopPText = string.Empty;
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
    private string _voiceSampleStatus = "No diagnostic sample recorded.";
    private string _voiceCalibrationStatus = $"Calibration phrase: \"{VoiceCalibrationEvaluator.CalibrationPrompt}\"";
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
    private VoiceTurnMetadata? _lastVoiceMetadata;

    public MainWindowViewModel(AliServices services)
    {
        _services = services;
        _sampleService = new VoiceDiagnosticSampleService(_services.VoiceRecorder, _services.SpeechPlayer);

        SendCommand = new AsyncRelayCommand(SendAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(ComposerText));
        StopCommand = new RelayCommand(_ => Stop(), _ => IsBusy);
        NewChatCommand = new RelayCommand(_ => StartNewChat());
        EraseHistoryCommand = new RelayCommand(_ => EraseHistory());
        EraseConversationCommand = new RelayCommand(EraseConversation);
        RenameConversationCommand = new RelayCommand(RenameConversation);
        CommitConversationRenameCommand = new RelayCommand(CommitConversationRename);
        FlagIncorrectCommand = new RelayCommand(FlagIncorrect);
        LoadRuntimeSettingsCommand = new RelayCommand(_ => LoadRuntimeSettings());
        SaveRuntimeSettingsCommand = new RelayCommand(_ => SaveRuntimeSettings());
        CheckRuntimeCommand = new AsyncRelayCommand(CheckRuntimeAsync, () => !IsBusy);
        RefreshRuntimeModelsCommand = new AsyncRelayCommand(RefreshRuntimeModelsAsync, () => !IsBusy);
        ActivateRuntimeCommand = new RelayCommand(_ => ActivateRuntime(), _ => CanActivateRuntime && !IsBusy);
        RevertToStubCommand = new RelayCommand(_ => RevertToStub(), _ => !IsBusy);
        RevertToLastKnownGoodCommand = new RelayCommand(_ => RevertToLastKnownGood(), _ => CanRevertToLastKnownGood && !IsBusy);
        PasteImageCommand = new AsyncRelayCommand(AddClipboardImageAsync);
        CaptureScreenCommand = new AsyncRelayCommand(CaptureFullScreenAsync);
        RemoveAttachmentCommand = new RelayCommand(RemoveAttachment);
        StartVoiceRecordingCommand = new AsyncRelayCommand(StartVoiceRecordingAsync, () => !IsBusy && !IsRecording && !IsTranscribing);
        StopVoiceRecordingCommand = new AsyncRelayCommand(StopVoiceRecordingOrTranscriptionAsync, () => IsRecording || IsTranscribing);
        ToggleVoiceRecordingCommand = new AsyncRelayCommand(ToggleVoiceRecordingAsync, () => !IsBusy || IsRecording || IsTranscribing);
        ToggleVoiceModeCommand = new RelayCommand(_ => AutoSendVoiceTranscripts = !AutoSendVoiceTranscripts);
        SendTranscriptCommand = new AsyncRelayCommand(SendTranscriptAsync, () => !AutoSendVoiceTranscripts && !IsBusy && !IsRecording && !IsTranscribing && !string.IsNullOrWhiteSpace(EditableTranscript));
        StopSpeakingCommand = new RelayCommand(_ => StopSpeaking(), _ => IsSpeaking);
        OpenVoiceSettingsCommand = new RelayCommand(_ => OpenVoiceSettings());
        ApplyVoiceToolSettingsCommand = new RelayCommand(_ => ApplyVoiceToolSettings());
        PlayPiperSampleCommand = new AsyncRelayCommand(PlayPiperSampleAsync, () => !IsSpeaking);
        RecordVoiceSampleCommand = new AsyncRelayCommand(RecordVoiceSampleAsync, () => !IsBusy && !IsRecording && !IsTranscribing && !IsRecordingSample && !IsCalibrating);
        PlayVoiceSampleCommand = new AsyncRelayCommand(PlayVoiceSampleAsync, () => _lastDiagnosticSample is not null && !IsSpeaking);
        DeleteVoiceSampleCommand = new RelayCommand(_ => DeleteVoiceSample(), _ => _lastDiagnosticSample is not null);
        CalibrateVoiceCommand = new AsyncRelayCommand(CalibrateVoiceAsync, () => !IsBusy && !IsRecording && !IsTranscribing && !IsRecordingSample && !IsCalibrating);

        _voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(_services.DataRoot);
        _extraInputGainDb = _voiceSettings.ExtraInputGainDb;
        _normalizeBeforeStt = _voiceSettings.NormalizeBeforeStt;
        _retainDebugAudio = _voiceSettings.RetainDebugAudio;
        _autoSendVoiceTranscripts = _voiceSettings.AutoSendVoiceTranscripts;
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
        VoiceDiagnosticsText = "Open Voice Settings or start a voice action to monitor the microphone.";

        RefreshSpeechToolStatuses();

        _runtimeDisplay = FormatRuntimeDisplay();
        LoadRuntimeSettings();

        Messages.Add(new ChatMessageViewModel(
            id: $"msg_asst_{Guid.NewGuid():N}",
            role: ChatRole.Assistant,
            text: "Ali bootstrap ready. I can prove the WPF chat loop, cancellation, and correction queue. A real local model runtime must pass a health check and be activated before I answer through it.",
            createdAt: DateTimeOffset.UtcNow,
            evidenceStatus: EvidenceStatus.Verified));
        _activeConversationHistoryItem = new ConversationHistoryItemViewModel(_conversationId, "Current chat");
        ConversationHistory.Add(_activeConversationHistoryItem);
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = new();

    public ObservableCollection<ConversationHistoryItemViewModel> ConversationHistory { get; } = new();

    public ObservableCollection<string> VoiceInputDevices { get; } = new();

    public ObservableCollection<string> VoiceOutputDevices { get; } = new();

    public ObservableCollection<string> VoiceInputPresets { get; } = new();

    public ObservableCollection<string> VoiceInputChannelModes { get; } = new();

    public ObservableCollection<string> PiperVoiceChoices { get; } = new();

    public ObservableCollection<string> RuntimeModelChoices { get; } = new();

    public ObservableCollection<string> RuntimeQuantizationChoices { get; } = new();

    public ObservableCollection<string> RuntimeContextChoices { get; } = new();

    public ObservableCollection<string> RuntimeOutputLimitChoices { get; } = new();

    public ICommand SendCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand NewChatCommand { get; }

    public ICommand EraseHistoryCommand { get; }

    public ICommand EraseConversationCommand { get; }

    public ICommand RenameConversationCommand { get; }

    public ICommand CommitConversationRenameCommand { get; }

    public ICommand FlagIncorrectCommand { get; }

    public ICommand LoadRuntimeSettingsCommand { get; }

    public ICommand SaveRuntimeSettingsCommand { get; }

    public ICommand CheckRuntimeCommand { get; }

    public ICommand RefreshRuntimeModelsCommand { get; }

    public ICommand ActivateRuntimeCommand { get; }

    public ICommand RevertToStubCommand { get; }

    public ICommand RevertToLastKnownGoodCommand { get; }

    public ICommand PasteImageCommand { get; }

    public ICommand CaptureScreenCommand { get; }

    public ICommand RemoveAttachmentCommand { get; }

    public ICommand StartVoiceRecordingCommand { get; }

    public ICommand StopVoiceRecordingCommand { get; }

    public ICommand ToggleVoiceRecordingCommand { get; }

    public ICommand ToggleVoiceModeCommand { get; }

    public ICommand SendTranscriptCommand { get; }

    public ICommand StopSpeakingCommand { get; }

    public ICommand OpenVoiceSettingsCommand { get; }

    public ICommand ApplyVoiceToolSettingsCommand { get; }

    public ICommand PlayPiperSampleCommand { get; }

    public ICommand RecordVoiceSampleCommand { get; }

    public ICommand PlayVoiceSampleCommand { get; }

    public ICommand DeleteVoiceSampleCommand { get; }

    public ICommand CalibrateVoiceCommand { get; }

    public string RuntimeSettingsPath => _services.RuntimeSettingsPath;

    public string MicButtonText => IsRecording ? "Stop Mic" : IsTranscribing ? "Transcribing" : "Mic";

    public string VoiceModeButtonText => AutoSendVoiceTranscripts ? "Hands Free On" : "Voice Mode";

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

    public string VoiceSampleStatus
    {
        get => _voiceSampleStatus;
        private set => SetProperty(ref _voiceSampleStatus, value);
    }

    public string VoiceCalibrationStatus
    {
        get => _voiceCalibrationStatus;
        private set => SetProperty(ref _voiceCalibrationStatus, value);
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
                OnPropertyChanged(nameof(ManualTranscriptReviewEnabled));
                OnPropertyChanged(nameof(ManualTranscriptReviewOpacity));
                OnPropertyChanged(nameof(VoiceModeButtonText));
                SaveVoiceSettings(autoSendVoiceTranscripts: value);
                RaiseCommandStates();
            }
        }
    }

    public bool ManualTranscriptReviewEnabled => !AutoSendVoiceTranscripts;

    public double ManualTranscriptReviewOpacity => AutoSendVoiceTranscripts ? 0.45d : 1d;

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
                RaiseCommandStates();
            }
        }
    }

    public bool IsRecordingSample
    {
        get => _isRecordingSample;
        private set
        {
            if (SetProperty(ref _isRecordingSample, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set
        {
            if (SetProperty(ref _isCalibrating, value))
            {
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
                RaiseCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (string.IsNullOrWhiteSpace(text) || IsBusy)
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

        var userMessageId = $"msg_user_{Guid.NewGuid():N}";
        var assistantMessageId = $"msg_asst_{Guid.NewGuid():N}";
        var userMessage = new ChatMessageViewModel(
            userMessageId,
            ChatRole.User,
            text,
            DateTimeOffset.UtcNow,
            EvidenceStatus.Verified);

        var attachments = Attachments.Select(attachment => attachment.ToCoreAttachment()).ToList();

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
            }

            StatusText = "Response complete.";
            completed = true;
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Text += "\n\nStopped by user.";
            StatusText = "Response stopped.";
        }
        finally
        {
            _activeResponse.Dispose();
            _activeResponse = null;
            IsBusy = false;
            ClearTemporaryAttachments();
            UpdateRuntimeStatus();
        }

        if (completed && inputOrigin == VoiceInputOrigin.Voice && !string.IsNullOrWhiteSpace(assistantMessage.Text))
        {
            await SpeakAssistantAnswerAsync(assistantMessage.Text, voiceMetadata).ConfigureAwait(true);
        }
    }

    private void Stop() => _activeResponse?.Cancel();

    private void StartNewChat()
    {
        _conversationId = $"conv_{Guid.NewGuid():N}";
        _activeConversationHistoryItem = new ConversationHistoryItemViewModel(
            _conversationId,
            $"Chat {DateTime.Now:h:mm tt}");
        ConversationHistory.Insert(0, _activeConversationHistoryItem);
        Messages.Clear();
        Attachments.Clear();
        ComposerText = string.Empty;
        EditableTranscript = string.Empty;
        LastTranscript = string.Empty;
        StatusText = "New chat ready.";
        VoiceStatus = "Voice idle.";
    }

    private void EnsureActiveConversationHistoryItem()
    {
        if (_activeConversationHistoryItem is not null)
        {
            return;
        }

        _activeConversationHistoryItem = new ConversationHistoryItemViewModel(_conversationId, "Current chat");
        ConversationHistory.Insert(0, _activeConversationHistoryItem);
    }

    private void EraseHistory()
    {
        Stop();
        StopSpeaking();
        _activeVoiceInput?.Cancel();
        ClearTemporaryAttachments();
        Attachments.Clear();
        Messages.Clear();
        ConversationHistory.Clear();
        _conversationId = $"conv_{Guid.NewGuid():N}";
        _activeConversationHistoryItem = null;
        ComposerText = string.Empty;
        EditableTranscript = string.Empty;
        LastTranscript = string.Empty;
        StatusText = "Conversation history erased for this session.";
        VoiceStatus = "Voice idle.";
        AttachmentStatus = "Screenshots are temporary by default.";
    }

    private void EraseConversation(object? parameter)
    {
        if (parameter is not ConversationHistoryItemViewModel item)
        {
            return;
        }

        ConversationHistory.Remove(item);
        if (_activeConversationHistoryItem != item)
        {
            StatusText = $"Erased chat: {item.Title}";
            return;
        }

        Stop();
        StopSpeaking();
        ClearTemporaryAttachments();
        Attachments.Clear();
        Messages.Clear();
        _conversationId = $"conv_{Guid.NewGuid():N}";
        _activeConversationHistoryItem = null;
        ComposerText = string.Empty;
        EditableTranscript = string.Empty;
        LastTranscript = string.Empty;
        StatusText = $"Erased current chat: {item.Title}";
        VoiceStatus = "Voice idle.";
        AttachmentStatus = "Screenshots are temporary by default.";
    }

    private static void RenameConversation(object? parameter)
    {
        if (parameter is ConversationHistoryItemViewModel item)
        {
            item.BeginRename();
        }
    }

    private static void CommitConversationRename(object? parameter)
    {
        if (parameter is ConversationHistoryItemViewModel item)
        {
            item.CommitRename();
        }
    }

    private async Task ToggleVoiceRecordingAsync()
    {
        if (IsRecording || IsTranscribing)
        {
            await StopVoiceRecordingOrTranscriptionAsync().ConfigureAwait(true);
            return;
        }

        await StartVoiceRecordingAsync().ConfigureAwait(true);
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
            UpdateRuntimeStatus();
        }
        catch (Exception ex)
        {
            RuntimeHealthResult = ex.Message;
            StatusText = $"Runtime check failed: {ex.Message}";
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
        StatusText = "Verified runtime activated.";
    }

    private void RevertToStub()
    {
        _services.RuntimeController.RevertToFallback();
        CanActivateRuntime = _services.RuntimeController.CanActivateCandidate;
        UpdateRuntimeStatus();
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

            message.IsFlaggedForCorrection = true;
            StatusText = $"Flagged for correction: {report.Id}";
        }
        catch (Exception ex)
        {
            StatusText = $"Correction queue write failed: {ex.Message}";
        }
    }

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
            Temperature: 0.2,
            TopP: null,
            StreamingEnabled: selectedModel?.StreamingEnabled ?? true,
            SupportsVision: selectedModel?.SupportsVision ?? LooksLikeVisionModel(model),
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

    private async Task CaptureFullScreenAsync()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
        if (bounds is null)
        {
            AttachmentStatus = "No primary screen was available.";
            return;
        }

        using var bitmap = new Bitmap(bounds.Value.Width, bounds.Value.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Value.Left, bounds.Value.Top, 0, 0, bounds.Value.Size);
        }

        await using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        await AddPngBytesAsync(stream.ToArray(), "full-screen").ConfigureAwait(true);
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

            var transcriptGuard = SpeechTranscriptGuard.Evaluate(transcript.Text, requireAssistantName: true);
            if (!transcriptGuard.Accepted)
            {
                _lastVoiceMetadata = CreateVoiceMetadata(
                    transcript.Text,
                    audioInput,
                    suspiciousOrNoSpeech: true,
                    rejectionReason: transcriptGuard.Reason);
                VoiceStatus = transcriptGuard.Message;
                SttStatus = $"Transcript rejected: {transcriptGuard.Reason}. No transcript was sent.";
                return;
            }

            SaveLastSuccessfulSttDevice();
            LastTranscript = transcript.Text;
            EditableTranscript = transcript.Text;
            var routing = VoiceTranscriptRouting.Decide(AutoSendVoiceTranscripts);
            if (routing.PlaceTranscriptInComposer)
            {
                ComposerText = transcript.Text;
            }

            _lastVoiceMetadata = CreateVoiceMetadata(
                transcript.Text,
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
        var transcript = EditableTranscript.Trim();
        if (string.IsNullOrWhiteSpace(transcript) || IsBusy)
        {
            return;
        }

        var transcriptGuard = SpeechTranscriptGuard.Evaluate(transcript, requireAssistantName: true);
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

        var voiceMetadata = CreateVoiceMetadata(
            transcript,
            audioInput: null,
            suspiciousOrNoSpeech: false,
            rejectionReason: null) with
        {
            Transcript = transcript,
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
            RawAudioRetained = _lastVoiceMetadata?.RawAudioRetained ?? false
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

    private async Task SpeakAssistantAnswerAsync(string assistantText, VoiceTurnMetadata? voiceMetadata)
    {
        var spokenText = SpeechOutputCleaner.Clean(assistantText);
        if (string.IsNullOrWhiteSpace(spokenText))
        {
            TtsStatus = "No speakable response after cleanup.";
            return;
        }

        if (!_services.TextToSpeech.IsConfigured)
        {
            TtsStatus = "Local TTS is not configured. Text answer is available.";
            VoiceStatus = "Speech skipped because local TTS is not configured.";
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
            TtsStatus = "Synthesizing local speech...";
            var settings = new VoiceSettings(
                _services.TextToSpeech.VoiceId,
                Rate: 1.0,
                RetainAudio: false);

            speech = await _services.TextToSpeech.SynthesizeAsync(
                spokenText,
                settings,
                _activeSpeech.Token).ConfigureAwait(true);

            TtsStatus = "Speaking local response...";
            await _services.SpeechPlayer.PlayAsync(speech.AudioPath, _activeSpeech.Token).ConfigureAwait(true);
            TtsStatus = "Speech complete.";
            VoiceStatus = "Voice loop complete.";
            SaveLastSuccessfulTtsDevice();
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
            if (speech is not null && !speech.RetainAudio && File.Exists(speech.AudioPath))
            {
                TryDeleteFile(speech.AudioPath);
            }

            _activeSpeech?.Dispose();
            _activeSpeech = null;
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

    private async Task RecordVoiceSampleAsync()
    {
        if (IsRecordingSample || IsCalibrating || IsRecording || IsTranscribing)
        {
            return;
        }

        StopSpeaking();
        _sampleService.DeleteSample(_lastDiagnosticSample);
        _lastDiagnosticSample = null;
        VoiceSampleStatus = "Recording 5 second diagnostic sample...";
        _activeSample?.Dispose();
        _activeSample = new CancellationTokenSource();
        IsRecordingSample = true;

        try
        {
            var sample = await RecordDiagnosticSampleAsync(_activeSample.Token).ConfigureAwait(true);
            _lastDiagnosticSample = sample;
            ApplyInputLevelSnapshot(sample.Diagnostics.Level);
            VoiceSampleStatus = FormatSampleStatus(sample);
        }
        catch (OperationCanceledException)
        {
            VoiceSampleStatus = "Diagnostic sample canceled.";
        }
        catch (Exception ex)
        {
            VoiceSampleStatus = $"Diagnostic sample failed: {ex.Message}";
        }
        finally
        {
            IsRecordingSample = false;
            _activeSample?.Dispose();
            _activeSample = null;
            RaiseCommandStates();
        }
    }

    private async Task PlayVoiceSampleAsync()
    {
        if (_lastDiagnosticSample is null)
        {
            VoiceSampleStatus = "No diagnostic sample is available.";
            return;
        }

        if (!File.Exists(_lastDiagnosticSample.AudioInput.FilePath))
        {
            VoiceSampleStatus = "Diagnostic sample file is no longer available.";
            _lastDiagnosticSample = null;
            RaiseCommandStates();
            return;
        }

        StopSpeaking();
        _activeSpeech?.Dispose();
        _activeSpeech = new CancellationTokenSource();
        IsSpeaking = true;
        try
        {
            VoiceSampleStatus = "Playing diagnostic sample...";
            await _sampleService.PlaySampleAsync(_lastDiagnosticSample, _activeSpeech.Token).ConfigureAwait(true);
            VoiceSampleStatus = "Diagnostic sample playback complete.";
        }
        catch (OperationCanceledException)
        {
            VoiceSampleStatus = "Diagnostic sample playback stopped.";
        }
        catch (Exception ex)
        {
            VoiceSampleStatus = $"Diagnostic sample playback failed: {ex.Message}";
        }
        finally
        {
            IsSpeaking = false;
            _activeSpeech?.Dispose();
            _activeSpeech = null;
        }
    }

    private void DeleteVoiceSample()
    {
        _sampleService.DeleteSample(_lastDiagnosticSample, force: true);
        _lastDiagnosticSample = null;
        VoiceSampleStatus = "Diagnostic sample deleted.";
        RaiseCommandStates();
    }

    private async Task CalibrateVoiceAsync()
    {
        if (IsRecordingSample || IsCalibrating || IsRecording || IsTranscribing)
        {
            return;
        }

        StopSpeaking();
        _sampleService.DeleteSample(_lastDiagnosticSample);
        _lastDiagnosticSample = null;
        _lastCalibrationResult = null;
        VoiceCalibrationStatus = $"Say: \"{VoiceCalibrationEvaluator.CalibrationPrompt}\"";
        _activeSample?.Dispose();
        _activeSample = new CancellationTokenSource();
        IsCalibrating = true;

        try
        {
            var sample = await RecordDiagnosticSampleAsync(_activeSample.Token).ConfigureAwait(true);
            _lastDiagnosticSample = sample;
            ApplyInputLevelSnapshot(sample.Diagnostics.Level);

            SttStatus = "Transcribing calibration locally...";
            var transcript = await _services.SpeechToText.TranscribeAsync(sample.AudioInput, _activeSample.Token).ConfigureAwait(true);
            UpdateLastSttDebugText();
            var guard = SpeechTranscriptGuard.Evaluate(transcript.Text, requireAssistantName: true);
            _lastCalibrationResult = VoiceCalibrationEvaluator.Evaluate(sample, transcript, guard);
            LastTranscript = transcript.Text;
            EditableTranscript = transcript.Text;
            VoiceCalibrationStatus = FormatCalibrationStatus(_lastCalibrationResult);
            SttStatus = guard.Accepted
                ? "Calibration transcript accepted. No assistant action was run."
                : "Calibration transcript rejected. No assistant action was run.";
        }
        catch (OperationCanceledException)
        {
            VoiceCalibrationStatus = "Calibration canceled.";
            SttStatus = "Calibration canceled.";
        }
        catch (Exception ex)
        {
            UpdateLastSttDebugText();
            VoiceCalibrationStatus = $"Calibration failed: {ex.Message}";
            SttStatus = "Calibration failed. No assistant action was run.";
        }
        finally
        {
            IsCalibrating = false;
            _activeSample?.Dispose();
            _activeSample = null;
            RaiseCommandStates();
        }
    }

    private async Task<VoiceDiagnosticSample> RecordDiagnosticSampleAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            _services.DataRoot,
            "DiagnosticSamples",
            DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        try
        {
            StopInputLevelMonitor();
            SubscribeRecorderLevels();
            ApplyVoiceInputPreset(SelectedVoiceInputPreset);
            return await _sampleService.RecordSampleAsync(
                directory,
                TimeSpan.FromSeconds(5),
                CurrentInputDeviceNumber(),
                CurrentInputDeviceName(),
                CurrentInputChannelMode(),
                SelectedVoiceInputPreset,
                ExtraInputGainDb,
                NormalizeBeforeStt,
                RetainDebugAudio,
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            UnsubscribeRecorderLevels();
            StartInputLevelMonitor();
        }
    }

    private void OpenVoiceSettings()
    {
        if (_voiceSettingsWindow is { IsVisible: true })
        {
            _voiceSettingsWindow.Activate();
            _voiceMonitorRequested = true;
            StartInputLevelMonitor();
            return;
        }

        _voiceMonitorRequested = true;
        StartInputLevelMonitor();
        _voiceSettingsWindow = new VoiceSettingsWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow,
            DataContext = this
        };
        _voiceSettingsWindow.Closed += (_, _) =>
        {
            _voiceSettingsWindow = null;
            _voiceMonitorRequested = false;
            StopInputLevelMonitor();
            VoiceInputLevelPercent = 0;
            VoiceInputMeterText = "Input meter paused.";
            VoiceDiagnosticsText = "Microphone monitoring is off.";
        };
        _voiceSettingsWindow.Show();
    }

    private void LoadSpeechToolSettings()
    {
        _loadingSpeechToolSettings = true;
        var whisperDefaults = WhisperCliSpeechToTextOptions.FromEnvironment();
        var piperDefaults = PiperCliTextToSpeechOptions.FromEnvironment(_services.DataRoot);
        LoadPiperVoiceChoices();

        WhisperExecutableText = ToPortablePath(PreferConfigured(_voiceSettings.WhisperExecutablePath, whisperDefaults.ExecutablePath)) ?? string.Empty;
        WhisperModelText = ToPortablePath(PreferConfigured(_voiceSettings.WhisperModelPath, whisperDefaults.ModelPath)) ?? string.Empty;
        WhisperArgumentsText = PreferConfigured(_voiceSettings.WhisperArgumentsTemplate, whisperDefaults.ArgumentsTemplate);
        PiperExecutableText = ToPortablePath(PreferConfigured(
            _voiceSettings.PiperExecutablePath,
            PreferConfigured(FindLocalPiperExecutable(), piperDefaults.ExecutablePath))) ?? string.Empty;
        PiperModelText = ToPortablePath(PreferConfigured(
            _voiceSettings.PiperModelPath,
            PreferConfigured(PreferredPiperModelPath(), piperDefaults.ModelPath))) ?? string.Empty;
        PiperVoiceText = PreferConfigured(_voiceSettings.PiperVoiceId, piperDefaults.VoiceId);
        PiperArgumentsText = PreferConfigured(_voiceSettings.PiperArgumentsTemplate, piperDefaults.ArgumentsTemplate);
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
        RuntimeTemperatureText = options.Temperature.ToString(CultureInfo.InvariantCulture);
        RuntimeTopPText = options.TopP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
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

        if (!_selectedRuntimeModelChoice.Equals(label, StringComparison.Ordinal))
        {
            _selectedRuntimeModelChoice = label;
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

        return $"{health.Summary}\nEndpoint: {health.Endpoint ?? "n/a"}\nModel: {health.ModelPackageId ?? "n/a"}\nElapsed: {health.Elapsed.TotalMilliseconds:N0} ms\nStreaming supported: {streaming}";
    }

    private static string FormatSampleStatus(VoiceDiagnosticSample sample) =>
        $"Sample ready: {sample.Diagnostics.DurationSeconds:0.0}s | {sample.InputDeviceName} | {sample.InputChannelLabel} | {sample.InputPreset} | peak {sample.Diagnostics.Level.Peak:P0}, RMS {sample.Diagnostics.Level.Rms:P1}, {sample.Diagnostics.Level.State} | gain {sample.ExtraGainDb:+0.#;-0.#;0} dB | normalize {(sample.NormalizeBeforeStt ? "on" : "off")} | retained {sample.RetainDebugAudio}";

    private static string FormatCalibrationStatus(VoiceCalibrationResult result) =>
        $"Calibration {(result.Accepted ? "accepted" : "rejected")}: \"{result.Transcript}\" | {result.Sample.InputDeviceName} | {result.Sample.InputChannelLabel} | peak {result.Sample.Diagnostics.Level.Peak:P0}, RMS {result.Sample.Diagnostics.Level.Rms:P1}, clipping {(result.Clipping ? "yes" : "no")}, too quiet {(result.TooQuiet ? "yes" : "no")} | no assistant action run";

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
    }

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

        if (StartVoiceRecordingCommand is AsyncRelayCommand startVoice)
        {
            startVoice.RaiseCanExecuteChanged();
        }

        if (StopVoiceRecordingCommand is AsyncRelayCommand stopVoice)
        {
            stopVoice.RaiseCanExecuteChanged();
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

        if (RecordVoiceSampleCommand is AsyncRelayCommand recordSample)
        {
            recordSample.RaiseCanExecuteChanged();
        }

        if (PlayVoiceSampleCommand is AsyncRelayCommand playSample)
        {
            playSample.RaiseCanExecuteChanged();
        }

        if (DeleteVoiceSampleCommand is RelayCommand deleteSample)
        {
            deleteSample.RaiseCanExecuteChanged();
        }

        if (CalibrateVoiceCommand is AsyncRelayCommand calibrateVoice)
        {
            calibrateVoice.RaiseCanExecuteChanged();
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
        var inputDevices = NAudioVoiceRecorder.GetInputDevices();
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
        foreach (var device in NAudioWaveSpeechPlayer.GetOutputDevices())
        {
            VoiceOutputDevices.Add($"{device.DeviceNumber}: {device.Name}");
        }

        if (VoiceOutputDevices.Count == 0)
        {
            VoiceOutputDevices.Add("-1: Default playback device");
        }

        var outputSelection = VoiceDeviceSelection.ResolveOutput(_voiceSettings, NAudioWaveSpeechPlayer.GetOutputDevices());
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
        if (!_voiceMonitorRequested)
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
        bool? autoSendVoiceTranscripts = null)
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
            AutoSendVoiceTranscripts = autoSendVoiceTranscripts ?? _voiceSettings.AutoSendVoiceTranscripts
        };

        VoiceRuntimeSettingsStore.Save(_services.DataRoot, _voiceSettings);
    }

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

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetProcessEnvironment(string variableName, string? value) =>
        Environment.SetEnvironmentVariable(variableName, NullIfWhiteSpace(value), EnvironmentVariableTarget.Process);

    private static string AppBaseDirectory => Path.GetFullPath(AppContext.BaseDirectory);

    private static string? ResolvePortablePath(string? value)
    {
        var trimmed = NullIfWhiteSpace(value);
        if (trimmed is null)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(AppBaseDirectory, trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    private static string? ToPortablePath(string? value)
    {
        var fullPath = ResolvePortablePath(value);
        if (fullPath is null)
        {
            return null;
        }

        try
        {
            var relativePath = Path.GetRelativePath(AppBaseDirectory, fullPath);
            return string.IsNullOrWhiteSpace(relativePath) ? fullPath : relativePath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return fullPath;
        }
    }

    private static string? FindLocalPiperExecutable()
    {
        var voiceRoot = FindLocalVoiceResourceDirectory();
        var candidate = voiceRoot is null
            ? null
            : Path.Combine(voiceRoot, "python-venv", "Scripts", "piper.exe");

        return File.Exists(candidate) ? ToPortablePath(candidate) : null;
    }

    private static string? FindLocalPiperVoiceDirectory()
    {
        var voiceRoot = FindLocalVoiceResourceDirectory();
        var candidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "piper");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? FindLocalVoiceResourceDirectory()
    {
        var executableLocalVoiceRoot = Path.Combine(AppBaseDirectory, "lib", "voice");
        if (Directory.Exists(executableLocalVoiceRoot))
        {
            return executableLocalVoiceRoot;
        }

        var directory = new DirectoryInfo(AppBaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "lib", "voice");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

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
