using AvatarBuilder.Modules.Audio.SpeechToText;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace Ali.Modules.Interaction;

public sealed record AliAcceptedSpeech(
    string ExactText,
    string Provider,
    string AttentionSource,
    string PersonIdentityId,
    string ParticipantDisplayName,
    double VisualIdentityConfidence,
    double VoiceIdentityConfidence);

/// <summary>
/// Application composition facade. It owns lifecycle and selection only; all
/// capture, analysis, security, transcription, overlay, and viewport work is
/// delegated to reusable modules.
/// </summary>
public sealed class AliInteractionRuntime : IDisposable
{
    private readonly AliSpeechIngressPipeline _speech;
    private readonly SnapshotCursor<TranscriptionOutput> _transcript = new();
    private AliVisionPipeline? _vision;
    private bool _disposed;

    public AliInteractionRuntime(string assistantName, string aliDataRoot)
    {
        ConfigureBundledMediaPipeRuntime();
        DataFolder = Path.Combine(Path.GetFullPath(aliDataRoot), "Vision");
        Directory.CreateDirectory(DataFolder);
        _speech = new AliSpeechIngressPipeline(assistantName, DataFolder);
    }

    private static void ConfigureBundledMediaPipeRuntime()
    {
        const string mediaPipePythonVariable = "AVATAR_BUILDER_MEDIAPIPE_PYTHON";
        var configuredPython = Environment.GetEnvironmentVariable(mediaPipePythonVariable);
        if (!string.IsNullOrWhiteSpace(configuredPython) && File.Exists(configuredPython))
        {
            return;
        }

        var python = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        var packages = Path.Combine(AppContext.BaseDirectory, "runtime", "python-packages");
        if (!File.Exists(python) || !Directory.Exists(packages))
        {
            return;
        }

        Environment.SetEnvironmentVariable(mediaPipePythonVariable, python);
        var currentPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
        var pythonPath = string.IsNullOrWhiteSpace(currentPythonPath)
            ? packages
            : $"{packages}{Path.PathSeparator}{currentPythonPath}";
        Environment.SetEnvironmentVariable("PYTHONPATH", pythonPath);
    }

    public string DataFolder { get; }
    public System.Windows.FrameworkElement? ViewportHost => _vision?.ViewportHost;
    public bool CameraIsOn => _vision is not null;
    public bool HasVisualAttention => _vision?.HasStableAttention == true;
    public bool HasAttention => _speech.HasAttention;
    public string AttentionSource => _speech.AttentionSource.ToString();
    public string CameraStatus => _vision?.CameraStatus ?? "Camera off";
    public string SpeechStatus => _speech.Status;
    public string SpeechProviderName => _speech.ProviderName;
    public bool SpeechIsConfigured => _speech.IsConfigured;
    public double MicrophoneInputLevel => _speech.Microphone.GetInputLevel();

    public void StartSpeech(string? selectedMicrophoneName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _speech.Start();
        _speech.SelectMicrophoneByName(selectedMicrophoneName);
    }

    public IReadOnlyList<CameraDevice> GetCameras() =>
        CameraSourceSelection.GetCameras();

    public async Task<IReadOnlyList<CameraVideoMode>> GetModesAsync(
        CameraDevice camera,
        CancellationToken cancellationToken)
    {
        var modes = await new MediaFoundationCameraModeService()
            .GetModesAsync(camera, cancellationToken)
            .ConfigureAwait(false);
        if (modes.Any(mode => mode.IsAuto))
        {
            return modes;
        }
        return [CameraVideoMode.Auto, .. modes];
    }

    public System.Windows.FrameworkElement TurnCameraOn(
        CameraDevice camera,
        CameraVideoMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TurnCameraOff();
        var vision = new AliVisionPipeline(
            camera,
            mode,
            DataFolder,
            _speech.Speaker);
        _vision = vision;
        _speech.AttachVision(vision);
        return vision.ViewportHost;
    }

    public void TurnCameraOff()
    {
        _speech.AttachVision(null);
        var vision = _vision;
        _vision = null;
        vision?.Dispose();
    }

    public void UpdatePushToTalk(bool enabled, bool pressed) =>
        _speech.UpdatePushToTalk(enabled, pressed);

    public void SelectMicrophoneByName(string? deviceName) =>
        _speech.SelectMicrophoneByName(deviceName);

    public void SelectSpeechToText(AliSpeechToTextEngine engine) =>
        _speech.SelectEngine(engine);

    public void SetOverlays(bool tracking, bool faceMesh) =>
        _vision?.SetOptionalOverlays(tracking, faceMesh);

    public bool TryTakeAcceptedSpeech(out AliAcceptedSpeech? accepted)
    {
        accepted = null;
        if (!_speech.TryTakeTranscript(_transcript))
        {
            return false;
        }
        try
        {
            var output = _transcript.Current;
            if (!output.Transcription.Succeeded
                || string.IsNullOrWhiteSpace(output.ExactTextForAli))
            {
                return false;
            }
            var decision = output.Interaction.Decision;
            accepted = new AliAcceptedSpeech(
                output.ExactTextForAli,
                output.Transcription.Provider,
                decision.AttentionSource.ToString(),
                decision.PersonIdentityId,
                decision.ParticipantDisplayName,
                decision.VisualIdentityConfidence,
                decision.VoiceIdentityConfidence);
            return true;
        }
        finally
        {
            _transcript.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        TurnCameraOff();
        _transcript.Dispose();
        _speech.Dispose();
    }

}
