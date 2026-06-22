using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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
    private readonly AliServices _services;
    private readonly NAudioInputLevelMonitor _inputLevelMonitor = new();
    private readonly string _conversationId = $"conv_{Guid.NewGuid():N}";
    private VoiceRuntimeSettings _voiceSettings;
    private bool _loadingVoiceSettings;
    private CancellationTokenSource? _activeResponse;
    private CancellationTokenSource? _activeVoiceInput;
    private CancellationTokenSource? _activeSpeech;
    private string _composerText = string.Empty;
    private bool _isBusy;
    private bool _isRecording;
    private bool _isTranscribing;
    private bool _isSpeaking;
    private string _statusText = "Ready. Local runtime is not configured yet.";
    private string _runtimeDisplay;
    private string _runtimeEndpointText = string.Empty;
    private string _runtimeModelText = string.Empty;
    private string _runtimeContextText = "4096";
    private string _runtimeOutputLimitText = "512";
    private string _runtimeTemperatureText = "0.2";
    private string _runtimeTopPText = string.Empty;
    private bool _runtimeEnabled;
    private bool _runtimeStreamingEnabled = true;
    private bool _runtimeVisionEnabled;
    private bool _canActivateRuntime;
    private bool _canRevertToLastKnownGood;
    private string _runtimeHealthResult = "No runtime health check has been run.";
    private string _activeRuntimeStatus = "Using safe deterministic stub.";
    private string _attachmentStatus = "Screenshots are temporary by default.";
    private string _voiceStatus = "Voice idle.";
    private string _sttStatus;
    private string _ttsStatus;
    private string _lastTranscript = string.Empty;
    private string _editableTranscript = string.Empty;
    private string _selectedVoiceInputDevice = "Default microphone";
    private string _selectedVoiceOutputDevice = "Default speaker";
    private string _selectedVoiceInputPreset = VoiceInputPreset.HeadsetMic;
    private double _voiceInputLevelPercent;
    private string _voiceInputMeterText = "Input meter starting.";
    private string _voiceDiagnosticsText = "No voice capture yet.";
    private VoiceTurnMetadata? _lastVoiceMetadata;

    public MainWindowViewModel(AliServices services)
    {
        _services = services;

        SendCommand = new AsyncRelayCommand(SendAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(ComposerText));
        StopCommand = new RelayCommand(_ => Stop(), _ => IsBusy);
        FlagIncorrectCommand = new RelayCommand(FlagIncorrect);
        LoadRuntimeSettingsCommand = new RelayCommand(_ => LoadRuntimeSettings());
        SaveRuntimeSettingsCommand = new RelayCommand(_ => SaveRuntimeSettings());
        CheckRuntimeCommand = new AsyncRelayCommand(CheckRuntimeAsync, () => !IsBusy);
        ActivateRuntimeCommand = new RelayCommand(_ => ActivateRuntime(), _ => CanActivateRuntime && !IsBusy);
        RevertToStubCommand = new RelayCommand(_ => RevertToStub(), _ => !IsBusy);
        RevertToLastKnownGoodCommand = new RelayCommand(_ => RevertToLastKnownGood(), _ => CanRevertToLastKnownGood && !IsBusy);
        PasteImageCommand = new AsyncRelayCommand(AddClipboardImageAsync);
        CaptureScreenCommand = new AsyncRelayCommand(CaptureFullScreenAsync);
        RemoveAttachmentCommand = new RelayCommand(RemoveAttachment);
        StartVoiceRecordingCommand = new AsyncRelayCommand(StartVoiceRecordingAsync, () => !IsBusy && !IsRecording && !IsTranscribing);
        StopVoiceRecordingCommand = new AsyncRelayCommand(StopVoiceRecordingOrTranscriptionAsync, () => IsRecording || IsTranscribing);
        SendTranscriptCommand = new AsyncRelayCommand(SendTranscriptAsync, () => !IsBusy && !IsRecording && !IsTranscribing && !string.IsNullOrWhiteSpace(EditableTranscript));
        StopSpeakingCommand = new RelayCommand(_ => StopSpeaking(), _ => IsSpeaking);

        _voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(_services.DataRoot);
        foreach (var preset in VoiceInputPreset.All)
        {
            VoiceInputPresets.Add(preset);
        }

        _selectedVoiceInputPreset = VoiceInputPreset.Normalize(_voiceSettings.SelectedInputPreset);
        _inputLevelMonitor.LevelAvailable += InputLevelAvailable;

        _loadingVoiceSettings = true;
        LoadVoiceDevices();
        _loadingVoiceSettings = false;
        ApplyVoiceInputPreset(SelectedVoiceInputPreset);
        StartInputLevelMonitor();

        _sttStatus = _services.SpeechToText.IsConfigured
            ? $"STT ready: {_services.SpeechToText.ProviderName}"
            : "STT not configured. Set ALI_WHISPER_EXE for local transcription.";
        _ttsStatus = _services.TextToSpeech.IsConfigured
            ? $"TTS ready: {_services.TextToSpeech.ProviderName}"
            : "TTS not configured. Set ALI_PIPER_EXE and ALI_PIPER_MODEL for local speech.";

        _runtimeDisplay = FormatRuntimeDisplay();
        LoadRuntimeSettings();

        Messages.Add(new ChatMessageViewModel(
            id: $"msg_asst_{Guid.NewGuid():N}",
            role: ChatRole.Assistant,
            text: "Ali bootstrap ready. I can prove the WPF chat loop, cancellation, and correction queue. A real local model runtime must pass a health check and be activated before I answer through it.",
            createdAt: DateTimeOffset.UtcNow,
            evidenceStatus: EvidenceStatus.Verified));
    }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = new();

    public ObservableCollection<string> VoiceInputDevices { get; } = new();

    public ObservableCollection<string> VoiceOutputDevices { get; } = new();

    public ObservableCollection<string> VoiceInputPresets { get; } = new();

    public ICommand SendCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand FlagIncorrectCommand { get; }

    public ICommand LoadRuntimeSettingsCommand { get; }

    public ICommand SaveRuntimeSettingsCommand { get; }

    public ICommand CheckRuntimeCommand { get; }

    public ICommand ActivateRuntimeCommand { get; }

    public ICommand RevertToStubCommand { get; }

    public ICommand RevertToLastKnownGoodCommand { get; }

    public ICommand PasteImageCommand { get; }

    public ICommand CaptureScreenCommand { get; }

    public ICommand RemoveAttachmentCommand { get; }

    public ICommand StartVoiceRecordingCommand { get; }

    public ICommand StopVoiceRecordingCommand { get; }

    public ICommand SendTranscriptCommand { get; }

    public ICommand StopSpeakingCommand { get; }

    public string RuntimeSettingsPath => _services.RuntimeSettingsPath;

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

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
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

        if (!double.TryParse(RuntimeTemperatureText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
            || temperature < 0
            || temperature > 2)
        {
            throw new InvalidOperationException("Temperature must be between 0 and 2.");
        }

        double? topP = null;
        if (!string.IsNullOrWhiteSpace(RuntimeTopPText))
        {
            if (!double.TryParse(RuntimeTopPText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTopP)
                || parsedTopP <= 0
                || parsedTopP > 1)
            {
                throw new InvalidOperationException("Top-p must be greater than 0 and no more than 1.");
            }

            topP = parsedTopP;
        }

        var model = RuntimeModelText.Trim();

        return new OpenAiCompatibleRuntimeOptions(
            Enabled: RuntimeEnabled,
            Endpoint: endpoint,
            Model: model,
            DisplayName: string.IsNullOrWhiteSpace(model) ? "Local OpenAI-compatible runtime" : $"Local {model}",
            Family: "local",
            Size: "unknown",
            Quantization: "Q4",
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
        try
        {
            IsTranscribing = true;
            SttStatus = "Transcribing locally...";

            var transcript = await _services.SpeechToText.TranscribeAsync(
                audioInput,
                _activeVoiceInput?.Token ?? CancellationToken.None).ConfigureAwait(true);

            var transcriptGuard = SpeechTranscriptGuard.Evaluate(transcript.Text, requireAssistantName: true);
            if (!transcriptGuard.Accepted)
            {
                throw new InvalidOperationException(transcriptGuard.Message);
            }

            SaveLastSuccessfulSttDevice();
            LastTranscript = transcript.Text;
            EditableTranscript = transcript.Text;
            _lastVoiceMetadata = new VoiceTurnMetadata(
                VoiceInputOrigin.Voice,
                transcript.Text,
                transcript.ProviderName,
                transcript.Mode,
                _services.TextToSpeech.ProviderName,
                _services.TextToSpeech.VoiceId,
                audioInput.RetainAudio,
                CurrentInputDeviceNumber(),
                CurrentInputDeviceName(),
                SelectedVoiceInputPreset,
                CurrentSpeechToTextModel(),
                CurrentTextToSpeechModel(),
                SuspiciousOrNoSpeech: false);

            VoiceStatus = "Transcript ready to review.";
            SttStatus = $"Transcript created by {transcript.ProviderName}.";
        }
        catch (OperationCanceledException)
        {
            VoiceStatus = "Voice input canceled.";
            SttStatus = "Transcription canceled.";
        }
        catch (Exception ex)
        {
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
            VoiceStatus = transcriptGuard.Message;
            StatusText = VoiceStatus;
            return;
        }

        if (VoiceCommandSafety.RequiresVisibleConfirmation(transcript))
        {
            VoiceStatus = VoiceCommandSafety.BlockedPhaseOneCMessage();
            StatusText = VoiceStatus;
            return;
        }

        var voiceMetadata = (_lastVoiceMetadata ?? new VoiceTurnMetadata(
            VoiceInputOrigin.Voice,
            transcript,
            _services.SpeechToText.ProviderName,
            _services.SpeechToText.Mode,
            _services.TextToSpeech.ProviderName,
            _services.TextToSpeech.VoiceId,
            RawAudioRetained: false)) with
        {
            Transcript = transcript,
            TextToSpeechProvider = _services.TextToSpeech.ProviderName,
            TextToSpeechVoice = _services.TextToSpeech.VoiceId,
            InputDeviceNumber = CurrentInputDeviceNumber(),
            InputDeviceName = CurrentInputDeviceName(),
            InputPreset = SelectedVoiceInputPreset,
            SpeechToTextModel = CurrentSpeechToTextModel(),
            TextToSpeechModel = CurrentTextToSpeechModel()
        };

        VoiceStatus = "Voice transcript sent to Ali.";
        await SendTextAsync(transcript, VoiceInputOrigin.Voice, voiceMetadata).ConfigureAwait(true);
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
                voiceMetadata?.TextToSpeechVoice ?? _services.TextToSpeech.VoiceId,
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
        RuntimeEnabled = options.Enabled;
        RuntimeEndpointText = options.Endpoint.ToString();
        RuntimeModelText = options.Model;
        RuntimeContextText = options.ContextTokens.ToString(CultureInfo.InvariantCulture);
        RuntimeOutputLimitText = options.OutputTokenLimit.ToString(CultureInfo.InvariantCulture);
        RuntimeTemperatureText = options.Temperature.ToString(CultureInfo.InvariantCulture);
        RuntimeTopPText = options.TopP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RuntimeStreamingEnabled = options.StreamingEnabled;
        RuntimeVisionEnabled = options.SupportsVision;
    }

    private static string FormatHealthResult(RuntimeHealthCheck health)
    {
        var streaming = health.StreamingSupported is null
            ? "not checked"
            : health.StreamingSupported.Value ? "yes" : "no";

        return $"{health.Summary}\nEndpoint: {health.Endpoint ?? "n/a"}\nModel: {health.ModelPackageId ?? "n/a"}\nElapsed: {health.Elapsed.TotalMilliseconds:N0} ms\nStreaming supported: {streaming}";
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
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.ProcessorSettings = VoiceInputPreset.CreateSettings(presetName);
        }

        SaveVoiceSettings(selectedInputPreset: presetName);
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
        }
    }

    private void UnsubscribeRecorderLevels()
    {
        if (_services.VoiceRecorder is NAudioVoiceRecorder recorder)
        {
            recorder.LevelAvailable -= InputLevelAvailable;
        }
    }

    private void UpdateCaptureDiagnostics(VoiceAudioInput audioInput)
    {
        try
        {
            var diagnostics = VoiceAudioFileAnalyzer.AnalyzeWaveAudio(
                audioInput.FilePath,
                CurrentInputDeviceNumber(),
                CurrentInputDeviceName());
            VoiceDiagnosticsText = diagnostics.Summary;
            ApplyInputLevelSnapshot(diagnostics.Level);
        }
        catch (Exception ex)
        {
            VoiceDiagnosticsText = $"Capture diagnostics unavailable: {ex.Message}";
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
        string? selectedInputPreset = null)
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
            SelectedInputPreset = VoiceInputPreset.Normalize(selectedInputPreset ?? _voiceSettings.SelectedInputPreset)
        };

        VoiceRuntimeSettingsStore.Save(_services.DataRoot, _voiceSettings);
    }

    private int CurrentInputDeviceNumber() =>
        TryReadDeviceNumber(SelectedVoiceInputDevice, out var deviceNumber) ? deviceNumber : 0;

    private int CurrentOutputDeviceNumber() =>
        TryReadDeviceNumber(SelectedVoiceOutputDevice, out var deviceNumber) ? deviceNumber : -1;

    private string CurrentInputDeviceName() => ReadDeviceName(SelectedVoiceInputDevice);

    private string CurrentOutputDeviceName() => ReadDeviceName(SelectedVoiceOutputDevice);

    private string CurrentSpeechToTextModel() =>
        _services.SpeechToText is WhisperCliSpeechToTextProvider whisper ? whisper.ModelPath : string.Empty;

    private string CurrentTextToSpeechModel() =>
        _services.TextToSpeech is PiperCliTextToSpeechProvider piper ? piper.ModelPath : string.Empty;

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
