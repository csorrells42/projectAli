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
using Ali.Infrastructure.Bootstrap;
using Ali.Infrastructure.Runtime;

namespace Ali.App.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly AliServices _services;
    private readonly string _conversationId = $"conv_{Guid.NewGuid():N}";
    private CancellationTokenSource? _activeResponse;
    private string _composerText = string.Empty;
    private bool _isBusy;
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
            sourceUserMessageId: userMessageId,
            sourceQuestion: text);

        var history = Messages.Select(message => message.ToCoreMessage()).ToList();
        Messages.Add(userMessage);
        Messages.Add(assistantMessage);

        _activeResponse = new CancellationTokenSource();

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
                CancellationToken.None).ConfigureAwait(true);

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
    }

    private string FormatRuntimeDisplay()
    {
        var profile = _services.Orchestrator.Runtime.ActiveProfile;
        return $"{profile.DisplayName} | {profile.Quantization} | {profile.ContextTokens:N0} ctx";
    }
}
