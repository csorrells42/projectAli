using AvatarBuilder.Modules.Audio.Microphone;
using AvatarBuilder.Modules.Audio.ParakeetSpeechToText;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Audio.SpeechToText;
using AvatarBuilder.Modules.Audio.VoiceActivity;
using AvatarBuilder.Modules.Audio.WakeWord;
using AvatarBuilder.Modules.Confidence;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Security;
using AvatarBuilder.Modules.Vision.Attention;
using AvatarBuilder.Modules.Vision.TargetSelection;

namespace Ali.Modules.Interaction;

public enum AliSpeechToTextEngine
{
    Parakeet,
    Whisper
}

/// <summary>
/// Composition only for Ali's tested subscription-based speech chain.
/// </summary>
public sealed class AliSpeechIngressPipeline : IDisposable
{
    private readonly MicrophoneModule _microphone = new();
    private readonly VoiceActivityModule _voiceActivity;
    private readonly SpeakerRecognitionModule _speaker;
    private readonly WakeWordModule _wakeWord;
    private readonly ParakeetEnrollmentTranscriber _parakeetEnrollment = new();
    private readonly WhisperEnrollmentTranscriber _whisperEnrollment = new();
    private readonly ModuleOutputBroadcaster<TargetSelectionOutput> _cameraOffTarget = new();
    private readonly ModuleOutputBroadcaster<AttentionOutput> _cameraOffAttention = new();
    private readonly string _dataFolder;
    private AliSecurityModule? _security;
    private ISpeechToTextModule? _speechToText;
    private InteractionConfidenceModule? _logger;
    private IModuleOutputSubscription<TranscriptionOutput>? _transcripts;
    private AliVisionPipeline? _vision;
    private AliSpeechToTextEngine _engine = AliSpeechToTextEngine.Parakeet;
    private bool _pttEnabled;
    private bool _pttPressed;
    private bool _started;
    private bool _disposed;

    public AliSpeechIngressPipeline(
        string assistantName,
        string dataFolder)
    {
        _dataFolder = Path.GetFullPath(dataFolder);
        _voiceActivity = new VoiceActivityModule(_microphone);
        var enrollments = Path.Combine(
            _dataFolder,
            "AvatarSystem",
            "SpeakerRecognition",
            "Enrollments");
        _speaker = new SpeakerRecognitionModule(
            _voiceActivity,
            enrollments,
            enrollmentTranscriber: _parakeetEnrollment);
        _wakeWord = new WakeWordModule(_voiceActivity, assistantName);
    }

    public IModuleOutputSource<SpeakerRecognitionOutput> Speaker => _speaker;
    public ISpeakerEnrollmentService SpeakerEnrollmentService => _speaker;
    public IMicrophoneInputService Microphone => _microphone;
    public string ProviderName => _speechToText?.ProviderName ??
        (_engine == AliSpeechToTextEngine.Parakeet
            ? "Local Parakeet TDT v2 int8"
            : "Local Whisper CLI");
    public bool IsConfigured => _speechToText?.IsConfigured ?? false;
    public string Status => _microphone.InputStatus;
    public bool HasAttention =>
        _security?.LatestDecision.AttentionSource != AttentionGrantSource.None;
    public AttentionGrantSource AttentionSource =>
        _security?.LatestDecision.AttentionSource ?? AttentionGrantSource.None;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }
        _started = true;
        if (_security is null)
        {
            RebuildSecurityTail();
        }
        _voiceActivity.Start();
        _speaker.Start();
        _wakeWord.Start();
        _microphone.Start();
    }

    public void SelectMicrophoneByName(string? deviceName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }
        var match = _microphone.GetAvailableInputs()
            .FirstOrDefault(device =>
                device.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains(device.Name, StringComparison.OrdinalIgnoreCase));
        if (match is not null
            && !string.Equals(match.Id, _microphone.SelectedInputId, StringComparison.OrdinalIgnoreCase))
        {
            _microphone.SelectInput(match.Id);
        }
    }

    public void AttachVision(AliVisionPipeline? vision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_vision, vision))
        {
            return;
        }
        _vision = vision;
        RebuildSecurityTail();
    }

    public void UpdatePushToTalk(bool enabled, bool pressed)
    {
        _pttEnabled = enabled;
        _pttPressed = pressed;
        _security?.UpdatePushToTalk(enabled, pressed);
    }

    public void SelectEngine(AliSpeechToTextEngine engine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine == engine)
        {
            return;
        }
        _engine = engine;
        _speaker.SelectEnrollmentTranscriber(
            engine == AliSpeechToTextEngine.Parakeet
                ? _parakeetEnrollment
                : _whisperEnrollment);
        if (_security is not null)
        {
            BuildSpeechToText();
        }
    }

    public bool TryTakeTranscript(SnapshotCursor<TranscriptionOutput> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transcripts?.TryTake(destination) == true;
    }

    private void BuildSpeechToText()
    {
        if (_security is null)
        {
            return;
        }
        var oldSubscription = _transcripts;
        var oldLogger = _logger;
        var oldSpeechToText = _speechToText;
        _transcripts = null;
        _logger = null;
        _speechToText = _engine == AliSpeechToTextEngine.Parakeet
            ? new ParakeetSpeechToTextModule(_security)
            : new SpeechToTextModule(_security);
        _transcripts = _speechToText.Subscribe();
        _logger = new InteractionConfidenceModule(_speechToText, _dataFolder);
        _logger.Start();
        _speechToText.Start();
        oldSubscription?.Dispose();
        _ = Task.Run(() =>
        {
            oldLogger?.Dispose();
            oldSpeechToText?.Dispose();
        });
    }

    private void RebuildSecurityTail()
    {
        var newSecurity = new AliSecurityModule(
            _vision?.TargetSelection ?? _cameraOffTarget,
            _vision?.Attention ?? _cameraOffAttention,
            _speaker,
            _wakeWord);
        newSecurity.UpdatePushToTalk(_pttEnabled, _pttPressed);

        ISpeechToTextModule? newSpeechToText = null;
        IModuleOutputSubscription<TranscriptionOutput>? newTranscripts = null;
        InteractionConfidenceModule? newLogger = null;
        try
        {
            newSpeechToText = _engine == AliSpeechToTextEngine.Parakeet
                ? new ParakeetSpeechToTextModule(newSecurity)
                : new SpeechToTextModule(newSecurity);
            newTranscripts = newSpeechToText.Subscribe();
            newLogger = new InteractionConfidenceModule(newSpeechToText, _dataFolder);
            newSecurity.Start();
            newLogger.Start();
            newSpeechToText.Start();
        }
        catch
        {
            newTranscripts?.Dispose();
            newLogger?.Dispose();
            newSpeechToText?.Dispose();
            newSecurity.Dispose();
            throw;
        }

        var oldTranscripts = _transcripts;
        var oldLogger = _logger;
        var oldSpeechToText = _speechToText;
        var oldSecurity = _security;
        _security = newSecurity;
        _speechToText = newSpeechToText;
        _transcripts = newTranscripts;
        _logger = newLogger;

        // A vision pipeline can be disposed immediately after this returns.
        // Stop its consumers synchronously so none retain a disposed camera signal.
        oldSecurity?.Dispose();
        oldTranscripts?.Dispose();
        _ = Task.Run(() =>
        {
            oldLogger?.Dispose();
            oldSpeechToText?.Dispose();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _transcripts?.Dispose();
        _logger?.Dispose();
        _speechToText?.Dispose();
        _security?.Dispose();
        _wakeWord.Dispose();
        _speaker.Dispose();
        _parakeetEnrollment.Dispose();
        _whisperEnrollment.Dispose();
        _voiceActivity.Dispose();
        _microphone.Dispose();
        _cameraOffAttention.Dispose();
        _cameraOffTarget.Dispose();
    }
}
